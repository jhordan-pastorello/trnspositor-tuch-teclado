namespace DeskLink;

/// <summary>
/// Janela invisivel de 2x2 px usada como "estacionamento" do cursor enquanto o
/// controle esta no Linux. Ela responde WM_SETCURSOR com um cursor nulo, o que
/// faz o ponteiro do Windows desaparecer sem alterar o cursor do sistema.
/// </summary>
internal sealed class HiddenCursor : IDisposable
{
    private const string ClassName = "DeskLinkHiddenCursor";
    private readonly Native.WndProc _proc;
    private readonly IntPtr _hwnd;

    public HiddenCursor(int x, int y)
    {
        _proc = WindowProc;
        var hInstance = Native.GetModuleHandle(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = ClassName,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero
        };
        Native.RegisterClassEx(ref wc);

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT,
            ClassName, "", Native.WS_POPUP, x, y, 2, 2,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_SETCURSOR)
        {
            Native.SetCursor(IntPtr.Zero);
            return (IntPtr)1;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Show()
    {
        Native.ShowWindow(_hwnd, Native.SW_SHOWNA);
        Native.SetCursor(IntPtr.Zero);
    }

    public void Hide() => Native.ShowWindow(_hwnd, Native.SW_HIDE);

    public void Dispose() => Hide();
}
