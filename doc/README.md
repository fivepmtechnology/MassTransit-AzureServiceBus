# Spec

A discussion on how messages should be routed.

## How will routing work?

This section deals with how to handle routing.

### Subscriptions

Because SBQS provides subscriptions with topics, we'll try to use those.

Subscriptions subscribe to the *topic* of the URI-normalized message type name. Single MT subscription subscribes to hierarchy by means of multiple SBQS subscription. E.g.

`namespace A { class B : C { B(){} } interface C {} }`

then 

`bus.Subscribe(s => s.Handler<B>( b => ... ));`

causes

<table>
    <tr>
        <td>&nbsp;</td>
        <td><b>SBQS topic</b></td>
        <td><b>Subscriber</b></td>
    </tr>
    <tr>
        <td>1.</td>
        <td>A.B</td>
        <td>Mps.App1*</td>
    </tr>
    <tr>
        <td>2.</td>
        <td>A.C</td>
        <td>Mps.App1</td>
    </tr>
</table>

\* *Mps.App1* equal to the queue that we use for sending messages directly to *App1*. *Mps* is a namespace.

So if B didn't implement C:

<table>
    <tr>
        <td>&nbsp;</td>
        <td><b>SBQS topic</b></td>
        <td><b>Subscriber</b></td>
    </tr>
    <tr>
        <td>1.</td>
        <td>A.B</td>
        <td>Mps.App1*</td>
    </tr>
</table>

E.g. `bus.Publish(new B())` instance *b1* causes state:

<table><tr><td><b>Topic</b></td><td><b>Messages</b></td></tr>
<tr><td>A.B</td><td>{ b1 }</td></tr>
<tr><td>A.C</td><td>{ b1 }</td></tr>
</table>

*App1* subscribes to both these queues because of polymorphic routing. This is problematic because b1 needs to be de-duped at receiver. **Fine** - let's finish the dedup spike and make it an MT service.

**Currently Pub/Sub isn't implemented - because of the above I'm trying to decide whether to use topics or the MT subscription service**.

## On communication w/ AzureServiceBus

 * **LockDuration** - default 30 s
 * **MaxSizeInMegabytes** - default 1024 MiB
 * **RequiresDuplicateDetection** - defaults to false, duplicates are not vetted on message broker
 * **RequiresSession** -  defaults to false, we may have multiple `MessageFactory` instances in a single transport for performance
 * **DefaultMessageTimeToLive** - default
 * **EnableDeadLetteringOnMessageExpiration** - default
 * **EnableBatchedOperations** - non default - **true** - will batch **50 ms** on receive from queue.
 
Further:

 * Currently the project uses async receive and sync send. This is aimed to change to async everything, but requires changes to MT to support the asynchronous flow that is required (with its connection handler and connection policies).
 * Outbound allows for **100 messages in flight** concurrently on async send.
 * Outbound retries messages met with **ServerBusyException** after **10 s**.
 * Transports log to *MassTransit.Messages*.
 
## Endpoints

Endpoint in MT, tuple: { bus instance, inbound queue/transport, n x outbound queue/transport (one for each target we're sending to) }. In this case the endpoint needs to be, tuple { bus instance, inbound queue, n x inbound subscriptions that is polled round robin, n x outbound queue/transport (`GetEndpoint().Send()`), n x outbound subscription client (send to topic) }

## Thoughs on async

So, I can cap the concurrency; i.e. the number of asynchronous BeginSend and BeginReceive that I have outstanding, without corresponding EndXXX having been called. Since I'm using batching, the question is whether I have say enqueued on a thread/spinwait/IO-port (???) the first 50 ms.

## Thoughts on rate limits

This transport will rate-limit on the persistency/quorum of service bus itself; i.e. when a message is considered safe - ZMQ wouldn't write to disk, but rather send the message asynchronously directly, so for example, given messages { 1,2,3,4,5 }, this transport could send { 1, receive ACK 1, 2 receive ACK 2, 3, receive ACK 3, ... } or optionally, { 1, 2, receive ACK 1, 3, 4, 5, receive ACK 2, receive ACK 3, ... }. ZeroMQ would do { 1, 2, 3, 4, 5 } and we'd never know from a sending app whether they arrived. It would be up to the producer to persist its current ACKed message for service bus and the ZMQ transport SHOULD have a PUSH/PULL socket that returns with an ACKs with the message id.

## Thoughts on sending medium/large messages

 * Forum topic [BrokeredMessage 256KB limit](http://social.msdn.microsoft.com/Forums/en-US/windowsazureconnectivity/thread/b804b71e-831d-43b6-a38c-847d01034471)

The way of sending large messages (over 256K) is to upload it to Azure Blob Storage. Another way is to chunk it. Another way is to use Azure Cache.

Another method is using the session feature and session state available in Service Bus to track consumed data while writing it to storage.

Doesn't this make the node a proxy from ASB to Blob? Perhaps it would be preferrable to extend the asynchronous API ideas with a consumer that returns a function; `handle<'TMsg> :: Async<ChunkOf<'TMsg>> -> Async<unit>` and let the consumer feed asynchronously off of a async monad that yields ordered byte arrays in the correct order. Node death is handled by keeping the last sequence id in the session state of ASB and then continuing consuming from where the sequence was interrupted. If the handler wants to be able to handle things reliably, it should perform work on the asynchronous byte array stream as it goes; the successful execution of its Async<unit> return value means that it's done.

```
type ChunkOf<'TMsg> = {
  SeqId : BigInteger,
  Data : byte array }

let handler (item : Async<ChunkOf<LargeMessage>>) =
  async {
    let! ({ seqId, data }) = item
    do! deleteAfter seqId // deletes works after seq id, through custom checkpointing
    let! digest = Async.Sleep(1000) // actual work returning some partial work result
    do! persistWork digest seqId+1 }
```

Successful execution of this Async<unit> means that the transports checkpoints that chunk as successfully written into the session state.

Multiplexing would not really improve our position: http://www.250bpm.com/multiplexing so one MessagingFactory per queue. Data that is sent inside of a MessagingSession should be its own MessagingFactory + MessageReceiver, or we'd be multiplexing and wouldn't be able to select only the chunked message stream that is the large message.

## Thoughts on ServerTooBusyException - retry in 10 seconds

Retry in 1 second instead.

But we might still be choking the server that we're communicating with; our actual asynchronous requests will pile up locally on our sender on the IO completion ports, even if the receiving server is busy. We may handle this by temporarily blocking the send operation and/or throttling down the sending threaded publishers.

[Learn from previous successes though](http://en.wikipedia.org/wiki/TCP_congestion_avoidance_algorithm).

## Thoughts on maximum messaging factories per (?)

Only 100? Is that per-IP or per namespace?