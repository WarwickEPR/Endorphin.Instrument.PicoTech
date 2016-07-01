// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

namespace Endorphin.Instrument.PicoTech.PicoScope5000

open System
open System.Runtime.InteropServices

module Async =

    /// Semaphore to ensure only one continuation path is followed in a raw "from continuations"
    /// computational expression
    type ContinuationGuard() =
        let sync = new obj()
        let mutable didCancel = false
        let mutable didFinish = false
        member x.IsCancelled = lock sync (fun () -> didCancel)
        member x.Cancel = lock sync (fun () -> if didCancel || didFinish then false else didCancel <- true; true)
        member x.IsFinished = lock sync (fun () -> didFinish)
        member x.Finish = lock sync (fun () -> if didFinish || didCancel then false else didFinish <- true; true)

    /// registers a compensation function, with a guard, which will evaluated before cancellation continuation
    /// if cancellation is requested in the current context
    let registerCompensation (guard:ContinuationGuard) (ct:System.Threading.CancellationToken) ccont compensation =
        let compensation' = match compensation with
                            | Some compensation -> compensation
                            | None -> System.OperationCanceledException()
        async { return ct.Register (fun () -> if guard.Cancel then compensation' |>  ccont) }

    type private Continuations<'T> = ('T->unit) * (exn->unit) * (OperationCanceledException->unit)

    type private Message<'T> =
    | CallbackReturned of 'T
    | CallbackCancelled
    | CallbackError of exn
    | SetContinuations of Continuations<'T>

    type private Continuation<'T> =
    | Continue of 'T
    | Error of exn
    | Cancelled

    type DeferredContinuation<'T>() =

        let mutable callbackHandle : GCHandle option = None

        let agent = MailboxProcessor.Start(fun inbox ->
            let rec loop result continuations = async {

                let! (msg:Message<'T>) = inbox.Receive()

                match msg with
                | CallbackReturned value ->
                    match continuations with
                    | Some (cont,_,_) ->
                        value |> cont
                        return ()
                    | None ->
                        match result with
                        | None ->
                            return! loop (Some <| Continue value) None
                        | Some _ ->
                            // ignore further requests for continuation
                            return! loop result None
                | CallbackCancelled ->
                    match continuations with
                    | Some (_,_,ccont) ->
                        OperationCanceledException() |> ccont
                        return ()
                    | None ->
                        match result with
                        | None ->
                            return! loop (Some Cancelled) None
                        | Some _ ->
                            // ignore further requests for continuation
                            return! loop result None
                | CallbackError exn ->
                    match continuations with
                    | Some (_,econt,_) ->
                        exn |> econt
                        return ()
                    | None ->
                        match result with
                        | None ->
                            return! loop (Some <| Error exn) None
                        | Some _ ->
                            // ignore further requests for continuation
                            return! loop result None
                | SetContinuations (cont,econt,ccont) ->
                    // Only on of these should have been set
                    match result with
                    | Some result' ->
                        match result' with
                        | Continue value ->
                            value |> cont
                        | Error exn ->
                            exn |> econt
                        | Cancelled ->
                            OperationCanceledException() |> ccont
                        return()
                    | None ->
                        return! loop None (Some (cont,econt,ccont))}
            loop None None )

        let freeHandle() =
            callbackHandle |> Option.iter (fun (h:GCHandle) -> h.Free())
            callbackHandle <- None

        member __.Continue value = CallbackReturned value |> agent.Post
        member __.Error exn = CallbackError exn |> agent.Post
        member __.Cancel = CallbackCancelled |> agent.Post
        member __.RegisterContinuations c = SetContinuations c |> agent.Post
        member __.CallbackHandle
            with set handle = freeHandle()
                              callbackHandle <- Some handle

        interface IDisposable with
            member x.Dispose() = freeHandle()

