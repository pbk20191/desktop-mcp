using DesktopMcp.Desktop.Errors;

namespace DesktopMcp.Desktop.Uia;

/// <summary>Runs the body of a UIA job so that no raw COM error leaves it, except the ones its callers classify.</summary>
internal static class UiaJob
{
    /// <summary>
    /// COM and WinRT failures become <see cref="DesktopException"/>s (<see cref="ComErrors.Translate"/>), except a dead
    /// element and UIA's own timeout, which stay raw so <see cref="ComErrors.IsDeadElement"/> and
    /// <see cref="ComErrors.IsTimeout"/> still see them; the window catalog and the tree service consume those.
    /// </summary>
    public static T Run<T>(string operation, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception e) when (!ComErrors.IsDeadElement(e) && !ComErrors.IsTimeout(e) && ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate(operation, e);
        }
    }
}
