using System.Text;

namespace DeskLink;

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Config.Dir, "desklink.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Config.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    File.Delete(FilePath + ".old");
                    File.Move(FilePath, FilePath + ".old");
                }
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging nunca deve derrubar o processo
        }
    }
}
