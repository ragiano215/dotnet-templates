﻿module PrunerTemplate.Program

open Propulsion.CosmosStore
open Serilog
open System

exception MissingArg of message : string with override this.Message = this.message

type Configuration(tryGet) =

    let get key =
        match tryGet key with
        | Some value -> value
        | None -> raise (MissingArg (sprintf "Missing Argument/Environment Variable %s" key))

    member _.CosmosConnection =             get "EQUINOX_COSMOS_CONNECTION"
    member _.CosmosDatabase =               get "EQUINOX_COSMOS_DATABASE"
    member _.CosmosContainer =              get "EQUINOX_COSMOS_CONTAINER"

module Args =

    open Argu
    [<NoEquality; NoComparison>]
    type Parameters =
        | [<AltCommandLine "-V"; Unique>]   Verbose
        | [<AltCommandLine "-p"; Unique>]   PrometheusPort of int
        | [<AltCommandLine "-g"; Mandatory>] ProcessorName of string
        | [<AltCommandLine "-r"; Unique>]   MaxReadAhead of int
        | [<AltCommandLine "-w"; Unique>]   MaxWriters of int
        | [<CliPrefix(CliPrefix.None); AltCommandLine "cosmos"; Unique(*ExactlyOnce is not supported*); Last>] SrcCosmos of ParseResults<CosmosSourceParameters>
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | Verbose ->                "request Verbose Logging. Default: off"
                | PrometheusPort _ ->       "port from which to expose a Prometheus /metrics endpoint. Default: off"
                | ProcessorName _ ->        "Projector consumer group name."
                | MaxReadAhead _ ->         "maximum number of batches to let processing get ahead of completion. Default: 8."
                | MaxWriters _ ->           "maximum number of concurrent writes to target. Default: 4."
                | SrcCosmos _ ->            "Cosmos Archive parameters."
    and Arguments(c : Configuration, a : ParseResults<Parameters>) =
        member val Verbose =                a.Contains Parameters.Verbose
        member val PrometheusPort =         a.TryGetResult PrometheusPort
        member val ProcessorName =          a.GetResult ProcessorName
        member val MaxReadAhead =           a.GetResult(MaxReadAhead, 8)
        member val MaxWriters =             a.GetResult(MaxWriters, 4)
        member val StatsInterval =          TimeSpan.FromMinutes 1.
        member val StateInterval =          TimeSpan.FromMinutes 5.
        member val Source : CosmosSourceArguments =
            match a.TryGetSubCommand() with
            | Some (SrcCosmos cosmos) -> (CosmosSourceArguments (c, cosmos))
            | _ -> raise (MissingArg "Must specify cosmos for Source")
        member x.DeletionTarget = x.Source.Target
        member x.MonitoringParams() =
            let srcC = x.Source
            let leases : Microsoft.Azure.Cosmos.Container =
                let dstC : CosmosSinkArguments = srcC.Target
                match srcC.LeaseContainerId, dstC.LeaseContainerId with
                | None, None ->     srcC.ConnectLeases(srcC.ContainerId + "-aux")
                | Some sc, None ->  srcC.ConnectLeases(sc)
                | None, Some dc ->  dstC.ConnectLeases(dc)
                | Some _, Some _ -> raise (MissingArg "LeaseContainerSource and LeaseContainerDestination are mutually exclusive - can only store in one database")
            Log.Information("Pruning... {dop} writers, max {maxReadAhead} batches read ahead", x.MaxWriters, x.MaxReadAhead)
            Log.Information("ChangeFeed {processorName} Leases Database {db} Container {container}. MaxItems limited to {maxItems}",
                x.ProcessorName, leases.Database.Id, leases.Id, Option.toNullable srcC.MaxItems)
            if srcC.FromTail then Log.Warning("(If new projector group) Skipping projection of all existing events.")
            Log.Information("ChangeFeed Lag stats interval {lagS:n0}s", let f = srcC.LagFrequency in f.TotalSeconds)
            let monitored = srcC.MonitoredContainer()
            (monitored, leases, x.ProcessorName, srcC.FromTail, srcC.MaxItems, srcC.LagFrequency)
    and [<NoEquality; NoComparison>] CosmosSourceParameters =
        | [<AltCommandLine "-V"; Unique>]   Verbose
        | [<AltCommandLine "-Z"; Unique>]   FromTail
        | [<AltCommandLine "-mi"; Unique>]  MaxItems of int
        | [<AltCommandLine "-l"; Unique>]   LagFreqM of float
        | [<AltCommandLine "-a"; Unique>]   LeaseContainer of string

        | [<AltCommandLine "-m">]           ConnectionMode of Microsoft.Azure.Cosmos.ConnectionMode
        | [<AltCommandLine "-s">]           Connection of string
        | [<AltCommandLine "-d">]           Database of string
        | [<AltCommandLine "-c"; Unique>]   Container of string // Actually Mandatory, but stating that is not supported
        | [<AltCommandLine "-o">]           Timeout of float
        | [<AltCommandLine "-r">]           Retries of int
        | [<AltCommandLine "-rt">]          RetriesWaitTime of float

        | [<CliPrefix(CliPrefix.None); AltCommandLine "cosmos"; Unique(*ExactlyOnce is not supported*); Last>] DstCosmos of ParseResults<CosmosSinkParameters>
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | Verbose ->                "request Verbose Change Feed Processor Logging. Default: off"
                | FromTail ->               "(iff the Consumer Name is fresh) - force skip to present Position. Default: Never skip an event."
                | MaxItems _ ->             "maximum item count to request from feed. Default: unlimited"
                | LagFreqM _ ->             "frequency (in minutes) to dump lag stats. Default: 1"
                | LeaseContainer _ ->       "specify Container Name for Leases container. Default: `sourceContainer` + `-aux`."

                | ConnectionMode _ ->       "override the connection mode. Default: Direct."
                | Connection _ ->           "specify a connection string for a Cosmos account. (optional if environment variable EQUINOX_COSMOS_CONNECTION specified)"
                | Database _ ->             "specify a database name for Cosmos account. (optional if environment variable EQUINOX_COSMOS_DATABASE specified)"
                | Container _ ->            "specify a container name within `Database` to apply the pruning to"
                | Timeout _ ->              "specify operation timeout in seconds. Default: 5."
                | Retries _ ->              "specify operation retries. Default: 5."
                | RetriesWaitTime _ ->      "specify max wait-time for retry when being throttled by Cosmos in seconds. Default: 30."

                | DstCosmos _ ->            "CosmosDb Pruning Target parameters."
    and CosmosSourceArguments(c : Configuration, a : ParseResults<CosmosSourceParameters>) =
        let discovery =                     a.TryGetResult CosmosSourceParameters.Connection |> Option.defaultWith (fun () -> c.CosmosConnection) |> Equinox.CosmosStore.Discovery.ConnectionString
        let mode =                          a.TryGetResult CosmosSourceParameters.ConnectionMode
        let timeout =                       a.GetResult(CosmosSourceParameters.Timeout, 5.) |> TimeSpan.FromSeconds
        let retries =                       a.GetResult(CosmosSourceParameters.Retries, 5)
        let maxRetryWaitTime =              a.GetResult(CosmosSourceParameters.RetriesWaitTime, 30.) |> TimeSpan.FromSeconds
        let connector =                     Equinox.CosmosStore.CosmosStoreConnector(discovery, timeout, retries, maxRetryWaitTime, ?mode = mode)
        member val DatabaseId =             a.TryGetResult CosmosSourceParameters.Database |> Option.defaultWith (fun () -> c.CosmosDatabase)
        member val ContainerId =            a.GetResult CosmosSourceParameters.Container
        member x.MonitoredContainer() =     connector.ConnectMonitored(x.DatabaseId, x.ContainerId)

        member val Verbose =                a.Contains Verbose
        member val FromTail =               a.Contains CosmosSourceParameters.FromTail
        member val MaxItems =               a.TryGetResult MaxItems
        member val LagFrequency : TimeSpan = a.GetResult(LagFreqM, 1.) |> TimeSpan.FromMinutes
        member val LeaseContainerId =       a.TryGetResult CosmosSourceParameters.LeaseContainer
        member x.ConnectLeases containerId = connector.CreateUninitialized(x.DatabaseId, containerId)

        member val Target =
            match a.TryGetSubCommand() with
            | Some (DstCosmos cosmos) -> CosmosSinkArguments (c, cosmos)
            | _ -> raise (MissingArg "Must specify cosmos for Target")
    and [<NoEquality; NoComparison>] CosmosSinkParameters =
        | [<AltCommandLine "-m">]           ConnectionMode of Microsoft.Azure.Cosmos.ConnectionMode
        | [<AltCommandLine "-s">]           Connection of string
        | [<AltCommandLine "-d">]           Database of string
        | [<AltCommandLine "-c">]           Container of string
        | [<AltCommandLine "-a"; Unique>]   LeaseContainer of string
        | [<AltCommandLine "-o">]           Timeout of float
        | [<AltCommandLine "-r">]           Retries of int
        | [<AltCommandLine "-rt">]          RetriesWaitTime of float
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | ConnectionMode _ ->       "override the connection mode. Default: Direct."
                | Connection _ ->           "specify a connection string for a Cosmos account. (optional if environment variable EQUINOX_COSMOS_CONNECTION specified)"
                | Database _ ->             "specify a database name for Cosmos account. (optional if environment variable EQUINOX_COSMOS_DATABASE specified)"
                | Container _ ->            "specify a Container name for Cosmos account. (optional if environment variable EQUINOX_COSMOS_CONTAINER specified)"
                | LeaseContainer _ ->       "specify Container Name (in this [target] Database) for Leases container. Default: `SourceContainer` + `-aux`."
                | Timeout _ ->              "specify operation timeout in seconds. Default: 5."
                | Retries _ ->              "specify operation retries. Default: 0."
                | RetriesWaitTime _ ->      "specify max wait-time for retry when being throttled by Cosmos in seconds. Default: 5."
    and CosmosSinkArguments(c : Configuration, a : ParseResults<CosmosSinkParameters>) =
        let discovery =                     a.TryGetResult Connection |> Option.defaultWith (fun () -> c.CosmosConnection) |> Equinox.CosmosStore.Discovery.ConnectionString
        let mode =                          a.TryGetResult ConnectionMode
        let timeout =                       a.GetResult(CosmosSinkParameters.Timeout, 5.) |> TimeSpan.FromSeconds
        let retries =                       a.GetResult(CosmosSinkParameters.Retries, 0)
        let maxRetryWaitTime =              a.GetResult(RetriesWaitTime, 5.) |> TimeSpan.FromSeconds
        let connector =                     Equinox.CosmosStore.CosmosStoreConnector(discovery, timeout, retries, maxRetryWaitTime, ?mode = mode)

        member val DatabaseId =             a.TryGetResult Database   |> Option.defaultWith (fun () -> c.CosmosDatabase)
        member val ContainerId =            a.TryGetResult Container  |> Option.defaultWith (fun () -> c.CosmosContainer)
        member x.Connect() =                connector.ConnectStore("DELETION Target", x.DatabaseId, x.ContainerId)

        member val LeaseContainerId =       a.TryGetResult LeaseContainer
        member x.ConnectLeases containerId = connector.CreateUninitialized(x.DatabaseId, containerId)


    /// Parse the commandline; can throw exceptions in response to missing arguments and/or `-h`/`--help` args
    let parse tryGetConfigValue argv : Arguments =
        let programName = System.Reflection.Assembly.GetEntryAssembly().GetName().Name
        let parser = ArgumentParser.Create<Parameters>(programName=programName)
        Arguments(Configuration tryGetConfigValue, parser.ParseCommandLine argv)

let [<Literal>] AppName = "PrunerTemplate"

let build (args : Args.Arguments, log : ILogger) =
    let archive = args.Source
    // NOTE - DANGEROUS - events submitted to this sink get DELETED from the supplied Context!
    let deletingEventsSink =
        let target = args.DeletionTarget
        if (target.DatabaseId, target.ContainerId) = (archive.DatabaseId, archive.ContainerId) then
            raise (MissingArg "Danger! Can not prune a target based on itself")
        let context = target.Connect() |> Async.RunSynchronously |> CosmosStoreContext.create
        let eventsContext = Equinox.CosmosStore.Core.EventsContext(context, Config.log)
        CosmosStorePruner.Start(Log.Logger, args.MaxReadAhead, eventsContext, args.MaxWriters, args.StatsInterval, args.StateInterval)
    let source =
        let observer = CosmosStoreSource.CreateObserver(log.ForContext<CosmosStoreSource>(), deletingEventsSink.StartIngester, Seq.collect Handler.selectPrunable)
        let monitored, leases, processorName, startFromTail, maxItems, lagFrequency = args.MonitoringParams()
        CosmosStoreSource.Start(log, monitored, leases, processorName, observer, startFromTail, ?maxItems=maxItems, lagReportFreq=lagFrequency)
    deletingEventsSink, source

// A typical app will likely have health checks etc, implying the wireup would be via `endpoints.MapMetrics()` and thus not use this ugly code directly
let startMetricsServer port : IDisposable =
    let metricsServer = new Prometheus.KestrelMetricServer(port = port)
    let ms = metricsServer.Start()
    Log.Information("Prometheus /metrics endpoint on port {port}", port)
    { new IDisposable with member x.Dispose() = ms.Stop(); (metricsServer :> IDisposable).Dispose() }

open Propulsion.CosmosStore.Infrastructure // AwaitKeyboardInterruptAsTaskCancelledException

let run (args : Args.Arguments) = async {
    let log = (Log.forGroup args.ProcessorName).ForContext<Propulsion.Streams.Scheduling.StreamSchedulingEngine>()
    let sink, source = build (args, log)
    use _metricsServer : IDisposable = args.PrometheusPort |> Option.map startMetricsServer |> Option.toObj
    return! Async.Parallel [
        Async.AwaitKeyboardInterruptAsTaskCancelledException()
        source.AwaitWithStopOnCancellation()
        sink.AwaitWithStopOnCancellation()
    ] |> Async.Ignore<unit[]>
}

[<EntryPoint>]
let main argv =
    try let args = Args.parse EnvVar.tryGet argv
        try Log.Logger <- LoggerConfiguration().Configure(AppName, args.Verbose, args.Source.Verbose).CreateLogger()
            try run args |> Async.RunSynchronously; 0
            with e when not (e :? MissingArg) -> Log.Fatal(e, "Exiting"); 2
        finally Log.CloseAndFlush()
    with MissingArg msg -> eprintfn "%s" msg; 1
        | :? Argu.ArguParseException as e -> eprintfn "%s" e.Message; 1
        | e -> eprintf "Exception %s" e.Message; 1
