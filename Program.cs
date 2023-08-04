namespace GameTracker;

using System;

class Program
{
    public static Config Config;

    private static void Main()
    {
        Config = Config.LoadFromFile("Config.json");
        Console.WriteLine($"Watching app {Config.AppToWatch} with user {Config.Username}");

        // Setup content & temporary dirs
        Config.SetupDirs();

        LocalConfig.Set("lastProcessedChangeNumber", "0");

        Console.WriteLine("Connecting...");
        SteamSession.Instance.Run();
        Console.WriteLine("Done");
    }
}