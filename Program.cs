namespace GameTracker;

using System;

class Program
{
    private static void Main()
    {
        // Setup content & temporary dirs
        Config.SetupDirs();

        LocalConfig.Set("lastProcessedChangeNumber", "0");

        Console.WriteLine("Connecting...");
        SteamSession.Instance.Run();
        Console.WriteLine("Done");
    }
}