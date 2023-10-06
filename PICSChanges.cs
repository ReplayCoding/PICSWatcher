namespace GameTracker;

using Dapper;

using SteamKit2;

using System.Data;
using System.Linq;

class PICSChanges
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private uint LastChangeNumber = 0;
    private uint TickerHash = 0;

    private class DepotManifestPair
    {
        public uint DepotID;
        public ulong ManifestID;

        public DepotManifestPair(uint depotID, ulong manifestID)
            => (DepotID, ManifestID) = (depotID, manifestID);
    }

    public PICSChanges()
    {
        LastChangeNumber = LocalConfig.Get<uint>("lastSeenChangeNumber");

        using (var db = Database.GetConnection())
        {
            if (LastChangeNumber == 0)
                LastChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `DepotVersions` ORDER BY `ChangeID` DESC LIMIT 1");
        }

        // Apparently we don't get forced full updates without this? SteamDB also does this.
        if (LastChangeNumber == 0)
            LastChangeNumber = 1;

        Logger.Info($"Previous changelist was {LastChangeNumber}");
    }

    public void StartTick()
    {
        TickerHash++;
        Task.Run(Tick);
    }

    public void StopTick()
    {
        TickerHash++;
    }

    private async Task Tick()
    {
        var currentHash = TickerHash;
        while (currentHash == TickerHash)
        {
            var changes = await SteamSession.Instance.apps.PICSGetChangesSince(LastChangeNumber, true, false);
            await OnPICSChanges(changes);

            await Task.Delay(Program.Config.PicsRefreshDelay);
        };
    }

    private async Task<uint?> GetPrevChangeNumber(uint currentChangeNumber, IDbConnection db, IDbTransaction transaction)
    {
        var results = await db.QueryAsync<uint>(@"SELECT `ChangeID` FROM `BuildInfo` WHERE ChangeID < @currentChange ORDER BY `ChangeID` DESC LIMIT 1", new { currentChange = currentChangeNumber }, transaction);

        if (results.Count() == 0)
        {
            return null;
        }

        return results.First();
    }

    private async Task AddChangesToDB(InfoFetcher.AppInfo appInfo, IDbConnection db, IDbTransaction transaction)
    {
        foreach (var depot in appInfo.Depots)
        {
            await db.ExecuteAsync(@"
                            INSERT INTO `DepotVersions`
                                (`ChangeID`, `AppID`, `DepotID`, `ManifestID`)
                                VALUES (@ChangeID, @AppID, @DepotID, @ManifestID)
                                ON DUPLICATE KEY UPDATE `ChangeID`=`ChangeID`;",
            new
            {
                ChangeID = appInfo.ChangeId,
                AppID = appInfo.AppId,
                DepotID = depot.DepotId,
                ManifestID = depot.ManifestId,
            }, transaction);
        }

        await db.ExecuteAsync(@"
                            INSERT INTO `BuildInfo`
                                (`ChangeID`, `Branch`, `BuildID`, `TimeUpdated`)
                                VALUES (@ChangeID, @Branch, @BuildID, @TimeUpdated)
                                ON DUPLICATE KEY UPDATE `ChangeID`=`ChangeID`;",
        new
        {
            ChangeID = appInfo.ChangeId,
            Branch = Program.Config.Branch,
            BuildID = appInfo.BuildID,
            TimeUpdated = appInfo.TimeUpdated
        }, transaction);
    }

    public async Task<bool> ChangeDiffersFromPrev(InfoFetcher.AppInfo appInfo, uint lastChangeNumber, IDbConnection db, IDbTransaction transaction)
    {
        Dictionary<uint, DepotManifestPair> prevDepots = (await db.QueryAsync<DepotManifestPair>(
                @"SELECT `DepotID`, `ManifestID` FROM `DepotVersions`
                    WHERE `ChangeID` = @ChangeID AND `AppID` = @AppID", new
                {
                    ChangeID = lastChangeNumber,
                    AppID = appInfo.AppId,
                }, transaction: transaction)).ToDictionary(depot => depot.DepotID);

        Dictionary<uint, DepotManifestPair> currentDepots =
            appInfo.Depots
            .Select(depot => new DepotManifestPair(depot.DepotId, depot.ManifestId))
            .ToDictionary(depot => depot.DepotID);

        // If the number of depots differs, a depot was added or removed.
        if (prevDepots.Count != currentDepots.Count)
            return true;

        foreach (uint depotId in currentDepots.Keys)
        {
            // prev doesn't contain a depot, so it must have been removed.
            if (!prevDepots.ContainsKey(depotId))
                return true;

            // prev contains depot, but the manifest differs.
            if (prevDepots[depotId].ManifestID != currentDepots[depotId].ManifestID)
                return true;
        }

        return false;
    }

    public async Task OnPICSChanges(SteamApps.PICSChangesCallback cb)
    {
        bool needsUpdate = false;
        uint changeNumber = 0;

        if (cb.CurrentChangeNumber == LastChangeNumber)
            return;

        if (cb.RequiresFullAppUpdate)
        {
            Logger.Info("Full app update required for change {0}", cb.CurrentChangeNumber);
            needsUpdate = true;
        }

        foreach (var appChange in cb.AppChanges.Values)
        {
            if (appChange.ID == Program.Config.AppToWatch)
            {
                needsUpdate = true;
                changeNumber = appChange.ChangeNumber;
                break;
            }
        }

        await using var db = await Database.GetConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();

        if (needsUpdate)
        {
            var appInfo = await InfoFetcher.FetchAppInfo(Program.Config.AppToWatch, changeNumber);
            if (appInfo != null)
            {
                uint? prevChangeNumber = await GetPrevChangeNumber(appInfo.ChangeId, db, transaction);
                if (prevChangeNumber != null)
                {
                    // casts :(
                    needsUpdate = await ChangeDiffersFromPrev(appInfo, (uint)prevChangeNumber, db, transaction);
                }

                if (needsUpdate)
                {
                    await AddChangesToDB(appInfo, db, transaction);
                }
                else
                {
                    Logger.Info("Ignoring empty change: {0}", appInfo.ChangeId);
                }
            }
            else
            {
                throw new Exception("Couldn't get appinfo!");
            }
        }


        LastChangeNumber = cb.CurrentChangeNumber;
        await LocalConfig.SetAsync("lastSeenChangeNumber", LastChangeNumber.ToString());
        await transaction.CommitAsync();

        if (needsUpdate)
        {
            Logger.Info("Update needed");
            _ = Task.Run(Downloader.RunUpdates);
        }
    }
}