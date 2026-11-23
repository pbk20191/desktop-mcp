//! The `shortcuts` command line and its private temporary files, with a stand-in runner: no shortcut is run.
mod support;
use desktop_mcp_core::shell::Run;
use desktop_mcp_macos::main_loop::run;
use desktop_mcp_macos::shortcuts::run_with;
use std::cell::RefCell;
use std::future::Future;
use std::os::unix::fs::PermissionsExt;
use std::pin::Pin;
use std::rc::Rc;
use std::time::Duration;

/// What the stand-in saw: argv, the input file's text and mode, and the output path.
#[derive(Default)]
struct Seen {
    argv: Vec<String>,
    input: Option<(String, u32)>,
    output_path: Option<String>,
}

type Pending<T> = Pin<Box<dyn Future<Output = T>>>;

/// A runner that records its call, writes `output` to the output path, and answers `run`.
fn stand_in(seen: Rc<RefCell<Seen>>, output: &'static str, answer: Run) -> impl Fn(Vec<String>, Duration) -> Pending<Result<Run, String>> {
    move |argv, _timeout| {
        let mut record = Seen { argv: argv.clone(), ..Seen::default() };
        if let Some(i) = argv.iter().position(|arg| arg == "--input-path") {
            let path = &argv[i + 1];
            record.input = Some((std::fs::read_to_string(path).unwrap(), std::fs::metadata(path).unwrap().permissions().mode() & 0o777));
        }
        if let Some(i) = argv.iter().position(|arg| arg == "--output-path") {
            std::fs::write(&argv[i + 1], output).unwrap();
            record.output_path = Some(argv[i + 1].clone());
        }
        *seen.borrow_mut() = record;
        let answer = answer.clone();
        Box::pin(async move { Ok(answer) })
    }
}

fn ok() -> Run {
    Run { stdout: "A\nB\n".into(), stderr: String::new(), status: 0, timed_out: false }
}

fn input_goes_through_a_private_file_and_both_files_are_removed() {
    let seen = Rc::new(RefCell::new(Seen::default()));
    let runner = stand_in(seen.clone(), "<out>", ok());
    let ran = run(|_| async move { run_with("Resize", Some("hello"), Duration::from_secs(5), runner).await }).unwrap();
    assert_eq!((ran.status, ran.output.as_str(), ran.timed_out), (0, "<out>", false));
    let seen = seen.borrow();
    assert_eq!(seen.argv[..3], ["/usr/bin/shortcuts", "run", "Resize"]);
    assert_eq!(seen.input, Some(("hello".to_string(), 0o600)));
    let input_path = &seen.argv[seen.argv.iter().position(|arg| arg == "--input-path").unwrap() + 1];
    assert!(!std::path::Path::new(input_path).exists(), "input file left behind");
    assert!(!std::path::Path::new(seen.output_path.as_ref().unwrap()).exists(), "output file left behind");
}

fn main() {
    support::run(&[
        ("input_goes_through_a_private_file_and_both_files_are_removed", input_goes_through_a_private_file_and_both_files_are_removed),
    ])
}
