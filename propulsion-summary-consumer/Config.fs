module ConsumerTemplate.Config

let log = Serilog.Log.ForContext("isMetric", true)
let createDecider stream = Equinox.Decider(log, stream, maxAttempts = 3)

module EventCodec =

    open FsCodec.SystemTextJson

    let private defaultOptions = Options.Create()
    let create<'t when 't :> TypeShape.UnionContract.IUnionContract> () =
        Codec.Create<'t>(options = defaultOptions).ToByteArrayCodec()
    let private withUpconverter<'c, 'e when 'c :> TypeShape.UnionContract.IUnionContract> up : FsCodec.IEventCodec<'e, _, _> =
        let down (_ : 'e) = failwith "Unexpected"
        Codec.Create<'e, 'c, _>(up, down, options = defaultOptions).ToByteArrayCodec()
    let withIndex<'c when 'c :> TypeShape.UnionContract.IUnionContract> : FsCodec.IEventCodec<int64 * 'c, _, _> =
        let up (raw : FsCodec.ITimelineEvent<_>, e) = raw.Index, e
        withUpconverter<'c, int64 * 'c> up

module Cosmos =

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = Equinox.CosmosStore.CachingStrategy.SlidingWindow (cache, System.TimeSpan.FromMinutes 20.)
        Equinox.CosmosStore.CosmosStoreCategory(context, codec, fold, initial, cacheStrategy, accessStrategy)

    let createRollingState codec initial fold toSnapshot (context, cache) =
        let accessStrategy = Equinox.CosmosStore.AccessStrategy.RollingState toSnapshot
        createCached codec initial fold accessStrategy (context, cache)

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type Store =
    | Cosmos of Equinox.CosmosStore.CosmosStoreContext * Equinox.Core.ICache
