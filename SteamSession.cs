namespace GameTracker;

using System;
using System.Threading.Tasks;
using System.Timers;

using Dapper;

using SteamKit2;
using SteamKit2.CDN;

class SteamSession
{
    public static SteamSession Instance { get; } = new SteamSession();

    public readonly SteamClient client;
    public readonly SteamUser user;
    public readonly SteamApps apps;
    public readonly SteamContent content;

    public readonly Client cdnClient;
    public IReadOnlyCollection<Server> cdnServers;

    private readonly CallbackManager callbackManager;

    private uint lastChangeNumber = 0;

    public bool isLoggedOn = false;
    public bool isRunning = true;
    public static uint tickerHash = 0;

    public SteamSession()
    {
        client = new SteamClient();
        callbackManager = new CallbackManager(client);

        user = client.GetHandler<SteamUser>();
        apps = client.GetHandler<SteamApps>();
        content = client.GetHandler<SteamContent>();

        cdnClient = new Client(client);

        callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        lastChangeNumber = LocalConfig.Get<uint>("lastSeenChangeNumber");

        using (var db = Database.GetConnection())
        {
            if (lastChangeNumber == 0)
                lastChangeNumber = db.ExecuteScalar<uint>("SELECT `ChangeID` FROM `DepotVersions` ORDER BY `ChangeID` DESC LIMIT 1");

        }

        Console.WriteLine($"Previous changelist was {lastChangeNumber}");
    }

    public void Run()
    {
        client.Connect();

        while (isRunning)
        {
            callbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Console.WriteLine("Connected!");

        user.LogOnAnonymous();
    }

    private async void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        if (!isRunning)
            return;

        if (isLoggedOn)
        {
            isLoggedOn = false;
            tickerHash++;
        }

        Console.WriteLine("Disconnected! Trying to reconnect...");
        await Task.Delay(TimeSpan.FromSeconds(15));

        client.Connect();
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
            return;

        cdnServers = await content.GetServersForSteamPipe();

        // foreach (var server in cdnServers)
        // {
        //     Console.WriteLine("Server: {0}", server.Host);
        //     Console.WriteLine("\tVHost: {0}", server.VHost);
        //     Console.WriteLine("\tPort: {0}", server.Port);
        //     Console.WriteLine("\tProtocol: {0}", server.Protocol);
        // }

        Console.WriteLine("Logged On! Server time is {0}. Got {1} CDN servers.", cb.ServerTime, cdnServers.Count);

        isLoggedOn = true;
        Downloader.downloadSignal = 1;

        Task.Run(ChangesTick);
        Task.Run(Downloader.DownloadThread);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Console.WriteLine("Logged Off");

        if (isLoggedOn)
        {
            isLoggedOn = false;
            Interlocked.Increment(ref tickerHash);
        }

        // client.Disconnect();
    }


    private async Task OnPICSChanges(SteamApps.PICSChangesCallback cb)
    {
        // Console.WriteLine($"PICS Update {cb.LastChangeNumber} -> {cb.CurrentChangeNumber}");
        if (cb.CurrentChangeNumber == lastChangeNumber)
            return;

        bool needsUpdate = false;
        uint? changeNumber = null;

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

            if (appChange.ID == Config.AppToWatch)
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
            Console.WriteLine("Update needed!");
            var appInfo = await InfoFetcher.FetchAppInfo(Config.AppToWatch, changeNumber);
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

            }
            else
            {
                Console.WriteLine("Couldn't get appinfo!");
            }
        }

        lastChangeNumber = cb.CurrentChangeNumber;
        LocalConfig.Set("lastSeenChangeNumber", lastChangeNumber.ToString());
        await transaction.CommitAsync();
        if (needsUpdate)
        {
            // Signal to downloader to check for updates
            Downloader.downloadSignal = 1;
        }
    }

    private async Task ChangesTick()
    {
        var currentHash = tickerHash;

        while (tickerHash == currentHash)
        {
            try
            {
                var changes = await apps.PICSGetChangesSince(lastChangeNumber, true, false);
                await OnPICSChanges(changes);

            }
            catch (TaskCanceledException e)
            {
                Console.WriteLine($"PICSGetChangesSince TaskCancelledException, restarting");
                client.Disconnect();
            }
            catch (Exception e)
            {
                Console.WriteLine($"PICSGetChangesSince: {e.GetType().Name}: {e.Message}");
            }

            await Task.Delay(3000);
        }

        Console.WriteLine("ticker died...");
    }
}