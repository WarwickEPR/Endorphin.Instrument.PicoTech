// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

#r "bin/Debug/Endorphin.Instrument.PicoTech.PicoScope3000.dll"
#r "System.Windows.Forms.DataVisualization.dll"

#I "../../packages/"

#r "Endorphin.Core/lib/net452/Endorphin.Core.dll"
#r "Endorphin.Abstract/lib/net452/Endorphin.Abstract.dll"
#r "FSharp.Control.Reactive/lib/net40/FSharp.Control.Reactive.dll"
#r "Rx-Linq/lib/net45/System.Reactive.Linq.dll"
#r "Rx-Interfaces/lib/net45/System.Reactive.Interfaces.dll"
#r "Rx-Core/lib/net45/System.Reactive.Core.dll"
#r "FSharp.Charting/lib/net40/FSharp.Charting.dll"
#r "bin/Debug/Endorphin.Instrument.PicoTech.PicoScope3000.dll"
#r "System.Windows.Forms.DataVisualization.dll"

open Endorphin.Instrument.PicoTech.PicoScope3000
open Model.Triggering.Complex

let triggerA = Require <| (Channel <| AnalogueTrigger ChannelA, true)
let triggerB = Require <| (Channel <| AnalogueTrigger ChannelB, true)
let digitalTrigger = Require <| (DigitalTrigger, false)
let andTrigger = (And (triggerA,triggerB))
let orTrigger = (Or (triggerA,triggerB))
let andOrTriggerR = (And (digitalTrigger,Or (triggerA,triggerB)))
let andOrTriggerL = (And (Or (triggerA,triggerB),digitalTrigger))
let orAndTriggerR = (Or (digitalTrigger,And (triggerA,triggerB)))
let orAndTriggerL = (Or (And (triggerA,triggerB),digitalTrigger))

let testFlatten txt =
    printfn "About to %s" txt
    PicoScope.Triggering.Complex.flattenConditionTreeToStrings >> List.map (printfn "%s: %A" txt)

triggerA |> testFlatten "triggerA"
printfn "Que?"
andTrigger |> testFlatten "andTrigger"
orTrigger |> testFlatten "orTrigger"
andOrTriggerL |> testFlatten "andOrTriggerL"
andOrTriggerR |> testFlatten "andOrTriggerR"
orAndTriggerL |> testFlatten "orAndTriggerL"
orAndTriggerR |> testFlatten "orAndTriggerR"
