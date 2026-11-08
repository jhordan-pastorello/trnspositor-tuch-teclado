using System.Runtime.InteropServices;

namespace DeskLink;

/// <summary>
/// Janela invisivel que recebe WM_INPUT (raw input). Os deltas vem do
/// dispositivo, nao da posicao do cursor: funcionam durante arrasto/captura,
/// quando SetCursorPos e ignorado pelo Windows.
/// </summary>
internal sealed class RawInputWindow : IDisposable
{
    private const string ClassName = "DeskLinkRawInput";
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Native.WndProc _proc;
    private readonly IntPtr _hwnd;

    public event Action<int, int>? MouseMoved;

    public RawInputWindow()
    {
        _proc = WndProc;
        var hInstance = Native.GetModuleHandle(null);
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = ClassName
        };
        Native.RegisterClassEx(ref wc);

        _hwnd = Native.CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        var rid = new Native.RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = _hwnd
        };
        Native.RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { Handle(lParam); } catch { }
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Handle(IntPtr lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        Native.GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(lParam, RID_INPUT, buf, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
            if (header.dwType != 0) return;

            var mouse = Marshal.PtrToStructure<Native.RAWMOUSE>(buf + (int)headerSize);
            if ((mouse.usFlags & 0x01) != 0) return; // MOUSE_MOVE_ABSOLUTE
            if (mouse.lLastX == 0 && mouse.lLastY == 0) return;

            MouseMoved?.Invoke(mouse.lLastX, mouse.lLastY);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose() { }
}
