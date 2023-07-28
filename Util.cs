namespace GameTracker;

using System.IO;

class Util
{
    public static string GetNewTempDir()
    {
        // TOCTOU, who cares :)
        while (true)
        {
            var path = Path.Combine(Config.TempDir, "gametracking-" + Path.GetRandomFileName());
            if (!Path.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}