namespace GameTracker;

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

using Dapper;

using SteamKit2;

using LibGit2Sharp;

class Downloader
{
    private static readonly SemaphoreSlim DownloadSem = new SemaphoreSlim(1, 1);
    class BuildInfo
    {
        public readonly uint ChangeID;
        public readonly string Branch;
        public readonly uint BuildID;
        public readonly long TimeUpdated;

        public BuildInfo(uint changeID, string branch, uint buildID, long timeUpdated)
        {
            ChangeID = changeID;
            Branch = branch;
            BuildID = buildID;
            TimeUpdated = timeUpdated;
        }
    }
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

    async static Task DownloadFile(string outFile, DepotManifest manifest, DepotManifest.FileData manifestFile, byte[] depotKey, DepotManifest.FileData? prevManifestFile, string? prevDir = null)
    {
        System.IO.FileStream? prevFile = null;
        try
        {
            if (prevDir != null && prevManifestFile != null)
                prevFile = File.OpenRead(Path.Join(prevDir, manifestFile.FileName));
        }
        catch
        {
            // Don't care if we don't find a previous file, just ignore it and download
        }

        using (var of = File.Create(outFile))
        {
            of.SetLength((long)manifestFile.TotalSize);

            // TODO: Download multiple chunks at the same time
            foreach (var chunk in manifestFile.Chunks)
            {
                byte[]? downloadedChunk = null;
                DepotManifest.ChunkData? prevChunk = null;
                if (prevManifestFile != null)
                    prevChunk = prevManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                if (prevChunk != null && prevFile != null)
                {
                    try
                    {
                        var tmp = new byte[prevChunk.UncompressedLength];

                        prevFile.Seek((long)prevChunk.Offset, SeekOrigin.Begin);
                        await prevFile.ReadAsync(tmp, 0, tmp.Length);

                        var adler = Util.AdlerHash(tmp);
                        if (adler.SequenceEqual(prevChunk.Checksum))
                        {
                            // We found a chunk to reuse!
                            downloadedChunk = tmp;
                        }
                        else
                        {
                            Console.WriteLine($"Couldn't reuse chunk for file {0} because hash doesn't match", manifestFile.FileName);
                        }
                    }
                    catch
                    {
                        // Ignore, will download
                    }
                }

                // Couldn't reuse chunk, download it
                if (downloadedChunk == null)
                {
                    var downloadedChunkInfo = await InfoFetcher.DownloadChunk(manifest, chunk, depotKey);
                    if (downloadedChunkInfo != null)
                        downloadedChunk = downloadedChunkInfo.Data;
                }

                if (downloadedChunk == null)
                    throw new InvalidDataException($"Failed to download chunk {BitConverter.ToString(chunk.ChunkID)} from manifest {manifest.ManifestGID}");

                of.Seek((long)chunk.Offset, SeekOrigin.Begin);
                of.Write(downloadedChunk, 0, downloadedChunk.Length);
            }
        };

        if (prevFile != null)
            await prevFile.DisposeAsync();
    }

    async static Task DownloadManifest(DepotManifest manifest, byte[] depotKey, string outputDir, DepotManifest? prevManifest = null, string? prevDir = null)
    {
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

            DepotManifest.FileData? prevFile = null;
            if (prevFiles != null)
            {
                prevFiles.TryGetValue(file.FileName, out prevFile);
            }

            await DownloadFile(outFile, manifest, file, depotKey, prevFile, prevDir);
            if (!VerifyFile(outFile, file))
            {
                Console.WriteLine($"File {file.FileName} failed to verify, retrying download");
                await DownloadFile(outFile, manifest, file, depotKey, null);
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
            throw new Exception($"Couldn't get depot key for depot {depot.DepotID}");

        var manifest = await InfoFetcher.FetchManifest(depot, depotKey);
        if (manifest.Files == null || manifest.FilenamesEncrypted)
        {
            Console.WriteLine($"Manifest {depot.ManifestID} has no files, skipping");
            return;
        }

        DepotManifest? prevManifest = await GetPrevPulledManifest(depot.AppID, depot.DepotID);
        string? prevPath = null;
        if (prevManifest != null)
            prevPath = Program.Config.ContentDir;
        if (!Path.Exists(prevPath))
            prevPath = null;

        await DownloadManifest(manifest, depotKey, downloadPath, prevManifest, prevPath);
    }

    async static Task DownloadChange(uint changeId, string downloadPath)
    {
        await using var db = await Database.GetConnectionAsync();
        var depots = await db.QueryAsync<InfoFetcher.ManifestInfo>(
                "select `AppID`, `DepotID`, `ManifestID` from DepotVersions WHERE ChangeID = @ChangeID AND AppID = @AppID",
                new { ChangeID = changeId, AppID = Program.Config.AppToWatch }
        );

        try
        {
            foreach (var depot in depots)
            {
                if (Program.Config.DepotsToDownload.Count() != 0 && !Program.Config.DepotsToDownload.Contains(depot.DepotID))
                    continue;

                await DownloadDepot(depot, downloadPath, changeId);
                Console.WriteLine("\t{0}", depot);
            }
        }
        catch
        {
            Directory.Delete(downloadPath, true);
            throw;
        }

    }

    async static Task ProcessContent(string inDir, string outDir)
    {

        var p = new Process();
        p.StartInfo.WorkingDirectory = Program.Config.ProcessorWorkingDir;
        p.StartInfo.FileName = Program.Config.Processor;
        p.StartInfo.Arguments = $"{Program.Config.ProcessorArgs} \"{Path.GetFullPath(inDir)}\" \"{Path.GetFullPath(outDir)}\"";
        p.Start();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new Exception($"Got exit code {p.ExitCode} while running processer");
    }

    async static Task<string> GetCommitMessage(uint changeId)
    {
        await using var db = await Database.GetConnectionAsync();
        BuildInfo? buildInfo = await db.QueryFirstAsync<BuildInfo?>("SELECT `ChangeID`, `Branch`, `BuildID`, `TimeUpdated` FROM `BuildInfo` WHERE `ChangeID` = @ChangeID", new { ChangeID = changeId });

        if (buildInfo == null)
            throw new Exception($"Couldn't get build info for change {changeId}");

        var timeUpdated = DateTimeOffset.FromUnixTimeSeconds(buildInfo.TimeUpdated);
        return $"build {buildInfo.BuildID} on {timeUpdated.ToString("r")}";
    }

    async static Task CommitToRepo(uint changeId)
    {
        using (var repo = new Repository(Program.Config.RepoDir))
        {
            Commands.Stage(repo, "*");

            Signature author = new Signature("gametracking", "gametracking@example.com", DateTime.Now);
            var message = await GetCommitMessage(changeId);

            try
            {
                repo.Commit(message, author, author);

                var remote = repo.Network.Remotes["origin"];
                if (remote != null)
                {
                    var options = new PushOptions();
                    options.CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials()
                    {
                        Username = Program.Config.GitUsername,
                        Password = Program.Config.GitPassword
                    };

                    repo.Network.Push(remote, $"refs/heads/{Program.Config.GitBranch}", options);
                }
            }
            catch (EmptyCommitException e)
            {
                Console.WriteLine($"Note: change {changeId} was empty, not committing: {e.Message}");
            }
        }
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
            Console.WriteLine("Downloading ChangeID {0}...", changeId);
            string tempDownloadPath = Util.GetNewTempDir("download");
            string tempProcessedPath = Util.GetNewTempDir("processed");

            await DownloadChange(changeId, tempDownloadPath);

            await ProcessContent(tempDownloadPath, tempProcessedPath);

            // Successfully downloaded & processed, move to final folders and updated last processed.
            string repoOutDir = Path.Join(Program.Config.RepoDir, "Content");
            if (Directory.Exists(repoOutDir))
                Directory.Delete(repoOutDir, true);
            Directory.Move(tempProcessedPath, repoOutDir);

            if (Directory.Exists(Program.Config.ContentDir))
                Directory.Delete(Program.Config.ContentDir, true);
            Directory.Move(tempDownloadPath, Program.Config.ContentDir);

            await CommitToRepo(changeId);

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