using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DesktopMcp.Desktop.Errors;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DesktopMcp.Desktop.Apps;

/// <summary>
/// Starts apps for App <c>launch</c> and <c>launch_executable</c> (P3 Decisions 31 and 34, with Open question 1's ruling:
/// launched apps die with the server). <see cref="ActivatePackaged"/> and <see cref="StartFromShell"/> can block on
/// other applications, so callers run them on an STA thread under a deadline (<see cref="AppService"/>).
/// </summary>
internal interface IAppLauncher
{
    /// <summary>Activates a packaged app by its AppUserModelID; its process id, or 0 when none was reported.</summary>
    int ActivatePackaged(string appUserModelId);

    /// <summary>ShellExecuteEx of <c>shell:AppsFolder\&lt;parsing name&gt;</c>; the started process's id, or null when the shell returned no process.</summary>
    int? StartFromShell(string parsingName);

    /// <summary>CreateProcess of the command line, std handles on NUL; the process id. Throws <see cref="Win32Exception"/>.</summary>
    int StartExecutable(string commandLine, string? cwd);
}

internal sealed partial class AppLauncher : IAppLauncher
{
    /// <summary>A packaged app's AppUserModelID, <c>&lt;package family&gt;!&lt;app id&gt;</c>.</summary>
    internal static bool IsPackaged(string parsingName) => PackagedName().IsMatch(parsingName);

    [GeneratedRegex(@"^[^\\!]+![^\\!]+$")]
    private static partial Regex PackagedName();

    /// <summary>
    /// IApplicationActivationManager.ActivateApplication (AO_NONE). The system starts the app outside this server's job,
    /// so it survives the server (Open question 1's documented exception).
    /// </summary>
    public unsafe int ActivatePackaged(string appUserModelId)
    {
        try
        {
            var manager = ApplicationActivationManager.CreateInstance<IApplicationActivationManager>();
            uint processId;
            fixed (char* id = appUserModelId)
                manager.ActivateApplication(id, default, ACTIVATEOPTIONS.AO_NONE, out processId);
            return (int)processId;
        }
        catch (Exception e) when (ComErrors.ShouldTranslate(e))
        {
            throw ComErrors.Translate("Activating the app", e);
        }
    }

    /// <summary>
    /// ShellExecuteEx with SEE_MASK_NOCLOSEPROCESS (spec §8), no error UI, and SEE_MASK_NOASYNC because the calling STA
    /// thread ends right after. A process it creates directly is in this server's kill-on-close job.
    /// </summary>
    public unsafe int? StartFromShell(string parsingName)
    {
        var file = @"shell:AppsFolder\" + parsingName;
        fixed (char* path = file)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_NOCLOSEPROCESS | PInvoke.SEE_MASK_FLAG_NO_UI | PInvoke.SEE_MASK_NOASYNC,
                lpFile = path,
                nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            };
            if (!PInvoke.ShellExecuteEx(&info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (info.hProcess.IsNull) return null;
            try
            {
                var processId = PInvoke.GetProcessId(info.hProcess);
                return processId == 0 ? null : (int)processId;
            }
            finally
            {
                PInvoke.CloseHandle(info.hProcess);
            }
        }
    }

    /// <summary>
    /// Python's <c>Popen(argv, stdin/stdout/stderr=DEVNULL, close_fds=True)</c>: CreateProcess with
    /// EXTENDED_STARTUPINFO_PRESENT only (no breakaway, so the process stays in the server's job), the std handles on one
    /// inheritable NUL handle, and a PROC_THREAD_ATTRIBUTE_HANDLE_LIST holding only that handle, so nothing else is
    /// inherited. The process, thread and NUL handles are closed.
    /// </summary>
    public unsafe int StartExecutable(string commandLine, string? cwd)
    {
        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Inheritable);
        var handle = new HANDLE(nul.DangerousGetHandle());

        nuint size = 0;
        PInvoke.InitializeProcThreadAttributeList(default, 1, 0, &size);
        if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var memory = NativeMemory.Alloc(size);
        try
        {
            var attributes = new LPPROC_THREAD_ATTRIBUTE_LIST(memory);
            if (!PInvoke.InitializeProcThreadAttributeList(attributes, 1, 0, &size)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!PInvoke.UpdateProcThreadAttribute(attributes, 0, PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &handle, (nuint)sizeof(HANDLE), null, null))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());

                var startup = default(STARTUPINFOEXW);
                startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                startup.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = handle;
                startup.StartupInfo.hStdOutput = handle;
                startup.StartupInfo.hStdError = handle;
                startup.lpAttributeList = attributes;

                // CreateProcessW may write to the command line, so it gets its own terminated copy.
                var line = new char[commandLine.Length + 1];
                commandLine.CopyTo(line);
                PROCESS_INFORMATION process;
                fixed (char* command = line)
                fixed (char* directory = cwd)
                {
                    if (!PInvoke.CreateProcess(default, command, null, null, true, PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT, null,
                            directory, &startup.StartupInfo, &process))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                PInvoke.CloseHandle(process.hThread);
                PInvoke.CloseHandle(process.hProcess);
                return (int)process.dwProcessId;
            }
            finally
            {
                PInvoke.DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }
}
