namespace GameTracker;

using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

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

            foreach (var chunk in file.Chunks)
            {
                // TODO: Reuse previous version *chunks*
                // TODO: Cycle through multiple servers when fetching chunks, in case a server is down
                var downloadedChunk = await SteamSession.Instance.cdnClient.DownloadDepotChunkAsync(manifest.DepotID, chunk, SteamSession.Instance.cdnServers.First(), depotKey);
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

        // Create dirs first
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

    async static Task<DepotManifest?> GetPrevManifest(uint changeId, uint appId, uint depotId)
    {
        await using var db = await Database.GetConnectionAsync();
        var prevManifestIdResult = await db.QueryAsync<ulong>(
                    "SELECT `ManifestID` from DepotVersions WHERE `ChangeID` < @ChangeID AND `DepotID` = @DepotID ORDER BY `ChangeID` DESC LIMIT 1",
                    new { ChangeID = changeId, DepotID = depotId });

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

    async static Task DownloadChange(uint changeId)
    {
        Console.WriteLine("ChangeID: {0}", changeId);

        await using var db = await Database.GetConnectionAsync();
        var depots = await db.QueryAsync<InfoFetcher.ManifestInfo>(
                "select `AppID`, `DepotID`, `ManifestID` from DepotVersions WHERE ChangeID = @ChangeID",
                new { ChangeID = changeId }
        );

        var downloadPath = Util.GetNewTempDir();

        try
        {
            foreach (var depot in depots)
            {
                // XXX: For testing
                if (232256 != depot.DepotID)
                    continue;

                var depotKey = await InfoFetcher.GetDepotKey(depot.AppID, depot.DepotID);
                if (depotKey == null)
                {
                    Console.WriteLine("Couldn't get depot key for depot {0}, skipping", depot.DepotID);
                    continue;
                }

                var manifest = await InfoFetcher.FetchManifest(depot, depotKey);
                if (manifest.Files == null || manifest.FilenamesEncrypted)
                {
                    Console.WriteLine($"Manifest {depot.ManifestID} has no files, skipping");
                    continue;
                }

                DepotManifest? prevManifest = await GetPrevManifest(changeId, depot.AppID, depot.DepotID);
                string? prevPath = null;
                if (prevManifest != null)
                    prevPath = Config.ContentDir;

                await DownloadManifest(manifest, depotKey, downloadPath, prevManifest, prevPath);

                Console.WriteLine("\t{0} {1}", depot, downloadPath);
            }
        }
        catch
        {
            Directory.Delete(downloadPath, true);
            throw;
        }

        Directory.Delete(Config.ContentDir, true);
        Directory.Move(downloadPath, Config.ContentDir);
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
            // await LocalConfig.Set("lastProcessedChangeNumber", changeIdsToProcess.LastOrDefault(lastProcessedChangeNumber).ToString());
        }
    }

    public async static void RunUpdates()
    {
        await DownloadSem.WaitAsync();
        await CheckUpdates();
        DownloadSem.Release();
    }
}