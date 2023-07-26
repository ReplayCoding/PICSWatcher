namespace GameTracker;

using System.Linq;
using System.Threading;

using Dapper;

using SteamKit2;

class Downloader
{
    // 1 for needs update, 0 for not
    // I want a bool :(
    public static int downloadSignal;

    class ManifestInfo
    {
        public readonly uint AppID;
        public readonly uint DepotID;
        public readonly ulong ManifestID;

        public override string ToString()
        {
            return $"App: {AppID} Depot: {DepotID} Manifest: {ManifestID}";
        }
    };

    async static Task<byte[]?> GetDepotKey(uint appId, uint depotId, bool bypassCache = false)
    {
        await using var db = await Database.GetConnectionAsync();
        var decryptionKeys = await db.QueryAsync<string>("SELECT `Key` FROM `DepotKeys` WHERE `DepotID` = @DepotID", new { DepotID = depotId });
        if (decryptionKeys.Count() > 0)
        {
            var key = decryptionKeys.First();
            // Console.WriteLine("Cached depot key is {0}", decryptionKeys.First());
            return Convert.FromHexString(key);
        };

        var depotKey = await SteamSession.Instance.apps.GetDepotDecryptionKey(depotId, appId);
        if (depotKey.Result == EResult.OK)
        {
            await db.ExecuteAsync("INSERT INTO `DepotKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { DepotID = depotId, Key = Convert.ToHexString(depotKey.DepotKey) });
            return depotKey.DepotKey;
        }

        Console.WriteLine($"Couldn't get depot key for {depotId}, got result {depotKey.Result}");
        return null;
    }

    async static Task CheckUpdates()
    {
        var lastProcessedChangeNumber = await LocalConfig.GetAsync<uint>("lastProcessedChangeNumber");

        // Get sorted list of changeids to process
        await using var db = await Database.GetConnectionAsync();
        IEnumerable<uint> changeIdsToProcess = await db.QueryAsync<uint>(
                "select DISTINCT `ChangeID` from DepotVersions WHERE ChangeID > @LastProcessedChangeNumber ORDER BY `ChangeID` ASC ",
                new { LastProcessedChangeNumber = lastProcessedChangeNumber }
        );

        foreach (uint changeId in changeIdsToProcess)
        {
            Console.WriteLine("ChangeID: {0}", changeId);
            var depots = await db.QueryAsync<ManifestInfo>(
                    "select `AppID`, `DepotID`, `ManifestID` from DepotVersions WHERE ChangeID = @ChangeID",
                    new { ChangeID = changeId }
            );
            foreach (var depot in depots)
            {
                var depotKey = await GetDepotKey(depot.AppID, depot.DepotID);
                if (depotKey == null)
                {
                    Console.WriteLine("Couldn't get depot key for depot {0}, skipping", depot.DepotID);
                    continue;
                }

                Console.WriteLine("\t{0}", depot);
            }

            // await LocalConfig.Set("lastProcessedChangeNumber", changeIdsToProcess.LastOrDefault(lastProcessedChangeNumber).ToString());
        }
    }

    public async static void DownloadThread()
    {
        var currentHash = SteamSession.Instance.tickerHash;

        while (currentHash == SteamSession.Instance.tickerHash)
        {
            if (1 == Interlocked.CompareExchange(ref downloadSignal, 0, 1))
            {
                Console.WriteLine("Update check was requested");
                try
                {
                    await CheckUpdates();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error on downloader thread: {e.GetType().Name}: {e.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(55));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("ticker changed, downloader thread dying now");
    }
}