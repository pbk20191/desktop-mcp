//! The Shell tool on Windows: PowerShell 7 when installed, else Windows PowerShell 5.1.
use crate::process::{self, Spawn};
use base64::Engine;
use desktop_mcp_core::platform::ToolText;
use desktop_mcp_core::shell::Run;
use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

/// The server's own secrets, which no child sees.
pub const SECRET_VARIABLES: [&str; 2] = ["DESKTOP_MCP_AUTH_KEY", "DESKTOP_MCP_OAUTH_CLIENT_SECRET"];

pub const FALLBACK_NOTE: &str = "PowerShell 7 was not found, so this ran in Windows PowerShell 5.1. Install PowerShell 7 with: winget install Microsoft.PowerShell";

pub const DESCRIPTION: &str = "Execute commands on Windows in PowerShell (PowerShell 7 when installed, else Windows PowerShell 5.1). Use for file system, process management, system operations, and automation scripts. SECURITY WARNING: Commands are executed with the same permissions as the terminal/application running this server. Review and understand all actions before execution, especially those involving file operations, system modifications, or external processes.";

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Interpreter {
    Pwsh(PathBuf),
    WindowsPowerShell(PathBuf),
}

impl Interpreter {
    fn path(&self) -> &Path {
        match self {
            Interpreter::Pwsh(path) | Interpreter::WindowsPowerShell(path) => path,
        }
    }
}

/// `pwsh.exe` on `path_var` (which includes the Store alias folder when it is installed from the Store), then under
/// `program_files`, else Windows PowerShell under `system_root`.
pub fn resolve_interpreter(path_var: Option<&OsStr>, program_files: Option<&Path>, system_root: &Path, exists: &dyn Fn(&Path) -> bool) -> Interpreter {
    let on_path = path_var.into_iter().flat_map(std::env::split_paths).map(|folder| folder.join("pwsh.exe"));
    let installed = program_files.map(|folder| folder.join(r"PowerShell\7\pwsh.exe"));
    match on_path.chain(installed).find(|candidate| exists(candidate)) {
        Some(pwsh) => Interpreter::Pwsh(pwsh),
        None => Interpreter::WindowsPowerShell(system_root.join(r"System32\WindowsPowerShell\v1.0\powershell.exe")),
    }
}

fn interpreter() -> Interpreter {
    let system_root = std::env::var_os("SystemRoot").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
    let program_files = std::env::var_os("ProgramFiles").map(PathBuf::from);
    resolve_interpreter(std::env::var_os("PATH").as_deref(), program_files.as_deref(), &system_root, &|path| path.is_file())
}

/// The child's environment: this process's, less the server's secrets, with colour codes off.
pub fn child_environment() -> Vec<(OsString, OsString)> {
    let mut environment: Vec<(OsString, OsString)> = std::env::vars_os().filter(|(key, _)| !SECRET_VARIABLES.iter().any(|secret| key.eq_ignore_ascii_case(secret))).collect();
    environment.retain(|(key, _)| !key.eq_ignore_ascii_case("NO_COLOR"));
    environment.push(("NO_COLOR".into(), "1".into()));
    environment
}

static FALLBACK_REPORTED: AtomicBool = AtomicBool::new(false);

/// Runs `command` in PowerShell with UTF-8 output; the first run that falls back to 5.1 says so on stdout.
pub async fn run(command: &str, timeout: Duration) -> Result<Run, String> {
    run_with(&interpreter(), command, timeout).await
}

/// [`run`] with a given interpreter.
pub async fn run_with(interpreter: &Interpreter, command: &str, timeout: Duration) -> Result<Run, String> {
    // The command travels base64-encoded, so no quoting rule can change it, but under -Command rather than
    // -EncodedCommand: with -EncodedCommand Windows PowerShell 5.1 writes errors and progress as CLIXML.
    let script = format!("$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8\n{command}");
    let utf16: Vec<u8> = script.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let encoded = base64::engine::general_purpose::STANDARD.encode(utf16);
    // A syntax error in the command surfaces from Create wrapped in a MethodInvocationException; the parser's own
    // message is what the caller needs, with exit status 1.
    let invoke = format!(
        "try {{ $command = [scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{encoded}'))) }} catch {{ [Console]::Error.WriteLine($_.Exception.InnerException.Message); exit 1 }}; & $command"
    );
    let args: Vec<OsString> = ["-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-Command", &invoke].iter().map(OsString::from).collect();
    let mut run = process::run(Spawn { program: interpreter.path(), args: &args, env: child_environment() }, timeout).await?;
    // Line ends as on macOS: PowerShell writes CRLF.
    run.stdout = run.stdout.replace("\r\n", "\n");
    run.stderr = run.stderr.replace("\r\n", "\n");
    if matches!(interpreter, Interpreter::WindowsPowerShell(_)) && !FALLBACK_REPORTED.swap(true, Ordering::Relaxed) {
        run.stdout = format!("{FALLBACK_NOTE}\n{}", run.stdout);
    }
    Ok(run)
}

/// Shell's Windows text: the description, and `mode` with PowerShell only.
pub fn text() -> ToolText {
    ToolText {
        description: Some(DESCRIPTION),
        schema_patches: vec![("/properties/mode", serde_json::json!({"type": "string", "enum": ["powershell"], "default": "powershell"}))],
    }
}
