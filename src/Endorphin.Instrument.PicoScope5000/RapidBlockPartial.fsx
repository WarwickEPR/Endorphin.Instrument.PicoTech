// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

#load "Common.fsx"
open Common

open System
open System.Threading
open Microsoft.FSharp.Data.UnitSystems.SI.UnitSymbols
open Endorphin.Instrument.PicoTech.PicoScope5000
open Endorphin.Abstract.Time

let channels = [ ChannelA; ChannelB]
let channelOut = [ (ChannelA,AveragedBuffer), (ChannelB,AveragedBuffer) ]
let parameters =
    let triggerLevel = 1s<<<6 // Adc count, half max 14 bit signed TODO should be voltage
    let triggerDelayInSamples' = 100<us>
    let sampleCount = 1000
    let downsamplingRatio' = 100u
    let sweepTrigger = Trigger.simple Trigger.external triggerLevel Rising 100u
    Parameters.Acquisition.create (Interval.from_us 10<us>) Resolution_14bit (64 * 1024)
    |> Parameters.Acquisition.enableChannels channels DC Range_10V 0.0f<V> Bandwidth_20MHz
    |> Parameters.Acquisition.sampleChannels channels Averaged
    |> Parameters.Acquisition.withDownsamplingRatio downsamplingRatio'
    |> Parameters.Acquisition.withTrigger sweepTrigger
    |> Parameters.Block.create
    |> Parameters.Block.withPostTriggerSamples sampleCount
    |> Parameters.Block.withBuffering AllCaptures
//    |> Parameters.Block.withBuffering SingleCapture
    |> Parameters.Block.rapidBlockCapture 10u


/// Returns an observable sequence of CW EPR sample blocks from the streaming acquisition.
let samplesForAcquisition acquisition =
    let inputs = channels |> List.map (fun channel -> (channel, AveragedBuffer)) |> Array.ofList
    Signal.Block.voltages inputs acquisition

let processSamples =
    printfn "Process Samples called"
    samplesForAcquisition
    >> Observable.subscribe (printfn "Samples: %A")

let experiment picoScope = async {
    // create an acquisition with the previously defined parameters and start it after subscribing to its events

    for i in 0 .. 100 do
        let acquisition = Acquisition.prepare picoScope parameters
        use __ = acquisition |> processSamples
        let! acqHandle = Acquisition.startAsChild acquisition
        printfn "Started child"
        let! acquisitionResult = Acquisition.waitToFinish acqHandle
        printfn "Finished"
        match acquisitionResult with
        | AcquisitionCompleted -> ()
        | AcquisitionCancelled -> printfn "Acquisition cancelled"
        | AcquisitionError err -> failwithf "Acquisition failed: %A" err
}


let cts = new CancellationTokenSource()

Async.Start (async {
    try
        let! picoScope = PicoScope.openFirst()
        try
            do! experiment picoScope
        finally
            Async.StartWithContinuations(
                PicoScope.close picoScope,
                (fun ()  -> printfn "Successfully closed connection to PicoScope."),
                (fun exn -> printfn "Failed to close connection to PicoScope: %s" exn.Message),
                ignore)
    with exn -> printfn "Experiment failed: %s" exn.Message })

