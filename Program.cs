namespace GameTracker;

using System;
using System.CommandLine;

class Program
{
    public static Config Config;

    private static int Main(string[] args)
    {
        var configOption = new Option<FileInfo>(name: "--config", description: "the configuration file to use");
        var resetLastProcessedToZero = new Option<bool>(name: "--reset-last-processed-to-zero", description: "this resets the stored last processed change id to zero, forcing a redownload of every change in the db");

        var rootCommand = new RootCommand("PICS Watcher");
        rootCommand.AddOption(configOption);
        rootCommand.AddOption(resetLastProcessedToZero);

        rootCommand.SetHandler((configFile, resetLastProcessedToZero) =>
        {
            Config = Config.LoadFromFile(configFile.OpenRead());
            Console.WriteLine($"Watching app {Config.AppToWatch} with user {Config.Username}");

            // Setup content & temporary dirs
            Config.SetupDirs();

            if (resetLastProcessedToZero)
                LocalConfig.Set("lastProcessedChangeNumber", "0");

            Console.WriteLine("Connecting...");
            SteamSession.Instance.Run();
            Console.WriteLine("Done");

        }, configOption, resetLastProcessedToZero);

        return rootCommand.Invoke(args);
    }
}