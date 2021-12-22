namespace Fc.Domain.Location

[<NoComparison; NoEquality>]
type Wip<'R> =
    | Pending of decide : (Epoch.Fold.State -> 'R * Epoch.Events.Event list)
    | Complete of 'R

/// Manages Reads and Writes for a Series of Epochs, with a running total being carried forward to the next Epoch when it's marked Closed
type Service internal (zeroBalance, toBalanceCarriedForward, shouldClose, series : Series.Service, epochs : Epoch.Service) =

    let execute locationId originEpochId =
        let rec aux epochId balanceToCarryForward wip = async {
            let decide state = match wip with Complete r -> r, [] | Pending decide -> decide state
            match! epochs.Sync(locationId, epochId, balanceToCarryForward, decide, shouldClose) with
            | { result = Some res; isOpen = true } ->
                if originEpochId <> epochId then
                    do! series.AdvanceIngestionEpoch(locationId, epochId)
                return res
            | { history = history; result = Some res } ->
                let successorEpochId = LocationEpochId.next epochId
                let cf = toBalanceCarriedForward history
                return! aux successorEpochId (Some cf) (Complete res)
            | { history = history } ->
                let successorEpochId = LocationEpochId.next epochId
                let cf = toBalanceCarriedForward history
                return! aux successorEpochId (Some cf) wip }
        aux

    member __.Execute(locationId, decide) = async {
        let! activeEpoch = series.TryReadIngestionEpoch locationId
        let originEpochId, epochId, balanceCarriedForward =
            match activeEpoch with
            | None -> LocationEpochId.parse -1, LocationEpochId.parse 0, Some zeroBalance
            | Some activeEpochId -> activeEpochId, activeEpochId, None
        return! execute locationId originEpochId epochId balanceCarriedForward (Pending decide)}

module Config =
    let create (zeroBalance, toBalanceCarriedForward, shouldClose) store =
        let series = Series.Config.create store
        let epochs = Epoch.Config.create store

        Service(zeroBalance, toBalanceCarriedForward, shouldClose, series, epochs)
