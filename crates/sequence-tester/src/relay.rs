use crate::client::LineClient;
use crate::harness::{spawn_signaling, LiveHost, Target};
use crate::report::SequenceReport;
use serde_json::{json, Value};
use tokio::time::Duration;
use x0_core::protocol::MessageType;
use x0_core::PacketCrypto;

const T: Duration = Duration::from_secs(3);

pub async fn run(target: Target, host: Option<&LiveHost>) -> SequenceReport {
    let mut r = SequenceReport::new("릴레이 시퀀스");
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
            r.pass("실제 시그널링", &h.signaling.tcp);
            (h.signaling.clone(), h.crypto.clone())
        }
        Target::InProcess => {
            let e = spawn_signaling().await?;
            r.pass("임시 시그널링(테스트)", &e.tcp);
            (e, PacketCrypto::new(None))
        }
    };

    let mut a = LineClient::connect_crypto(&env.tcp, crypto.clone()).await?;
    let mut b = LineClient::connect_crypto(&env.tcp, crypto.clone()).await?;
    let mut c = LineClient::connect_crypto(&env.tcp, crypto).await?;
    r.pass("TCP 연결", &env.tcp);

    let tag = unique_tag();
    let room_ab = 8300 + (std::process::id() % 80) as i32;
    let room_c = room_ab + 1;
    let id_a = format!("ra{tag}");
    let id_b = format!("rb{tag}");
    let id_c = format!("rc{tag}");
    let token_ab = format!("SEQ-RELAY-AB-{tag}");
    let token_ba = format!("SEQ-RELAY-BA-{tag}");

    a.send(
        MessageType::Register,
        json!({ "id": id_a, "name": "RelA", "localPort": 1, "roomNumber": room_ab }),
    )
    .await?;
    a.recv_type(MessageType::RegisterAck, T).await?;
    b.send(
        MessageType::Register,
        json!({ "id": id_b, "name": "RelB", "localPort": 1, "roomNumber": room_ab }),
    )
    .await?;
    b.recv_type(MessageType::RegisterAck, T).await?;
    c.send(
        MessageType::Register,
        json!({ "id": id_c, "name": "RelC", "localPort": 1, "roomNumber": room_c }),
    )
    .await?;
    c.recv_type(MessageType::RegisterAck, T).await?;
    r.pass(
        "Register",
        format!("룸 {room_ab}: {id_a}/{id_b} , 룸 {room_c}: {id_c}"),
    );

    a.send(MessageType::RoomBroadcast, json!({ "message": token_ab }))
        .await?;
    let got = b.recv_type(MessageType::RoomBroadcast, T).await?;
    let text = got
        .payload
        .as_ref()
        .and_then(|p| p.get("message"))
        .and_then(Value::as_str)
        .unwrap_or("");
    if text != token_ab {
        r.fail("A→B RoomBroadcast", format!("{:?}", got.payload));
        anyhow::bail!("ab");
    }
    let from = got
        .payload
        .as_ref()
        .and_then(|p| p.get("fromId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    r.pass("A→B 릴레이", format!("fromId={from}"));

    b.send(MessageType::RoomBroadcast, json!({ "message": token_ba }))
        .await?;
    let got = a.recv_type(MessageType::RoomBroadcast, T).await?;
    let text = got
        .payload
        .as_ref()
        .and_then(|p| p.get("message"))
        .and_then(Value::as_str)
        .unwrap_or("");
    if text != token_ba {
        r.fail("B→A RoomBroadcast", format!("{:?}", got.payload));
        anyhow::bail!("ba");
    }
    r.pass("B→A 릴레이", "양방향");

    match c
        .recv_type(MessageType::RoomBroadcast, Duration::from_millis(400))
        .await
    {
        Ok(msg) => {
            r.fail(
                "룸 격리",
                format!("다른 룸 C가 수신함: {:?}", msg.payload),
            );
            anyhow::bail!("leak");
        }
        Err(_) => r.pass("룸 격리", format!("룸 {room_c} 클라 C는 미수신")),
    }

    a.send(
        MessageType::SignalingPing,
        json!({ "clientTicks": 42 }),
    )
    .await?;
    let pong = a.recv_type(MessageType::SignalingPong, T).await?;
    r.pass("SignalingPing → Pong", format!("{:?}", pong.payload));

    a.send(MessageType::Ping, json!({ "clientTicks": 7 })).await?;
    let pong10 = a.recv_type(MessageType::Pong, T).await?;
    r.pass("Ping(10) → Pong(11)", format!("{:?}", pong10.payload));

    drop(b);
    tokio::time::sleep(Duration::from_millis(80)).await;
    let left = a.recv_type(MessageType::Disconnect, T).await?;
    let left_id = left
        .payload
        .as_ref()
        .and_then(|p| p.get("peerId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    if left_id != id_b {
        r.fail("Disconnect 통지", format!("{:?}", left.payload));
        anyhow::bail!("disconnect");
    }
    r.pass("Disconnect 통지", format!("{left_id} 퇴장"));
    Ok(())
}

fn unique_tag() -> String {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    format!("{:x}", ms % 0xFFFFF)
}
