//! JSON 직렬화

use crate::protocol::NetworkMessage;
use serde::de::DeserializeOwned;
use serde::Serialize;

pub struct MessageSerializer;

impl MessageSerializer {
    pub fn serialize<T: Serialize>(obj: &T) -> String {
        serde_json::to_string(obj).unwrap_or_else(|_| "null".to_string())
    }

    pub fn deserialize<T: DeserializeOwned>(json: &str) -> Option<T> {
        serde_json::from_str(json.trim()).ok()
    }

    pub fn parse_network_message(json: &str) -> Option<NetworkMessage> {
        NetworkMessage::deserialize(json)
    }

    pub fn to_bytes(json: &str) -> Vec<u8> {
        json.as_bytes().to_vec()
    }

    pub fn from_bytes(bytes: &[u8]) -> String {
        String::from_utf8_lossy(bytes).into_owned()
    }
}
