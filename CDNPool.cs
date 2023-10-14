namespace GameTracker;

using System.Collections.Concurrent;

using SteamKit2.CDN;

class CDNPool
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly SteamSession Session;
    private BlockingCollection<Server> Servers = new BlockingCollection<Server>(new ConcurrentQueue<Server>());

    public CDNPool(SteamSession session) => (Session) = (session);

    public async Task FetchNewServers()
    {
        var serverList = new List<Server>();
        while (serverList.Count < Program.Config.MinRequiredCDNServers)
        {
            // According to steam-lancache-prefill:
            // GetServersForSteamPipe() sometimes hangs and never times out.  Wrapping the call in another task, so that we can timeout the entire method.
            foreach (var server in await Session.content.GetServersForSteamPipe().WaitAsync(TimeSpan.FromSeconds(15)))
            {
                // Ignore servers that don't have our app
                if (server.AllowedAppIds.Count() < 0 && !server.AllowedAppIds.Contains(Program.Config.AppToWatch))
                    continue;

                serverList.Add(server);
            }


            serverList = serverList.DistinctBy(s => s.Host).ToList();
            if (serverList.Count < Program.Config.MinRequiredCDNServers)
                await Task.Delay(Program.Config.RetryDelay);
        }

        Servers = new BlockingCollection<Server>(new ConcurrentQueue<Server>(serverList));
        Logger.Info($"Got {Servers.Count} CDN servers");
    }

    public Server TakeConnection()
    {
        return Servers.Take();
    }

    public void ReturnConnection(Server server)
    {
        Servers.Add(server);
    }
}