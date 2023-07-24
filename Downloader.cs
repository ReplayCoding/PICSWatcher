namespace GameTracker;

using System.Threading;
using System.Linq;
using Dapper;

class Downloader
{
    // 1 for needs update, 0 for not
    // I want a bool :(
    public static int downloadSignal;

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
            IEnumerable<uint> depots = await db.QueryAsync<uint>(
                    "select `AppID`, `DepotID`, `ManifestID` from DepotVersions WHERE ChangeID = @ChangeID",
                    new { ChangeID = changeId }
            );
            Console.WriteLine("\t{0}", depots);

            // await LocalConfig.Set("lastProcessedChangeNumber", changeIdsToProcess.LastOrDefault(lastProcessedChangeNumber).ToString());
        }
    }

    public async static void DownloadThread()
    {
        var currentHash = SteamSession.tickerHash;

        while (currentHash == SteamSession.tickerHash)
        {
            if (1 == Interlocked.CompareExchange(ref downloadSignal, 0, 1))
            {
                Console.WriteLine("Update check was requested");
                try {
                    await CheckUpdates();
                } catch (Exception e) {
                    Console.WriteLine($"Error on downloader thread: {e.GetType().Name}: {e.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(55));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Console.WriteLine("ticker changed, downloader thread dying now");
    }
}