﻿module TestbedTemplate.Services

open System

module Domain =
    module Favorites =

        let streamName (id : ClientId) = FsCodec.StreamName.create "Favorites" (ClientId.toString id)

        // NB - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
        module Events =

            type Favorited =                            { date: System.DateTimeOffset; skuId: SkuId }
            type Unfavorited =                          { skuId: SkuId }
            module Compaction =
                type Snapshotted =                        { net: Favorited[] }

            type Event =
                | Snapshotted                           of Compaction.Snapshotted
                | Favorited                             of Favorited
                | Unfavorited                           of Unfavorited
                interface TypeShape.UnionContract.IUnionContract
            let codec = Config.EventCodec.create<Event>()

        module Fold =

            type State = Events.Favorited []

            type private InternalState(input: State) =
                let dict = new System.Collections.Generic.Dictionary<SkuId, Events.Favorited>()
                let favorite (e : Events.Favorited) =   dict.[e.skuId] <- e
                let favoriteAll (xs: Events.Favorited seq) = for x in xs do favorite x
                do favoriteAll input
                member _.ReplaceAllWith xs =           dict.Clear(); favoriteAll xs
                member _.Favorite(e : Events.Favorited) =  favorite e
                member _.Unfavorite id =               dict.Remove id |> ignore
                member _.AsState() =                   Seq.toArray dict.Values

            let initial : State = [||]
            let private evolve (s: InternalState) = function
                | Events.Snapshotted { net = net } ->   s.ReplaceAllWith net
                | Events.Favorited e ->                 s.Favorite e
                | Events.Unfavorited { skuId = id } ->  s.Unfavorite id
            let fold (state: State) (events: seq<Events.Event>) : State =
                let s = InternalState state
                for e in events do evolve s e
                s.AsState()
            let isOrigin = function Events.Snapshotted _ -> true | _ -> false
            let toSnapshot state = Events.Snapshotted { net = state }

        let private doesntHave skuId (state : Fold.State) = state |> Array.exists (fun x -> x.skuId = skuId) |> not

        let favorite date skuIds (state : Fold.State) =
            [ for skuId in Seq.distinct skuIds do
                if state |> doesntHave skuId then
                    yield Events.Favorited { date = date; skuId = skuId } ]

        let unfavorite skuId (state : Fold.State) =
            if state |> doesntHave skuId then [] else
            [ Events.Unfavorited { skuId = skuId } ]

        type Service internal (resolve : ClientId -> Equinox.Decider<Events.Event, Fold.State>) =

            member x.Favorite(clientId, skus) =
                let decider = resolve clientId
                decider.Transact(favorite DateTimeOffset.Now skus)

            member x.Unfavorite(clientId, sku) =
                let decider = resolve clientId
                decider.Transact(unfavorite sku)

            member _.List(clientId) : Async<Events.Favorited []> =
                let decider = resolve clientId
                decider.Query id

        let create log resolve =
            let resolve clientId =
                let stream = resolve (streamName clientId)
                Equinox.Decider(log, stream, maxAttempts=3)
            Service(resolve)

        module Config =

            let snapshot = Fold.isOrigin, Fold.toSnapshot
            let private resolveStream = function
//#if memoryStore || (!cosmos && !eventStore)
                | Config.Store.Memory store ->
                    (Config.Memory.create Events.codec Fold.initial Fold.fold store).Resolve
//#endif
//#if cosmos
                | Config.Store.Cosmos (context, caching, unfolds) ->
                    let accessStrategy = if unfolds then Equinox.CosmosStore.AccessStrategy.Snapshot snapshot else Equinox.CosmosStore.AccessStrategy.Unoptimized
                    (Config.Cosmos.create Events.codec Fold.initial Fold.fold caching accessStrategy context).Resolve
//#endif
//#if eventStore
                | Config.Store.Esdb (context, caching, unfolds) ->
                    let accessStrategy = if unfolds then Equinox.EventStore.AccessStrategy.RollingSnapshots snapshot |> Some else None
                    (Config.Esdb.create Events.codec Fold.initial Fold.fold caching accessStrategy context).Resolve
//#endif
            let private resolveDecider store = streamName >> resolveStream store >> Config.createDecider
            let create = resolveDecider >> Service

open Microsoft.Extensions.DependencyInjection

let register (services : IServiceCollection, storageConfig) =
    services.AddSingleton(Domain.Favorites.Config.create storageConfig) |> ignore
