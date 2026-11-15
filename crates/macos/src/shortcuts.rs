//! The user's Shortcuts, through `/usr/bin/shortcuts`. Input and output pass through temporary files that only
//! this user can read and that are removed when the run ends, however it ends.
use desktop_mcp_core::shell::Run;
use desktop_mcp_core::shortcuts::ShortcutRun;
use std::future::Future;
use std::io::Write;
use std::os::unix::fs::OpenOptionsExt;
use std::path::PathBuf;
use std::time::Duration;

pub const BINARY: &str = "/usr/bin/shortcuts";

/// `shortcuts list`, optionally `--folder-name folder`.
pub async fn list(folder: Option<&str>, timeout: Duration) -> Result<Run, String> {
    list_with(folder, timeout, crate::process::run).await
}

/// Runs the shortcut `name` with `input`.
pub async fn run(name: &str, input: Option<&str>, timeout: Duration) -> Result<ShortcutRun, String> {
    run_with(name, input, timeout, crate::process::run).await
}

/// [`list`] with another process runner.
pub async fn list_with<F: Future<Output = Result<Run, String>>>(folder: Option<&str>, timeout: Duration, runner: impl Fn(Vec<String>, Duration) -> F) -> Result<Run, String> {
    let mut argv = vec![BINARY.to_string(), "list".to_string()];
    if let Some(folder) = folder {
        argv.extend(["--folder-name".to_string(), folder.to_string()]);
    }
    runner(argv, timeout).await
}

/// [`run`] with another process runner.
pub async fn run_with<F: Future<Output = Result<Run, String>>>(
    name: &str,
    input: Option<&str>,
    timeout: Duration,
    runner: impl Fn(Vec<String>, Duration) -> F,
) -> Result<ShortcutRun, String> {
    let id = uuid::Uuid::new_v4();
    let files = TemporaryFiles {
        input: std::env::temp_dir().join(format!("desktop-mcp-shortcut-{id}.in")),
        output: std::env::temp_dir().join(format!("desktop-mcp-shortcut-{id}.out")),
    };
    let mut argv = vec![BINARY.to_string(), "run".to_string(), name.to_string()];
    if let Some(input) = input {
        write_private(&files.input, input)?;
        argv.extend(["--input-path".to_string(), files.input.to_string_lossy().into_owned()]);
    }
    argv.extend(["--output-path".to_string(), files.output.to_string_lossy().into_owned()]);
    let run = runner(argv, timeout).await?;
    let output = std::fs::read(&files.output).ok().and_then(|bytes| String::from_utf8(bytes).ok()).unwrap_or_default();
    Ok(ShortcutRun { status: run.status, timed_out: run.timed_out, output, stderr: run.stderr })
}

/// Removed when dropped: after the run, or when the call is abandoned.
struct TemporaryFiles {
    input: PathBuf,
    output: PathBuf,
}

impl Drop for TemporaryFiles {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.input);
        let _ = std::fs::remove_file(&self.output);
    }
}

/// Creates `path` exclusively with mode 0600 from the start, so no other user can read it.
fn write_private(path: &std::path::Path, text: &str) -> Result<(), String> {
    let mut file = std::fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(path).map_err(|error| format!("Could not create a temporary file: {error}"))?;
    file.write_all(text.as_bytes()).map_err(|error| format!("Could not write a temporary file: {error}"))
}
