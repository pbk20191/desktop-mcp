using System.Diagnostics;
using DesktopMcp.Core.Interop;
using DesktopMcp.Core.PowerShell;

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
                Assert.True(CoreNative.AssignProcessToJobObject(job, child.Handle));
                Assert.True(CoreNative.IsProcessInJob(child.Handle, job, out var inJob));
                Assert.True(inJob);
            }
            finally
            {
                CoreNative.CloseHandle(job);
            }
            Assert.True(child.WaitForExit(5000), "the child outlived its kill-on-close job");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
