namespace DeskLink;

/// <summary>Scan code set 1 (o que o Windows entrega no hook) -> evdev/Linux keycode.</summary>
internal static class KeyMap
{
    private static readonly Dictionary<int, int> Map = new()
    {
        [0x01] = 1,   // esc
        [0x02] = 2, [0x03] = 3, [0x04] = 4, [0x05] = 5, [0x06] = 6, [0x07] = 7,
        [0x08] = 8, [0x09] = 9, [0x0A] = 10, [0x0B] = 11,   // 1..0
        [0x0C] = 12, [0x0D] = 13, [0x0E] = 14, [0x0F] = 15,  // - = backspace tab
        [0x10] = 16, [0x11] = 17, [0x12] = 18, [0x13] = 19, [0x14] = 20,
        [0x15] = 21, [0x16] = 22, [0x17] = 23, [0x18] = 24, [0x19] = 25,  // q..p
        [0x1A] = 26, [0x1B] = 27, [0x1C] = 28, [0x1D] = 29,               // [ ] enter lctrl
        [0x1E] = 30, [0x1F] = 31, [0x20] = 32, [0x21] = 33, [0x22] = 34,
        [0x23] = 35, [0x24] = 36, [0x25] = 37, [0x26] = 38,               // a..l
        [0x27] = 39, [0x28] = 40, [0x29] = 41, [0x2A] = 42, [0x2B] = 43, // ; ' ` lshift \
        [0x2C] = 44, [0x2D] = 45, [0x2E] = 46, [0x2F] = 47,
        [0x30] = 48, [0x31] = 49, [0x32] = 50,                           // z..m
        [0x33] = 51, [0x34] = 52, [0x35] = 53, [0x36] = 54, [0x37] = 55, // , . / rshift kp*
        [0x38] = 56, [0x39] = 57, [0x3A] = 58,                           // lalt space capslock
        [0x3B] = 59, [0x3C] = 60, [0x3D] = 61, [0x3E] = 62, [0x3F] = 63, // f1..f5
        [0x40] = 64, [0x41] = 65, [0x42] = 66, [0x43] = 67, [0x44] = 68, // f6..f10
        [0x45] = 69, [0x46] = 70,                                         // numlock scrolllock
        [0x47] = 71, [0x48] = 72, [0x49] = 73, [0x4A] = 74, [0x4B] = 75,
        [0x4C] = 76, [0x4D] = 77, [0x4E] = 78, [0x4F] = 79, [0x50] = 80,
        [0x51] = 81, [0x52] = 82, [0x53] = 83,
        [0x56] = 86,                                                      // 102nd (tecla <> ABNT2)
        [0x57] = 87, [0x58] = 88,                                         // f11 f12
    };

    private static readonly Dictionary<int, int> ExtMap = new()
    {
        [0x1C] = 96,   // kp enter
        [0x1D] = 97,   // rctrl
        [0x35] = 98,   // kp /
        [0x37] = 99,   // print screen
        [0x38] = 100,  // ralt
        [0x47] = 102, [0x48] = 103, [0x49] = 104,   // home up pgup
        [0x4B] = 105, [0x4D] = 106,                  // left right
        [0x4F] = 107, [0x50] = 108, [0x51] = 109,   // end down pgdn
        [0x52] = 110, [0x53] = 111,                  // insert delete
        [0x5B] = 125, [0x5C] = 126, [0x5D] = 127,   // lmeta rmeta compose
        [0x5E] = 127,
    };

    public static bool TryMap(uint scanCode, bool extended, out int evdev)
    {
        int sc = (int)scanCode & 0xFF;
        if (extended && ExtMap.TryGetValue(sc, out evdev))
            return true;
        return Map.TryGetValue(sc, out evdev);
    }
}
