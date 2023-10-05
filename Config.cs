namespace GameTracker;

using System.Text.Json;
using System.Text.Json.Serialization;

class Config
{
    [JsonRequired]
    public string Username { get; set; }
    [JsonRequired]
    public string Password { get; set; }

    [JsonRequired]
    public uint AppToWatch { get; set; }
    [JsonRequired]
    public string Branch { get; set; }

    [JsonRequired]
    public IEnumerable<uint> DepotsToDownload { get; set; }

    [JsonRequired]
    public string DbConnectionString { get; set; }

    [JsonRequired]
    public string DataDir { get; set; }
    [JsonRequired]
    public string RepoDir { get; set; }

    [JsonRequired]
    public uint MaxChunkRetries { get; set; }
    [JsonRequired]
    public uint MinRequiredCDNServers { get; set; }

    [JsonRequired]
    public string Processor { get; set; }
    [JsonRequired]
    public string ProcessorArgs { get; set; }
    [JsonRequired]
    public string ProcessorWorkingDir { get; set; }

    [JsonRequired]
    public string GitUsername { get; set; }
    [JsonRequired]
    public string GitPassword { get; set; }
    [JsonRequired]
    public string GitBranch { get; set; }

    public string ContentDir { get; set; }
    public string TempDir { get; set; }

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