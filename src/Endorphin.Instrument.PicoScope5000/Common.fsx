// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

#I "../../packages/"

#r "Endorphin.Core/lib/net452/Endorphin.Core.dll"
#r "Endorphin.Abstract/lib/net452/Endorphin.Abstract.dll"
#r "FSharp.Control.Reactive/lib/net40/FSharp.Control.Reactive.dll"
#r "Rx-Linq/lib/net45/System.Reactive.Linq.dll"
#r "Rx-Interfaces/lib/net45/System.Reactive.Interfaces.dll"
#r "Rx-Core/lib/net45/System.Reactive.Core.dll"
#r "FSharp.Charting/lib/net40/FSharp.Charting.dll"
#r "bin/Debug/Endorphin.Instrument.PicoTech.PicoScope5000.dll"
#r "System.Windows.Forms.DataVisualization.dll"

open System
open FSharp.Control.Reactive
open Endorphin.Instrument.PicoTech.PicoScope5000
open Endorphin.Abstract.Time

[<AutoOpen>]
module Common =
    let printStatusUpdates acquisition =
        Acquisition.status acquisition
        |> Observable.add (printfn "%A") // print stream status updates (preparing, streaming, finished...)

    let printSamples inputs acquisition =
        Signal.Single.voltageByTime inputs acquisition
        |> Observable.add (printfn "Sample: %A")

    let printAdc inputs acquisition =
        Signal.Single.adcCount inputs acquisition
        |> Observable.add (printfn "Sample Adc: %A")

    let printBlockCount inputs acquisition =
        Signal.blockSampleCount inputs acquisition
        |> Observable.add (printfn "Block count: %A")

    let printSampled inputs acquisition =
        Signal.Single.voltageByTime inputs acquisition
        |> Observable.sample (TimeSpan.FromMilliseconds 50.0)
        |> Observable.add (printfn "Sample: %A")

    let printSampledBlocks inputs acquisition =
        Signal.Block.voltages inputs acquisition
        |> Observable.sample (TimeSpan.FromMilliseconds 500.0)
        |> Observable.add (printfn "Sample: %A")
    let printRate inputs acquisition =
        Signal.Single.adcCountByTime inputs acquisition
        |> Observable.bufferSpan (TimeSpan.FromSeconds 0.5)
        |> Observable.add (fun x -> (printfn "Rate: %.1f ks/s" (float x.Count * 0.002)))

    let printTotalCount inputs acquisition =
        let timer = System.Diagnostics.Stopwatch()
        timer.Start()
        Signal.Single.adcCountByTime inputs acquisition
        |> Observable.count
        |> Observable.add (fun x -> let t = timer.ElapsedMilliseconds;
                                    printfn "Received %d samples in %.1f s. Approx rate: %d ks/s" x (float t*0.001) (x/int t))

    let printWhenFinished inputs acquisition =
        Signal.Single.adcCount inputs acquisition
        |> Observable.last
        |> Observable.add (fun x -> printfn "Reached the end of the sample stream")

    let noWork = async { return () }
