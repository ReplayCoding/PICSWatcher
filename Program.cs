namespace GameTracker;

using System;

class Program
{
    private static void Main()
    {
        // Setup content & temporary dirs
        Config.SetupDirs();

        Console.WriteLine("Connecting...");
        SteamSession.Instance.Run();
    }
}