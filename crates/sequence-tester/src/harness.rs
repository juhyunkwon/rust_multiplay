use anyhow::{bail, Context};
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use tokio::net::TcpStream;
use tokio::process::{Child, Command};
use tokio::time::{sleep, Duration};
use x0_core::{AppConfig, PacketCrypto};

use crate::report::SequenceReport;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Target {
    /// config/appsettings.json 기준 실제 서버
    Live,
    /// cargo test 용 임시 포트
    InProcess,
}

pub struct DedicatedEnv {
    pub addr: String,
}

#[derive(Clone)]
pub struct SignalingEnv {
    pub tcp: String,
    pub udp: std::net::SocketAddr,
}

/// 떠 있는 server-host
/// 원래 떠 있던 서버에 붙은 거면 Drop 돼도 안 끔
/// 직접 띄운 경우도 기본은 살려 둠. SEQ_KILL_SERVER=1 이면 끔
pub struct LiveHost {
    _child: Option<Child>,
    pub signaling: SignalingEnv,
    pub dedicated: String,
    pub crypto: PacketCrypto,
}

pub fn crypto_from_config(cfg: &AppConfig) -> PacketCrypto {
    PacketCrypto::new(Some(cfg.encryption.key.as_str()).filter(|s| !s.is_empty()))
}

fn workspace_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../..")
        .canonicalize()
        .unwrap_or_else(|_| std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")))
}

fn restart_requested() -> bool {
    matches!(
        std::env::var("SEQ_RESTART_SERVER").as_deref(),
        Ok("1") | Ok("true") | Ok("TRUE")
    )
}

fn kill_spawned_on_drop() -> bool {
    matches!(
        std::env::var("SEQ_KILL_SERVER").as_deref(),
        Ok("1") | Ok("true") | Ok("TRUE")
    )
}

/// 19050 열려 있으면 그 서버 사용. 없으면 _bin exe 나 cargo 빌드해서 띄움
pub async fn ensure_live_host() -> anyhow::Result<LiveHost> {
    let cfg = AppConfig::load();
    let host = cfg.signaling_server.host.trim();
    let host = if host.is_empty() { "127.0.0.1" } else { host };
    let tcp = format!("{}:{}", host, cfg.signaling_server.tcp_port);
    let dedicated = format!("{}:{}", cfg.dedicated_server.host, cfg.dedicated_server.port);
    let udp: std::net::SocketAddr = format!("{}:{}", host, cfg.signaling_server.udp_port)
        .parse()
        .with_context(|| "시그널링 UDP 주소")?;

    let child = if !restart_requested()
        && (tcp_open(&tcp).await || server_host_running() || !listening_pids(cfg.signaling_server.tcp_port).is_empty())
    {
        None
    } else {
        if restart_requested() {
            free_listen_ports(&[
                cfg.signaling_server.tcp_port,
                cfg.signaling_server.udp_port,
                cfg.dedicated_server.port,
            ])
            .await;
        }
        if tcp_open(&tcp).await || server_host_running() {
            None
        } else {
            spawn_server_host().await?
        }
    };

    if !wait_tcp(&tcp, 80).await {
        bail!(
            "시그널링({tcp})에 연결하지 못했습니다. `cargo run -p server-host` 또는 `scripts/run-server.ps1` 로 서버를 먼저 켜세요."
        );
    }
    let _ = wait_tcp(&dedicated, 40).await;

    Ok(LiveHost {
        _child: child,
        signaling: SignalingEnv { tcp, udp },
        dedicated,
        crypto: crypto_from_config(&cfg),
    })
}

async fn tcp_open(addr: &str) -> bool {
    TcpStream::connect(addr).await.is_ok()
}

async fn wait_tcp(addr: &str, tries: u32) -> bool {
    for _ in 0..tries {
        if tcp_open(addr).await {
            return true;
        }
        sleep(Duration::from_millis(250)).await;
    }
    false
}

async fn free_listen_ports(ports: &[u16]) {
    for port in ports {
        for pid in listening_pids(*port) {
            if pid == 0 || pid == std::process::id() {
                continue;
            }
            let _ = Command::new("taskkill")
                .args(["/F", "/PID", &pid.to_string()])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .await;
        }
    }
    sleep(Duration::from_millis(500)).await;
}

fn listening_pids(port: u16) -> Vec<u32> {
    let Ok(out) = std::process::Command::new("netstat").args(["-ano"]).output() else {
        return Vec::new();
    };
    let text = String::from_utf8_lossy(&out.stdout);
    let mut pids = Vec::new();
    for line in text.lines() {
        let line = line.trim();
        let cols: Vec<&str> = line.split_whitespace().collect();
        if cols.len() < 4 {
            continue;
        }
        let local = cols[1];
        let local_port = local.rsplit(':').next().unwrap_or("");
        if local_port != port.to_string() {
            continue;
        }
        let pid_s = cols.last().copied().unwrap_or("");
        if let Ok(pid) = pid_s.parse::<u32>() {
            if !pids.contains(&pid) {
                pids.push(pid);
            }
        }
    }
    pids
}

fn cargo_executable() -> PathBuf {
    if let Ok(home) = std::env::var("CARGO_HOME") {
        let p = PathBuf::from(home).join("bin").join(cargo_name());
        if p.is_file() {
            return p;
        }
    }
    if let Ok(profile) = std::env::var("USERPROFILE") {
        let p = PathBuf::from(profile).join(".cargo").join("bin").join(cargo_name());
        if p.is_file() {
            return p;
        }
    }
    PathBuf::from(cargo_name())
}

fn cargo_name() -> &'static str {
    if cfg!(windows) {
        "cargo.exe"
    } else {
        "cargo"
    }
}

fn exe_name(pkg: &str) -> String {
    if cfg!(windows) {
        format!("{pkg}.exe")
    } else {
        pkg.to_string()
    }
}

fn server_host_running() -> bool {
    #[cfg(windows)]
    {
        let Ok(out) = std::process::Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq server-host.exe", "/NH"])
            .output()
        else {
            return false;
        };
        String::from_utf8_lossy(&out.stdout)
            .to_ascii_lowercase()
            .contains("server-host.exe")
    }
    #[cfg(not(windows))]
    {
        false
    }
}

fn find_server_host_exe(root: &Path) -> Option<PathBuf> {
    let name = exe_name("server-host");
    let cands = [
        root.join("_bin").join(&name),
        root.join("_garbage").join("debug").join(&name),
        root.join("target").join("debug").join(&name),
    ];
    cands.into_iter().find(|p| p.is_file())
}

async fn spawn_server_host() -> anyhow::Result<Option<Child>> {
    let root = workspace_root();
    let garbage = root.join("_garbage");
    let bin_dir = root.join("_bin");
    let log_dir = root.join("_log");
    fs::create_dir_all(&garbage).ok();
    fs::create_dir_all(&bin_dir).ok();
    fs::create_dir_all(&log_dir).ok();

    let mut exe = find_server_host_exe(&root);
    if exe.is_none() {
        let cargo = cargo_executable();
        let output = Command::new(&cargo)
            .args(["build", "-p", "server-host"])
            .current_dir(&root)
            .env("CARGO_TARGET_DIR", &garbage)
            .stdin(Stdio::null())
            .output()
            .await
            .with_context(|| format!("{} 실행 실패 (PATH에 cargo가 없을 수 있음)", cargo.display()))?;
        if !output.status.success() {
            let err = String::from_utf8_lossy(&output.stderr);
            bail!(
                "server-host 빌드 실패\n{}",
                err.chars().take(1500).collect::<String>()
            );
        }
        exe = find_server_host_exe(&root);
    }

    let built = exe.context("_bin/server-host.exe 없음. 먼저 .\\scripts\\build.ps1 을 실행하세요.")?;
    let staged = bin_dir.join(exe_name("server-host"));
    if built != staged {
        let _ = fs::copy(&built, &staged);
    }
    let run_exe = if staged.is_file() { staged } else { built };

    let mut std_cmd = std::process::Command::new(&run_exe);
    std_cmd.current_dir(&root).stdin(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // 새 콘솔 + 새 프로세스 그룹. 테스터 끝나도 서버 창은 남게
        std_cmd.creation_flags(0x0000_0010 | 0x0000_0200);
    }

    if kill_spawned_on_drop() {
        let mut cmd = Command::from(std_cmd);
        cmd.kill_on_drop(true);
        let child = cmd
            .spawn()
            .with_context(|| format!("server-host 실행 실패: {}", run_exe.display()))?;
        Ok(Some(child))
    } else {
        let child = std_cmd
            .spawn()
            .with_context(|| format!("server-host 실행 실패: {}", run_exe.display()))?;
        std::mem::forget(child);
        Ok(None)
    }
}

pub fn live_unavailable(title: &str, err: impl std::fmt::Display) -> SequenceReport {
    let mut r = SequenceReport::new(title);
    r.fail("실제 서버 연결", err.to_string());
    r
}

pub async fn spawn_dedicated() -> anyhow::Result<DedicatedEnv> {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await?;
    let port = listener.local_addr()?.port();
    let users = x0_db::MemoryUserRepository::default();
    users.add("seq_a", "pw_a", "SeqA", 1);
    users.add("seq_b", "pw_b", "SeqB", 2);
    let users: Box<dyn x0_db::UserRepository> = Box::new(users);
    let crypto = PacketCrypto::new(None);
    let server = dedicated_server::AuthorityServer::new_with_users(
        port,
        users,
        &dedicated_server::ServerCapacity::for_tests(),
        crypto,
    );
    tokio::spawn(async move {
        let _ = server.run_accept(listener).await;
    });
    sleep(Duration::from_millis(50)).await;
    Ok(DedicatedEnv {
        addr: format!("127.0.0.1:{port}"),
    })
}

pub async fn spawn_signaling() -> anyhow::Result<SignalingEnv> {
    let tcp = tokio::net::TcpListener::bind("127.0.0.1:0").await?;
    let udp = tokio::net::UdpSocket::bind("127.0.0.1:0").await?;
    let tcp_port = tcp.local_addr()?.port();
    let udp_addr = udp.local_addr()?;
    let crypto = PacketCrypto::new(None);
    let server = signaling_server::SignalingServerHost::new(tcp_port, udp_addr.port(), crypto);
    tokio::spawn(async move {
        let _ = server.run(tcp, udp).await;
    });
    sleep(Duration::from_millis(50)).await;
    Ok(SignalingEnv {
        tcp: format!("127.0.0.1:{tcp_port}"),
        udp: udp_addr,
    })
}
