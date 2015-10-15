#r "../Endorphin.Core/bin/Debug/Endorphin.Core.dll"
#r "../packages/Rx-Core.2.2.5/lib/net45/System.Reactive.Core.dll"
#r "../packages/Rx-Interfaces.2.2.5/lib/net45/System.Reactive.Interfaces.dll"
#r "../packages/Rx-Linq.2.2.5/lib/net45/System.Reactive.Linq.dll"
#r "../packages/FSharp.Control.Reactive.3.2.0/lib/net40/FSharp.Control.Reactive.dll"
#r "../packages/FSharp.Charting.0.90.12/lib/net40/FSharp.Charting.dll"
#r "bin/Debug/Endorphin.Instrument.PicoScope5000.dll"
#r "System.Windows.Forms.DataVisualization.dll"

open Microsoft.FSharp.Data.UnitSystems.SI.UnitSymbols
open System
open System.Threading
open System.Windows.Forms

open ExtCore.Control
open FSharp.Charting
open FSharp.Control.Reactive

open Endorphin.Core
open Endorphin.Instrument.PicoScope5000

let form = new Form(Visible = true, TopMost = true, Width = 800, Height = 600)
let uiContext = SynchronizationContext.Current
let cts = new CancellationTokenSource()

form.Closed |> Observable.add (fun _ -> cts.Cancel())

let streamingParameters = 
    // define the streaming parameters: 14 bit resolution, 20 ms sample interval, 64 kSample bufffer
    Streaming.Parameters.create Resolution_14bit (Interval.fromMicroseconds 100<us>) (64u * 1024u)
    |> Streaming.Parameters.enableChannel ChannelA DC Range_500mV Voltage.zero FullBandwidth
    |> Streaming.Parameters.sampleChannel ChannelA Averaged
    |> Streaming.Parameters.withDownsamplingRatio 200u

let showTimeChart acquisition = async {
    do! Async.SwitchToContext uiContext // add the chart to the form using the UI thread context
    
    let initialTime = DateTime.Now    

    let picoBlock = 
        Streaming.Signal.voltageByBlock (ChannelA, AveragedBuffer) acquisition
        |> Observable.map (fun samples -> Array.average samples)

    let chart = 
        picoBlock
        |> Observable.map (fun sample -> Seq.singleton ("Channel A", sample))
        |> Observable.observeOnContext uiContext
        |> LiveChart.Bar
        |> Chart.WithYAxis (Min = -0.005, Max = 0.005, Title = "Voltage")

    let timeChart =
        picoBlock
        |> Observable.bufferCountOverlapped 264
        |> Observable.map (Array.mapi (fun i x -> (i, x) ))
        |> Observable.observeOnContext uiContext
        |> LiveChart.FastLine

    //new ChartTypes.ChartControl(chart, Dock = DockStyle.Fill)
    //|> form.Controls.Add

    new ChartTypes.ChartControl(timeChart, Dock = DockStyle.Fill)
    |> form.Controls.Add
    
    // return to the thread pool context
    do! Async.SwitchToThreadPool() }

let printStatusUpdates acquisition =
    Streaming.Acquisition.status acquisition
    |> Observable.add (printfn "%A") // print stream status updates (preparing, streaming, finished...) 

let experiment picoScope = async {
    // create an acquisition with the previously defined parameters and start it after subscribing to its events
    let acquisition = Streaming.Acquisition.create picoScope streamingParameters
    do! showTimeChart acquisition // use showTimeChart to show X and Y vs T or showXYChart to to plot Y vs X 
    printStatusUpdates acquisition

    let acquisitionHandle = Streaming.Acquisition.startWithCancellationToken acquisition cts.Token
    
    // wait for the acquisition to finish automatically or by cancellation   
    let! result = Streaming.Acquisition.waitToFinish acquisitionHandle
    match result with
    | Streaming.StreamCompleted -> printfn "Stream completed successfully."
    | Streaming.StreamError exn -> printfn "Stream failed: %s" exn.Message
    | Streaming.StreamCancelled -> printfn "Stream cancelled successuflly." }

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