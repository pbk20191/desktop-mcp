//! stdio as a byte stream, with anonymous pipes standing in for the console.
#[cfg(windows)]
fn main() {
    use desktop_mcp_windows::main_loop::run;
    use desktop_mcp_windows::stdio::StdIo;
    use hyper::rt::{Read, ReadBuf, Write};
    use std::future::poll_fn;
    use std::mem::MaybeUninit;
    use std::os::windows::io::{FromRawHandle, OwnedHandle};
    use std::pin::Pin;
    use std::time::{Duration, Instant};
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::Storage::FileSystem::{ReadFile, WriteFile};
    use windows::Win32::System::Pipes::CreatePipe;

    fn pipe() -> (OwnedHandle, OwnedHandle) {
        let (mut read, mut write) = (HANDLE::default(), HANDLE::default());
        unsafe { CreatePipe(&mut read, &mut write, None, 0) }.unwrap();
        unsafe { (OwnedHandle::from_raw_handle(read.0), OwnedHandle::from_raw_handle(write.0)) }
    }
    async fn read_some(io: &mut StdIo) -> Vec<u8> {
        let mut storage = [MaybeUninit::<u8>::uninit(); 1024];
        let mut buf = ReadBuf::uninit(&mut storage);
        poll_fn(|cx| Pin::new(&mut *io).poll_read(cx, buf.unfilled())).await.unwrap();
        buf.filled().to_vec()
    }
    let raw = |handle: &OwnedHandle| HANDLE(std::os::windows::io::AsRawHandle::as_raw_handle(handle));

    // Bytes in, bytes out, then end of input.
    run(|_| async move {
        let (input_read, input_write) = pipe();
        let (output_read, output_write) = pipe();
        let mut io = StdIo::from_handles(input_read, output_write);
        unsafe { WriteFile(raw(&input_write), Some(b"hello"), None, None) }.unwrap();
        drop(input_write);
        assert_eq!(read_some(&mut io).await, b"hello");
        assert_eq!(read_some(&mut io).await, b"", "end of input");
        poll_fn(|cx| Pin::new(&mut io).poll_write(cx, b"out\n")).await.unwrap();
        poll_fn(|cx| Pin::new(&mut io).poll_flush(cx)).await.unwrap();
        let mut got = [0u8; 4];
        unsafe { ReadFile(raw(&output_read), Some(&mut got), None, None) }.unwrap();
        assert_eq!(&got, b"out\n");
    });

    // A read blocked on input that never comes ends promptly when cancelled.
    run(|_| async move {
        let (input_read, _input_write) = pipe();
        let (_output_read, output_write) = pipe();
        let mut io = StdIo::from_handles(input_read, output_write);
        let canceller = io.canceller();
        let started = Instant::now();
        let pending = async { read_some(&mut io).await };
        let cancel = async {
            desktop_mcp_windows::pool::sleep(Duration::from_millis(100)).await;
            canceller.cancel();
        };
        let (read, ()) = tokio::join!(pending, cancel);
        assert_eq!(read, b"");
        assert!(started.elapsed() < Duration::from_secs(1), "took {:?}", started.elapsed());
    });

    // A write blocked on a client that does not read fails promptly when cancelled.
    run(|_| async move {
        let (input_read, _input_write) = pipe();
        let (_output_read, output_write) = pipe();
        let mut io = StdIo::from_handles(input_read, output_write);
        let canceller = io.canceller();
        let started = Instant::now();
        let bytes = vec![b'x'; 1 << 20];
        let pending = poll_fn(|cx| Pin::new(&mut io).poll_write(cx, &bytes));
        let cancel = async {
            desktop_mcp_windows::pool::sleep(Duration::from_millis(100)).await;
            canceller.cancel();
        };
        let (written, ()) = tokio::join!(pending, cancel);
        assert!(written.is_err(), "{written:?}");
        assert!(started.elapsed() < Duration::from_secs(1), "took {:?}", started.elapsed());
    });
    println!("stdio: ok");
}

#[cfg(not(windows))]
fn main() {}
