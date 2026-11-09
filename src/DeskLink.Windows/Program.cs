using System.Text;
using System.Threading;

namespace DeskLink;

internal static class Program
{
    private static Mutex? _mutex;

    // Referencias estaticas: sem isso o GC pode coletar o InputRouter (e os
    // delegates dos hooks) enquanto o loop roda -> "callback made on a garbage
    // collected delegate" e fail-fast.
    private static LinkClient? _link;
    private static InputRouter? _router;
    private static ClipboardSync? _clip;

    private static void Main()
    {
        // DPI: sem isso GetSystemMetrics (e as coordenadas) vem escalado
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

        _mutex = new Mutex(true, @"Global\DeskLink.SingleInstance", out bool created);
        if (!created)
            return;

        Config.Load();
        Log.Write($"=== DeskLink iniciando (pid {Environment.ProcessId}, usuario {Environment.UserName}) ===");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("FATAL nao tratado: " + e.ExceptionObject);

        _link = new LinkClient(Config.Current);
        _router = new InputRouter(Config.Current, _link);
        _clip = new ClipboardSync(_link);

        _link.OnFrame += (type, payload) =>
        {
            if (type == Proto.Clipboard)
                _clip.Apply(Encoding.UTF8.GetString(payload));
            else if (type == Proto.ClipboardImage)
                _clip.ApplyImage(payload);
        };

        _link.Start();
        _router.Start();
        _clip.Start();

        while (true)
        {
            int r = Native.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }

        GC.KeepAlive(_router);
        Log.Write("loop de mensagens encerrado");
    }
}
