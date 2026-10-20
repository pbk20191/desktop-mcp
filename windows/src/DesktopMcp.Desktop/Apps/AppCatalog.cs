using DesktopMcp.Desktop.Errors;
using DesktopMcp.Desktop.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace DesktopMcp.Desktop.Apps;

/// <summary>One Start menu app: its display name and its AppsFolder parsing name (an AppUserModelID or a path).</summary>
internal sealed record AppEntry(string Name, string ParsingName);

/// <summary>The Start menu apps (P3 Decisions 31; upstream desktop/service.py:330-373 get_apps_from_start_menu).</summary>
internal interface IAppCatalog
{
    Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct);
}

/// <summary>
/// The AppsFolder (<c>shell:AppsFolder</c>), the folder behind upstream's <c>Get-StartApps</c> and its AppsFolder display
/// names: every item's normal display name and parsing name, read on a new STA thread. The enumeration can block on
/// shell extensions, so the wait is bounded at 10 s; past it the thread is abandoned (Global Constraints).
/// </summary>
internal sealed class AppCatalog : IAppCatalog
{
    /// <summary>How long the enumeration may take.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Operation = "Listing the Start menu apps";

    private readonly Func<IReadOnlyList<AppEntry>> _read;
    private readonly TimeProvider _time;

    public AppCatalog()
        : this(ReadAppsFolder, TimeProvider.System)
    {
    }

    /// <param name="read">Reads the entries; runs on the STA thread.</param>
    internal AppCatalog(Func<IReadOnlyList<AppEntry>> read, TimeProvider time)
    {
        _read = read;
        _time = time;
    }

    public async Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct)
    {
        try
        {
            return await StaThread.RunAsync(_read, ct).WaitAsync(Limit, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DesktopException($"{Operation} failed: it did not finish within 10 s.");
        }
    }

    private static unsafe IReadOnlyList<AppEntry> ReadAppsFolder()
    {
        try
        {
            PInvoke.SHCreateItemFromParsingName("shell:AppsFolder", null, out IShellItem folder).ThrowOnFailure();
            folder.BindToHandler(null, PInvoke.BHID_EnumItems, out IEnumShellItems items);
            var entries = new List<AppEntry>();
            var batch = new IShellItem[1];
            while (true)
            {
                uint fetched = 0;
                items.Next(1, batch, &fetched);
                if (fetched == 0) break;
                var item = batch[0];
                batch[0] = null!;
                // The parsing name relative to the AppsFolder is the AppUserModelID or path that upstream's FolderItem.Path
                // reports and that shell:AppsFolder\<name> opens.
                if (DisplayName(item, SIGDN.SIGDN_NORMALDISPLAY) is { } name && DisplayName(item, SIGDN.SIGDN_PARENTRELATIVEPARSING) is { } parsingName)
                    entries.Add(new AppEntry(name, parsingName));
            }
            return entries;
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(Operation, e);
        }
    }

    // IShellItem::GetDisplayName; the string is CoTaskMem-allocated. Null when the item has no such name.
    private static unsafe string? DisplayName(IShellItem item, SIGDN kind)
    {
        PWSTR text = default;
        try
        {
            item.GetDisplayName(kind, &text);
            return text.Value is null ? null : new string(text.Value);
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            return null;
        }
        finally
        {
            if (text.Value is not null) PInvoke.CoTaskMemFree(text.Value);
        }
    }
}
