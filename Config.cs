namespace GameTracker;

using System.Text.Json;

class Config
{
    public string Username { get; set; }
    public string Password { get; set; }

    public uint AppToWatch { get; set; }
    public string Branch { get; set; }

    public string DbConnectionString { get; set; }

    public string DataDir { get; set; }
    public string RepoDir { get; set; }

    public string ContentDir { get; set; }
    public string TempDir { get; set; }

    public uint MaxChunkRetries { get; set; }
    public uint MinRequiredCDNServers { get; set; }


    public Config()
    {
        Username = "anonymous";
        Password = "";

        AppToWatch = 232250;
        Branch = "public";

        DbConnectionString = "server=localhost;userid=gametracking;password=password;database=gametracking;";

        DataDir = "Data";
        RepoDir = "Repo";

        ContentDir = Path.Join(DataDir, "Content");
        TempDir = Path.Join(DataDir, "Temp");

        MaxChunkRetries = 3;
        MinRequiredCDNServers = 5;
    }

    public static Config LoadFromFile(string path)
    {
        var text = File.ReadAllText(path);
        Config? config = JsonSerializer.Deserialize<Config>(text);

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