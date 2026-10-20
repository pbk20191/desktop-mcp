using System.Collections.Frozen;

namespace DesktopMcp.Core.Input;

/// <summary>
/// The key tables the SendKeys port needs, as pure data. Scan codes and the extended flag are computed from these
/// in <c>DesktopMcp.Desktop</c> when the events are built.
/// </summary>
public static class KeyTables
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkReturn = 0x0D;
    public const ushort VkTab = 0x09;
    public const ushort VkBack = 0x08;
    public const ushort VkHome = 0x24;
    public const ushort VkEnd = 0x23;
    public const ushort VkA = 0x41;
    public const ushort VkV = 0x56;

    /// <summary>uia/enums.py:1399-1548. Keys are upper-case.</summary>
    public static IReadOnlyDictionary<string, ushort> SpecialKeyNames { get; } = new Dictionary<string, ushort>
    {
        ["LBUTTON"] = 0x01,
        ["RBUTTON"] = 0x02,
        ["CANCEL"] = 0x03,
        ["MBUTTON"] = 0x04,
        ["XBUTTON1"] = 0x05,
        ["XBUTTON2"] = 0x06,
        ["BACK"] = 0x08,
        ["TAB"] = 0x09,
        ["CLEAR"] = 0x0C,
        ["RETURN"] = 0x0D,
        ["ENTER"] = 0x0D,
        ["SHIFT"] = 0x10,
        ["CTRL"] = 0x11,
        ["CONTROL"] = 0x11,
        ["ALT"] = 0x12,
        ["PAUSE"] = 0x13,
        ["CAPITAL"] = 0x14,
        ["KANA"] = 0x15,
        ["HANGUEL"] = 0x15,
        ["HANGUL"] = 0x15,
        ["JUNJA"] = 0x17,
        ["FINAL"] = 0x18,
        ["HANJA"] = 0x19,
        ["KANJI"] = 0x19,
        ["ESC"] = 0x1B,
        ["ESCAPE"] = 0x1B,
        ["CONVERT"] = 0x1C,
        ["NONCONVERT"] = 0x1D,
        ["ACCEPT"] = 0x1E,
        ["MODECHANGE"] = 0x1F,
        ["SPACE"] = 0x20,
        ["PRIOR"] = 0x21,
        ["PAGEUP"] = 0x21,
        ["NEXT"] = 0x22,
        ["PAGEDOWN"] = 0x22,
        ["END"] = 0x23,
        ["HOME"] = 0x24,
        ["LEFT"] = 0x25,
        ["UP"] = 0x26,
        ["RIGHT"] = 0x27,
        ["DOWN"] = 0x28,
        ["SELECT"] = 0x29,
        ["PRINT"] = 0x2A,
        ["EXECUTE"] = 0x2B,
        ["SNAPSHOT"] = 0x2C,
        ["PRINTSCREEN"] = 0x2C,
        ["INSERT"] = 0x2D,
        ["INS"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["DEL"] = 0x2E,
        ["HELP"] = 0x2F,
        ["WIN"] = 0x5B,
        ["LWIN"] = 0x5B,
        ["RWIN"] = 0x5C,
        ["APPS"] = 0x5D,
        ["SLEEP"] = 0x5F,
        ["NUMPAD0"] = 0x60,
        ["NUMPAD1"] = 0x61,
        ["NUMPAD2"] = 0x62,
        ["NUMPAD3"] = 0x63,
        ["NUMPAD4"] = 0x64,
        ["NUMPAD5"] = 0x65,
        ["NUMPAD6"] = 0x66,
        ["NUMPAD7"] = 0x67,
        ["NUMPAD8"] = 0x68,
        ["NUMPAD9"] = 0x69,
        ["MULTIPLY"] = 0x6A,
        ["ADD"] = 0x6B,
        ["SEPARATOR"] = 0x6C,
        ["SUBTRACT"] = 0x6D,
        ["DECIMAL"] = 0x6E,
        ["DIVIDE"] = 0x6F,
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["F13"] = 0x7C,
        ["F14"] = 0x7D,
        ["F15"] = 0x7E,
        ["F16"] = 0x7F,
        ["F17"] = 0x80,
        ["F18"] = 0x81,
        ["F19"] = 0x82,
        ["F20"] = 0x83,
        ["F21"] = 0x84,
        ["F22"] = 0x85,
        ["F23"] = 0x86,
        ["F24"] = 0x87,
        ["NUMLOCK"] = 0x90,
        ["SCROLL"] = 0x91,
        ["LSHIFT"] = 0xA0,
        ["RSHIFT"] = 0xA1,
        ["LCONTROL"] = 0xA2,
        ["LCTRL"] = 0xA2,
        ["RCONTROL"] = 0xA3,
        ["RCTRL"] = 0xA3,
        ["LALT"] = 0xA4,
        ["RALT"] = 0xA5,
        ["BROWSER_BACK"] = 0xA6,
        ["BROWSER_FORWARD"] = 0xA7,
        ["BROWSER_REFRESH"] = 0xA8,
        ["BROWSER_STOP"] = 0xA9,
        ["BROWSER_SEARCH"] = 0xAA,
        ["BROWSER_FAVORITES"] = 0xAB,
        ["BROWSER_HOME"] = 0xAC,
        ["VOLUME_MUTE"] = 0xAD,
        ["VOLUME_DOWN"] = 0xAE,
        ["VOLUME_UP"] = 0xAF,
        ["MEDIA_NEXT_TRACK"] = 0xB0,
        ["MEDIA_PREV_TRACK"] = 0xB1,
        ["MEDIA_STOP"] = 0xB2,
        ["MEDIA_PLAY_PAUSE"] = 0xB3,
        ["LAUNCH_MAIL"] = 0xB4,
        ["LAUNCH_MEDIA_SELECT"] = 0xB5,
        ["LAUNCH_APP1"] = 0xB6,
        ["LAUNCH_APP2"] = 0xB7,
        ["OEM_1"] = 0xBA,
        ["OEM_PLUS"] = 0xBB,
        ["OEM_COMMA"] = 0xBC,
        ["OEM_MINUS"] = 0xBD,
        ["OEM_PERIOD"] = 0xBE,
        ["OEM_2"] = 0xBF,
        ["OEM_3"] = 0xC0,
        ["OEM_4"] = 0xDB,
        ["OEM_5"] = 0xDC,
        ["OEM_6"] = 0xDD,
        ["OEM_7"] = 0xDE,
        ["OEM_8"] = 0xDF,
        ["OEM_102"] = 0xE2,
        ["PROCESSKEY"] = 0xE5,
        ["PACKET"] = 0xE7,
        ["ATTN"] = 0xF6,
        ["CRSEL"] = 0xF7,
        ["EXSEL"] = 0xF8,
        ["EREOF"] = 0xF9,
        ["PLAY"] = 0xFA,
        ["ZOOM"] = 0xFB,
        ["NONAME"] = 0xFC,
        ["PA1"] = 0xFD,
        ["OEM_CLEAR"] = 0xFE,
    }.ToFrozenDictionary();

    /// <summary>uia/enums.py:1551-1637.</summary>
    public static IReadOnlyDictionary<char, ushort> CharacterCodes { get; } = new Dictionary<char, ushort>
    {
        ['0'] = 0x30,
        ['1'] = 0x31,
        ['2'] = 0x32,
        ['3'] = 0x33,
        ['4'] = 0x34,
        ['5'] = 0x35,
        ['6'] = 0x36,
        ['7'] = 0x37,
        ['8'] = 0x38,
        ['9'] = 0x39,
        ['a'] = 0x41,
        ['A'] = 0x41,
        ['b'] = 0x42,
        ['B'] = 0x42,
        ['c'] = 0x43,
        ['C'] = 0x43,
        ['d'] = 0x44,
        ['D'] = 0x44,
        ['e'] = 0x45,
        ['E'] = 0x45,
        ['f'] = 0x46,
        ['F'] = 0x46,
        ['g'] = 0x47,
        ['G'] = 0x47,
        ['h'] = 0x48,
        ['H'] = 0x48,
        ['i'] = 0x49,
        ['I'] = 0x49,
        ['j'] = 0x4A,
        ['J'] = 0x4A,
        ['k'] = 0x4B,
        ['K'] = 0x4B,
        ['l'] = 0x4C,
        ['L'] = 0x4C,
        ['m'] = 0x4D,
        ['M'] = 0x4D,
        ['n'] = 0x4E,
        ['N'] = 0x4E,
        ['o'] = 0x4F,
        ['O'] = 0x4F,
        ['p'] = 0x50,
        ['P'] = 0x50,
        ['q'] = 0x51,
        ['Q'] = 0x51,
        ['r'] = 0x52,
        ['R'] = 0x52,
        ['s'] = 0x53,
        ['S'] = 0x53,
        ['t'] = 0x54,
        ['T'] = 0x54,
        ['u'] = 0x55,
        ['U'] = 0x55,
        ['v'] = 0x56,
        ['V'] = 0x56,
        ['w'] = 0x57,
        ['W'] = 0x57,
        ['x'] = 0x58,
        ['X'] = 0x58,
        ['y'] = 0x59,
        ['Y'] = 0x59,
        ['z'] = 0x5A,
        ['Z'] = 0x5A,
        [' '] = 0x20,
        ['`'] = 0xC0,
        ['-'] = 0xBD,
        ['='] = 0xBB,
        ['['] = 0xDB,
        [']'] = 0xDD,
        ['\\'] = 0xDC,
        [';'] = 0xBA,
        ['\''] = 0xDE,
        [','] = 0xBC,
        ['.'] = 0xBE,
        ['/'] = 0xBF,
    }.ToFrozenDictionary();

    /// <summary>desktop/service.py:47-54: shortcut key names that differ from the SendKeys names. Keys are lower-case.</summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } = new Dictionary<string, string>
    {
        ["backspace"] = "Back",
        ["capslock"] = "Capital",
        ["scrolllock"] = "Scroll",
        ["windows"] = "Win",
        ["command"] = "Win",
        ["option"] = "Alt",
    }.ToFrozenDictionary();

    /// <summary>uia/core.py:1516-1532: the keys SendKeys holds down around the key that follows (upper-case names).</summary>
    public static IReadOnlySet<string> HoldKeys { get; } = new[]
    {
        "WIN", "LWIN", "RWIN", "SHIFT", "LSHIFT", "RSHIFT", "CTRL", "CONTROL", "LCTRL", "RCTRL", "LCONTROL",
        "ALT", "LALT", "RALT",
    }.ToFrozenSet();

    /// <summary>uia/core.py:1434-1459: virtual keys whose scan code is fixed (bit 0x100 is the extended flag).</summary>
    public static IReadOnlyDictionary<ushort, ushort> FixedScanCodes { get; } = new Dictionary<ushort, ushort>
    {
        [0xA0] = 0x02A, // VK_LSHIFT
        [0xA1] = 0x136, // VK_RSHIFT
        [0xA2] = 0x01D, // VK_LCONTROL
        [0xA3] = 0x11D, // VK_RCONTROL
        [0xA4] = 0x038, // VK_LMENU
        [0xA5] = 0x138, // VK_RMENU
        [0x5B] = 0x15B, // VK_LWIN
        [0x5C] = 0x15C, // VK_RWIN
        [0x60] = 0x52, // VK_NUMPAD0
        [0x61] = 0x4F, // VK_NUMPAD1
        [0x62] = 0x50, // VK_NUMPAD2
        [0x63] = 0x51, // VK_NUMPAD3
        [0x64] = 0x4B, // VK_NUMPAD4
        [0x65] = 0x4C, // VK_NUMPAD5
        [0x66] = 0x4D, // VK_NUMPAD6
        [0x67] = 0x47, // VK_NUMPAD7
        [0x68] = 0x48, // VK_NUMPAD8
        [0x69] = 0x49, // VK_NUMPAD9
        [0x6E] = 0x53, // VK_DECIMAL
        [0x90] = 0x145, // VK_NUMLOCK
        [0x6F] = 0x135, // VK_DIVIDE
        [0x6A] = 0x037, // VK_MULTIPLY
        [0x6D] = 0x04A, // VK_SUBTRACT
        [0x6B] = 0x04E, // VK_ADD
    }.ToFrozenDictionary();

    /// <summary>uia/core.py:1473-1481: keys that get the 0x100 flag on top of their MapVirtualKey scan code.</summary>
    public static IReadOnlySet<ushort> ScanExtendedKeys { get; } = new ushort[]
    {
        0x5D, // VK_APPS
        0x03, // VK_CANCEL
        0x2C, // VK_SNAPSHOT
        0x6F, // VK_DIVIDE
        0x90, // VK_NUMLOCK
    }.ToFrozenSet();

    /// <summary>Keys sent with KEYEVENTF_EXTENDEDKEY.</summary>
    public static IReadOnlySet<ushort> ExtendedKeys { get; } = new ushort[]
    {
        0xA5, 0xA3, // VK_RMENU, VK_RCONTROL
        0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, // INSERT, DELETE, HOME, END, PRIOR, NEXT
        0x25, 0x26, 0x27, 0x28, // LEFT, UP, RIGHT, DOWN
        0x90, 0x2C, 0x6F, // NUMLOCK, SNAPSHOT, DIVIDE
        0x5B, 0x5C, 0x5D, 0x03, // LWIN, RWIN, APPS, CANCEL
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, // browser keys
        0xAD, 0xAE, 0xAF, // volume
        0xB0, 0xB1, 0xB2, 0xB3, // media
        0xB4, 0xB5, 0xB6, 0xB7, // launch
    }.ToFrozenSet();
}
