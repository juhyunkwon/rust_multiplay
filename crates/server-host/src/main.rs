use dedicated_server::{AuthorityServer, ServerCapacity};
use x0_core::{AppConfig, ConfigPath, ConsoleBanner, PacketCrypto};
use signaling_server::SignalingServerHost;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    ensure_taskbar_console();

    let cfg = AppConfig::load();
    let log_dir = ConfigPath::get_log_directory(Some(&cfg.logging.log_path));
    let _log_guard = x0_core::logging::init("server-host", &log_dir, "serverhost");

    let crypto = PacketCrypto::new(Some(cfg.encryption.key.as_str()).filter(|s| !s.is_empty()));
    if crypto.is_enabled() {
        tracing::info!("패킷 암호화 활성화");
    }

    let tcp_port = cfg.signaling_server.tcp_port;
    let udp_port = cfg.signaling_server.udp_port;
    let dedicated_port = cfg.dedicated_server.port;
    let capacity = ServerCapacity::new(
        cfg.server_capacity.max_rooms,
        cfg.server_capacity.max_players_per_room,
    );

    tracing::info!(
        signaling_tcp = tcp_port,
        signaling_udp = udp_port,
        dedicated = dedicated_port,
        "Signaling + Dedicated 동시 구동"
    );
    ConsoleBanner::write(
        "ServerHost",
        "Signaling + Dedicated 동시 구동",
        &[
            ("Signaling", format!("{tcp_port}/{udp_port}")),
            ("Dedicated", dedicated_port.to_string()),
            ("Capacity", capacity.to_string()),
            ("Config", ConfigPath::get_config_directory().display().to_string()),
            ("LogDir", log_dir.display().to_string()),
            (
                "DB",
                cfg.connection_strings
                    .resolved()
                    .map(|c| x0_core::describe_connection(&c))
                    .unwrap_or_else(|_| "(없음)".into()),
            ),
        ],
    );

    let conn = match cfg.connection_strings.resolved() {
        Ok(c) => c,
        Err(e) => fail_startup(e)?,
    };

    if let Err(e) = assert_ports_free(tcp_port, dedicated_port) {
        fail_startup::<()>(e.to_string())?;
    }

    let signaling = SignalingServerHost::new(tcp_port, udp_port, crypto.clone());
    let dedicated = AuthorityServer::new(dedicated_port, &conn, &capacity, crypto)?;

    let sig_task = tokio::spawn(async move {
        if let Err(e) = signaling.start().await {
            tracing::error!("[ServerHost] SignalingServer 오류: {e}");
        }
    });
    let ded_task = tokio::spawn(async move {
        if let Err(e) = dedicated.start().await {
            tracing::error!("[ServerHost] DedicatedServer 오류: {e}");
        }
    });

    tokio::select! {
        _ = sig_task => {},
        _ = ded_task => {},
        _ = tokio::signal::ctrl_c() => {
            tracing::info!("종료 요청 (Ctrl+C)");
        }
    }
    Ok(())
}

fn assert_ports_free(signaling_tcp: u16, dedicated: u16) -> anyhow::Result<()> {
    for (name, port) in [("Signaling TCP", signaling_tcp), ("Dedicated", dedicated)] {
        if !tcp_port_free(port) {
            anyhow::bail!(
                "{name} {port} 이 이미 사용 중입니다. 작업 관리자에서 server-host.exe 를 종료하세요."
            );
        }
    }
    Ok(())
}

fn tcp_port_free(port: u16) -> bool {
    std::net::TcpListener::bind(("0.0.0.0", port)).is_ok()
        || std::net::TcpListener::bind(("127.0.0.1", port)).is_ok()
}

/// CREATE_NO_WINDOW 로 떠도 콘솔 창 띄움
/// 시작 실패. 더블클릭으로 띄웠을 때 창이 바로 꺼지지 않게 Enter 대기
fn fail_startup<T>(msg: String) -> anyhow::Result<T> {
    use std::io::IsTerminal;
    eprintln!("[ServerHost] 시작 실패: {msg}");
    tracing::error!("시작 실패: {msg}");
    if std::io::stdin().is_terminal() {
        eprintln!("Enter 를 누르면 종료합니다.");
        let _ = std::io::stdin().read_line(&mut String::new());
    }
    anyhow::bail!(msg)
}

fn ensure_taskbar_console() {
    #[cfg(windows)]
    unsafe {
        win_console::ensure_visible();
    }
}

#[cfg(windows)]
mod win_console {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;

    const STD_INPUT_HANDLE: u32 = 0xFFFF_FFF6;
    const STD_OUTPUT_HANDLE: u32 = 0xFFFF_FFF5;
    const STD_ERROR_HANDLE: u32 = 0xFFFF_FFF4;
    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const FILE_SHARE_READ: u32 = 1;
    const FILE_SHARE_WRITE: u32 = 2;
    const OPEN_EXISTING: u32 = 3;
    const INVALID_HANDLE: isize = -1;
    const SW_SHOW: i32 = 5;
    const SW_RESTORE: i32 = 9;

    #[link(name = "kernel32")]
    extern "system" {
        fn GetConsoleWindow() -> *mut core::ffi::c_void;
        fn AllocConsole() -> i32;
        fn SetConsoleTitleW(title: *const u16) -> i32;
        fn SetStdHandle(n: u32, h: *mut core::ffi::c_void) -> i32;
        fn CreateFileW(
            name: *const u16,
            access: u32,
            share: u32,
            sec: *mut core::ffi::c_void,
            disp: u32,
            flags: u32,
            template: *mut core::ffi::c_void,
        ) -> *mut core::ffi::c_void;
    }

    #[link(name = "user32")]
    extern "system" {
        fn ShowWindow(hwnd: *mut core::ffi::c_void, cmd: i32) -> i32;
    }

    fn wide(s: &str) -> Vec<u16> {
        OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
    }

    pub unsafe fn ensure_visible() {
        let mut hwnd = GetConsoleWindow();
        if hwnd.is_null() && AllocConsole() != 0 {
            attach_stdio();
            hwnd = GetConsoleWindow();
        }
        if !hwnd.is_null() {
            ShowWindow(hwnd, SW_RESTORE);
            ShowWindow(hwnd, SW_SHOW);
        }
        let title = wide("X0 ServerHost  —  시그널링 19050  Dedicated 19060");
        SetConsoleTitleW(title.as_ptr());
    }

    unsafe fn attach_stdio() {
        let out = wide("CONOUT$");
        let inp = wide("CONIN$");
        let h_out = CreateFileW(
            out.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        );
        let h_in = CreateFileW(
            inp.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null_mut(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        );
        if h_out as isize != INVALID_HANDLE {
            SetStdHandle(STD_OUTPUT_HANDLE, h_out);
            SetStdHandle(STD_ERROR_HANDLE, h_out);
        }
        if h_in as isize != INVALID_HANDLE {
            SetStdHandle(STD_INPUT_HANDLE, h_in);
        }
    }
}
