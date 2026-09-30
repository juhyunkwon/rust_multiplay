//! 메시지 타입, 페이로드 정의
//!
//! MessageType 은 JSON 에서 숫자. 필드명은 camelCase

use serde::{Deserialize, Serialize};
use serde_json::Value;
use serde_repr::{Deserialize_repr, Serialize_repr};

/// 메시지 타입. C#/Unity MessageType 이랑 번호 맞춰야 함
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize_repr, Deserialize_repr)]
#[repr(u8)]
pub enum MessageType {
    Unknown = 0,

    // 시그널링
    Register = 1,
    RegisterAck = 2,
    PeerList = 3,
    ConnectRequest = 4,
    ConnectResponse = 5,
    HolePunch = 6,
    RoomBroadcast = 7,
    SignalingPing = 8,
    SignalingPong = 9,

    // P2P / 게임
    Ping = 10,
    Pong = 11,
    Chat = 12,
    GameState = 13,
    PlayerInput = 14,
    Disconnect = 15,

    // 로비/룸
    Login = 20,
    LoginAck = 21,
    LobbyList = 22,
    RoomList = 23,
    CreateRoom = 24,
    CreateRoomAck = 25,
    JoinRoom = 26,
    JoinRoomAck = 27,
    LeaveRoom = 28,
    RoomUpdate = 29,
    StartGame = 30,
    PingRequest = 31,
    PingResponse = 32,
}

impl Default for MessageType {
    fn default() -> Self {
        Self::Unknown
    }
}

/// {"t":타입, "p":내용, "ts":시간}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NetworkMessage {
    #[serde(rename = "t")]
    pub msg_type: MessageType,
    #[serde(rename = "p", default)]
    pub payload: Option<Value>,
    #[serde(rename = "ts")]
    pub timestamp: i64,
}

impl NetworkMessage {
    pub fn new(msg_type: MessageType, payload: impl Serialize) -> Self {
        let payload = serde_json::to_value(payload).ok();
        Self {
            msg_type,
            payload,
            timestamp: unix_now_ms(),
        }
    }

    pub fn empty(msg_type: MessageType) -> Self {
        Self {
            msg_type,
            payload: Some(serde_json::json!({})),
            timestamp: unix_now_ms(),
        }
    }

    pub fn serialize(&self) -> String {
        serde_json::to_string(self).unwrap_or_else(|_| "{}".to_string())
    }

    pub fn deserialize(json: &str) -> Option<Self> {
        serde_json::from_str(json.trim()).ok()
    }
}

pub fn unix_now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// C# DateTime.UtcNow.Ticks 랑 같은 값 (100ns, 0001-01-01 기준). 핑 RTT 계산용
pub fn csharp_utc_ticks() -> i64 {
    // 1970-01-01 = 621355968000000000 ticks
    const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;
    let unix_100ns = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| (d.as_nanos() / 100) as i64)
        .unwrap_or(0);
    UNIX_EPOCH_TICKS + unix_100ns
}

/// ticks 차이를 ms 로
pub fn ticks_to_rtt_ms(sent_ticks: i64) -> Option<i32> {
    let elapsed = ((csharp_utc_ticks() - sent_ticks) / 10_000) as i32;
    if (0..=60_000).contains(&elapsed) {
        Some(elapsed)
    } else {
        // 예전 클라는 unix ms 로 보냄
        let now = unix_now_ms();
        let elapsed_ms = (now - sent_ticks) as i32;
        if (0..=60_000).contains(&elapsed_ms) {
            Some(elapsed_ms)
        } else {
            None
        }
    }
}

// ----- 시그널링 -----

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct RegisterRequest {
    pub id: String,
    pub name: String,
    pub local_port: i32,
    #[serde(default = "default_room_number")]
    pub room_number: i32,
    #[serde(default)]
    pub local_ip: String,
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub dedicated_room_id: String,
}

fn default_room_number() -> i32 {
    1
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct RegisterResponse {
    pub registered_id: String,
    pub public_ip: String,
    pub public_port: i32,
    #[serde(default)]
    pub is_master: bool,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub udp_token: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PeerInfo {
    pub id: String,
    pub name: String,
    pub public_ip: String,
    pub public_port: i32,
    #[serde(default)]
    pub is_master: bool,
    #[serde(default)]
    pub local_ip: String,
    #[serde(default)]
    pub local_port: i32,
    #[serde(default)]
    pub user_id: String,
    #[serde(default)]
    pub dedicated_room_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ConnectRequestPayload {
    pub peer_id: String,
    pub requester_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub requester_info: Option<PeerInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ConnectResponsePayload {
    pub accepted: bool,
    pub peer_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub requester_info: Option<PeerInfo>,
}

// ----- 로비 -----

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct LoginRequest {
    pub user_id: String,
    pub password: String,
    #[serde(default)]
    pub shop_id: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct LoginResponse {
    pub success: bool,
    #[serde(default)]
    pub message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub user: Option<UserInfo>,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub connection_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct UserInfo {
    pub id: i64,
    pub user_id: String,
    pub user_name: String,
    pub display_name: String,
    /// M, F, 없으면 빈 값
    #[serde(default)]
    pub gender: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct RoomInfo {
    pub room_id: String,
    pub name: String,
    pub host_id: String,
    pub host_name: String,
    pub player_count: i32,
    pub max_players: i32,
    pub is_playing: bool,
}

pub const MAX_PLAYERS_PER_ROOM: i32 = 1000;

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct CreateRoomRequest {
    pub room_name: String,
    #[serde(default = "default_max_players")]
    pub max_players: i32,
}

fn default_max_players() -> i32 {
    6
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct JoinRoomRequest {
    pub room_id: String,
}

// ----- 게임 -----

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct GameStatePayload {
    pub server_tick: i64,
    #[serde(default)]
    pub players: Vec<PlayerState>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PlayerState {
    pub id: String,
    pub name: String,
    pub x: f32,
    pub y: f32,
    pub health: i32,
    pub last_input_tick: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PlayerInputPayload {
    pub player_id: String,
    pub input_tick: i64,
    pub move_x: f32,
    pub move_y: f32,
    #[serde(default)]
    pub action: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn message_type_serializes_as_number() {
        let msg = NetworkMessage::new(MessageType::Register, serde_json::json!({"id": "ab"}));
        let s = msg.serialize();
        assert!(s.contains("\"t\":1"), "{s}");
        let back = NetworkMessage::deserialize(&s).unwrap();
        assert_eq!(back.msg_type, MessageType::Register);
    }
}
