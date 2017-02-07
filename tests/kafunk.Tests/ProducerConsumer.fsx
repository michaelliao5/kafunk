﻿#r "bin/release/fsharp.control.asyncseq.dll"
#r "bin/Release/kafunk.dll"
#time "on"

open FSharp.Control
open Kafunk
open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Collections.Concurrent

let Log = Log.create __SOURCE_FILE__

let argiDefault i def = fsi.CommandLineArgs |> Seq.tryItem i |> Option.getOr def

let host = argiDefault 1 "localhost"
let topicName = argiDefault 2 "absurd-topic"
let totalMessageCount = argiDefault 3 "10000" |> Int32.Parse
let batchSize = argiDefault 4 "1000" |> Int32.Parse
let consumerCount = argiDefault 5 "1" |> Int32.Parse
let producerThreads = argiDefault 6 "100" |> Int32.Parse

let testId = Guid.NewGuid().ToString("n")
let consumerGroup = "kafunk-producer-consumer-test-" + testId

let messageKey = "at-least-once-test-" + testId
let messageKeyBytes = messageKey |> System.Text.Encoding.UTF8.GetBytes |> Binary.ofArray
let chanConfig = ChanConfig.create (requestTimeout = TimeSpan.FromSeconds 10.0)

let consuming = new CountdownEvent(consumerCount)
let completed = IVar.create ()


type ReportReq = 
  | Received of values:(int * (Partition * Offset))[] * messageCount:int
  | Produced of count:int * offsets:(Partition * Offset)[]
  | Report of AsyncReplyChannel<Report>

and Report =
  struct
    val received : int
    val duplicates : int
    val produced : int
    val skipped : int
    val contigCount : int
    val lastContigOffset : (Partition * Offset) option
    val offsets : Map<Partition, Offset>
    new (r,d,p,s,cc,lco,os) = 
      { received = r ; duplicates = d ; produced = p ; skipped = s ; contigCount = cc ; lastContigOffset = lco ; offsets = os }
  end

let mb = Mb.Start (fun mb ->
  
  let duplicates = ResizeArray<int>()
  let received = SortedList<int, _>()
  let produced = ref 0
  let skipped = ref 0
  let offsets = ref Map.empty
  let lastContigIndex = ref 0

  mb.Error.Add (fun e -> Log.error "mailbox_error|%O" e)

  let lastAndCountMonoid =
    Monoid.product Monoid.optionLast Monoid.intSum

  let skipMapi (ls:System.Collections.Generic.IList<_>) (skip:int) (f:int -> 'a -> 'b) = 
    Seq.unfold (fun i -> 
      if i < ls.Count then
        Some (f i ls.[i], i + 1)
      else
        None) skip

//  let contig (skip:int) =
//    if received.Count < 2 then Seq.empty else
//    seq {
//      let i = ref skip
//      let go = ref true
//      while !i < received.Count && !go do
//        if !i > 0 then
//          let i = !i
//          let j = i - 1
//          let currKey = received.Keys.[i]
//          let prevKey = received.Keys.[j]
//          if currKey <> prevKey + 1 then
//            go := false
//          else
//            yield (prevKey,(j,received.Values.[j])),(currKey,(i,received.Values.[i]))
//        incr i }

  let report () =
    let lastContigOffsetAndIndex,contigCount =
      skipMapi received.Keys !lastContigIndex (fun i k -> k, (i, received.Values.[i]))
      |> Seq.pairwise
      |> Seq.takeWhile (fun ((x,_),(y,_)) -> y = x + 1)
      //contig !lastContigIndex
      |> Seq.foldMap lastAndCountMonoid (fun ((x,_),(y,(i,os))) -> Some (i,os), 1)
    let contigCount = contigCount + !lastContigIndex
    let lastContigOffset = lastContigOffsetAndIndex |> Option.map snd
    lastContigIndex := lastContigOffsetAndIndex |> Option.map fst |> Option.getOr 0
    Report(received.Count, duplicates.Count, !produced, !skipped, contigCount, lastContigOffset, !offsets) 

  let rec loop () = async {
    let! req = mb.Receive ()
    match req with
    | Received (values,messageBatchCount) ->
      
      for (v,(p,o)) in values do
        if received.ContainsKey v then
          duplicates.Add v
        else
          received.Add (v,(p,o))

      if received.Count >= totalMessageCount then
        Log.info "received_complete_set|receive_count=%i" received.Count
        IVar.put () completed

      Interlocked.Add(skipped, messageBatchCount - values.Length) |> ignore

    | Produced (count,os) ->
      Interlocked.Add (produced, count) |> ignore
      offsets := (!offsets, os |> Map.ofArray) ||> Map.mergeWith max

    | Report rep ->
      rep.Reply (report ())

    return! loop () }

  loop ())




let producer = async {
 
  let message (messageNumber:int) =
    let value = Binary.ofArray (Array.zeroCreate 4)
    let _ = Binary.writeInt32 messageNumber value
    ProducerMessage.ofBytes (value, messageKeyBytes)

  let messageBatch (batchNumber:int) (pc:PartitionCount) = 
    let p = batchNumber % pc
    let messages = Array.init batchSize (fun j -> message (batchNumber * batchSize + j))
    p, messages

  let batchCount = totalMessageCount / batchSize

  do! consuming.WaitHandle |> Async.AwaitWaitHandle |> Async.Ignore
  do! Async.Sleep 5000 // TODO: consumer coordination

  Log.info "starting_producer_process|batch_count=%i" batchCount

  let connCfg = KafkaConfig.create ([KafkaUri.parse host], tcpConfig = chanConfig)
  use! conn = Kafka.connAsync connCfg

  let producerCfg =
    ProducerConfig.create (
      topic = topicName, 
      partition = Partitioner.roundRobin,
      requiredAcks = RequiredAcks.Local,
      batchSizeBytes = ProducerConfig.DefaultBatchSizeBytes,
      bufferSize = 1000)

  let! producer = Producer.createAsync conn producerCfg

  let produceProcess =
    Seq.init batchCount id
    |> Seq.map (fun batchNumber -> async {
      try
        let! res = Producer.produceBatch producer (messageBatch batchNumber)
        mb.Post (ReportReq.Produced (batchSize, ([|res.partition,res.offset|])))
      with ex ->
        Log.error "produce_error|error=%O" ex })
    |> Async.ParallelThrottledIgnore producerThreads

  return! Async.choose (IVar.get completed) produceProcess

  Log.info "producer_done" }


let consumer = async {

  let handle (_:GroupMemberState) (ms:ConsumerMessageSet) = async {
        
    let values = 
      ms.messageSet.messages
      |> Seq.choose (fun (o,_,m) ->
        let key = Binary.toString m.key
        if key = messageKey then
          let i,_ = Binary.readInt32 m.value
          Some (i,(ms.partition,o))
        else
          None)
      |> Seq.toArray

    mb.Post (ReportReq.Received (values,ms.messageSet.messages.Length)) }

  let connCfg = KafkaConfig.create ([KafkaUri.parse host], tcpConfig = chanConfig)
  use! conn = Kafka.connAsync connCfg

  let consumerCfg = 
    ConsumerConfig.create (
      consumerGroup, 
      topic = topicName, 
      initialFetchTime = Time.LatestOffset, 
      outOfRangeAction = ConsumerOffsetOutOfRangeAction.HaltConsumer,
      endOfTopicPollPolicy = RetryPolicy.constantMs 1000)

  let! consumer = Consumer.createAsync conn consumerCfg

  consuming.Signal () |> ignore

  let consumeProcess = 
    Consumer.consumePeriodicCommit consumer (TimeSpan.FromSeconds 5.0) handle 

  return! Async.choose (consumeProcess) (IVar.get completed) }

let sw = System.Diagnostics.Stopwatch.StartNew()

let printReport (report:Report) =
  let pending = totalMessageCount - report.received
  let lag = report.produced - report.received
  let offsetStr = report.offsets |> Seq.map (fun kvp -> sprintf "p=%i o=%i" kvp.Key kvp.Value) |> String.concat " ; "
  let contigDelta = report.received - report.contigCount 
  Log.info "monitor|produced=%i received=%i lag=%i duplicates=%i pending=%i contig=%i contig_delta=%i last_contig_offset=%A offsets=[%s] running_time_min=%f" 
    report.produced report.received lag report.duplicates pending report.contigCount contigDelta report.lastContigOffset offsetStr sw.Elapsed.TotalMinutes

let monitor = async {
  while not completed.Task.IsCompleted do 
    do! Async.Sleep 5000
    let! report = mb.PostAndAsyncReply (ReportReq.Report)
    printReport report
    if (report.received - report.contigCount) > 1000000 then
      Log.error "contig_delta_surpassed_threshold"
      IVar.put () completed }

Log.info "starting_producer_consumer_test|host=%s topic=%s message_count=%i batch_size=%i consumer_count=%i producer_parallelism=%i" 
  host topicName totalMessageCount batchSize consumerCount producerThreads

let go = 
  Async.Parallel
    [
      yield monitor
      for _ in [1..consumerCount] do
        yield (consumer |> Async.tryWith (fun ex -> async { Log.error "consumer_error|%O" ex }))
        Thread.Sleep 100
      yield (producer |> Async.tryWith (fun ex -> async { Log.error "producer_errror|%O" ex }))
    ]
  |> Async.Ignore

try
  Async.RunSynchronously (go)
with ex ->
  Log.error "%O" ex

sw.Stop()

let report = mb.PostAndAsyncReply (ReportReq.Report) |> Async.RunSynchronously
printReport report
