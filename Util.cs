namespace GameTracker;

using System.IO;

class Util
{
    public static string GetNewTempDir(string prefix)
    {
        // TOCTOU, who cares :)
        while (true)
        {
            var path = Path.Combine(Program.Config.TempDir, prefix + "-" + Path.GetRandomFileName());
            if (!Path.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}