use dedicated_server::{AuthorityServer, ServerCapacity};
use x0_core::{AppConfig, ConfigPath, ConsoleBanner, PacketCrypto};

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let cfg = AppConfig::load();
    let log_dir = ConfigPath::get_log_directory(Some(&cfg.logging.log_path));
    let _log_guard = x0_core::logging::init(
        "dedicated-server",
        &log_dir,
        &cfg.logging.dedicated_file_name,
    );

    let crypto = PacketCrypto::new(Some(cfg.encryption.key.as_str()).filter(|s| !s.is_empty()));
    let capacity = ServerCapacity::new(
        cfg.server_capacity.max_rooms,
        cfg.server_capacity.max_players_per_room,
    );

    ConsoleBanner::write(
        "DedicatedServer",
        "Authority / Lobby / Room",
        &[
            ("Port", cfg.dedicated_server.port.to_string()),
            ("Capacity", capacity.to_string()),
            ("Config", ConfigPath::get_config_directory().display().to_string()),
            ("LogDir", log_dir.display().to_string()),
            ("Encryption", if crypto.is_enabled() { "Y" } else { "N" }.into()),
            (
                "DB",
                cfg.connection_strings
                    .resolved()
                    .map(|c| x0_core::describe_connection(&c))
                    .unwrap_or_else(|_| "(없음)".into()),
            ),
        ],
    );

    let conn = cfg.connection_strings.resolved().map_err(|e| {
        eprintln!("[DedicatedServer] 시작 실패: {e}");
        anyhow::anyhow!(e)
    })?;

    let server = AuthorityServer::new(cfg.dedicated_server.port, &conn, &capacity, crypto)?;
    server.start().await
}
