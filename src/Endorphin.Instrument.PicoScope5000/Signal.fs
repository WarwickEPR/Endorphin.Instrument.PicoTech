// Copyright (c) University of Warwick. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE.txt in the project root for license information.

namespace Endorphin.Instrument.PicoTech.PicoScope5000

open System
open Microsoft.FSharp.Data.UnitSystems.SI.UnitSymbols

open Endorphin.Core
open FSharp.Control.Reactive
open System.Reactive.Concurrency
open Endorphin.Abstract.Time

/// Functions for obtaining the signal projections for an acquisition.
module Signal =
    /// Completes the given observable when the acquisition completes.
    let private takeUntilFinished acquisition =
        acquisition |> (Acquisition.status >> Observable.last >> Observable.takeUntilOther)

    /// Computes the timestamp of a sample for the given index and sample interval.
    let private time (interval : Interval) (downsampling : DownsamplingRatio option) index =
        let ratio =
            match downsampling with
            | None -> 1.0
            | Some ratio -> float ratio
        interval.asFloat_s * (float index) * ratio

    /// Returns a function which converts an ADC count to a voltage for the given input sampling in an
    /// acquisition.
    let private adcCountToVoltage (inputChannel, _) (acquisition:AcquisitionParameters) =
        let channelSettings = Inputs.settingsForChannel inputChannel acquisition.Inputs
        match channelSettings with
        | EnabledChannel settings ->
            let voltageRange   = Range.voltage settings.Range
            let analogueOffset = settings.AnalogueOffset
            (fun (adcCounts : int16) ->
                    voltageRange * (float32 adcCounts) / (float32 <| Resolution.maximumAdcCounts acquisition.Resolution)
                    - analogueOffset)
        | DisabledChannel -> failwithf "Cannot calculate voltage for channel %A as it is not enabled." inputChannel

    /// Returns a function which converts an array of samples from ADC counts to voltages according to
    /// the given array of input channel acquisition settings.
    let private adcCountsToVoltages (inputs : (InputChannel * BufferDownsampling) array) acquisition =
        Array.mapi (fun i adcCount -> adcCountToVoltage inputs.[i] acquisition adcCount)

    let private rawToByte raw =
        uint16 raw &&& uint16 0xff |> uint8

    let private rawToBit bit raw =
        let selector = uint16 1 <<< bit
        uint16 raw &&& uint16 0xff &&& selector > 0us

    /// Returns a sequence of sample arrays in the given sample block for the specified array of inputs.
    let private takeInputs inputs samples = seq {
        let length = samples.Length
        for i in 0 .. length - 1 ->
            samples.Samples
            |> Map.findArray inputs
            |> Array.map (fun block -> block.[i]) }

    let private samplesObserved acquisition =
        (common acquisition).SamplesObserved.Publish

    let private acquisitionParameters acquisition =
        (common acquisition).Parameters

    let private sampleInterval a =
        (acquisitionParameters a).SampleInterval

    let private sampleIntervalEvent acquisition =
        Acquisition.status acquisition
        |> Observable.choose (function | Acquiring interval -> Some interval | _ -> None)
        |> Observable.first
        |> Observable.repeat

    let private previousSamplesCount acquisition =
        samplesObserved acquisition
        |> Observable.scanInit 0 (fun c x -> c + x.Length)
        |> Observable.startWith [0]
        |> Observable.skipLast 1

    let private adcCountIndexedEvent input acquisition =
        let indexBlock (offset,block) =
            Seq.ofArray block |> Seq.mapi (fun i x -> (offset+i,x))
        samplesObserved acquisition
        |> Event.map (fun samples -> samples.Samples |> Map.find input)
        |> Observable.zip (previousSamplesCount acquisition)
        |> Observable.flatmapSeq indexBlock

    let private adcCountTimedEvent input acquisition =
        let downsamplingRatio = (acquisitionParameters acquisition).DownsamplingRatio
        let timestampBlock (offset,interval,block) =
            Seq.ofArray block
            |> Seq.mapi (fun i x -> (offset+i,x))
            |> Seq.map (fun (i,x) -> (time interval downsamplingRatio i, x))
        samplesObserved acquisition
        |> Event.map (fun samples -> samples.Samples |> Map.find input)
        |> Observable.zip3 (previousSamplesCount acquisition) (sampleIntervalEvent acquisition)
        |> Observable.flatmapSeq timestampBlock

    /// Count the number of samples observed so far in the acquisition.
    let blockSampleCount input acquisition =
        samplesObserved acquisition
        |> Event.map (fun block -> block.Length)

    /// Map an array index from an acquisition into the time that acquisition was taken at.
    let private timeMap acquisition = time (sampleInterval acquisition) (acquisitionParameters acquisition).DownsamplingRatio

    /// Tools for dealing with data points as singletons, more associated with the streaming model of data acquisition.
    module Single =
        /// Returns the n-th signal from an observable which emits an array of signals at each observation.
        let nth n = Observable.map (fun samples -> Array.get samples n)

        /// Takes the n-th and m-th signals from an observable which emits an array of signals at each
        /// observation and combines them into a tuple.
        let takeXY n m = Observable.map (fun samples -> (Array.get samples n, Array.get samples m))

        /// Takes the n-th and m-th signals from an observable which emits an array of indexed or timestamped
        /// signals at each observation and combines the values into a tuple, discarding the indecies.
        let takeXYFromIndexed n m = Observable.map (fun (_, samples) -> (Array.get samples n, Array.get samples m))

        /// Helper function for constructing observables based on the ADC count samples for a given input.
        let private adcCountEvent input acquisition =
            samplesObserved acquisition
            |> Event.map (fun samples -> samples.Samples |> Map.find input)
            |> Observable.flatmapSeq Seq.ofArray

        /// Helper function for constructing observables based on the ADC count samples for a given array of
        /// inputs.
        let private adcCountsEvent inputs acquisition =
            samplesObserved acquisition
            |> Observable.flatmapSeq (takeInputs inputs)

        /// Returns an observable which emits an array of 2-tuples for each block of samples observed on the
        /// specified input in an acquisition, where the first member is created by applying `map` to the array
        /// index, and the second is the type of the IObservable created by `primitive`.
        let private observableBy createObservable map input acquisition =
            createObservable input acquisition
            |> Observable.mapi (fun i count -> (map i, count))

        /// Generic to create an observable from an event fetched from the acquisition and a mapping to convert
        /// that event into a different observable.
        let private createObservable event map input acquisition =
            event input acquisition
            |> Observable.map map
            |> takeUntilFinished acquisition

        /// Returns an observable which emits the ADC count for every sample observed on the specified input
        /// in an acquisition.
        let adcCount = createObservable adcCountEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and ADC count for every sample observed on the specified input in an acquisition.
        let adcCountBy (map : int -> 'a) = observableBy adcCount map
        /// Returns an observable which emits a tuple of sample index and ADC count for every sample observed
        /// on the specified input in an acquisition.
        let adcCountByIndex = adcCountBy id
        /// Returns an observable which emits a tuple of timestamp and ADC count for every sample observed on
        /// the specified input in an acquisition.
        let adcCountByTime input acquisition = adcCountBy (timeMap acquisition) input acquisition

        /// Returns an observable which emits the voltage for every sample observed on the specified input in
        /// an acquisition.
        let voltage input acquisition = createObservable adcCountEvent (adcCountToVoltage input (acquisitionParameters acquisition)) input acquisition
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and voltage for every sample observed on the specified input in an acquisition.
        let voltageBy (map : int -> 'a) = observableBy voltage map
        /// Returns an observable which emits a tuple of sample index and voltage for every sample observed on
        /// the specified input in an acquisition.
        let voltageByIndex = voltageBy id
        /// Returns an observable which emits a tuple of timestamp and voltage for every sample observed on the
        /// specified input in an acquisition.
        let voltageByTime input acquisition = voltageBy (timeMap acquisition) input acquisition

        /// Returns an observable which emits an array of ADC counts for every sample observed on the specified
        /// array of inputs in an acquisition.
        let adcCounts = createObservable adcCountsEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of ADC counts for every sample observed on the specified array of inputs
        /// in an acquisition.
        let adcCountsBy (map : int -> 'a) = observableBy adcCounts map
        /// Returns an observable which emits a tuple of sample index and array of ADC counts for every sample
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByIndex = adcCountsBy id
        /// Returns an observable which emits a tuple of timestamp and array of ADC counts for every sample
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByTime inputs acquisition = adcCountsBy (timeMap acquisition) inputs acquisition

        /// Returns an observable which emits an array of voltages for every sample observed on the specified
        /// array of inputs in an acquisition.
        let voltages inputs acquisition = createObservable adcCountsEvent (adcCountsToVoltages inputs (acquisitionParameters acquisition)) inputs acquisition
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of voltages for every sample observed on the specified array of inputs in an
        /// acquisition.
        let voltagesBy (map : int -> 'a) = observableBy voltages map
        /// Returns an observable which emits a tuple of sample index and array of voltages for every sample
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByIndex = voltagesBy id
        /// Returns an observable which emits a tuple of timestamp and array of voltages for every sample
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByTime inputs acquisition = voltagesBy (timeMap acquisition) inputs acquisition

        /// Accumulates a signal, emitting the sequence of samples observed so far at each observation.
        let accumulate signal = Observable.scanInit List.empty List.cons signal |> Observable.map Seq.ofList

        /// Returns an observable which emits a tuple of ADC counts for each pair of samples observed on the
        /// specified inputs in an acquisition.
        let adcCountXY xInput yInput acquisition =
            adcCounts [| xInput ; yInput |] acquisition
            |> takeXY 0 1

        /// Returns an observable which emits a tuple of voltages for each pair of samples observed on the
        /// specified inputs in an acquisition.
        let voltageXY xInput yInput acquisition =
            voltages [| xInput ; yInput |] acquisition
            |> takeXY 0 1

    /// Tools for dealing with signal data as complete acquisition blocks, similar to block acquisition modes.  For
    /// streaming data buffered into blocks of user-defined size, use Signal.Buffer instead.
    module Block =
        /// Helper function for constructing observables based on sample blocks for a given input in an
        /// acquisition.
        let private adcCountEvent input acquisition =
            samplesObserved acquisition
            |> Event.map (fun samples -> samples.Samples |> Map.find input)

        /// Helper function for constructing observables based on the sample blocks for a given array of inputs
        /// in an acquisition.
        let private adcCountsEvent inputs acquisition =
            samplesObserved acquisition
            |> Event.map (fun samples -> samples.Samples |> Map.findArray inputs )

        /// Returns an observable which emits an array of 2-tuples for each block of samples observed on the
        /// specified input in an acquisition, where the first member is created by applying `map` to the array
        /// index, and the second is the type of the IObservable created by `primitive`.
        let private observableBy createObservable map input acquisition =
            createObservable input acquisition
            |> Observable.map (Array.mapi (fun i count -> (map i, count)))

        /// Generic to create an observable from an event fetched from the acquisition and a mapping to convert
//        /// that event into a different observable.
        let private createObservable event map input acquisition =
            event input acquisition
            |> Observable.map (Array.map map)
            |> takeUntilFinished acquisition

        /// Returns an observable which emits the ADC count for every block observed on the specified input
        /// in an acquisition.
        let adcCount = createObservable adcCountEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and ADC count for every block observed on the specified input in an acquisition.
        let adcCountBy (map : int -> 'a) = observableBy adcCount map
        /// Returns an observable which emits a tuple of sample index and ADC count for every block observed
        /// on the specified input in an acquisition.
        let adcCountByIndex = adcCountBy id
        /// Returns an observable which emits a tuple of timestamp and ADC count for every block observed on
        /// the specified input in an acquisition.
        let adcCountByTime input acquisition = adcCountBy (timeMap acquisition) input acquisition

        /// Returns an observable which emits the voltage for every block observed on the specified input in
        /// an acquisition.
        let voltage input acquisition = createObservable adcCountEvent (adcCountToVoltage input (acquisitionParameters acquisition)) input acquisition
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and voltage for every block observed on the specified input in an acquisition.
        let voltageBy (map : int -> 'a) = observableBy voltage map
        /// Returns an observable which emits a tuple of sample index and voltage for every block observed on
        /// the specified input in an acquisition.
        let voltageByIndex = voltageBy id
        /// Returns an observable which emits a tuple of timestamp and voltage for every block observed on the
        /// specified input in an acquisition.
        let voltageByTime input acquisition = voltageBy (timeMap acquisition) input acquisition

        /// Returns an observable which emits an array of ADC counts for every block observed on the specified
        /// array of inputs in an acquisition.
        let adcCounts = createObservable adcCountsEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of ADC counts for every block observed on the specified array of inputs
        /// in an acquisition.
        let adcCountsBy (map : int -> 'a) = observableBy adcCounts map
        /// Returns an observable which emits a tuple of sample index and array of ADC counts for every block
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByIndex = adcCountsBy id
        /// Returns an observable which emits a tuple of timestamp and array of ADC counts for every block
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByTime inputs acquisition = adcCountsBy (timeMap acquisition) inputs acquisition


        /// Returns an observable which emits an array of voltages for every block observed on the specified
        /// array of inputs in an acquisition.
        let voltages (inputs:(InputChannel*BufferDownsampling)[]) acquisition =

            let adcToVoltage input data =
                let toVoltage = adcCountToVoltage input (acquisitionParameters acquisition)
                Array.map toVoltage data

            adcCountsEvent inputs acquisition
            |> Observable.map ( Array.zip inputs >> Array.map (fun (input,adcCounts) ->  adcToVoltage input adcCounts ) )
            |> takeUntilFinished acquisition

        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of voltages for every block observed on the specified array of inputs in an
        /// acquisition.
        let voltagesBy (map : int -> 'a) = observableBy voltages map
        /// Returns an observable which emits a tuple of sample index and array of voltages for every block
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByIndex = voltagesBy id
        /// Returns an observable which emits a tuple of timestamp and array of voltages for every block
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByTime inputs acquisition = voltagesBy (timeMap acquisition) inputs acquisition

    /// Tools for working with signals buffered into blocks of user-defined size, rather than an "acquisition block" obtained
    /// from using the PicoScope's block mode (for this, use Signal.Block instead).
    module Buffer =
        /// Helper function for constructing observables which buffer a specified number of the latest samples
        /// on a given input in acquisition.
        let private adcCountEvent count input acquisition =
            samplesObserved acquisition
            |> Observable.flatmapSeq (fun samples -> samples.Samples |> Map.find input |> Array.toSeq)
            |> Observable.ringBuffer count

        /// Helper function for constructing observables which buffer a specified number of the latest samples
        /// on a given array of inputs in an acquisition.
        let private adcCountsEvent count inputs acquisition =
            samplesObserved acquisition
            |> Observable.flatmapSeq (takeInputs inputs)
            |> Observable.ringBuffer count

        /// Returns an observable which emits an array of 2-tuples for each block of samples observed on the
        /// specified input in an acquisition, where the first member is created by applying `map` to the array
        /// index, and the second is the type of the IObservable created by `primitive`.
        let private observableBy createObservable map count input acquisition =
            createObservable count input acquisition
            |> Observable.map (Seq.mapi (fun i count -> (map i, count)))

        /// Generic to create an observable from an event fetched from the acquisition and a mapping to convert
        /// that event into a different observable.
        let private createObservable event map count input acquisition =
            event count input acquisition
            |> Observable.map (Seq.map map)
            |> takeUntilFinished acquisition

        /// Returns an observable which emits the ADC count for every block observed on the specified input
        /// in an acquisition.
        let adcCount = createObservable adcCountEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and ADC count for every block observed on the specified input in an acquisition.
        let adcCountBy (map : int -> 'a) = observableBy adcCount map
        /// Returns an observable which emits a tuple of sample index and ADC count for every block observed
        /// on the specified input in an acquisition.
        let adcCountByIndex = adcCountBy id
        /// Returns an observable which emits a tuple of timestamp and ADC count for every block observed on
        /// the specified input in an acquisition.
        let adcCountByTime count input acquisition = adcCountBy (timeMap acquisition) count input acquisition

        /// Returns an observable which emits the voltage for every block observed on the specified input in
        /// an acquisition.
        let voltage count input acquisition = createObservable adcCountEvent (adcCountToVoltage input (acquisitionParameters acquisition)) count input acquisition
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and voltage for every block observed on the specified input in an acquisition.
        let voltageBy (map : int -> 'a) = observableBy voltage map
        /// Returns an observable which emits a tuple of sample index and voltage for every block observed on
        /// the specified input in an acquisition.
        let voltageByIndex = voltageBy id
        /// Returns an observable which emits a tuple of timestamp and voltage for every block observed on the
        /// specified input in an acquisition.
        let voltageByTime count input acquisition = voltageBy (timeMap acquisition) count input acquisition

        /// Returns an observable which emits an array of ADC counts for every block observed on the specified
        /// array of inputs in an acquisition.
        let adcCounts = createObservable adcCountsEvent id
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of ADC counts for every block observed on the specified array of inputs
        /// in an acquisition.
        let adcCountsBy (map : int -> 'a) = observableBy adcCounts map
        /// Returns an observable which emits a tuple of sample index and array of ADC counts for every block
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByIndex = adcCountsBy id
        /// Returns an observable which emits a tuple of timestamp and array of ADC counts for every block
        /// observed on the specified array of inputs in an acquisition.
        let adcCountsByTime count inputs acquisition = adcCountsBy (timeMap acquisition) count inputs acquisition

        /// Returns an observable which emits an array of voltages for every block observed on the specified
        /// array of inputs in an acquisition.
        let voltages count inputs acquisition = createObservable adcCountsEvent (adcCountsToVoltages inputs (acquisitionParameters acquisition)) count inputs acquisition
        /// Returns an observable which emits a tuple of sample index mapped with the given index mapping
        /// function and array of voltages for every block observed on the specified array of inputs in an
        /// acquisition.
        let voltagesBy (map : int -> 'a) = observableBy voltages map
        /// Returns an observable which emits a tuple of sample index and array of voltages for every block
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByIndex = voltagesBy id
        /// Returns an observable which emits a tuple of timestamp and array of voltages for every block
        /// observed on the specified array of inputs in an acquisition.
        let voltagesByTime count inputs acquisition = voltagesBy (timeMap acquisition) count inputs acquisition

    /// Returns an observable which emits a set of channels on which voltage overflow occurred if voltage
    /// overflow occurs on any input channel in an acquisition.
    let voltageOverflow acquisition =
        samplesObserved acquisition
        |> Event.filter (fun block -> not <| Set.isEmpty block.VoltageOverflows)
        |> Event.map (fun block -> block.VoltageOverflows)
        |> takeUntilFinished acquisition

    type EdgeDirection = RisingEdge | FallingEdge

    let digitalEdge direction (signal : IObservable<float<s>*bool>) =
        signal |> Observable.pairwise
        |> Observable.filter (fun ((_,a),(_,b)) ->
                                match direction with
                                | RisingEdge -> (not a) && b
                                | FallingEdge -> a && not b )
        |> Observable.map (fun ((at,_),(bt,_)) -> at)

    let debounce (d:float<s>) =
        Observable.scan (fun s t -> if t-s < d then s else t)
        >> Observable.distinctUntilChanged
