using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace DesktopMcp.Core.PowerShell;

/// <summary>
/// spec §5: the server puts itself in a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so the processes it starts
/// (PowerShell children, and anything they start, GUI apps included: no breakaway) cannot outlive it. Nested jobs are
/// fine on Windows 8 and later. Only serve calls this, at startup.
/// </summary>
public static unsafe class ProcessJob
{
    private static readonly Lock Gate = new();
    private static nint _job;

    public static bool TryAssignKillOnClose()
    {
        lock (Gate)
        {
            if (_job != 0) return true;
            var job = CreateKillOnCloseJob();
            if (job == 0) return false;
            if (!PInvoke.AssignProcessToJobObject((HANDLE)job, PInvoke.GetCurrentProcess()))
            {
                PInvoke.CloseHandle((HANDLE)job);
                return false;
            }
            // Never closed: the handle closes when this process exits, and the job then kills what is left in it.
            _job = job;
            return true;
        }
    }

    /// <summary>A new job that kills its processes when its last handle closes, or 0 on failure.</summary>
    internal static nint CreateKillOnCloseJob()
    {
        var job = PInvoke.CreateJobObject(null, default(PCWSTR));
        if (job.IsNull) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &info,
                (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            PInvoke.CloseHandle(job);
            return 0;
        }
        return job;
    }
}
