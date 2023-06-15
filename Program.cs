namespace GameTracker;

using System;

class Program
{
    private static void Main()
    {
        Console.WriteLine("Connecting...");

        SteamSession.Instance.Run();
    }
}