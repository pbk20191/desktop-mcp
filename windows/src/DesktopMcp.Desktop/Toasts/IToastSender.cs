namespace DesktopMcp.Desktop.Toasts;

public interface IToastSender
{
    /// <summary>Shows a ToastGeneric toast with a title and a body under the given Application User Model ID.</summary>
    void Show(string title, string message, string appId);
}
