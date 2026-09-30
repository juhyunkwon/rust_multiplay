//! 로비/룸 관리

use dashmap::DashMap;
use x0_core::protocol::{RoomInfo, UserInfo, MAX_PLAYERS_PER_ROOM};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::Arc;

#[derive(Debug, Clone)]
pub struct LobbyUser {
    pub connection_id: String,
    pub seq_id: i64,
    pub user_id: String,
    pub display_name: String,
}

#[derive(Default)]
pub struct LobbyManager {
    users: DashMap<String, LobbyUser>,
}

impl LobbyManager {
    pub fn add_user(&self, connection_id: &str, user: &UserInfo) {
        self.users.insert(
            connection_id.to_string(),
            LobbyUser {
                connection_id: connection_id.to_string(),
                seq_id: user.id,
                user_id: user.user_id.clone(),
                display_name: user.display_name.clone(),
            },
        );
    }

    pub fn remove_user(&self, connection_id: &str) {
        self.users.remove(connection_id);
    }

    pub fn get_user(&self, connection_id: &str) -> Option<LobbyUser> {
        self.users.get(connection_id).map(|u| u.clone())
    }

    pub fn users(&self) -> Vec<LobbyUser> {
        self.users.iter().map(|u| u.clone()).collect()
    }

    pub fn connection_id_for_user(&self, user_id: &str) -> Option<String> {
        self.users
            .iter()
            .find(|u| u.user_id == user_id)
            .map(|u| u.connection_id.clone())
    }
}

pub struct GameRoom {
    pub room_id: String,
    pub name: String,
    pub host_connection_id: std::sync::Mutex<String>,
    pub max_players: i32,
    pub is_playing: std::sync::atomic::AtomicBool,
    pub players: DashMap<String, LobbyUser>,
}

impl GameRoom {
    pub fn add_player(&self, connection_id: &str, user: LobbyUser) -> bool {
        self.players
            .insert(connection_id.to_string(), user)
            .is_none()
    }

    pub fn remove_player(&self, connection_id: &str) -> bool {
        self.players.remove(connection_id).is_some()
    }

    pub fn is_playing(&self) -> bool {
        self.is_playing.load(Ordering::Relaxed)
    }

    pub fn set_playing(&self, v: bool) {
        self.is_playing.store(v, Ordering::Relaxed);
    }

    pub fn host_id(&self) -> String {
        self.host_connection_id.lock().unwrap().clone()
    }

    pub fn set_host(&self, id: String) {
        *self.host_connection_id.lock().unwrap() = id;
    }

    pub fn to_room_info(&self) -> RoomInfo {
        let host_id = self.host_id();
        let host = self
            .players
            .get(&host_id)
            .map(|h| h.clone())
            .or_else(|| self.players.iter().next().map(|p| p.clone()));
        RoomInfo {
            room_id: self.room_id.clone(),
            name: self.name.clone(),
            host_id: host.as_ref().map(|h| h.connection_id.clone()).unwrap_or_default(),
            host_name: host.as_ref().map(|h| h.display_name.clone()).unwrap_or_default(),
            player_count: self.players.len() as i32,
            max_players: self.max_players,
            is_playing: self.is_playing(),
        }
    }
}

pub struct RoomManager {
    rooms: DashMap<String, Arc<GameRoom>>,
    counter: AtomicI32,
    max_rooms: i32,
    max_players_per_room: i32,
}

impl RoomManager {
    pub fn new(max_rooms: i32, max_players_per_room: i32) -> Self {
        Self {
            rooms: DashMap::new(),
            counter: AtomicI32::new(0),
            max_rooms,
            max_players_per_room,
        }
    }

    pub fn max_rooms(&self) -> i32 {
        self.max_rooms
    }
    pub fn max_players_per_room(&self) -> i32 {
        self.max_players_per_room
    }

    pub fn get_room_list(&self) -> Vec<RoomInfo> {
        self.rooms
            .iter()
            .filter(|r| !r.is_playing())
            .map(|r| r.to_room_info())
            .collect()
    }

    pub fn create_room(
        &self,
        host_connection_id: &str,
        host: LobbyUser,
        room_name: &str,
        max_players: i32,
    ) -> Option<Arc<GameRoom>> {
        if self.rooms.len() as i32 >= self.max_rooms {
            return None;
        }
        let capped = max_players.min(self.max_players_per_room).min(MAX_PLAYERS_PER_ROOM);
        let id = self.counter.fetch_add(1, Ordering::Relaxed) + 1;
        let room_id = format!("room_{id}");
        let room = Arc::new(GameRoom {
            room_id: room_id.clone(),
            name: room_name.to_string(),
            host_connection_id: std::sync::Mutex::new(host_connection_id.to_string()),
            max_players: capped,
            is_playing: std::sync::atomic::AtomicBool::new(false),
            players: DashMap::new(),
        });
        room.add_player(host_connection_id, host);
        self.rooms.insert(room_id, Arc::clone(&room));
        Some(room)
    }

    pub fn get_room(&self, room_id: &str) -> Option<Arc<GameRoom>> {
        self.rooms.get(room_id).map(|r| Arc::clone(r.value()))
    }

    pub fn join_room(&self, room_id: &str, connection_id: &str, user: LobbyUser) -> bool {
        let Some(room) = self.get_room(room_id) else {
            return false;
        };
        if room.is_playing() || room.players.len() as i32 >= room.max_players {
            return false;
        }
        room.add_player(connection_id, user)
    }

    pub fn leave_room(&self, connection_id: &str) {
        let mut empty_id: Option<String> = None;
        for room in self.rooms.iter() {
            if room.remove_player(connection_id) {
                if room.players.is_empty() {
                    empty_id = Some(room.room_id.clone());
                } else if room.host_id() == connection_id {
                    if let Some(first) = room.players.iter().next() {
                        room.set_host(first.key().clone());
                    }
                }
                break;
            }
        }
        if let Some(id) = empty_id {
            self.rooms.remove(&id);
        }
    }
}
