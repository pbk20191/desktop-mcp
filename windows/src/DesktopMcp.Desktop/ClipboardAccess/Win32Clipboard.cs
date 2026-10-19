using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;

namespace DesktopMcp.Desktop.ClipboardAccess;

/// <summary>The Win32 clipboard with CF_UNICODETEXT, as upstream's pywin32 code uses it (OpenClipboard with no owner).</summary>
internal static unsafe class Win32Clipboard
{
    private const uint UnicodeText = (uint)CLIPBOARD_FORMAT.CF_UNICODETEXT;

    public static string? GetText()
    {
        Open();
        try
        {
            if (!PInvoke.IsClipboardFormatAvailable(UnicodeText)) return null;
            var handle = PInvoke.GetClipboardData(UnicodeText);
            if (handle.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var memory = (HGLOBAL)(nint)handle;
            var pointer = PInvoke.GlobalLock(memory);
            if (pointer is null) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                // Another application wrote this block: read no further than its size, up to the first NUL.
                var size = PInvoke.GlobalSize(memory);
                if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                var text = new ReadOnlySpan<char>(pointer, (int)Math.Min(size / sizeof(char), int.MaxValue));
                var end = text.IndexOf('\0');
                return new string(end < 0 ? text : text[..end]);
            }
            finally { PInvoke.GlobalUnlock(memory); }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    public static void SetText(string text)
    {
        Open();
        try
        {
            if (!PInvoke.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);
            if (memory.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var pointer = (char*)PInvoke.GlobalLock(memory);
            if (pointer is null)
            {
                var error = Marshal.GetLastPInvokeError();
                PInvoke.GlobalFree(memory);
                throw new Win32Exception(error);
            }
            text.AsSpan().CopyTo(new Span<char>(pointer, text.Length));
            pointer[text.Length] = '\0';
            PInvoke.GlobalUnlock(memory);
            // On success the system owns the memory; on failure it is still ours to free.
            if (PInvoke.SetClipboardData(UnicodeText, (HANDLE)(nint)memory).IsNull)
            {
                var error = Marshal.GetLastPInvokeError();
                PInvoke.GlobalFree(memory);
                throw new Win32Exception(error);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Empties the clipboard (tests and the probe restore an empty clipboard with this).</summary>
    public static void Clear()
    {
        Open();
        try
        {
            if (!PInvoke.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    // Another application may hold the clipboard for a moment: retry for about 200 ms.
    private static void Open()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (PInvoke.OpenClipboard(HWND.Null)) return;
            if (attempt == 9) throw new Win32Exception(Marshal.GetLastPInvokeError());
            Thread.Sleep(20);
        }
    }
}
