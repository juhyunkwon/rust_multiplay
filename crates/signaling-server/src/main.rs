use x0_core::{AppConfig, ConfigPath, ConsoleBanner, PacketCrypto};
use signaling_server::SignalingServerHost;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let cfg = AppConfig::load();
    let log_dir = ConfigPath::get_log_directory(Some(&cfg.logging.log_path));
    let _log_guard = x0_core::logging::init(
        "signaling-server",
        &log_dir,
        &cfg.logging.signaling_file_name,
    );

    let crypto = PacketCrypto::new(Some(cfg.encryption.key.as_str()).filter(|s| !s.is_empty()));
    ConsoleBanner::write(
        "SignalingServer",
        "Relay / NAT traversal",
        &[
            ("TCP", cfg.signaling_server.tcp_port.to_string()),
            ("UDP", cfg.signaling_server.udp_port.to_string()),
            ("Config", ConfigPath::get_config_directory().display().to_string()),
            ("LogDir", log_dir.display().to_string()),
            ("Encryption", if crypto.is_enabled() { "Y" } else { "N" }.into()),
        ],
    );

    let server = SignalingServerHost::new(
        cfg.signaling_server.tcp_port,
        cfg.signaling_server.udp_port,
        crypto,
    );
    server.start().await
}
