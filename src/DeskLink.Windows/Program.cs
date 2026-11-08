using System.Text;
using System.Threading;

namespace DeskLink;

internal static class Program
{
    private static Mutex? _mutex;

    private static void Main()
    {
        // DPI: sem isso GetSystemMetrics (e as coordenadas) vem escalado
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

        _mutex = new Mutex(true, @"Global\DeskLink.SingleInstance", out bool created);
        if (!created)
            return;

        Config.Load();
        Log.Write($"=== DeskLink iniciando (pid {Environment.ProcessId}, usuario {Environment.UserName}) ===");

        var link = new LinkClient(Config.Current);
        var router = new InputRouter(Config.Current, link);
        var clip = new ClipboardSync(link);

        link.OnFrame += (type, payload) =>
        {
            if (type == Proto.Clipboard)
                clip.Apply(Encoding.UTF8.GetString(payload));
            else if (type == Proto.ClipboardImage)
                clip.ApplyImage(payload);
        };

        link.Start();
        router.Start();
        clip.Start();

        while (true)
        {
            int r = Native.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        Log.Write("loop de mensagens encerrado");
    }
}
