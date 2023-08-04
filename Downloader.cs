namespace GameTracker;

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Diagnostics;

using Dapper;

using SteamKit2;

class Downloader
{
    private static readonly SemaphoreSlim DownloadSem = new SemaphoreSlim(1, 1);

    // TODO: Hash the chunks instead? Although, this is probably robust enough.
    static bool VerifyFile(string fileName, DepotManifest.FileData fileData)
    {

        var sha1 = SHA1.Create();
        using (var stream = File.OpenRead(fileName))
        {
            byte[] hash = sha1.ComputeHash(stream);
            return fileData.FileHash.SequenceEqual(hash);
        }
    }

    async static Task DownloadFile(string outFile, DepotManifest manifest, DepotManifest.FileData file, byte[] depotKey)
    {
        using (var of = File.Create(outFile))
        {
            of.SetLength((long)file.TotalSize);

            // TODO: Download multiple chunks at the same time
            foreach (var chunk in file.Chunks)
            {
                // TODO: Reuse previous version *chunks*
                // TODO: Cycle through multiple servers when fetching chunks, in case a server is down
                var server = SteamSession.Instance.CDNPool.TakeConnection();
                var downloadedChunk = await SteamSession.Instance.cdnClient.DownloadDepotChunkAsync(manifest.DepotID, chunk, server, depotKey);
                SteamSession.Instance.CDNPool.ReturnConnection(server);

                if (downloadedChunk == null)
                    throw new InvalidDataException($"Failed to download chunk {chunk.ChunkID} from manifest {manifest.ManifestGID}");

                of.Seek((long)chunk.Offset, SeekOrigin.Begin);
                of.Write(downloadedChunk.Data, 0, downloadedChunk.Data.Length);

                // Console.WriteLine("\tchunk {0}", BitConverter.ToString(chunk.ChunkID));
                // Console.WriteLine("\t\tchecksum {0}", BitConverter.ToString(chunk.Checksum));
                // Console.WriteLine("\t\toffset {0}", chunk.Offset);
                // Console.WriteLine("\t\tlength compressed {0}", chunk.CompressedLength);
                // Console.WriteLine("\t\tlength uncompressed {0}", chunk.UncompressedLength);
            }
        };
    }

    async static Task DownloadManifest(DepotManifest manifest, byte[] depotKey, string outputDir, DepotManifest? prevManifest = null, string? prevDir = null)
    {
        // Console.WriteLine($"Downloading manifest {manifest.ManifestGID}");

        Dictionary<string, DepotManifest.FileData>? prevFiles = null;
        if (prevManifest != null)
        {
            prevFiles = prevManifest.Files.ToDictionary(f => f.FileName);
        }

        // Pre-allocate dirs
        foreach (var file in manifest.Files)
        {
            var outPath = Path.Join(outputDir, file.FileName);

            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
            {
                Directory.CreateDirectory(outPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            }
        }

        // Download files
        foreach (var file in manifest.Files)
        {
            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                continue;

            var outFile = Path.Join(outputDir, file.FileName);
            if (Path.Exists(outFile))
            {
                Console.WriteLine($"Path {outFile} already exists, not replacing");
                continue;
            };

            // Console.WriteLine("{0} | size: {1} | flags: {2}", file.FileName, file.TotalSize, file.Flags);
            bool canReuse = false;
            if (prevFiles != null)
            {
                DepotManifest.FileData? prevFile = null;
                prevFiles.TryGetValue(file.FileName, out prevFile);

                if (prevFile != null && prevFile.FileHash.SequenceEqual(file.FileHash))
                {
                    // Console.WriteLine("We could reuse {0}", prevFile.FileName);
                    canReuse = true;
                }
            }

            if (canReuse)
            {
                // Console.WriteLine("Copying to {0} from {1}", Path.Join(outputDir, file.FileName), Path.Join(prevDir, file.FileName));
                File.Copy(Path.Join(prevDir, file.FileName), outFile);
            }
            else
            {
                // Console.WriteLine("Downloading {0}", outFile);
                await DownloadFile(outFile, manifest, file, depotKey);
                // Console.WriteLine("Downloaded {0}", outFile);
            }

            if (!VerifyFile(outFile, file))
            {
                Console.WriteLine($"File {file.FileName} failed to verify, retrying download");
                await DownloadFile(outFile, manifest, file, depotKey);
                if (!VerifyFile(outFile, file))
                {
                    throw new InvalidDataException($"File {file.FileName} failed to verify a second time, something is very wrong!");
                }
            }
        }
    }

    async static Task<DepotManifest?> GetPrevPulledManifest(uint appId, uint depotId)
    {
        await using var db = await Database.GetConnectionAsync();
        var prevChangeId = LocalConfig.Get<uint?>("lastProcessedChangeNumber");
        if (prevChangeId == null)
            return null;

        var prevManifestIdResult = await db.QueryAsync<ulong>(
                    "SELECT `ManifestID` from DepotVersions WHERE `ChangeID` = @ChangeID AND `DepotID` = @DepotID ORDER BY `ChangeID` DESC LIMIT 1",
                    new { ChangeID = prevChangeId, DepotID = depotId });

        if (prevManifestIdResult.Count() > 0)
        {
            var prevManifestId = prevManifestIdResult.First();
            var mi = new InfoFetcher.ManifestInfo(appId, depotId, prevManifestId);

            var depotKey = await InfoFetcher.GetDepotKey(appId, depotId);
            if (depotKey == null)
                return null;

            try
            {
                DepotManifest manifest = await InfoFetcher.FetchManifest(mi, depotKey);
                if (manifest.Files == null || manifest.FilenamesEncrypted)
                    return null;

                return manifest;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    async static Task DownloadDepot(InfoFetcher.ManifestInfo depot, string downloadPath, uint changeId)
    {
        var depotKey = await InfoFetcher.GetDepotKey(depot.AppID, depot.DepotID);
        if (depotKey == null)
        {
            Console.WriteLine("Couldn't get depot key for depot {0}, skipping", depot.DepotID);
            return;
        }

        var manifest = await InfoFetcher.FetchManifest(depot, depotKey);
        if (manifest.Files == null || manifest.FilenamesEncrypted)
        {
            Console.WriteLine($"Manifest {depot.ManifestID} has no files, skipping");
            return;
        }

        DepotManifest? prevManifest = await GetPrevPulledManifest(depot.AppID, depot.DepotID);
        string? prevPath = null;
        if (prevManifest != null)
            prevPath = Config.ContentDir;
        if (!Path.Exists(prevPath))
            prevPath = null;

        await DownloadManifest(manifest, depotKey, downloadPath, prevManifest, prevPath);
    }

    async static Task DownloadChange(uint changeId)
    {
        Console.WriteLine("ChangeID: {0}", changeId);

        await using var db = await Database.GetConnectionAsync();
        var depots = await db.QueryAsync<InfoFetcher.ManifestInfo>(
                "select `AppID`, `DepotID`, `ManifestID` from DepotVersions WHERE ChangeID = @ChangeID AND AppID = @AppID",
                new { ChangeID = changeId, AppID = Config.AppToWatch }
        );

        var tempDownloadPath = Util.GetNewTempDir("download");

        try
        {
            foreach (var depot in depots)
            {
                // XXX: For testing
                if (232252 != depot.DepotID)
                    continue;

                await DownloadDepot(depot, tempDownloadPath, changeId);
                Console.WriteLine("\t{0}", depot);
            }
        }
        catch
        {
            Directory.Delete(tempDownloadPath, true);
            throw;
        }

        Directory.Delete(Config.ContentDir, true);
        Directory.Move(tempDownloadPath, Config.ContentDir);
    }

    async static Task ProcessContent(string inDir, string outDir, string message)
    {
        var tempOut = Util.GetNewTempDir("processed");

        var p = new Process();
        p.StartInfo.WorkingDirectory = "/home/user/Projects/tf2_stuff/GameTracking-FINAL/DataMiner";
        p.StartInfo.FileName = "/home/user/Projects/tf2_stuff/GameTracking-FINAL/DataMiner/__main__.py";
        p.StartInfo.Arguments = $"--config=/home/user/Projects/tf2_stuff/GameTracking-FINAL/DataMiner/config.yaml \"{Path.GetFullPath(inDir)}\" \"{Path.GetFullPath(tempOut)}\"";
        p.Start();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new Exception($"Got exit code {p.ExitCode} while running processer");

        if (Directory.Exists(outDir))
            Directory.Delete(outDir, true);
        Directory.Move(tempOut, outDir);

        var gitProc = new Process();
        gitProc.StartInfo.WorkingDirectory = outDir;
        gitProc.StartInfo.FileName = "git";
        gitProc.StartInfo.Arguments = "add -A";
        gitProc.Start();
        await gitProc.WaitForExitAsync();

        if (gitProc.ExitCode != 0)
            throw new Exception($"Got exit code {gitProc.ExitCode} while running git add");

        gitProc = new Process();
        gitProc.StartInfo.WorkingDirectory = outDir;
        gitProc.StartInfo.FileName = "git";
        gitProc.StartInfo.Arguments = $"commit --allow-empty -a -m \"{message}\"";
        gitProc.Start();
        await gitProc.WaitForExitAsync();

        if (gitProc.ExitCode != 0)
            throw new Exception($"Got exit code {gitProc.ExitCode} while running git commit");
    }

    async static Task CheckUpdates()
    {
        var lastProcessedChangeNumber = await LocalConfig.GetAsync<uint>("lastProcessedChangeNumber");

        // Get sorted list of changeids to process
        await using var db = await Database.GetConnectionAsync();
        IEnumerable<uint> changeIdsToProcess = await db.QueryAsync<uint>(
                "select DISTINCT `ChangeID` from DepotVersions WHERE ChangeID > @LastProcessedChangeNumber ORDER BY `ChangeID` ASC",
                new { LastProcessedChangeNumber = lastProcessedChangeNumber }
        );

        foreach (uint changeId in changeIdsToProcess)
        {
            await DownloadChange(changeId);
            await ProcessContent(Config.ContentDir, Path.Join(Config.RepoDir, "Content"), $"change {changeId}");
            await LocalConfig.SetAsync("lastProcessedChangeNumber", changeId.ToString());
        }
    }

    public async static void RunUpdates()
    {
        await DownloadSem.WaitAsync();
        await CheckUpdates();
        DownloadSem.Release();
    }
}