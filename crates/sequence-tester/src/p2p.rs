use crate::client::LineClient;
use crate::harness::{spawn_signaling, LiveHost, SignalingEnv, Target};
use crate::report::SequenceReport;
use serde_json::{json, Value};
use tokio::net::UdpSocket;
use tokio::time::{timeout, Duration};
use x0_core::protocol::{MessageType, NetworkMessage};
use x0_core::PacketCrypto;

const T: Duration = Duration::from_secs(3);

pub async fn run(target: Target, host: Option<&LiveHost>) -> SequenceReport {
    let mut r = SequenceReport::new("P2P 시퀀스");
    match run_inner(&mut r, target, host).await {
        Ok(()) => {}
        Err(e) => {
            if r.failed() == 0 {
                r.fail("시퀀스 중단", e.to_string());
            }
        }
    }
    r
}

async fn run_inner(
    r: &mut SequenceReport,
    target: Target,
    host: Option<&LiveHost>,
) -> anyhow::Result<()> {
    let (env, crypto) = match target {
        Target::Live => {
            let h = host.expect("live host");
            r.pass(
                "실제 시그널링",
                format!("tcp={} udp={}", h.signaling.tcp, h.signaling.udp),
            );
            (h.signaling.clone(), h.crypto.clone())
        }
        Target::InProcess => {
            let e = spawn_signaling().await?;
            r.pass(
                "임시 시그널링(테스트)",
                format!("tcp={} udp={}", e.tcp, e.udp),
            );
            (e, PacketCrypto::new(None))
        }
    };

    run_p2p_flow(r, &env, crypto).await
}

async fn run_p2p_flow(
    r: &mut SequenceReport,
    env: &SignalingEnv,
    crypto: PacketCrypto,
) -> anyhow::Result<()> {
    let mut a = LineClient::connect_crypto(&env.tcp, crypto.clone()).await?;
    let mut b = LineClient::connect_crypto(&env.tcp, crypto.clone()).await?;
    r.pass("TCP 연결", &env.tcp);

    let tag = unique_tag();
    let room = 8100 + (std::process::id() % 90) as i32;
    let want_a = format!("pa{tag}");
    let want_b = format!("pb{tag}");

    a.send(
        MessageType::Register,
        json!({ "id": want_a, "name": "P2PA", "localPort": 0, "roomNumber": room }),
    )
    .await?;
    let ack_a = a.recv_type(MessageType::RegisterAck, T).await?;
    let id_a = ack_a
        .payload
        .as_ref()
        .and_then(|p| p.get("registeredId"))
        .and_then(Value::as_str)
        .unwrap_or(&want_a)
        .to_string();
    let udp_token_a = ack_a
        .payload
        .as_ref()
        .and_then(|p| p.get("udpToken"))
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    r.pass("A Register", format!("{id_a} room={room}"));

    b.send(
        MessageType::Register,
        json!({ "id": want_b, "name": "P2PB", "localPort": 0, "roomNumber": room }),
    )
    .await?;
    let ack_b = b.recv_type(MessageType::RegisterAck, T).await?;
    let id_b = ack_b
        .payload
        .as_ref()
        .and_then(|p| p.get("registeredId"))
        .and_then(Value::as_str)
        .unwrap_or(&want_b)
        .to_string();
    let udp_token_b = ack_b
        .payload
        .as_ref()
        .and_then(|p| p.get("udpToken"))
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    if udp_token_a.is_empty() || udp_token_b.is_empty() {
        r.fail("UDP 토큰", "RegisterAck.udpToken 없음");
        anyhow::bail!("udp token");
    }
    r.pass("B Register", &id_b);

    let udp_a = UdpSocket::bind("0.0.0.0:0").await?;
    let udp_b = UdpSocket::bind("0.0.0.0:0").await?;
    let pkt_a = format!("REGISTER_UDP:{id_a}:{}:{udp_token_a}", udp_a.local_addr()?.port());
    let pkt_b = format!("REGISTER_UDP:{id_b}:{}:{udp_token_b}", udp_b.local_addr()?.port());
    udp_a.send_to(pkt_a.as_bytes(), env.udp).await?;
    udp_b.send_to(pkt_b.as_bytes(), env.udp).await?;
    tokio::time::sleep(Duration::from_millis(120)).await;
    r.pass("UDP REGISTER", format!("stun → {}", env.udp));

    b.send(MessageType::PeerList, json!({})).await?;
    let list = b.recv_type(MessageType::PeerList, T).await?;
    let peers = list
        .payload
        .as_ref()
        .and_then(|p| p.get("peers"))
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let Some(peer_a) = peers
        .iter()
        .find(|p| p.get("id").and_then(Value::as_str) == Some(id_a.as_str()))
    else {
        r.fail("PeerList", format!("A 없음 {:?}", peers));
        anyhow::bail!("peerlist");
    };
    let (ip_a, port_a) = public_ep(peer_a);
    r.pass("PeerList", format!("A={id_a} {ip_a}:{port_a}"));

    a.send(MessageType::PeerList, json!({})).await?;
    let list_a = a.recv_type(MessageType::PeerList, T).await?;
    let peers_a = list_a
        .payload
        .as_ref()
        .and_then(|p| p.get("peers"))
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let Some(peer_b) = peers_a
        .iter()
        .find(|p| p.get("id").and_then(Value::as_str) == Some(id_b.as_str()))
    else {
        r.fail("PeerList B", format!("B 없음 {:?}", peers_a));
        anyhow::bail!("peerlist b");
    };
    let (ip_b, port_b) = public_ep(peer_b);

    b.send(
        MessageType::ConnectRequest,
        json!({ "peerId": id_a, "requesterId": id_b }),
    )
    .await?;
    let req = a.recv_type(MessageType::ConnectRequest, T).await?;
    r.pass("ConnectRequest", format!("{:?}", req.payload));

    a.send(
        MessageType::ConnectResponse,
        json!({ "accepted": true, "peerId": id_b }),
    )
    .await?;
    let resp = b.recv_type(MessageType::ConnectResponse, T).await?;
    let accepted = resp
        .payload
        .as_ref()
        .and_then(|p| p.get("accepted"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if !accepted {
        r.fail("ConnectResponse", format!("{:?}", resp.payload));
        anyhow::bail!("connect resp");
    }
    r.pass("ConnectResponse", "accepted=true");

    tokio::time::sleep(Duration::from_millis(50)).await;
    b.send(MessageType::HolePunch, json!({ "peerId": id_a })).await?;
    let hp_a = a.recv_type(MessageType::HolePunch, T).await?;
    r.pass("HolePunch 시그널링 → A", format!("{:?}", hp_a.payload));
    a.send(MessageType::HolePunch, json!({ "peerId": id_b })).await?;
    let hp_b = b.recv_type(MessageType::HolePunch, T).await?;
    r.pass("HolePunch 시그널링 → B", format!("{:?}", hp_b.payload));

    // PeerList 받을 때 상대 REGISTER_UDP 가 아직 안 들어왔을 수 있음. HolePunch 에 온 주소를 씀
    let (ip_b, port_b) = hp_a
        .payload
        .as_ref()
        .map(public_ep)
        .filter(|(_, p)| *p != 0)
        .unwrap_or((ip_b, port_b));
    let (ip_a, port_a) = hp_b
        .payload
        .as_ref()
        .map(public_ep)
        .filter(|(_, p)| *p != 0)
        .unwrap_or((ip_a, port_a));

    if port_a == 0 || port_b == 0 {
        r.fail(
            "UDP 공인 포트",
            format!("A={ip_a}:{port_a} B={ip_b}:{port_b} — REGISTER_UDP 실패"),
        );
        anyhow::bail!("public port");
    }

    let dest_a = format!("{ip_a}:{port_a}");
    let dest_b = format!("{ip_b}:{port_b}");
    let punch_a = format!("HOLEPUNCH:{tag}:A");
    let punch_b = format!("HOLEPUNCH:{tag}:B");
    let mut got_a = false;
    let mut got_b = false;
    let mut buf_a = [0u8; 2048];
    let mut buf_b = [0u8; 2048];
    let punch_deadline = tokio::time::Instant::now() + Duration::from_secs(3);
    while tokio::time::Instant::now() < punch_deadline && (!got_a || !got_b) {
        udp_a.send_to(punch_a.as_bytes(), dest_b.as_str()).await?;
        udp_b.send_to(punch_b.as_bytes(), dest_a.as_str()).await?;
        let slice = Duration::from_millis(80);
        tokio::select! {
            Ok(Ok((n, from))) = timeout(slice, udp_a.recv_from(&mut buf_a)) => {
                let got = String::from_utf8_lossy(&buf_a[..n]);
                if got.contains(&punch_b) && !got_a {
                    got_a = true;
                    r.pass("UDP HOLEPUNCH 수신 A", format!("from={from} {got}"));
                }
            }
            Ok(Ok((n, from))) = timeout(slice, udp_b.recv_from(&mut buf_b)) => {
                let got = String::from_utf8_lossy(&buf_b[..n]);
                if got.contains(&punch_a) && !got_b {
                    got_b = true;
                    r.pass("UDP HOLEPUNCH 수신 B", format!("from={from} {got}"));
                }
            }
            else => {}
        }
    }
    if !got_a || !got_b {
        r.fail(
            "UDP 홀펀치 패킷",
            format!("A수신={got_a} B수신={got_b} dest {dest_a} <-> {dest_b}"),
        );
        anyhow::bail!("holepunch udp");
    }

    let token = format!("seq-p2p-{tag}");
    let chat = NetworkMessage::new(
        MessageType::Chat,
        json!({ "fromId": id_b, "message": token }),
    )
    .serialize();
    let wire = crypto.encode_for_send(&chat);
    udp_b.send_to(wire.as_bytes(), dest_a.as_str()).await?;
    let mut buf = [0u8; 2048];
    let deadline = tokio::time::Instant::now() + Duration::from_secs(2);
    loop {
        let left = deadline.saturating_duration_since(tokio::time::Instant::now());
        if left.is_zero() {
            r.fail("UDP Chat", format!("수신 없음 dest={dest_a}"));
            anyhow::bail!("udp chat");
        }
        match timeout(left, udp_a.recv_from(&mut buf)).await {
            Ok(Ok((n, from))) => {
                let raw = String::from_utf8_lossy(&buf[..n]);
                let got = crypto.decode_from_receive(raw.trim());
                if got.contains(&token) {
                    r.pass("UDP Chat 직접 전송", format!("from={from} {token}"));
                    break;
                }
            }
            _ => {
                r.fail("UDP Chat", format!("수신 없음 dest={dest_a}"));
                anyhow::bail!("udp chat");
            }
        }
    }

    drop(b);
    tokio::time::sleep(Duration::from_millis(80)).await;
    let left = a.recv_type(MessageType::Disconnect, T).await?;
    let left_id = left
        .payload
        .as_ref()
        .and_then(|p| p.get("peerId").or_else(|| p.get("fromId")))
        .and_then(Value::as_str)
        .unwrap_or("");
    if left_id != id_b {
        r.fail("Disconnect 통지", format!("{:?}", left.payload));
        anyhow::bail!("disconnect");
    }
    r.pass("Disconnect 통지", format!("B 퇴장 → A {left_id}"));
    Ok(())
}

fn public_ep(peer: &Value) -> (String, u16) {
    let ip = peer
        .get("publicIp")
        .and_then(Value::as_str)
        .unwrap_or("127.0.0.1");
    let ip = if ip == "0.0.0.0" || ip.is_empty() {
        "127.0.0.1"
    } else {
        ip
    };
    let port = peer
        .get("publicPort")
        .and_then(Value::as_i64)
        .unwrap_or(0) as u16;
    (ip.to_string(), port)
}

fn unique_tag() -> String {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    format!("{:x}", ms % 0xFFFFF)
}
