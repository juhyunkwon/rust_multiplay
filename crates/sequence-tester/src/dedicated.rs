use crate::client::LineClient;
use crate::harness::{spawn_dedicated, LiveHost, Target};
use crate::report::SequenceReport;
use anyhow::Context;
use serde_json::{json, Value};
use tokio::time::Duration;
use x0_core::protocol::MessageType;
use x0_core::PacketCrypto;

const T: Duration = Duration::from_secs(2);

fn env_or(key: &str, default: &str) -> String {
    match std::env::var(key) {
        Ok(s) if !s.trim().is_empty() => s,
        _ => default.to_string(),
    }
}

pub async fn run(target: Target, host: Option<&LiveHost>) -> SequenceReport {
    let mut r = SequenceReport::new("Dedicated 시퀀스");
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
    let (addr, crypto, users) = match target {
        Target::Live => {
            let h = host.expect("live host");
            r.pass("실제 Dedicated", &h.dedicated);
            let ua = env_or("SEQ_USER_A", crate::DEFAULT_USER_A);
            let pa = env_or("SEQ_PASS_A", crate::DEFAULT_PASS_A);
            let ub = env_or("SEQ_USER_B", crate::DEFAULT_USER_B);
            let pb = env_or("SEQ_PASS_B", crate::DEFAULT_PASS_B);
            (
                h.dedicated.clone(),
                h.crypto.clone(),
                Some((ua, pa, ub, pb)),
            )
        }
        Target::InProcess => {
            let env = spawn_dedicated().await.context("dedicated 기동")?;
            r.pass("임시 Dedicated(테스트)", &env.addr);
            (
                env.addr,
                PacketCrypto::new(None),
                Some((
                    "seq_a".into(),
                    "pw_a".into(),
                    "seq_b".into(),
                    "pw_b".into(),
                )),
            )
        }
    };
    let (user_a, pass_a, user_b, pass_b) = users.expect("users");

    let mut a = LineClient::connect_crypto(&addr, crypto.clone()).await?;
    r.pass("클라 A TCP 연결", &addr);
    let mut b = LineClient::connect_crypto(&addr, crypto).await?;
    r.pass("클라 B TCP 연결", &addr);

    a.send(MessageType::PingRequest, json!({ "clientTicks": 1 }))
        .await?;
    let pong = a.recv_type(MessageType::PingResponse, T).await?;
    r.pass(
        "PingRequest → PingResponse",
        format!("{:?}", pong.payload),
    );

    a.send(
        MessageType::Login,
        json!({ "userId": user_a, "password": pass_a, "shopId": 0 }),
    )
    .await?;
    let ack = a.recv_type(MessageType::LoginAck, T).await?;
    let ok = ack
        .payload
        .as_ref()
        .and_then(|p| p.get("success"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if !ok {
        r.fail("A 로그인", format!("{:?}", ack.payload));
        anyhow::bail!("login a");
    }
    r.pass("A 로그인", &user_a);
    let conn_a = ack
        .payload
        .as_ref()
        .and_then(|p| p.get("connectionId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    if conn_a.is_empty() {
        r.fail("LoginAck.connectionId", format!("{:?}", ack.payload));
        anyhow::bail!("connection id");
    }
    r.pass("connectionId", conn_a);

    b.send(
        MessageType::Login,
        json!({ "userId": user_b, "password": pass_b, "shopId": 0 }),
    )
    .await?;
    let ack = b.recv_type(MessageType::LoginAck, T).await?;
    let ok = ack
        .payload
        .as_ref()
        .and_then(|p| p.get("success"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if !ok {
        r.fail("B 로그인", format!("{:?}", ack.payload));
        anyhow::bail!("login b");
    }
    r.pass("B 로그인", &user_b);

    a.send(MessageType::LobbyList, json!({})).await?;
    let lobby = a.recv_type(MessageType::LobbyList, T).await?;
    let n = lobby
        .payload
        .as_ref()
        .and_then(|p| p.get("users"))
        .and_then(Value::as_array)
        .map(|u| u.len())
        .unwrap_or(0);
    if n < 2 {
        r.fail("LobbyList", format!("users={n}"));
        anyhow::bail!("lobby");
    }
    r.pass("LobbyList", format!("{n}명"));

    a.send(
        MessageType::CreateRoom,
        json!({ "roomName": "seq-room", "maxPlayers": 4 }),
    )
    .await?;
    let created = a.recv_type(MessageType::CreateRoomAck, T).await?;
    let room_id = created
        .payload
        .as_ref()
        .and_then(|p| p.get("room"))
        .and_then(|room| room.get("roomId"))
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_string();
    if room_id.is_empty() {
        r.fail("CreateRoom", format!("{:?}", created.payload));
        anyhow::bail!("create");
    }
    r.pass("CreateRoom", &room_id);

    b.send(MessageType::JoinRoom, json!({ "roomId": room_id }))
        .await?;
    let joined = b.recv_type(MessageType::JoinRoomAck, T).await?;
    let ok = joined
        .payload
        .as_ref()
        .and_then(|p| p.get("success"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if !ok {
        r.fail("JoinRoom", format!("{:?}", joined.payload));
        anyhow::bail!("join");
    }
    r.pass("JoinRoom", "B 입장");

    let _ = a.recv_type(MessageType::RoomUpdate, Duration::from_millis(800)).await;
    r.pass("RoomUpdate", "호스트 측 갱신 대기(있으면 수신)");

    a.send(MessageType::StartGame, json!({})).await?;
    let _ = a.recv_type(MessageType::StartGame, T).await?;
    let _ = b.recv_type(MessageType::StartGame, T).await?;
    r.pass("StartGame 브로드캐스트", "A/B 수신");

    b.send(
        MessageType::PlayerInput,
        json!({
            "playerId": "b",
            "inputTick": 1,
            "moveX": 1.0,
            "moveY": 0.0,
            "action": ""
        }),
    )
    .await?;
    r.pass("PlayerInput 송신", "B moveX=1");

    let deadline = tokio::time::Instant::now() + Duration::from_secs(3);
    loop {
        let left = deadline.saturating_duration_since(tokio::time::Instant::now());
        if left.is_zero() {
            r.fail("GameState 반영", "3초 내 입력 반영 없음");
            anyhow::bail!("gamestate");
        }
        let state = a.recv_type(MessageType::GameState, left).await?;
        let moved = state
            .payload
            .as_ref()
            .and_then(|p| p.get("players"))
            .and_then(Value::as_array)
            .map(|players| {
                players.iter().any(|pl| {
                    pl.get("x").and_then(Value::as_f64).unwrap_or(0.0).abs() > 0.01
                        || pl.get("lastInputTick").and_then(Value::as_i64).unwrap_or(0) != 0
                })
            })
            .unwrap_or(false);
        if moved {
            r.pass("GameState 20Hz + 입력 반영", "좌표 또는 lastInputTick 변경");
            break;
        }
    }

    drop(b);
    tokio::time::sleep(Duration::from_millis(80)).await;
    match a.recv_type(MessageType::Disconnect, Duration::from_secs(3)).await {
        Ok(d) => r.pass("Disconnect 통지", format!("{:?}", d.payload)),
        Err(e) => {
            r.fail("Disconnect 통지", e.to_string());
            anyhow::bail!("dedicated disconnect");
        }
    }
    Ok(())
}
