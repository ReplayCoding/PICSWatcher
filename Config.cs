namespace GameTracker;

class Config
{
    public static readonly uint AppToWatch = 232250;
    public static readonly string Branch = "public";

    public static readonly string DbConnectionString = "server=localhost;userid=gametracking;password=password;database=gametracking;";

    public static readonly string DataDir = "Data";

    public static readonly string ContentDir = Path.Join(DataDir, "Content");
    public static readonly string TempDir = Path.Join(DataDir, "Temp");

    public static void SetupDirs()
    {
        // Get rid of old temp data
        if (Path.Exists(TempDir))
            Directory.Delete(TempDir, true);

        Directory.CreateDirectory(TempDir);

        Directory.CreateDirectory(ContentDir);
    }
}