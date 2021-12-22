module Fc.Domain.Config

/// Tag log entries so we can filter them out if logging to the console
let log = Serilog.Log.ForContext("isMetric", true)
let createDecider stream = Equinox.Decider(log, stream, maxAttempts = 3)

module Cosmos =

    open Equinox.CosmosStore

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.)
        Equinox.CosmosStore.CosmosStoreCategory(context, codec, fold, initial, cacheStrategy, accessStrategy)

    let createUnoptimized codec initial fold (context, cache) =
        let accessStrategy = AccessStrategy.Unoptimized
        createCached codec initial fold accessStrategy (context, cache)

    let createSnapshotted codec initial fold (isOrigin, toSnapshot) (context, cache) =
        let accessStrategy = AccessStrategy.Snapshot (isOrigin, toSnapshot)
        createCached codec initial fold accessStrategy (context, cache)

    let createLatestKnown codec initial fold (context, cache) =
        let accessStrategy = AccessStrategy.LatestKnownEvent
        createCached codec initial fold accessStrategy (context, cache)

module Esdb =

    open Equinox.EventStore

    let createSnapshotted codec initial fold (isOrigin, toSnapshot) (context, cache) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.)
        let accessStrategy = AccessStrategy.RollingSnapshots (isOrigin, toSnapshot)
        Equinox.EventStore.EventStoreCategory(context, codec, fold, initial, cacheStrategy, accessStrategy)

    let createLatestKnown codec initial fold (context, cache) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.)
        let accessStrategy = AccessStrategy.LatestKnownEvent
        EventStoreCategory(context, codec, fold, initial, cacheStrategy, accessStrategy)

    let create codec initial fold (context, cache) =
        let cacheStrategy = CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.)
        EventStoreCategory(context, codec, fold, initial, cacheStrategy)

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type Store =
    | Cosmos of Equinox.CosmosStore.CosmosStoreContext * Equinox.Core.ICache
    | Esdb of Equinox.EventStore.EventStoreContext * Equinox.Core.ICache