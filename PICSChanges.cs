namespace GameTracker;

using Dapper;

using SteamKit2;

class PICSChanges
{
    private uint LastChangeNumber = 0;
    private uint TickerHash = 0;

    public PICSChanges(CallbackManager manager)
    {
        manager.Subscribe<SteamApps.PICSChangesCallback>(OnPICSChanges);

        LastChangeNumber = LocalConfig.Get<uint>("lastSeenChangeNumber");

        using (var db = Database.GetConnection())
        {
            if (LastChangeNumber == 0)
                LastChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `DepotVersions` ORDER BY `ChangeID` DESC LIMIT 1");
        }

        // Apparently we don't get forced full updates without this? SteamDB also does this.
        if (LastChangeNumber == 0)
            LastChangeNumber = 1;

        Console.WriteLine($"Previous changelist was {LastChangeNumber}");
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
            await SteamSession.Instance.apps.PICSGetChangesSince(LastChangeNumber, true, false);
            await Task.Delay(3000);
        };
    }

    public async void OnPICSChanges(SteamApps.PICSChangesCallback cb)
    {
        bool needsUpdate = false;
        uint? changeNumber = null;

        // Console.WriteLine($"PICS Update {cb.LastChangeNumber} -> {cb.CurrentChangeNumber}");
        if (cb.CurrentChangeNumber == LastChangeNumber)
            return;

        if (cb.RequiresFullAppUpdate)
        {
            Console.WriteLine("Full app update required for change {0}", cb.CurrentChangeNumber);
            needsUpdate = true;
        }

        foreach (var appChange in cb.AppChanges.Values)
        {
            // Console.WriteLine("\tApp {0}", appChange.ID);
            // Console.WriteLine("\t\tchangelist: {0}", appChange.ChangeNumber);
            // Console.WriteLine("\t\tneedstoken: {0}", appChange.NeedsToken);

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
            else
            {
                Console.WriteLine("Couldn't get appinfo!");
            }
        }

        LastChangeNumber = cb.CurrentChangeNumber;
        await LocalConfig.SetAsync("lastSeenChangeNumber", LastChangeNumber.ToString());
        await transaction.CommitAsync();

        if (needsUpdate)
        {
            Console.WriteLine("Update needed");
            _ = Task.Run(Downloader.RunUpdates);
        }
    }
}