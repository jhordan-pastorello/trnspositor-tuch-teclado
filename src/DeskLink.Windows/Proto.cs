using System.Buffers.Binary;
using System.Text.Json;

namespace DeskLink;

internal static class Proto
{
    public const byte Hello = 1, HelloAck = 2, Pointer = 3, Button = 4, Wheel = 5, Key = 6, Leave = 7, Heartbeat = 8, Clipboard = 9, ClipboardImage = 10;

    private static byte[] Frame(byte type, int len)
    {
        var b = new byte[5 + len];
        b[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), (uint)len);
        return b;
    }

    public static byte[] PointerMsg(int x, int y)
    {
        var b = Frame(Pointer, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), x);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), y);
        return b;
    }

    public static byte[] ButtonMsg(byte btn, bool down)
    {
        var b = Frame(Button, 2);
        b[5] = btn;
        b[6] = down ? (byte)1 : (byte)0;
        return b;
    }

    public static byte[] WheelMsg(int dx, int dy)
    {
        var b = Frame(Wheel, 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), dx);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), dy);
        return b;
    }

    public static byte[] KeyMsg(int code, bool down)
    {
        var b = Frame(Key, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)code);
        b[7] = down ? (byte)1 : (byte)0;
        return b;
    }

    public static byte[] LeaveMsg() => Frame(Leave, 0);

    public static byte[] ClipboardMsg(string text)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        if (data.Length > 4_000_000) data = data.AsSpan(0, 4_000_000).ToArray();
        var b = Frame(Clipboard, data.Length);
        data.CopyTo(b, 5);
        return b;
    }

    public static byte[] ClipboardImageMsg(byte[] png)
    {
        if (png.Length > 20_000_000) png = png.AsSpan(0, 20_000_000).ToArray();
        var b = Frame(ClipboardImage, png.Length);
        png.CopyTo(b, 5);
        return b;
    }

    public static byte[] HeartbeatMsg() => Frame(Heartbeat, 0);

    public static byte[] HelloMsg(string name, int w, int h)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, name, w, h });
        var b = Frame(Hello, json.Length);
        json.CopyTo(b, 5);
        return b;
    }
}
