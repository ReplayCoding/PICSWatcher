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

    // copied from depotdownloader
    public static byte[] AdlerHash(byte[] input)
    {
        uint a = 0, b = 0;
        for (var i = 0; i < input.Length; i++)
        {
            a = (a + input[i]) % 65521;
            b = (b + a) % 65521;
        }

        return BitConverter.GetBytes(a | (b << 16));
    }
}