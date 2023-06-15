namespace GameTracker;

using System;
using System.Timers;

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
    public IReadOnlyCollection<Server>? cdnServers;

    private readonly CallbackManager callbackManager;

    private uint lastChangeNumber = 0;

    public bool isLoggedOn = false;
    public bool isRunning = true;
    public uint tickerHash = 0;

    public class AppWatchInfo
    {
        public readonly uint Id;
        public readonly uint Depot;
        public readonly string Branch;

        public AppWatchInfo(uint app, uint depot, string branch = "public") => (Id, Depot, Branch) = (app, depot, branch);
    }

    public readonly List<AppWatchInfo> appsToWatch =
      new List<AppWatchInfo> { new AppWatchInfo(232250, 232256) };

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

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        if (!isRunning)
            return;

        if (isLoggedOn)
        {
            isLoggedOn = false;
            tickerHash++;
        }

        Console.WriteLine("Disconnected! Trying to reconnect...");
        Thread.Sleep(TimeSpan.FromSeconds(15));

        client.Connect();
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
            return;

        cdnServers = await content.GetServersForSteamPipe();

        foreach (var server in cdnServers)
        {
            Console.WriteLine("Server: {0}", server.Host);
            Console.WriteLine("\tVHost: {0}", server.VHost);
            Console.WriteLine("\tPort: {0}", server.Port);
            Console.WriteLine("\tProtocol: {0}", server.Protocol);
        }

        Console.WriteLine("Logged On! Server time is {0}", cb.ServerTime);

        isLoggedOn = true;

        Task.Run(ChangesTick);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Console.WriteLine("Logged Off");

        if (isLoggedOn)
        {
            isLoggedOn = false;
            tickerHash++;
        }

        // client.Disconnect();
    }


    private async Task OnPICSChanges(SteamApps.PICSChangesCallback cb)
    {
        var appsToProcess = new List<uint>();

        var appIdsToWatch = appsToWatch.Select(i => i.Id);

        if (cb.CurrentChangeNumber == lastChangeNumber)
            return;

        if (cb.RequiresFullUpdate || cb.RequiresFullAppUpdate)
        {
            appsToProcess.AddRange(appIdsToWatch);
            Console.WriteLine("Full app update required for change {0}", cb.CurrentChangeNumber);
        }

        Console.WriteLine($"PICS Update {cb.LastChangeNumber} -> {cb.CurrentChangeNumber}");
        foreach (var appChange in cb.AppChanges.Values)
        {
            Console.WriteLine("\tApp {0}", appChange.ID);
            Console.WriteLine("\t\tchangelist: {0}", appChange.ChangeNumber);
            Console.WriteLine("\t\tneedstoken: {0}", appChange.NeedsToken);
        }

        appsToProcess.AddRange(
          cb.AppChanges.Values
            .Select(change => change.ID)
            .Intersect(appIdsToWatch)
        );


        if (appsToProcess.Any())
        {
            var manifestsToCheck =
                await ManifestDownloader.FetchManifests(appsToProcess);

            await ManifestDownloader.FilterManifests(manifestsToCheck);
        }

        lastChangeNumber = cb.CurrentChangeNumber;
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
            catch (Exception e)
            {
                Console.WriteLine($"PICSGetChangesSince: {e.GetType().Name}: {e.Message}");
            }

            await Task.Delay(3000);
        }
    }
}