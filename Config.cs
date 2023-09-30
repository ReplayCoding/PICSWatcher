namespace GameTracker;

using System.Text.Json;

class Config
{
    public string Username { get; set; }
    public string Password { get; set; }

    public uint AppToWatch { get; set; }
    public string Branch { get; set; }

    public IEnumerable<uint> DepotsToDownload { get; set; }

    public string DbConnectionString { get; set; }

    public string DataDir { get; set; }
    public string RepoDir { get; set; }

    public string ContentDir { get; set; }
    public string TempDir { get; set; }

    public uint MaxChunkRetries { get; set; }
    public uint MinRequiredCDNServers { get; set; }

    public string Processor { get; set; }
    public string ProcessorArgs { get; set; }
    public string ProcessorWorkingDir { get; set; }

    public string GitUsername { get; set; }
    public string GitPassword { get; set; }
    public string GitBranch { get; set; }

    public static Config LoadFromFile(FileStream file)
    {
        Config? config = JsonSerializer.Deserialize<Config>(file);

        if (config == null)
            throw new Exception("Failed to decode config");

        if (config.ContentDir == null)
            config.ContentDir = Path.Join(config.DataDir, "Content");
        if (config.TempDir == null)
            config.TempDir = Path.Join(config.DataDir, "Temp");

        return config;
    }

    public void SetupDirs()
    {
        // Get rid of old temp data
        if (Path.Exists(TempDir))
            Directory.Delete(TempDir, true);

        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(ContentDir);
    }
}