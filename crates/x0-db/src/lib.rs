//! app_users 로그인 처리

use async_trait::async_trait;
use mysql_async::prelude::*;
use mysql_async::{Opts, Pool, Row};
use sha2::{Digest, Sha256};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum DbError {
    #[error("mysql: {0}")]
    Mysql(#[from] mysql_async::Error),
    #[error("연결 문자열 오류: {0}")]
    ConnStr(String),
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum Gender {
    #[default]
    Unknown,
    Male,
    Female,
}

impl Gender {
    pub fn parse(raw: &str) -> Self {
        match raw.trim().to_ascii_lowercase().as_str() {
            "1" | "m" | "male" | "man" | "남" | "남자" => Self::Male,
            "2" | "f" | "female" | "woman" | "여" | "여자" => Self::Female,
            _ => Self::Unknown,
        }
    }

    /// 클라로 보낼 때는 M, F, 빈 값
    pub fn code(self) -> &'static str {
        match self {
            Self::Male => "M",
            Self::Female => "F",
            Self::Unknown => "",
        }
    }
}

/// app_users 에서 필요한 것만
#[derive(Debug, Clone, Default)]
pub struct AppUser {
    /// seq
    pub id: i64,
    /// 로그인 아이디
    pub uid: String,
    /// bcrypt (라라벨)
    pub password: Option<String>,
    /// SHA-256 hex
    pub sha_pwd: Option<String>,
    pub name: String,
    pub nickname: String,
    pub gender: Gender,
}

impl AppUser {
    /// 닉네임 없으면 이름
    pub fn display_name(&self) -> &str {
        if self.nickname.trim().is_empty() {
            &self.name
        } else {
            &self.nickname
        }
    }
}

#[async_trait]
pub trait UserRepository: Send + Sync {
    async fn get_by_uid(&self, uid: &str) -> Result<Option<AppUser>, DbError>;
    async fn validate_password(&self, uid: &str, password: &str) -> Result<bool, DbError>;
    async fn update_last_login(&self, id: i64) -> Result<(), DbError>;
}

pub struct MysqlUserRepository {
    pool: Pool,
}

impl MysqlUserRepository {
    pub fn new(ado_or_url: &str) -> Result<Self, DbError> {
        let url = to_mysql_url(ado_or_url)?;
        let opts = Opts::from_url(&url).map_err(|e| DbError::ConnStr(e.to_string()))?;
        Ok(Self {
            pool: Pool::new(opts),
        })
    }

    /// 테스터용. 없으면 계정 만들고 있으면 비번만 바꿈 (이름은 그대로)
    pub async fn upsert_login(
        &self,
        uid: &str,
        password: &str,
        display_name: &str,
    ) -> Result<MemberUpsert, DbError> {
        let uid = uid.trim();
        let password = password.trim();
        if uid.is_empty() {
            return Err(DbError::ConnStr("uid 가 비어 있습니다".into()));
        }
        if password.is_empty() {
            return Err(DbError::ConnStr("password 가 비어 있습니다".into()));
        }
        let name = if display_name.trim().is_empty() {
            uid
        } else {
            display_name.trim()
        };
        let sha = sha256_hex(password);
        let bcrypt_pw = laravel_bcrypt(password)?;
        if let Some(existing) = self.get_by_uid(uid).await? {
            let mut conn = self.pool.get_conn().await?;
            conn.exec_drop(
                r#"UPDATE app_users
                   SET password = :password, sha_pwd = :sha_pwd, is_password_set = 1, updated_at = NOW()
                   WHERE id = :id"#,
                params! {
                    "password" => bcrypt_pw.as_str(),
                    "sha_pwd" => sha.as_str(),
                    "id" => existing.id,
                },
            )
            .await?;
            return Ok(MemberUpsert::Updated {
                id: existing.id,
                uid: uid.to_string(),
            });
        }

        let mut conn = self.pool.get_conn().await?;
        conn.exec_drop(
            r#"INSERT INTO app_users
               (uid, name, nickname, email, password, sha_pwd, is_password_set,
                joined_at, created_at, updated_at)
               VALUES
               (:uid, :name, :nickname, :email, :password, :sha_pwd, 1,
                NOW(), NOW(), NOW())"#,
            params! {
                "uid" => uid,
                "name" => name,
                "nickname" => name,
                "email" => format!("{uid}@x0-multiplay.local"),
                "password" => bcrypt_pw.as_str(),
                "sha_pwd" => sha.as_str(),
            },
        )
        .await?;
        let id: i64 = conn
            .query_first("SELECT LAST_INSERT_ID()")
            .await?
            .unwrap_or(0);
        Ok(MemberUpsert::Created {
            id,
            uid: uid.to_string(),
        })
    }
}

#[derive(Debug, Clone)]
pub enum MemberUpsert {
    Created { id: i64, uid: String },
    Updated { id: i64, uid: String },
}

impl MemberUpsert {
    pub fn describe(&self) -> String {
        match self {
            Self::Created { id, uid } => format!("생성 {uid} id={id}"),
            Self::Updated { id, uid } => format!("갱신 {uid} id={id}"),
        }
    }
}

/// 차단/삭제된 회원은 제외
const SELECT_ACTIVE_BY_UID: &str = r#"
    SELECT id, uid, password, sha_pwd, name, nickname, gender
    FROM app_users
    WHERE uid = :uid AND deleted_at IS NULL AND blocked_at IS NULL
    LIMIT 1
"#;

fn map_user(mut row: Row) -> AppUser {
    let text = |row: &mut Row, i: usize| -> Option<String> {
        row.take::<Option<String>, _>(i)
            .flatten()
            .filter(|s| !s.is_empty())
    };
    AppUser {
        id: row.take::<Option<i64>, _>(0).flatten().unwrap_or(0),
        uid: text(&mut row, 1).unwrap_or_default(),
        password: text(&mut row, 2),
        sha_pwd: text(&mut row, 3),
        name: text(&mut row, 4).unwrap_or_default(),
        nickname: text(&mut row, 5).unwrap_or_default(),
        gender: Gender::parse(&text(&mut row, 6).unwrap_or_default()),
    }
}

#[async_trait]
impl UserRepository for MysqlUserRepository {
    async fn get_by_uid(&self, uid: &str) -> Result<Option<AppUser>, DbError> {
        let mut conn = self.pool.get_conn().await?;
        let row: Option<Row> = conn
            .exec_first(SELECT_ACTIVE_BY_UID, params! { "uid" => uid.trim() })
            .await?;
        Ok(row.map(map_user))
    }

    async fn validate_password(&self, uid: &str, password: &str) -> Result<bool, DbError> {
        let Some(user) = self.get_by_uid(uid).await? else {
            return Ok(false);
        };
        Ok(password_matches(&user, password))
    }

    async fn update_last_login(&self, id: i64) -> Result<(), DbError> {
        let mut conn = self.pool.get_conn().await?;
        conn.exec_drop(
            "UPDATE app_users SET last_login_at = NOW() WHERE id = :id",
            params! { "id" => id },
        )
        .await?;
        Ok(())
    }
}

/// sha_pwd 먼저 보고 안 맞으면 bcrypt
fn password_matches(user: &AppUser, password: &str) -> bool {
    if let Some(sha) = user.sha_pwd.as_deref() {
        if eq_ignore_ascii(sha256_hex(password).as_bytes(), sha.as_bytes()) {
            return true;
        }
    }
    user.password
        .as_deref()
        .is_some_and(|hash| verify_bcrypt(password, hash))
}

fn sha256_hex(input: &str) -> String {
    hex::encode(Sha256::digest(input.as_bytes()))
}

/// 라라벨 Hash::make 랑 같은 형식 ($2y$10$...)
fn laravel_bcrypt(password: &str) -> Result<String, DbError> {
    let hash = bcrypt::hash(password, 10).map_err(|e| DbError::ConnStr(e.to_string()))?;
    Ok(hash.replacen("$2b$", "$2y$", 1))
}

fn verify_bcrypt(password: &str, hash: &str) -> bool {
    hash.starts_with("$2") && bcrypt::verify(password, hash).unwrap_or(false)
}

fn eq_ignore_ascii(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter()
        .zip(b.iter())
        .fold(0u8, |acc, (x, y)| acc | (x.to_ascii_lowercase() ^ y.to_ascii_lowercase()))
        == 0
}

/// Server=..;Database=..;User=..;Password=.. 형식이나 mysql:// URL 둘 다 받음
pub fn to_mysql_url(s: &str) -> Result<String, DbError> {
    let trimmed = s.trim();
    if trimmed.starts_with("mysql://") {
        return Ok(trimmed.to_string());
    }

    let mut host = "127.0.0.1".to_string();
    let mut port = "3306".to_string();
    let mut database = String::new();
    let mut user = "root".to_string();
    let mut password = String::new();

    for part in trimmed.split(';') {
        let part = part.trim();
        if part.is_empty() {
            continue;
        }
        let Some((k, v)) = part.split_once('=') else {
            continue;
        };
        match k.trim().to_ascii_lowercase().as_str() {
            "server" | "data source" | "host" => host = v.trim().to_string(),
            "port" => port = v.trim().to_string(),
            "database" | "initial catalog" => database = v.trim().to_string(),
            "user" | "uid" | "user id" => user = v.trim().to_string(),
            "password" | "pwd" => password = v.trim().to_string(),
            _ => {}
        }
    }

    if database.is_empty() {
        return Err(DbError::ConnStr("Database 이름이 없습니다".into()));
    }

    Ok(format!(
        "mysql://{}:{}@{}:{}/{}",
        urlencoding::encode(&user),
        urlencoding::encode(&password),
        host,
        port,
        database
    ))
}

/// 테스트용 메모리 저장소
#[derive(Default)]
pub struct MemoryUserRepository {
    users: std::sync::Mutex<std::collections::HashMap<String, (AppUser, String)>>,
}

impl MemoryUserRepository {
    pub fn add(&self, uid: &str, password: &str, display_name: &str, id: i64) {
        let user = AppUser {
            id,
            uid: uid.to_string(),
            name: display_name.to_string(),
            nickname: display_name.to_string(),
            ..AppUser::default()
        };
        self.users
            .lock()
            .expect("memory user repo")
            .insert(uid.to_string(), (user, password.to_string()));
    }
}

#[async_trait]
impl UserRepository for MemoryUserRepository {
    async fn get_by_uid(&self, uid: &str) -> Result<Option<AppUser>, DbError> {
        Ok(self
            .users
            .lock()
            .expect("memory user repo")
            .get(uid)
            .map(|(u, _)| u.clone()))
    }

    async fn validate_password(&self, uid: &str, password: &str) -> Result<bool, DbError> {
        Ok(self
            .users
            .lock()
            .expect("memory user repo")
            .get(uid)
            .is_some_and(|(_, pw)| pw == password))
    }

    async fn update_last_login(&self, _id: i64) -> Result<(), DbError> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_ado_connection_string() {
        let url = to_mysql_url(
            "Server=db.example.com;Port=3306;Database=member_db;User=app;Password=ab!@#$c;",
        )
        .unwrap();
        assert!(url.contains("db.example.com:3306/member_db"));
        assert!(url.starts_with("mysql://app:ab%21%40%23%24c@"));
    }

    #[test]
    fn laravel_bcrypt_round_trip() {
        let hash = laravel_bcrypt("1111").unwrap();
        assert!(hash.starts_with("$2y$10$"));
        assert!(verify_bcrypt("1111", &hash));
        assert!(!verify_bcrypt("2222", &hash));
        assert!(!verify_bcrypt("1111", "plain"));
    }

    #[test]
    fn sha_pwd_then_bcrypt() {
        let mut user = AppUser {
            sha_pwd: Some("0ffe1abd1a08215353c233d6e009613e95eec4253832a761af28ff37ac5a150c".into()),
            ..AppUser::default()
        };
        assert!(password_matches(&user, "1111"));
        assert!(!password_matches(&user, "2222"));
        user.sha_pwd = None;
        user.password = Some(laravel_bcrypt("2222").unwrap());
        assert!(password_matches(&user, "2222"));
    }

    #[test]
    fn gender_codes() {
        assert_eq!(Gender::parse("male"), Gender::Male);
        assert_eq!(Gender::parse("F"), Gender::Female);
        assert_eq!(Gender::parse("여자"), Gender::Female);
        assert_eq!(Gender::parse(""), Gender::Unknown);
        assert_eq!(Gender::Male.code(), "M");
    }

    #[test]
    fn display_name_falls_back_to_name() {
        let mut u = AppUser {
            name: "소년1".into(),
            ..AppUser::default()
        };
        assert_eq!(u.display_name(), "소년1");
        u.nickname = "boy".into();
        assert_eq!(u.display_name(), "boy");
    }
}
