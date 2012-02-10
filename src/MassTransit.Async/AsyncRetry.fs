﻿namespace MassTransit.Async

open System.Runtime.CompilerServices
open System.Runtime.InteropServices

[<AutoOpen>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AsyncRetry =
  
  open System
  open System.Threading
  
  // async work, continuation, retries left, maybe exception
  let rec bind w f n : Async<'T> =
    match n with
    | 0 -> async { let! v = w in return! f v }
    | _ -> 
      async {
        try let! v = w in return! f v
        with ex ->
          printfn "%A" ex
          return! bind w f (n-1) }

  let ret a = async { return a }

  let delay f = async { return! f() }
  
  type AsyncRetryBuilder(retries) =
    member x.Return(a) = ret a
    member x.ReturnFrom(a) = a
    member x.Delay(f) = delay f
    member x.Bind(work, f : ('a -> Async<'T>)) = bind work f retries
    member x.Zero() = ()
    member x.Using<'T, 'U when 'T :> IDisposable>(resource : 'T, work : ('T -> Async<'U>)) =
      async {
        use r = resource
        return! work r }

  let asyncRetry = AsyncRetryBuilder(4)

//type RetryBuilder(max) = 
//  member x.Return a = a               // Enable 'return'
//  member x.Delay f  = f                // Gets wrapped body and returns it (as it is)
//                                       // so that the body is passed to 'Run'
//  member x.Zero  = failwith "Zero"    // Support if .. then 
//  member x.Run f =                    // Gets function created by 'Delay'
//    let rec loop = function
//      | 0, Some(ex) -> raise ex
//      | n, _        -> try f() with ex -> loop (n-1, Some(ex))
//    loop(max, None)
//
//let retry = RetryBuilder(4)


//      try 
//        let! v = work
//        return! f v
//      with e ->
//        return! bind work f }