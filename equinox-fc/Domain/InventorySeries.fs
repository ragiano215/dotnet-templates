/// Manages a) the ingestion epoch id b) the current checkpointed read position for a long-running Inventory Series
/// See InventoryEpoch for the logic managing the actual events logged within a given epoch
/// See Inventory.Service for the surface API which manages the writing
module Fc.Domain.Inventory.Series

let [<Literal>] Category = "InventorySeries"
let streamName inventoryId = FsCodec.StreamName.create Category (InventoryId.toString inventoryId)

// NB - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
[<RequireQualifiedAccess>]
module Events =

    type Started = { epoch : InventoryEpochId }
    type Event =
        | Started of Started
        interface TypeShape.UnionContract.IUnionContract
    let codec = FsCodec.NewtonsoftJson.Codec.Create<Event>()

module Fold =

    type State = InventoryEpochId option
    let initial = None
    let evolve _state = function
        | Events.Started e -> Some e.epoch
    let fold : State -> Events.Event seq -> State = Seq.fold evolve

let queryActiveEpoch state = state |> Option.defaultValue (InventoryEpochId.parse 0)

let interpretAdvanceIngestionEpoch epochId (state : Fold.State) =
    if queryActiveEpoch state >= epochId then []
    else [Events.Started { epoch = epochId }]

type Service internal (resolve : InventoryId -> Equinox.Decider<Events.Event, Fold.State>) =

    member __.ReadIngestionEpoch(inventoryId) : Async<InventoryEpochId> =
        let decider = resolve inventoryId
        decider.Query queryActiveEpoch

    member __.AdvanceIngestionEpoch(inventoryId, epochId) : Async<unit> =
        let decider = resolve inventoryId
        decider.Transact(interpretAdvanceIngestionEpoch epochId)

open Fc.Domain

module Config =

    // For this stream, we uniformly use stale reads as:
    // a) we don't require any information from competing writers
    // b) while there are competing writers [which might cause us to have to retry a Transact], this should be infrequent
    let private resolveStream = function
        | Config.Store.Cosmos (context, cache) ->
            let cat = Config.Cosmos.createLatestKnown Events.codec Fold.initial Fold.fold (context, cache)
            fun sn -> cat.Resolve(sn, option = Equinox.AllowStale)
        | Config.Store.Esdb (context, cache) ->
            let cat = Config.Esdb.createLatestKnown Events.codec Fold.initial Fold.fold (context, cache)
            fun sn -> cat.Resolve(sn, option = Equinox.AllowStale)

    let private resolveDecider store = streamName >> resolveStream store >> Config.createDecider
    let create = resolveDecider >> Service
