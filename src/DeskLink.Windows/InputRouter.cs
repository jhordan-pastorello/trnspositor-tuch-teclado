using System.Runtime.InteropServices;

namespace DeskLink;

internal sealed class InputRouter
{
    private readonly Config _cfg;
    private readonly LinkClient _link;
    private readonly Native.HookProc _mouseProc;
    private readonly Native.HookProc _keyProc;

    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private HiddenCursor? _hidden;
    private RawInputWindow? _raw;
    private int _screenW;
    private int _screenH;

    private const int ParkX = 8;
    private const int ParkY = 8;

    private bool _remote;
    private bool _armed = true;
    private int _vx;
    private int _vy;
    private DateTime _edgeSince = DateTime.MinValue;
    private DateTime _exitSince = DateTime.MinValue;

    public InputRouter(Config cfg, LinkClient link)
    {
        _cfg = cfg;
        _link = link;
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
    }

    private string Edge => (_cfg.Edge ?? "top").Trim().ToLowerInvariant();

    public void Start()
    {
        _screenW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        _screenH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        _hidden = new HiddenCursor(ParkX, ParkY);
        _raw = new RawInputWindow();
        _raw.MouseMoved += OnRawMove;

        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, Native.GetModuleHandle(null), 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, Native.GetModuleHandle(null), 0);

        Log.Write($"hooks instalados (mouse={_mouseHook}, teclado={_keyHook}, tela={_screenW}x{_screenH}, borda={Edge})");
    }

    // ------------------------------------------------------------- hooks ----
    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if (HandleMouse((int)wParam, info.pt, info.mouseData))
                    return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            Log.Write("hook mouse: " + ex);
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if (HandleKey((int)wParam, info))
                    return (IntPtr)1;
            }
        }
        catch (Exception ex)
        {
            Log.Write("hook teclado: " + ex);
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    // ------------------------------------------------------------- mouse ----
    private bool HandleMouse(int msg, Native.POINT pt, int data)
    {
        if (!_cfg.Enabled || !_link.IsConnected)
        {
            if (_remote) ExitRemote();
            return false;
        }

        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                return OnMove(pt);

            case Native.WM_LBUTTONDOWN: return SendButton(1, true);
            case Native.WM_LBUTTONUP: return SendButton(1, false);
            case Native.WM_MBUTTONDOWN: return SendButton(2, true);
            case Native.WM_MBUTTONUP: return SendButton(2, false);
            case Native.WM_RBUTTONDOWN: return SendButton(3, true);
            case Native.WM_RBUTTONUP: return SendButton(3, false);
            case Native.WM_XBUTTONDOWN: return SendButton((short)(data >> 16) == 1 ? (byte)4 : (byte)5, true);
            case Native.WM_XBUTTONUP: return SendButton((short)(data >> 16) == 1 ? (byte)4 : (byte)5, false);
            case Native.WM_MOUSEWHEEL: return SendWheel(0, (short)(data >> 16));
            case Native.WM_MOUSEHWHEEL: return SendWheel((short)(data >> 16), 0);
            default:
                return false;
        }
    }

    private bool OnEdge(Native.POINT pt)
    {
        return Edge switch
        {
            "bottom" => pt.Y >= _screenH - 2,
            "left" => pt.X <= 1,
            "right" => pt.X >= _screenW - 2,
            _ => pt.Y <= 1
        };
    }

    private bool OnMove(Native.POINT pt)
    {
        if (!_remote)
        {
            if (OnEdge(pt))
            {
                if (!_armed) return false;
                if (_edgeSince == DateTime.MinValue)
                {
                    _edgeSince = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - _edgeSince).TotalMilliseconds >= _cfg.SwitchDelayMs)
                {
                    EnterRemote(pt);
                }
            }
            else
            {
                _edgeSince = DateTime.MinValue;
                _armed = true;
            }
            return false;
        }

        // o movimento remoto vem do Raw Input (deltas do dispositivo);
        // aqui so mantemos o cursor local parado e escondido.
        Native.SetCursorPos(ParkX, ParkY);
        return true;
    }

    private void OnRawMove(int dx, int dy)
    {
        if (!_remote || !_cfg.Enabled || !_link.IsConnected) return;

        int rw = Math.Max(1, _link.RemoteWidth);
        int rh = Math.Max(1, _link.RemoteHeight);

        double sx = (double)rw / Math.Max(1, _screenW) * _cfg.Speed;
        double sy = (double)rh / Math.Max(1, _screenH) * _cfg.Speed;

        int nx = _vx + (int)Math.Round(dx * sx);
        int ny = _vy + (int)Math.Round(dy * sy);

        bool exit = Edge switch
        {
            "bottom" => ny < 0,
            "left" => nx >= rw,
            "right" => nx < 0,
            _ => ny >= rh
        };

        if (exit)
        {
            if (_exitSince == DateTime.MinValue)
                _exitSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _exitSince).TotalMilliseconds >= 180)
            {
                ExitRemote();
                return;
            }
        }
        else
        {
            _exitSince = DateTime.MinValue;
        }

        _vx = Math.Clamp(nx, 0, rw - 1);
        _vy = Math.Clamp(ny, 0, rh - 1);
        _link.Send(Proto.PointerMsg(_vx, _vy));
    }

    private bool SendButton(byte btn, bool down)
    {
        if (!_remote) return false;
        _link.Send(Proto.ButtonMsg(btn, down));
        Log.Write($"botao {btn} {(down ? "down" : "up")} em ({_vx},{_vy})");
        return true;
    }

    private bool SendWheel(int dx, int dy)
    {
        if (!_remote) return false;
        _link.Send(Proto.WheelMsg(dx, dy));
        return true;
    }

    // ---------------------------------------------------------- teclado -----
    private bool HandleKey(int msg, Native.KBDLLHOOKSTRUCT k)
    {
        bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;

        if (_cfg.ToggleHotkeyEnabled && k.vkCode == 0x20 && CtrlAltDown())
        {
            if (down)
            {
                if (_remote) ExitRemote();
                else if (_link.IsConnected) EnterRemote(new Native.POINT { X = _screenW / 2, Y = _screenH / 2 });
            }
            return true;
        }

        if (!_remote || !_link.IsConnected) return false;

        bool extended = (k.flags & 0x01) != 0;
        if (!KeyMap.TryMap(k.scanCode, extended, out int code)) return false;

        _link.Send(Proto.KeyMsg(code, down));
        return true;
    }

    private static bool CtrlAltDown() =>
        (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;

    // ------------------------------------------------------------ estado ----
    private void EnterRemote(Native.POINT at)
    {
        if (!_link.IsConnected || _remote) return;

        int rw = Math.Max(1, _link.RemoteWidth);
        int rh = Math.Max(1, _link.RemoteHeight);

        // ponto de entrada: mesma proporcao do eixo compartilhado
        double relX = (double)at.X / Math.Max(1, _screenW);
        double relY = (double)at.Y / Math.Max(1, _screenH);

        _remote = true;
        _edgeSince = DateTime.MinValue;
        _exitSince = DateTime.MinValue;

        switch (Edge)
        {
            case "bottom":
                _vx = Math.Clamp((int)(relX * rw), 0, rw - 1);
                _vy = 0;
                break;
            case "left":
                _vx = rw - 1;
                _vy = Math.Clamp((int)(relY * rh), 0, rh - 1);
                break;
            case "right":
                _vx = 0;
                _vy = Math.Clamp((int)(relY * rh), 0, rh - 1);
                break;
            default: // top
                _vx = Math.Clamp((int)(relX * rw), 0, rw - 1);
                _vy = rh - 1;
                break;
        }

        _hidden?.Show();
        var clip = new Native.RECT { Left = ParkX, Top = ParkY, Right = ParkX + 1, Bottom = ParkY + 1 };
        Native.ClipCursor(ref clip);
        Native.SetCursorPos(ParkX, ParkY);
        _link.Send(Proto.PointerMsg(_vx, _vy));
        Log.Write($"entrou no Linux local=({at.X},{at.Y}) -> remoto=({_vx},{_vy}) borda={Edge}");
    }

    private void ExitRemote()
    {
        if (!_remote) return;
        _remote = false;
        _edgeSince = DateTime.MinValue;
        _exitSince = DateTime.MinValue;

        _link.Send(Proto.LeaveMsg());
        Native.ClipCursor(IntPtr.Zero);
        _hidden?.Hide();

        int rw = Math.Max(1, _link.RemoteWidth);
        int rh = Math.Max(1, _link.RemoteHeight);

        int lx = Math.Clamp((int)((double)_vx / rw * _screenW), 0, _screenW - 1);
        int ly = Math.Clamp((int)((double)_vy / rh * _screenH), 0, _screenH - 1);

        (int x, int y) back = Edge switch
        {
            "bottom" => (lx, _screenH - 3),
            "left" => (_screenW - 3, ly),
            "right" => (2, ly),
            _ => (lx, 3)
        };

        Native.SetCursorPos(back.x, back.y);
        _armed = false;
        Log.Write($"voltou ao Windows local=({back.x},{back.y}) vindo de=({_vx},{_vy})");
    }
}
