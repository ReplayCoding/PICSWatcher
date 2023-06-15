namespace GameTracker;

using System;
using System.Timers;

using SteamKit2;
using SteamKit2.CDN;

class ManifestDownloader
{
    // unorganized! (appid, depotid)
    static readonly Dictionary<KeyValuePair<uint, uint>, byte[]> DepotKeys;

    static ManifestDownloader()
    {
        DepotKeys = new Dictionary<KeyValuePair<uint, uint>, byte[]>();
    }

    private async static Task<byte[]?> GetDepotKey(uint app, uint depot)
    {
        var lookupKey = new KeyValuePair<uint, uint>(app, depot);

        byte[]? depotKey = null;
        if (!DepotKeys.TryGetValue(lookupKey, out depotKey))
        {
            var depotKeyResult =
                await SteamSession.Instance.apps.GetDepotDecryptionKey(depot, app);

            if (depotKeyResult.Result == EResult.OK)
                depotKey = depotKeyResult.DepotKey;

            DepotKeys.Add(new KeyValuePair<uint, uint>(app, depot), depotKey);
        }

        return depotKey;
    }

    private static IEnumerable<KeyValuePair<SteamSession.AppWatchInfo, ulong>> GetManifestIds(SteamApps.PICSProductInfoCallback.PICSProductInfo info)
    {
        Console.WriteLine($"app: {info.ID}");
        info.KeyValues.SaveToFile($"appinfo_{info.ID}", false);

        var depotInfo = info.KeyValues["depots"];

        foreach (var watchInfo in SteamSession.Instance.appsToWatch.Where(a => a.Id == info.ID))
        {
            var depot = depotInfo[watchInfo.Depot.ToString()];

            var manifests = depot["manifests"][watchInfo.Branch];
            var manifest = manifests.Children.Count > 0 ? manifests["gid"] : manifests;

            ulong manifestId = 0;
            if (!ulong.TryParse(manifest.Value, out manifestId))
                continue;

            yield return new KeyValuePair<SteamSession.AppWatchInfo, ulong>(watchInfo, manifestId);
        }
    }

    public static async Task<List<KeyValuePair<SteamSession.AppWatchInfo, ulong>>> FetchManifests(IEnumerable<uint> appIds)
    {
        // TODO: Do we need to re-request these? Or can we cache them...
        var accessTokenResult =
          await SteamSession.Instance.apps.PICSGetAccessTokens(appIds, Enumerable.Empty<uint>());

        Console.WriteLine("Got tokens for {0} apps, {1} denied.", accessTokenResult.AppTokens.Count, accessTokenResult.AppTokensDenied.Count);

        var appRequests = accessTokenResult.AppTokens
          .Select(i => new SteamApps.PICSRequest(i.Key, i.Value));

        var productInfoResults =
          await SteamSession.Instance.apps.PICSGetProductInfo(
              appRequests,
              Enumerable.Empty<SteamApps.PICSRequest>(),
              metaDataOnly: false
          );

        var manifests = new List<KeyValuePair<SteamSession.AppWatchInfo, ulong>>();

        foreach (var productInfoResult in productInfoResults.Results)
        {
            foreach (var appInfo in productInfoResult.Apps.Values)
            {
                foreach (var manifest in GetManifestIds(appInfo))
                {
                    manifests.Add(manifest);
                }
            }
        }

        return manifests;
    }

    public async static Task FilterManifests(IEnumerable<KeyValuePair<SteamSession.AppWatchInfo, ulong>> manifests)
    {
        foreach (var manifest in manifests)
        {
            var appId = manifest.Key.Id;
            var depotId = manifest.Key.Depot;
            var manifestId = manifest.Value;

            var depotKey = await GetDepotKey(appId, depotId);

            // Console.WriteLine("{0} {1} {2}: {3}", manifest.AppInfo.Id, manifest.AppInfo.Depot, manifest.AppInfo.Branch, manifest.Manifest);
            var requestCode = await SteamSession.Instance.content.GetManifestRequestCode(depotId, appId, manifestId, null);
            Console.WriteLine("manifest request code: {0}", requestCode);
            Console.WriteLine("depot key: {0}", Convert.ToHexString(depotKey));

            var manifestContent = await SteamSession.Instance.cdnClient.DownloadManifestAsync(depotId, manifestId, requestCode, SteamSession.Instance.cdnServers.First(), depotKey);
            manifestContent.SaveToFile($"manifest_{appId}_{depotId}_{manifestId}");
        }
    }
}