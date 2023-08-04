namespace GameTracker;

using System;

using Dapper;

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

    public class ManifestInfo
    {
        public readonly uint AppID;
        public readonly uint DepotID;
        public readonly ulong ManifestID;

        public override string ToString()
        {
            return $"App: {AppID} Depot: {DepotID} Manifest: {ManifestID}";
        }

        public ManifestInfo(uint appId, uint depotId, ulong manifestId) => (AppID, DepotID, ManifestID) = (appId, depotId, manifestId);
    };

    // unorganized! (appid, depotid)
    static readonly Dictionary<KeyValuePair<uint, uint>, byte[]> DepotKeys;

    static InfoFetcher()
    {
        DepotKeys = new Dictionary<KeyValuePair<uint, uint>, byte[]>();
    }

    public async static Task<byte[]?> GetDepotKey(uint appId, uint depotId, bool bypassCache = false)
    {
        await using var db = await Database.GetConnectionAsync();

        if (!bypassCache)
        {
            var decryptionKeys = await db.QueryAsync<string>("SELECT `Key` FROM `DepotKeys` WHERE `DepotID` = @DepotID", new { DepotID = depotId });
            if (decryptionKeys.Count() > 0)
            {
                var key = decryptionKeys.First();
                // Console.WriteLine("Cached depot key is {0}", decryptionKeys.First());
                return Convert.FromHexString(key);
            };
        }

        var depotKey = await SteamSession.Instance.apps.GetDepotDecryptionKey(depotId, appId);
        if (depotKey.Result == EResult.OK)
        {
            await db.ExecuteAsync("INSERT INTO `DepotKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { DepotID = depotId, Key = Convert.ToHexString(depotKey.DepotKey) });
            return depotKey.DepotKey;
        }

        Console.WriteLine($"Couldn't get depot key for {depotId}, got result {depotKey.Result}");
        return null;
    }

    public async static Task<DepotManifest> FetchManifest(ManifestInfo info, byte[] depotKey)
    {
        await using var db = await Database.GetConnectionAsync();
        var requestCode = await SteamSession.Instance.content.GetManifestRequestCode(info.DepotID, info.AppID, info.ManifestID, Program.Config.Branch);
        var server = SteamSession.Instance.CDNPool.TakeConnection();
        var manifestContent = await SteamSession.Instance.cdnClient.DownloadManifestAsync(info.DepotID, info.ManifestID, requestCode, server, depotKey);
        SteamSession.Instance.CDNPool.ReturnConnection(server);

        return manifestContent;
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

        if (productInfoResults.Results == null)
        {
            Console.WriteLine("Product info results are null!");
            return null;
        }

        var productInfo = productInfoResults.Results.FirstOrDefault(cb => cb.Apps.ContainsKey(appId));

        if (productInfo == null)
        {
            Console.WriteLine("Product info is null!");
            return null;
        }

        SteamApps.PICSProductInfoCallback.PICSProductInfo? info = null;
        if (productInfo.Apps.TryGetValue(appId, out info))
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

            if (depot["manifests"] == KeyValue.Invalid)
            {
                Console.WriteLine($"Invalid depot {depotID}: skipping (probably shared)");
                continue;
            }

            var branch = Program.Config.Branch;
            var manifestInfoKV = depot["manifests"][branch]["gid"];

            Console.WriteLine($"Got depot {depotID} {manifestInfoKV}");
            depots.Add(new DepotInfo(depotID, branch, manifestInfoKV.AsUnsignedLong()));
        }

        return new AppInfo(info["appid"].AsUnsignedInteger(), changeNumber, depots);
    }
}