namespace GameTracker;

using System.Collections.Concurrent;

using SteamKit2;
using SteamKit2.CDN;

class CDNPool
{
    private readonly SteamSession Session;
    private ConcurrentQueue<Server> Servers = new ConcurrentQueue<Server>();

    public CDNPool(SteamSession session) => (Session) = (session);

    public async Task FetchNewServers()
    {
        while (Servers.Count < Program.Config.MinRequiredCDNServers)
        {
            // According to steam-lancache-prefill:
            // GetServersForSteamPipe() sometimes hangs and never times out.  Wrapping the call in another task, so that we can timeout the entire method.
            foreach (var server in await Session.content.GetServersForSteamPipe().WaitAsync(TimeSpan.FromSeconds(15)))
            {
                // Ignore servers that don't have our app
                if (server.AllowedAppIds.Count() < 0 && !server.AllowedAppIds.Contains(Program.Config.AppToWatch))
                    continue;

                Servers.Enqueue(server);
            }


            Servers = new ConcurrentQueue<Server>(Servers.DistinctBy(s => s.Host));
            if (Servers.Count < Program.Config.MinRequiredCDNServers)
                await Task.Delay(1500);
        }

        // foreach (var server in Servers)
        // {
        //     Console.WriteLine("Server: {0}", server.Host);
        //     Console.WriteLine("\tVHost: {0}", server.VHost);
        //     Console.WriteLine("\tPort: {0}", server.Port);
        //     Console.WriteLine("\tProtocol: {0}", server.Protocol);
        // }
        Console.WriteLine($"Got {Servers.Count} CDN servers");
    }

    public Server TakeConnection()
    {
        Servers.TryDequeue(out Server? server);
        if (server == null)
        {
            throw new Exception("No more servers left!");
        }

        return server;
    }

    public void ReturnConnection(Server server)
    {
        Servers.Enqueue(server);
    }
}