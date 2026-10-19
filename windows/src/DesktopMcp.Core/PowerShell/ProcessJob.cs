using DesktopMcp.Core.Interop;

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
            if (!CoreNative.AssignProcessToJobObject(job, CoreNative.GetCurrentProcess()))
            {
                CoreNative.CloseHandle(job);
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
        var job = CoreNative.CreateJobObject(0, 0);
        if (job == 0) return 0;
        var info = new CoreNative.JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = CoreNative.JobObjectLimitKillOnJobClose;
        if (!CoreNative.SetInformationJobObject(job, CoreNative.JobObjectExtendedLimitInformationClass, &info,
                (uint)sizeof(CoreNative.JobObjectExtendedLimitInformation)))
        {
            CoreNative.CloseHandle(job);
            return 0;
        }
        return job;
    }
}
