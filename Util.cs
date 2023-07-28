namespace GameTracker;

using System.IO;

class Util {
    public static string GetNewTempDir() {
        var systemTempDir = Path.GetTempPath();

        // TOCTOU, who cares :)
        while (true) {
            var path = Path.Combine(systemTempDir, "gametracking-" + Path.GetRandomFileName());
            if (!Path.Exists(path)) {
                Directory.CreateDirectory(path);
                return path;
            }
        }
    }
}