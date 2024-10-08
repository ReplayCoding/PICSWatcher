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
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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

            Logger.Debug($"File verification for {fileName}: expected hash {BitConverter.ToString(fileData.FileHash)}, computed hash {BitConverter.ToString(hash)}");
            return fileData.FileHash.SequenceEqual(hash);
        }
    }

    async static Task DownloadFile(string outFile, DepotManifest manifest, DepotManifest.FileData manifestFile, byte[] depotKey, DepotManifest.FileData? prevManifestFile, string? prevDir = null)
    {
        var prevFilePath = Path.Join(prevDir, manifestFile.FileName);
        System.IO.FileStream? prevFile = null;
        try
        {
            if (prevDir != null && prevManifestFile != null)
            {
                if (prevManifestFile.FileHash.SequenceEqual(manifestFile.FileHash))
                {
                    // Perfect match, just copy entire file
                    // NOTE: This will create a hardlink, this could create problems later if we decide to modify files inplace
                    Logger.Debug($"Copying entire file {outFile}");
                    Mono.Unix.UnixFileSystemInfo.TryGetFileSystemEntry(prevFilePath, out var prevFileInfo);
                    if (prevFileInfo != null)
                    {
                        prevFileInfo.CreateLink(outFile);
                        return;
                    }
                }

                // Not a perfect match, load the previous file so we can copy what we need
                prevFile = File.OpenRead(prevFilePath);
            }
        }
        catch
        {
            // Don't care if we don't find a previous file, just ignore it and download
        }

        Logger.Debug($"Downloading file {outFile}");

        using (var of = File.Create(outFile))
        {
            of.SetLength((long)manifestFile.TotalSize);

            var chunksToDownload = new List<Task<InfoFetcher.DownloadedChunkInfo?>>();
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

                        uint adler = Util.AdlerHash(tmp);
                        if (adler == prevChunk.Checksum)
                        {
                            // We found a chunk to reuse!
                            downloadedChunk = tmp;
                        }
                        else
                        {
                            Logger.Warn($"Couldn't reuse chunk for file {manifestFile.FileName} because hash doesn't match ({adler} != {prevChunk.Checksum})");
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
                    Logger.Debug($"Queuing chunk {BitConverter.ToString(chunk.ChunkID)}");
                    chunksToDownload.Add(InfoFetcher.DownloadChunk(manifest, chunk, depotKey));
                }
                else
                {
                    Logger.Debug($"Reusing chunk {BitConverter.ToString(chunk.ChunkID)}");
                    of.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    of.Write(downloadedChunk, 0, downloadedChunk.Length);
                }

            }

            Logger.Debug("Queued chunks, waiting...");

            while (chunksToDownload.Any())
            {
                Task<InfoFetcher.DownloadedChunkInfo?> chunkTask = await Task.WhenAny(chunksToDownload);
                chunksToDownload.Remove(chunkTask);

                InfoFetcher.DownloadedChunkInfo? chunk = await chunkTask;

                if (chunk == null)
                    throw new InvalidDataException($"Failed to download chunk {BitConverter.ToString(chunk.ChunkInfo.ChunkID)} from manifest {manifest.ManifestGID}");

                byte[] chunkData = chunk.Data;

                of.Seek((long)chunk.ChunkInfo.Offset, SeekOrigin.Begin);
                of.Write(chunkData, 0, chunk.Length);
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
                Logger.Info($"Path {outFile} already exists, not replacing");
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
                Logger.Warn($"File {file.FileName} failed to verify, retrying download");
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
            Logger.Info($"Manifest {depot.ManifestID} has no files, skipping");
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
                Logger.Info($"\t{depot}");
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
                Logger.Info($"Change {changeId} was empty, not committing: {e.Message}");
            }
        }
    }

    async static Task CheckUpdates()
    {
        var lastProcessedChangeNumber = await LocalConfig.GetAsync<uint>("lastProcessedChangeNumber");

        // Get sorted list of changeids to process
        await using var db = await Database.GetConnectionAsync();
        List<uint> changeIdsToProcess = (await db.QueryAsync<uint>(
                "select DISTINCT `ChangeID` from DepotVersions WHERE ChangeID > @LastProcessedChangeNumber ORDER BY `ChangeID` ASC",
                new { LastProcessedChangeNumber = lastProcessedChangeNumber }
        )).ToList();

        Logger.Info($"Changes in queue: {String.Join(", ", changeIdsToProcess)}");

        foreach (uint changeId in changeIdsToProcess)
        {
            Logger.Info("Downloading ChangeID {0}...", changeId);
            string tempDownloadPath = Util.GetNewTempDir("download");
            string tempProcessedPath = Util.GetNewTempDir("processed");

            var downloadWatch = System.Diagnostics.Stopwatch.StartNew();
            await DownloadChange(changeId, tempDownloadPath);
            downloadWatch.Stop();

            Logger.Info("Processing ChangeID {0}...", changeId);
            var processWatch = System.Diagnostics.Stopwatch.StartNew();
            await ProcessContent(tempDownloadPath, tempProcessedPath);
            processWatch.Stop();

            // Successfully downloaded & processed, move to final folders and updated last processed.
            var moveCommitWatch = System.Diagnostics.Stopwatch.StartNew();
            string repoOutDir = Path.Join(Program.Config.RepoDir, "Content");
            if (Directory.Exists(repoOutDir))
                Directory.Delete(repoOutDir, true);
            Directory.Move(tempProcessedPath, repoOutDir);

            if (Directory.Exists(Program.Config.ContentDir))
                Directory.Delete(Program.Config.ContentDir, true);
            Directory.Move(tempDownloadPath, Program.Config.ContentDir);

            await CommitToRepo(changeId);
            Logger.Info("Committed ChangeID {0}...", changeId);
            moveCommitWatch.Stop();

            await LocalConfig.SetAsync("lastProcessedChangeNumber", changeId.ToString());

            var timingsString = @$"
==========================================================
Timings:
    Download: {downloadWatch.ElapsedMilliseconds}ms
    Process: {processWatch.ElapsedMilliseconds}ms
    Move & Commit: {moveCommitWatch.ElapsedMilliseconds}ms
==========================================================
            ";

            Logger.Warn(timingsString);
        }
    }

    public async static void RunUpdates()
    {
        await DownloadSem.WaitAsync();
        await CheckUpdates();
        DownloadSem.Release();
    }
}