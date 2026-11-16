//! Child processes on overlapped named pipes, and Shell's PowerShell.
#[cfg(windows)]
fn main() {
    use desktop_mcp_windows::main_loop::run;
    use desktop_mcp_windows::process::{self, Spawn};
    use desktop_mcp_windows::shell::{resolve_interpreter, Interpreter};
    use std::ffi::OsString;
    use std::path::{Path, PathBuf};
    use std::time::{Duration, Instant};

    let cmd = PathBuf::from(std::env::var_os("ComSpec").unwrap());
    let args = |line: &str| vec![OsString::from("/d"), OsString::from("/c"), OsString::from(line)];

    run(|_| async move {
        // stdout, stderr and the exit code come back.
        let run = process::run(Spawn { program: &cmd, args: &args("echo out& echo err 1>&2& exit /b 3"), env: process::inherited_environment() }, Duration::from_secs(10)).await.unwrap();
        assert_eq!((run.stdout.trim(), run.stderr.trim(), run.status, run.timed_out), ("out", "err", 3, false));

        // A timeout ends the command and what it started, and returns promptly.
        let started = Instant::now();
        let run = process::run(Spawn { program: &cmd, args: &args("echo early& ping -n 30 127.0.0.1 >nul"), env: process::inherited_environment() }, Duration::from_secs(1)).await.unwrap();
        assert!(run.timed_out);
        assert_eq!(run.stdout.trim(), "early");
        assert!(started.elapsed() < Duration::from_secs(3), "took {:?}", started.elapsed());

        // PowerShell output is UTF-8, whatever the console code page.
        let run = desktop_mcp_windows::shell::run("Write-Output '한글 ü'", Duration::from_secs(30)).await.unwrap();
        assert_eq!(run.stdout.trim_end(), "한글 ü", "stderr: {}", run.stderr);

        // Windows PowerShell 5.1 reports errors as text, not CLIXML.
        let system_root = PathBuf::from(std::env::var_os("SystemRoot").unwrap());
        let windows_powershell = Interpreter::WindowsPowerShell(system_root.join("System32").join("WindowsPowerShell").join("v1.0").join("powershell.exe"));
        let run = desktop_mcp_windows::shell::run_with(&windows_powershell, "Get-Item C:/no-such-path", Duration::from_secs(60)).await.unwrap();
        assert!(!run.stderr.contains("CLIXML") && run.stderr.contains("Cannot find path"), "stderr: {}", run.stderr);
    });

    // PowerShell 7 on PATH first, then under Program Files, else Windows PowerShell.
    let exists = |known: &'static [&'static str]| move |path: &Path| known.iter().any(|known| Path::new(known) == path);
    let root = Path::new(r"C:\Windows");
    assert_eq!(resolve_interpreter(Some(r"C:\a;C:\b".as_ref()), Some(Path::new(r"C:\PF")), root, &exists(&[r"C:\b\pwsh.exe", r"C:\PF\PowerShell\7\pwsh.exe"])), Interpreter::Pwsh(r"C:\b\pwsh.exe".into()));
    assert_eq!(resolve_interpreter(Some(r"C:\a".as_ref()), Some(Path::new(r"C:\PF")), root, &exists(&[r"C:\PF\PowerShell\7\pwsh.exe"])), Interpreter::Pwsh(r"C:\PF\PowerShell\7\pwsh.exe".into()));
    assert_eq!(resolve_interpreter(None, None, root, &exists(&[])), Interpreter::WindowsPowerShell(r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe".into()));
    println!("process: ok");
}

#[cfg(not(windows))]
fn main() {}
