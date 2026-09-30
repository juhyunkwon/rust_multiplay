use crate::client::LineClient;
use anyhow::{bail, Context};
use serde_json::{json, Value};
use tokio::time::Duration;
use x0_core::protocol::MessageType;
use x0_core::{AppConfig, PacketCrypto};
use x0_db::{MemberUpsert, MysqlUserRepository};

pub const DEFAULT_USER_A: &str = "boy1";
pub const DEFAULT_PASS_A: &str = "1111";
pub const DEFAULT_USER_B: &str = "boy2";
pub const DEFAULT_PASS_B: &str = "1111";

fn repo() -> anyhow::Result<MysqlUserRepository> {
    let cfg = AppConfig::load();
    let conn = cfg.connection_strings.resolved().map_err(anyhow::Error::msg)?;
    MysqlUserRepository::new(&conn).context("MySQL 연결")
}

pub async fn create_account(
    user_id: &str,
    password: &str,
    display_name: &str,
) -> anyhow::Result<MemberUpsert> {
    let db = repo()?;
    db.upsert_login(user_id, password, display_name)
        .await
        .context("app_users upsert")
}

#[derive(Debug, Clone)]
pub struct NewAccount {
    pub user_id: String,
    pub password: String,
    pub nick: String,
}

pub async fn create_many(accounts: &[NewAccount]) -> anyhow::Result<Vec<MemberUpsert>> {
    let db = repo()?;
    let mut out = Vec::new();
    for (i, acc) in accounts.iter().enumerate() {
        let id = acc.user_id.trim();
        if id.is_empty() {
            continue;
        }
        let nick = if acc.nick.trim().is_empty() {
            id
        } else {
            acc.nick.trim()
        };
        let r = db
            .upsert_login(id, acc.password.trim(), nick)
            .await
            .with_context(|| format!("계정 {}", i + 1))?;
        out.push(r);
    }
    if out.is_empty() {
        bail!("만들 계정이 없습니다. 아이디를 입력하세요.");
    }
    Ok(out)
}

pub async fn create_pair(
    user_a: &str,
    pass_a: &str,
    nick_a: &str,
    user_b: &str,
    pass_b: &str,
    nick_b: &str,
) -> anyhow::Result<Vec<MemberUpsert>> {
    let db = repo()?;
    let a = db
        .upsert_login(user_a, pass_a, nick_a)
        .await
        .context("계정 A")?;
    let b = db
        .upsert_login(user_b, pass_b, nick_b)
        .await
        .context("계정 B")?;
    Ok(vec![a, b])
}

pub async fn login_account(user_id: &str, password: &str, shop_id: i64) -> anyhow::Result<String> {
    let id = user_id.trim();
    if id.is_empty() {
        bail!("아이디가 비어 있습니다");
    }
    let cfg = AppConfig::load();
    let host = cfg.dedicated_server.host.trim();
    let host = if host.is_empty() { "127.0.0.1" } else { host };
    let addr = format!("{host}:{}", cfg.dedicated_server.port);
    let crypto = PacketCrypto::new(Some(cfg.encryption.key.as_str()).filter(|s| !s.is_empty()));
    let mut c = LineClient::connect_crypto(&addr, crypto)
        .await
        .with_context(|| format!("Dedicated 연결 {addr}"))?;
    c.send(
        MessageType::Login,
        json!({ "userId": id, "password": password, "shopId": shop_id }),
    )
    .await?;
    let ack = c
        .recv_type(MessageType::LoginAck, Duration::from_secs(3))
        .await
        .context("LoginAck")?;
    let ok = ack
        .payload
        .as_ref()
        .and_then(|p| p.get("success"))
        .and_then(Value::as_bool)
        .unwrap_or(false);
    if !ok {
        let msg = ack
            .payload
            .as_ref()
            .and_then(|p| p.get("message"))
            .and_then(Value::as_str)
            .unwrap_or("로그인 실패");
        bail!("{id}: {msg}");
    }
    let conn = ack
        .payload
        .as_ref()
        .and_then(|p| p.get("connectionId"))
        .and_then(Value::as_str)
        .unwrap_or("");
    Ok(if conn.is_empty() {
        format!("로그인 성공  {id}")
    } else {
        format!("로그인 성공  {id}  conn={conn}")
    })
}

pub async fn ensure_default_accounts() -> anyhow::Result<Vec<MemberUpsert>> {
    create_pair(
        DEFAULT_USER_A,
        DEFAULT_PASS_A,
        DEFAULT_USER_A,
        DEFAULT_USER_B,
        DEFAULT_PASS_B,
        DEFAULT_USER_B,
    )
    .await
}
