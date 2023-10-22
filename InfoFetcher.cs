namespace GameTracker;

using System;

using Dapper;

using SteamKit2;

class InfoFetcher
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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

        public readonly long TimeUpdated;
        public readonly uint BuildID;
        public readonly List<DepotInfo> Depots;

        public AppInfo(uint appId, uint changeId, List<DepotInfo> depots, long timeUpdated, uint buildId) =>
            (AppId, ChangeId, Depots, TimeUpdated, BuildID) =
                (appId, changeId, depots, timeUpdated, buildId);
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

    public async static Task<byte[]?> GetDepotKey(uint appId, uint depotId, bool bypassCache = false)
    {
        await using var db = await Database.GetConnectionAsync();

        if (!bypassCache)
        {
            var decryptionKeys = await db.QueryAsync<string>("SELECT `Key` FROM `DepotKeys` WHERE `DepotID` = @DepotID", new { DepotID = depotId });
            if (decryptionKeys.Count() > 0)
            {
                var key = decryptionKeys.First();
                return Convert.FromHexString(key);
            };
        }

        var depotKey = await SteamSession.Instance.apps.GetDepotDecryptionKey(depotId, appId);
        if (depotKey.Result == EResult.OK)
        {
            await db.ExecuteAsync("INSERT INTO `DepotKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { DepotID = depotId, Key = Convert.ToHexString(depotKey.DepotKey) });
            return depotKey.DepotKey;
        }

        Logger.Warn($"Couldn't get depot key for {depotId}, got result {depotKey.Result}");
        return null;
    }

    public async static Task<DepotManifest> FetchManifest(ManifestInfo info, byte[] depotKey)
    {
        await using var db = await Database.GetConnectionAsync();
        var requestCode = await SteamSession.Instance.content.GetManifestRequestCode(info.DepotID, info.AppID, info.ManifestID, Program.Config.Branch);

        uint retryCount = 0;
        DepotManifest? manifestContent = null;
        while (manifestContent == null && retryCount < Program.Config.MaxChunkRetries)
        {
            var server = SteamSession.Instance.CDNPool.TakeConnection();

            try
            {
                manifestContent =
                    await SteamSession.Instance.cdnClient.DownloadManifestAsync(
                        info.DepotID,
                        info.ManifestID,
                        requestCode,
                        server,
                        depotKey
                    );
            }
            catch (Exception e)
            {
                Logger.Warn($"Error while downloading manifest, retrying... ({e.Message})");
                await Task.Delay(Program.Config.RetryDelay);
            }

            SteamSession.Instance.CDNPool.ReturnConnection(server);

            retryCount++;
        }

        if (manifestContent == null)
        {
            throw new Exception($"couldn't fetch manifest {info.ManifestID}");
        }

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
            Logger.Warn("Product info results are null!");
            return null;
        }

        var productInfo = productInfoResults.Results.FirstOrDefault(cb => cb.Apps.ContainsKey(appId));

        if (productInfo == null)
        {
            Logger.Warn("Product info is null!");
            return null;
        }

        SteamApps.PICSProductInfoCallback.PICSProductInfo? info = null;
        if (productInfo.Apps.TryGetValue(appId, out info))
        {
            if (expectedChangeNumber != null && info.ChangeNumber != expectedChangeNumber)
                Logger.Warn($"Expected change number {expectedChangeNumber}, got {info.ChangeNumber}");

            return GetAppInfoFromKV(info.ChangeNumber, info.KeyValues);
        }
        else
        {
            Logger.Warn("Couldn't get appinfo for app!");
        }

        return null;
    }

    private static AppInfo GetAppInfoFromKV(uint changeNumber, KeyValue info)
    {
        var depotsKvs = info["depots"];
        var depots = new List<DepotInfo>();

        foreach (KeyValue depot in depotsKvs.Children)
        {
            if (!uint.TryParse(depot.Name, out uint depotID))
                continue;

            if (depot["manifests"] == KeyValue.Invalid)
            {
                Logger.Info($"Invalid depot {depotID}: skipping (shared or encrypted)");
                continue;
            }

            var branch = Program.Config.Branch;
            var manifestInfoKV = depot["manifests"][branch]["gid"];

            Logger.Info($"Got depot {depotID} {manifestInfoKV}");
            depots.Add(new DepotInfo(depotID, branch, manifestInfoKV.AsUnsignedLong()));
        }

        var branchInfo = depotsKvs["branches"][Program.Config.Branch];
        if (branchInfo == KeyValue.Invalid)
            throw new Exception("Couldn't get branch info (buildid, timeupdated).");

        var timeUpdated = branchInfo["timeUpdated"].AsLong();
        var buildId = branchInfo["buildid"].AsUnsignedInteger();

        return new AppInfo(info["appid"].AsUnsignedInteger(), changeNumber, depots, timeUpdated, buildId);
    }

    public async static Task<SteamKit2.CDN.DepotChunk?> DownloadChunk(DepotManifest manifest, DepotManifest.ChunkData chunk, byte[] depotKey)
    {
        uint retryCount = 0;
        SteamKit2.CDN.DepotChunk? downloadedChunk = null;
        while (downloadedChunk == null && retryCount < Program.Config.MaxChunkRetries)
        {
            var server = SteamSession.Instance.CDNPool.TakeConnection();
            try
            {
                // Logger.Debug($"Downloading chunk {BitConverter.ToString(chunk.ChunkID)} with server {server.VHost}");
                downloadedChunk = await SteamSession.Instance.cdnClient.DownloadDepotChunkAsync(manifest.DepotID, chunk, server, depotKey);
            }
            catch (Exception e)
            {
                Logger.Debug($"Error while downloading chunk, retrying... ({e.Message})");
                await Task.Delay(Program.Config.RetryDelay);
            }
            SteamSession.Instance.CDNPool.ReturnConnection(server);

            retryCount++;
        }

        return downloadedChunk;
    }
}