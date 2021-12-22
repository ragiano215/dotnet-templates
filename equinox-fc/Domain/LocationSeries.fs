/// Manages the active epoch for a given Location
module Fc.Domain.Location.Series

let [<Literal>] Category = "LocationSeries"
let streamName locationId = FsCodec.StreamName.create Category (LocationId.toString locationId)

// NOTE - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
[<RequireQualifiedAccess>]
module Events =

    type Started = { epoch : LocationEpochId }
    type Event =
        | Started of Started
        interface TypeShape.UnionContract.IUnionContract
    let codec = FsCodec.NewtonsoftJson.Codec.Create<Event>()

module Fold =

    type State = LocationEpochId option
    let initial : State = None
    let private evolve _state = function
        | Events.Started e -> Some e.epoch
    let fold = Seq.fold evolve

let interpretAdvanceIngestionEpoch (epochId : LocationEpochId) (state : Fold.State) =
    if epochId < LocationEpochId.parse 0 then [] else

    [if state |> Option.forall (fun s -> s < epochId) then yield Events.Started { epoch = epochId }]

type Service internal (resolve : LocationId -> Equinox.Decider<Events.Event, Fold.State>) =

    member __.TryReadIngestionEpoch(locationId) : Async<LocationEpochId option> =
        let decider = resolve locationId
        decider.Query id

    member __.AdvanceIngestionEpoch(locationId, epochId) : Async<unit> =
        let decider = resolve locationId
        decider.Transact(interpretAdvanceIngestionEpoch epochId)

open Fc.Domain

module Config =

    let private resolveStream = function
        | Config.Store.Cosmos (context, cache) ->
            let cat = Config.Cosmos.createLatestKnown Events.codec Fold.initial Fold.fold (context, cache)
            fun sn -> cat.Resolve(sn, option = Equinox.AllowStale)
        | Config.Store.Esdb (context, cache) ->
            let cat = Config.Esdb.createLatestKnown Events.codec Fold.initial Fold.fold (context, cache)
            fun sn -> cat.Resolve(sn, option = Equinox.AllowStale)

    let private resolveDecider store = streamName >> resolveStream store >> Config.createDecider
    let create = resolveDecider >> Service
