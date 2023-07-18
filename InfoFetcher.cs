namespace GameTracker;

using System;
using System.Timers;

using SteamKit2;
using SteamKit2.CDN;

class InfoFetcher
{
    public class DepotInfo
    {
        public readonly uint DepotId;
        public readonly string Branch;
        public readonly ulong ManifestId;

        public DepotInfo(uint depotId, string branch, ulong manifestId) => (DepotId, Branch, ManifestId) = (depotId, branch, manifestId);
    }

    public class AppInfo
    {
        public readonly uint AppId;
        public readonly uint ChangeId;
        public readonly List<DepotInfo> Depots;

        public AppInfo(uint appId, uint changeId, List<DepotInfo> depots) => (AppId, ChangeId, Depots) = (appId, changeId, depots);
    };

    // unorganized! (appid, depotid)
    static readonly Dictionary<KeyValuePair<uint, uint>, byte[]> DepotKeys;

    static InfoFetcher()
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

    public static async Task<AppInfo?> FetchAppInfo(uint appId, uint? expectedChangeNumber)
    {
        // TODO: Do we need to re-request these? Or can we cache them...
        var accessTokenResult =
          await SteamSession.Instance.apps.PICSGetAccessTokens(appId, package: null);

        ulong accessToken = 0;

        accessTokenResult.AppTokens.TryGetValue(appId, out accessToken);

        var productInfoResults =
          await SteamSession.Instance.apps.PICSGetProductInfo(
              app: new SteamApps.PICSRequest(appId, accessToken),
              package: null,
              metaDataOnly: false
          );

        var productInfo = productInfoResults.Results.FirstOrDefault(cb => cb.Apps.ContainsKey(appId));

        if (productInfo == null)
        {
            Console.WriteLine("Product info is null!");
            return null;
        }

        if (productInfo.Apps.TryGetValue(appId, out SteamApps.PICSProductInfoCallback.PICSProductInfo info))
        {
            if (expectedChangeNumber != null && info.ChangeNumber != expectedChangeNumber)
                Console.WriteLine($"Expected change number {expectedChangeNumber}, got {info.ChangeNumber}");

            return GetAppInfoFromKV(info.ChangeNumber, info.KeyValues);
        }
        else
        {
            Console.WriteLine("Couldn't get appinfo for app!");
        }

        return null;
    }

    private static AppInfo GetAppInfoFromKV(uint changeNumber, KeyValue info)
    {
        var depotsKvs = info["depots"];
        // depotsKvs.SaveToFile("depots", false);
        var depots = new List<DepotInfo>();

        foreach (KeyValue depot in depotsKvs.Children)
        {
            if (!uint.TryParse(depot.Name, out uint depotID))
                continue;

            // TODO: Allow selecting branch...
            var branch = "public";
            var manifestInfoKV = depot["manifests"][branch]["gid"];

            Console.WriteLine($"Got depot {depotID} {manifestInfoKV}");
            depots.Add(new DepotInfo(depotID, branch, manifestInfoKV.AsUnsignedLong()));
        }

        return new AppInfo(info["appid"].AsUnsignedInteger(), changeNumber, depots);
    }
}