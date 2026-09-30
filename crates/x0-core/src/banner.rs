//! 콘솔 시작 배너

pub struct ConsoleBanner;

impl ConsoleBanner {
    pub fn write(process_name: &str, process_type: &str, info: &[(&str, String)]) {
        let border = "=".repeat(60);
        println!();
        println!("{border}");
        println!("  {process_name} ({process_type})");
        println!("{border}");
        for (key, value) in info {
            println!("  {key}: {value}");
        }
        for line in prototype_lines(process_name) {
            println!("  {line}");
        }
        println!("{border}");
        println!();
    }
}

fn prototype_lines(process_name: &str) -> &'static [&'static str] {
    match process_name {
        "DedicatedServer" => &[
                "[Dedicated host] AuthorityServer(TCP), 로비/룸, MySQL(x0_member), Game loop 20 tick/sec",
        ],
        "SignalingServer" => &[
            "[Relay Server] TCP 시그널링, UDP 공인 IP/포트, 피어 등록·목록·연결 request-relay",
        ],
        "P2PClient" => &["[P2P] 시그널링 경유 NAT 통과, UDP 피어 직접 연결"],
        "AuthorityClient" => &[
            "[Log] tracing (Console+File)",
            "[Thread] tokio task (ReceiveLoop)",
        ],
        "ServerHost" => &["[Host] SignalingServer + DedicatedServer 동시 구동"],
        _ => &[],
    }
}
