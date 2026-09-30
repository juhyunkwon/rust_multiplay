//! 설정 파일 찾기/읽기

use serde::Deserialize;
use std::fs;
use std::path::{Path, PathBuf};

pub struct ConfigPath;

impl ConfigPath {
    pub fn get_config_directory() -> PathBuf {
        if let Ok(env_path) = std::env::var("CONFIG_PATH") {
            let p = PathBuf::from(env_path);
            if p.is_dir() {
                return p;
            }
            if p.is_file() {
                if let Some(parent) = p.parent() {
                    return parent.to_path_buf();
                }
            }
        }

        // exe 위치, 현재 폴더에서 위로 올라가며 config/appsettings.json 찾음
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|e| e.parent().map(Path::to_path_buf));
        let cwd = std::env::current_dir().ok();
        for start in [exe_dir, cwd.clone()].into_iter().flatten() {
            if let Some(dir) = find_config_upward(&start) {
                return dir;
            }
        }

        cwd.unwrap_or_else(|| PathBuf::from("."))
    }

    pub fn get_app_settings_path() -> PathBuf {
        Self::get_config_directory().join("appsettings.json")
    }

    pub fn get_log_directory(log_path: Option<&str>) -> PathBuf {
        let path = log_path.unwrap_or("logs");
        let p = Path::new(path);
        if p.is_absolute() {
            let _ = fs::create_dir_all(p);
            return p.to_path_buf();
        }

        let config_dir = Self::get_config_directory();
        if let Some(parent) = config_dir.parent() {
            let s = parent.to_string_lossy();
            if s.contains("bin") || s.contains("obj") || s.contains("target") {
                if let Some(root) = find_project_root_from(&config_dir) {
                    let log_dir = root.join(path);
                    let _ = fs::create_dir_all(&log_dir);
                    return log_dir;
                }
            }
            let fallback = parent.join(path);
            let _ = fs::create_dir_all(&fallback);
            return fallback;
        }

        let fallback = config_dir.join(path);
        let _ = fs::create_dir_all(&fallback);
        fallback
    }
}

fn find_config_upward(start: &Path) -> Option<PathBuf> {
    let mut dir = start.to_path_buf();
    for _ in 0..10 {
        let config = dir.join("config");
        if config.join("appsettings.json").is_file() {
            return Some(config);
        }
        dir = dir.parent()?.to_path_buf();
    }
    None
}

fn find_project_root_from(start: &Path) -> Option<PathBuf> {
    let mut dir = start.to_path_buf();
    for _ in 0..10 {
        let parent = dir.parent()?.to_path_buf();
        if parent.join("src").is_dir() || parent.join("crates").is_dir() {
            return Some(parent);
        }
        dir = parent;
    }
    None
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default)]
pub struct AppConfig {
    #[serde(rename = "DedicatedServer")]
    pub dedicated_server: DedicatedServerConfig,
    #[serde(rename = "SignalingServer")]
    pub signaling_server: SignalingServerConfig,
    #[serde(rename = "ConnectionStrings")]
    pub connection_strings: ConnectionStrings,
    #[serde(rename = "Encryption")]
    pub encryption: EncryptionConfig,
    #[serde(rename = "Logging")]
    pub logging: LoggingConfig,
    #[serde(rename = "ServerCapacity")]
    pub server_capacity: ServerCapacityConfig,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct DedicatedServerConfig {
    #[serde(rename = "Host")]
    pub host: String,
    #[serde(rename = "Port")]
    pub port: u16,
}

impl Default for DedicatedServerConfig {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".into(),
            port: 19060,
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct SignalingServerConfig {
    #[serde(rename = "Host")]
    pub host: String,
    #[serde(rename = "TcpPort")]
    pub tcp_port: u16,
    #[serde(rename = "UdpPort")]
    pub udp_port: u16,
}

impl Default for SignalingServerConfig {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".into(),
            tcp_port: 19050,
            udp_port: 19051,
        }
    }
}

/// 있으면 설정 파일 대신 이걸 DB 연결 문자열로 씀
pub const DB_CONNECTION_ENV: &str = "X0_DB_CONNECTION";

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default)]
pub struct ConnectionStrings {
    #[serde(rename = "Default")]
    pub default: String,
}

/// 배너 출력용 user@host:port/db (pw Y|N). 비번은 안 찍음
pub fn describe_connection(conn: &str) -> String {
    let mut host = "";
    let mut port = "3306";
    let mut db = "";
    let mut user = "";
    let mut has_pw = false;
    for part in conn.split(';') {
        let Some((k, v)) = part.split_once('=') else {
            continue;
        };
        let v = v.trim();
        match k.trim().to_ascii_lowercase().as_str() {
            "server" | "host" | "data source" => host = v,
            "port" => port = v,
            "database" | "initial catalog" => db = v,
            "user" | "uid" | "user id" => user = v,
            "password" | "pwd" => has_pw = !v.is_empty(),
            _ => {}
        }
    }
    format!("{user}@{host}:{port}/{db} (pw {})", if has_pw { "Y" } else { "N" })
}

impl ConnectionStrings {
    /// X0_DB_CONNECTION 먼저, 없으면 ConnectionStrings.Default. 둘 다 없으면 에러
    pub fn resolved(&self) -> Result<String, String> {
        if let Ok(env) = std::env::var(DB_CONNECTION_ENV) {
            if !env.trim().is_empty() {
                return Ok(env.trim().to_string());
            }
        }
        let s = self.default.trim();
        if s.is_empty() {
            return Err(format!(
                "DB 연결 문자열이 없습니다. config/appsettings.json 의 ConnectionStrings.Default \
                 (config/appsettings.example.json 참고) 또는 환경변수 {DB_CONNECTION_ENV} 를 설정하세요. \
                 설정 폴더: {}",
                ConfigPath::get_config_directory().display()
            ));
        }
        Ok(s.to_string())
    }
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default)]
pub struct EncryptionConfig {
    #[serde(rename = "Key")]
    pub key: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct LoggingConfig {
    #[serde(rename = "LogPath")]
    pub log_path: String,
    #[serde(rename = "DedicatedFileName")]
    pub dedicated_file_name: String,
    #[serde(rename = "SignalingFileName")]
    pub signaling_file_name: String,
    #[serde(rename = "RetainedFileCountLimit")]
    pub retained_file_count_limit: usize,
}

impl Default for LoggingConfig {
    fn default() -> Self {
        Self {
            log_path: "logs".into(),
            dedicated_file_name: "dedicated-.log".into(),
            signaling_file_name: "signaling-.log".into(),
            retained_file_count_limit: 31,
        }
    }
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(default)]
pub struct ServerCapacityConfig {
    #[serde(rename = "MaxRooms")]
    pub max_rooms: Option<i32>,
    #[serde(rename = "MaxPlayersPerRoom")]
    pub max_players_per_room: Option<i32>,
}

impl AppConfig {
    pub fn load() -> Self {
        let path = ConfigPath::get_app_settings_path();
        Self::load_from(&path)
    }

    pub fn load_from(path: &Path) -> Self {
        match fs::read_to_string(path) {
            Ok(text) => match serde_json::from_str(text.trim_start_matches('\u{feff}')) {
                Ok(cfg) => cfg,
                Err(e) => {
                    eprintln!("[config] {} 파싱 실패 — 기본값 사용: {e}", path.display());
                    Self::default()
                }
            },
            Err(e) => {
                eprintln!("[config] {} 읽기 실패 — 기본값 사용: {e}", path.display());
                Self::default()
            }
        }
    }
}
