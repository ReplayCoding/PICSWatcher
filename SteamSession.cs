namespace GameTracker;

using System;
using System.Threading.Tasks;
using System.Security.Cryptography;

using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

class SteamSession
{
    public static SteamSession Instance { get; } = new SteamSession();

    public readonly SteamClient client;
    public readonly SteamUser user;
    public readonly SteamApps apps;
    public readonly SteamContent content;

    public readonly SteamKit2.CDN.Client cdnClient;
    public readonly CDNPool CDNPool;

    private readonly CallbackManager CallbackManager;
    private readonly PICSChanges PICSChanges;

    public bool IsRunning = true;

    public SteamSession()
    {
        client = new SteamClient();

        user = client.GetHandler<SteamUser>();
        apps = client.GetHandler<SteamApps>();
        content = client.GetHandler<SteamContent>();

        cdnClient = new Client(client);
        CDNPool = new CDNPool(this);

        CallbackManager = new CallbackManager(client);

        CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        PICSChanges = new PICSChanges(CallbackManager);
    }

    public void Run()
    {
        client.Connect();

        while (IsRunning)
        {
            CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(5));
        }
    }


    private async void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Console.WriteLine("Connected!");

        if (Program.Config.Username == "anonymous")
        {
            user.LogOnAnonymous();
        }
        else
        {
            string? accessToken = await LocalConfig.GetAsync<string?>("accessToken");
            if (accessToken == null)
            {
                var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = Program.Config.Username,
                    Password = Program.Config.Password,
                    IsPersistentSession = true,
                    Authenticator = new UserConsoleAuthenticator(),
                });

                var pollResponse = await authSession.PollingWaitForResultAsync();

                accessToken = pollResponse.RefreshToken;
                await LocalConfig.SetAsync("accessToken", accessToken);
            }

            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = Program.Config.Username,
                AccessToken = accessToken,
                ShouldRememberPassword = true,
                LoginID = 0x47545446
            });
        }
    }

    private async void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        PICSChanges.StopTick();
        if (!IsRunning)
            return;

        Console.WriteLine("Disconnected! Trying to reconnect...");
        await Task.Delay(TimeSpan.FromSeconds(15));

        client.Connect();
    }

    private async void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            Console.WriteLine("Error while logging in: {0}", cb.Result);
            // await LocalConfig.SetAsync("accessToken", null);
            return;
        }

        Console.WriteLine($"Cell is {cb.CellID}.");
        await CDNPool.FetchNewServers();

        PICSChanges.StartTick();
        // Always force an update check on login
        _ = Task.Run(Downloader.RunUpdates);

        Console.WriteLine("Logged On! Server time is {0}", cb.ServerTime);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Console.WriteLine("Logged off, disconnecting");
        client.Disconnect();
    }
}