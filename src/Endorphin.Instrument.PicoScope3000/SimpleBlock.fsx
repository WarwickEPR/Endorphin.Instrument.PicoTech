// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

#load "Common.fsx"
open Common

open System
open System.Threading
open Microsoft.FSharp.Data.UnitSystems.SI.UnitSymbols
open Endorphin.Instrument.PicoTech.PicoScope3000
open Endorphin.Abstract.Time

//log4net.Config.BasicConfigurator.Configure()

let blockParametersNoDownsampling =
    Parameters.Acquisition.create (Interval.from_ps 2200<ps>) (1<<<10)
    |> Parameters.Acquisition.enableChannel ChannelA DC Range_50mV 0.0f<V> FullBandwidth
    |> Parameters.Acquisition.sampleChannel ChannelA NoDownsampling
    |> Parameters.Acquisition.withTrigger (Trigger.auto 50s<ms>)
    |> Parameters.Block.create
    |> Parameters.Block.withPostTriggerSamples 10000000
    |> Parameters.Block.withBuffering SingleCapture
    |> Parameters.Block.blockCapture

let noDownsampling picoScope = async {
    // create an acquisition with the previously defined parameters and start it after subscribing to its events
    let acquisition = Acquisition.prepare picoScope blockParametersNoDownsampling
    let input = (Analogue ChannelA, NoDownsamplingBuffer)
    printStatusUpdates acquisition
//    printSampled input acquisition
    printRate input acquisition
    printTotalCount input acquisition
    return acquisition
}

let cts = new CancellationTokenSource()

let experiment picoScope = async {
    // create an acquisition with the previously defined parameters and start it after subscribing to its events
    let! acquisition = noDownsampling picoScope
    let acquisitionHandle = Acquisition.startWithCancellationToken acquisition cts.Token

    // wait for the acquisition to finish automatically or by cancellation
    let! result = Acquisition.waitToFinish acquisitionHandle
    match result with
    | AcquisitionCompleted -> printfn "Stream completed successfully."
    | AcquisitionError exn -> printfn "Stream failed: %s" exn.Message
    | AcquisitionCancelled -> printfn "Stream cancelled successuflly." }

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
