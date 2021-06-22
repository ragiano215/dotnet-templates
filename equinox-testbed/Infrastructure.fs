﻿[<AutoOpen>]
module TestbedTemplate.Infrastructure

open FSharp.UMX
open System

module EnvVar =

    let tryGet varName : string option = Environment.GetEnvironmentVariable varName |> Option.ofObj

type Exception with
    // https://github.com/fsharp/fslang-suggestions/issues/660
    member this.Reraise () =
        (System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture this).Throw ()
        Unchecked.defaultof<_>

module Guid =
    let inline toStringN (x : Guid) = x.ToString "N"

/// ClientId strongly typed id; represented internally as a Guid; not used for storage so rendering is not significant
type ClientId = Guid<clientId>
and [<Measure>] clientId
module ClientId = let toString (value : ClientId) : string = Guid.toStringN %value

/// SkuId strongly typed id; represented internally as a Guid
// NB Perf is suboptimal as a key, see Equinox's samples/Store for expanded version
type SkuId = Guid<skuId>
and [<Measure>] skuId
module SkuId = let toString (value : SkuId) : string = Guid.toStringN %value

[<AutoOpen>]
module ConnectorExtensions =

    open Serilog

    type Equinox.CosmosStore.CosmosStoreConnector with

        member private x.LogConfiguration(connectionName, databaseId, containerId) =
            let o = x.Options
            let timeout, retries429, timeout429 = o.RequestTimeout, o.MaxRetryAttemptsOnRateLimitedRequests, o.MaxRetryWaitTimeOnRateLimitedRequests
            Log.Information("CosmosDb {name} {mode} {endpointUri} timeout {timeout}s; Throttling retries {retries}, max wait {maxRetryWaitTime}s",
                            connectionName, o.ConnectionMode, x.Endpoint, timeout.TotalSeconds, retries429, let t = timeout429.Value in t.TotalSeconds)
            Log.Information("CosmosDb {name} Database {database} Container {container}",
                            connectionName, databaseId, containerId)

        ///// Use sparingly; in general one wants to use CreateAndInitialize to avoid slow first requests
        //member x.CreateUninitialized(databaseId, containerId) =
        //    x.CreateUninitialized().GetDatabase(databaseId).GetContainer(containerId)
        
        /// Connect a CosmosStoreClient, including warming up
        member x.ConnectStore(connectionName, databaseId, containerId) =
            x.LogConfiguration(connectionName, databaseId, containerId)
            Equinox.CosmosStore.CosmosStoreClient.Connect(x.CreateAndInitialize, databaseId, containerId)
            
        ///// Creates a CosmosClient suitable for running a CFP via CosmosStoreSource
        //member x.ConnectMonitored(databaseId, containerId, ?connectionName) =
        //    x.LogConfiguration(defaultArg connectionName "Source", databaseId, containerId)
        //    x.CreateUninitialized(databaseId, containerId)

        ///// Connects to a Store as both a ChangeFeedProcessor Monitored Container and a CosmosStoreClient
        //member x.ConnectStoreAndMonitored(databaseId, containerId) =
        //    let monitored = x.ConnectMonitored(databaseId, containerId, "Main")
        //    let storeClient = Equinox.CosmosStore.CosmosStoreClient(monitored.Database.Client, databaseId, containerId)
        //    storeClient, monitored

