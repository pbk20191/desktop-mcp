using System.Diagnostics;
using DesktopMcp.Core.PowerShell;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DesktopMcp.Core.Tests;

/// <summary>
/// The kill-on-close job, tried on a child process. The test host itself never joins one: only serve does that at
/// startup, and a kill-on-close job around the test host would kill whatever the test runner shares it with.
/// </summary>
public class ProcessJobTests
{
    [Fact]
    public void ClosingTheJobKillsTheProcessesInIt()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", ["-n", "60", "127.0.0.1"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var job = ProcessJob.CreateKillOnCloseJob();
            Assert.NotEqual(0, job);
            try
            {
                Assert.True(PInvoke.AssignProcessToJobObject(new SafeFileHandle(job, ownsHandle: false), child.SafeHandle));
                Assert.True(PInvoke.IsProcessInJob(child.SafeHandle, new SafeFileHandle(job, ownsHandle: false), out var inJob));
                Assert.True(inJob);
            }
            finally
            {
                PInvoke.CloseHandle((HANDLE)job);
            }
            Assert.True(child.WaitForExit(5000), "the child outlived its kill-on-close job");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
