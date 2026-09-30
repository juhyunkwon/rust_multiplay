//! Dedicated 서버 본체

use crate::capacity::ServerCapacity;
use crate::lobby::{LobbyManager, RoomManager};
use dashmap::DashMap;
use x0_core::net::{encode_line, response_json};
use x0_core::protocol::{
    CreateRoomRequest, GameStatePayload, JoinRoomRequest, LoginRequest, LoginResponse, MessageType,
    NetworkMessage, PlayerInputPayload, PlayerState, UserInfo, MAX_PLAYERS_PER_ROOM,
};
use x0_core::recv_buffer::RecvLineBuffer;
use x0_core::{MessageSerializer, PacketCrypto};
use x0_db::{MysqlUserRepository, UserRepository};
use serde_json::json;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::OwnedWriteHalf;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::Mutex;
use tokio::time::{sleep, Duration, Instant};
use tracing::{debug, info, warn};

pub type Writer = Arc<Mutex<OwnedWriteHalf>>;

pub struct ClientSession {
    pub connection_id: String,
    pub user_id: std::sync::Mutex<Option<i64>>,
    pub username: std::sync::Mutex<String>,
    pub display_name: std::sync::Mutex<String>,
    pub writer: Writer,
    pub is_logged_in: std::sync::atomic::AtomicBool,
    pub current_room_id: std::sync::Mutex<Option<String>>,
    pub is_in_game: std::sync::atomic::AtomicBool,
    pub kicked: std::sync::atomic::AtomicBool,
}

impl ClientSession {
    fn logged_in(&self) -> bool {
        self.is_logged_in.load(Ordering::Relaxed)
    }
    fn in_game(&self) -> bool {
        self.is_in_game.load(Ordering::Relaxed)
    }
    fn room_id(&self) -> Option<String> {
        self.current_room_id.lock().unwrap().clone()
    }
    fn set_room(&self, id: Option<String>) {
        *self.current_room_id.lock().unwrap() = id;
    }
    fn display_name(&self) -> String {
        self.display_name.lock().unwrap().clone()
    }
    fn kick(&self) {
        self.kicked.store(true, Ordering::Relaxed);
    }
    fn is_kicked(&self) -> bool {
        self.kicked.load(Ordering::Relaxed)
    }
}

pub struct AuthorityServer {
    port: u16,
    users: Box<dyn UserRepository>,
    lobby: LobbyManager,
    rooms: RoomManager,
    crypto: PacketCrypto,
    clients: DashMap<String, Arc<ClientSession>>,
    game_state: DashMap<String, PlayerState>,
    server_tick: AtomicI64,
}

const TICK_RATE_MS: u64 = 50;

impl AuthorityServer {
    pub fn new(
        port: u16,
        mysql_connection_string: &str,
        capacity: &ServerCapacity,
        crypto: PacketCrypto,
    ) -> anyhow::Result<Arc<Self>> {
        let users: Box<dyn UserRepository> =
            Box::new(MysqlUserRepository::new(mysql_connection_string)?);
        Ok(Self::new_with_users(port, users, capacity, crypto))
    }

    pub fn new_with_users(
        port: u16,
        users: Box<dyn UserRepository>,
        capacity: &ServerCapacity,
        crypto: PacketCrypto,
    ) -> Arc<Self> {
        Arc::new(Self {
            port,
            users,
            lobby: LobbyManager::default(),
            rooms: RoomManager::new(capacity.max_rooms, capacity.max_players_per_room),
            crypto,
            clients: DashMap::new(),
            game_state: DashMap::new(),
            server_tick: AtomicI64::new(0),
        })
    }

    pub fn connection_count(&self) -> usize {
        self.clients.len()
    }

    pub async fn start(self: Arc<Self>) -> anyhow::Result<()> {
        let listener = TcpListener::bind(("0.0.0.0", self.port)).await?;
        self.run_accept(listener).await
    }

    pub async fn run_accept(self: Arc<Self>, listener: TcpListener) -> anyhow::Result<()> {
        info!(
            port = self.port,
            max_rooms = self.rooms.max_rooms(),
            max_players_per_room = self.rooms.max_players_per_room(),
            "로비/룸 + Authority 시작"
        );

        let this = Arc::clone(&self);
        tokio::spawn(async move { this.game_loop().await });

        loop {
            match listener.accept().await {
                Ok((stream, _)) => {
                    let _ = stream.set_nodelay(true);
                    let this = Arc::clone(&self);
                    tokio::spawn(async move {
                        if let Err(e) = this.handle_client(stream).await {
                            tracing::debug!("[DedicatedServer] 클라이언트 종료: {e}");
                        }
                    });
                }
                Err(e) => warn!("[DedicatedServer] accept 오류: {e}"),
            }
        }
    }

    async fn game_loop(self: Arc<Self>) {
        loop {
            let start = Instant::now();
            let tick = self.server_tick.fetch_add(1, Ordering::Relaxed) + 1;
            if !self.game_state.is_empty() {
                let players: Vec<PlayerState> =
                    self.game_state.iter().map(|p| p.clone()).collect();
                let state = GameStatePayload {
                    server_tick: tick,
                    players,
                };
                let msg = response_json(MessageType::GameState, state);
                let data = encode_line(&self.crypto, &msg);
                let in_game: Vec<Writer> = self
                    .clients
                    .iter()
                    .filter(|c| c.in_game())
                    .map(|c| Arc::clone(&c.writer))
                    .collect();
                for w in in_game {
                    let _ = write_flushed(&w, &data).await;
                }
            }
            let elapsed = start.elapsed();
            let delay = Duration::from_millis(TICK_RATE_MS).saturating_sub(elapsed);
            sleep(delay).await;
        }
    }

    async fn handle_client(self: Arc<Self>, stream: TcpStream) -> anyhow::Result<()> {
        let (mut reader, writer) = stream.into_split();
        let writer: Writer = Arc::new(Mutex::new(writer));
        let connection_id = short_id();
        let session = Arc::new(ClientSession {
            connection_id: connection_id.clone(),
            user_id: std::sync::Mutex::new(None),
            username: std::sync::Mutex::new(String::new()),
            display_name: std::sync::Mutex::new(String::new()),
            writer: Arc::clone(&writer),
            is_logged_in: std::sync::atomic::AtomicBool::new(false),
            current_room_id: std::sync::Mutex::new(None),
            is_in_game: std::sync::atomic::AtomicBool::new(false),
            kicked: std::sync::atomic::AtomicBool::new(false),
        });
        self.clients
            .insert(connection_id.clone(), Arc::clone(&session));

        let mut line_buf = RecvLineBuffer::new(8192);
        let mut buf = [0u8; 4096];
        let enc_str = if self.crypto.is_enabled() { "Y" } else { "N" };

        let result = async {
            loop {
                let n = reader.read(&mut buf).await?;
                if n == 0 {
                    break;
                }
                if session.is_kicked() {
                    break;
                }
                debug!(
                    kind = "packet",
                    conn = %connection_id,
                    dir = "rx",
                    bytes = n,
                    encrypted = enc_str,
                    "수신"
                );
                if !line_buf.on_write(&buf[..n]) {
                    warn!(conn = %connection_id, "수신 버퍼 상한 초과, 연결 종료");
                    break;
                }
                while let Some(line) = line_buf.read_line() {
                    let line = line.trim().to_string();
                    if line.is_empty() {
                        continue;
                    }
                    let plain = self.crypto.decode_from_receive(&line);
                    let Some(net_msg) = MessageSerializer::parse_network_message(&plain) else {
                        continue;
                    };
                    if let Some(resp) = self.process_message(&net_msg, &session).await {
                        if session.is_kicked() {
                            break;
                        }
                        let data = encode_line(&self.crypto, &resp);
                        write_flushed(&writer, &data).await?;
                        debug!(
                            kind = "packet",
                            conn = %connection_id,
                            dir = "tx",
                            bytes = data.len(),
                            encrypted = enc_str,
                            "송신"
                        );
                    }
                }
            }
            anyhow::Ok(())
        }
        .await;

        self.detach_session(&session).await;
        info!(
            name = %session.display_name(),
            conn = %connection_id,
            "접속 해제"
        );
        result
    }

    async fn detach_session(&self, session: &Arc<ClientSession>) {
        let connection_id = session.connection_id.clone();
        self.clients.remove(&connection_id);
        if session.logged_in() {
            self.lobby.remove_user(&connection_id);
        }
        if let Some(room_id) = session.room_id() {
            self.rooms.leave_room(&connection_id);
            session.set_room(None);
            session.is_in_game.store(false, Ordering::Relaxed);
            self.game_state.remove(&connection_id);
            if let Some(room) = self.rooms.get_room(&room_id) {
                self.broadcast_room_update(&room).await;
                self.broadcast_to_room(
                    &room,
                    MessageType::Disconnect,
                    json!({
                        "peerId": connection_id,
                        "fromId": connection_id,
                        "reason": "left"
                    }),
                )
                .await;
            }
        } else if session.in_game() {
            self.game_state.remove(&connection_id);
        }
    }

    async fn process_message(
        &self,
        msg: &NetworkMessage,
        session: &Arc<ClientSession>,
    ) -> Option<String> {
        match msg.msg_type {
            MessageType::Login => self.handle_login(msg, session).await,
            MessageType::LobbyList => {
                if !session.logged_in() {
                    return None;
                }
                let users: Vec<_> = self
                    .lobby
                    .users()
                    .into_iter()
                    .map(|u| {
                        json!({
                            "id": u.connection_id,
                            "seqId": u.seq_id,
                            "userId": u.user_id,
                            "displayName": u.display_name
                        })
                    })
                    .collect();
                Some(response_json(MessageType::LobbyList, json!({ "users": users })))
            }
            MessageType::RoomList => {
                if !session.logged_in() {
                    return None;
                }
                let rooms = self.rooms.get_room_list();
                Some(response_json(MessageType::RoomList, json!({ "rooms": rooms })))
            }
            MessageType::CreateRoom => {
                if !session.logged_in() {
                    return None;
                }
                self.handle_create_room(msg, session).await
            }
            MessageType::JoinRoom => {
                if !session.logged_in() {
                    return None;
                }
                self.handle_join_room(msg, session).await
            }
            MessageType::LeaveRoom => {
                if let Some(room_id) = session.room_id() {
                    self.rooms.leave_room(&session.connection_id);
                    session.set_room(None);
                    session.is_in_game.store(false, Ordering::Relaxed);
                    self.game_state.remove(&session.connection_id);
                    if let Some(room) = self.rooms.get_room(&room_id) {
                        self.broadcast_room_update(&room).await;
                    }
                }
                None
            }
            MessageType::StartGame => {
                if !session.logged_in() || session.room_id().is_none() {
                    return None;
                }
                self.handle_start_game(session).await;
                None
            }
            MessageType::PlayerInput => {
                if !session.in_game() {
                    return None;
                }
                let input: Option<PlayerInputPayload> = msg
                    .payload
                    .as_ref()
                    .and_then(|p| serde_json::from_value(p.clone()).ok());
                if let Some(input) = input {
                    let action = input.action.trim();
                    if let Some(target_id) = action.strip_prefix("hit:") {
                        let target_id = target_id.trim();
                        if let Some(mut target) = self.game_state.get_mut(target_id) {
                            target.health = (target.health - 10).max(0);
                        }
                    } else if let Some(mut state) = self.game_state.get_mut(&session.connection_id) {
                        if state.health > 0 {
                            state.x = (state.x + input.move_x * 2.0).clamp(-50.0, 50.0);
                            state.y = (state.y + input.move_y * 2.0).clamp(-50.0, 50.0);
                        }
                        state.last_input_tick = input.input_tick;
                    }
                }
                None
            }
            MessageType::PingRequest | MessageType::Ping => {
                let client_ticks = msg
                    .payload
                    .as_ref()
                    .and_then(|p| p.get("clientTicks"))
                    .and_then(|v| v.as_i64())
                    .unwrap_or(0);
                let reply = if msg.msg_type == MessageType::Ping {
                    MessageType::Pong
                } else {
                    MessageType::PingResponse
                };
                Some(response_json(reply, json!({ "clientTicks": client_ticks })))
            }
            MessageType::Chat => {
                if let Some(room_id) = session.room_id() {
                    if let Some(room) = self.rooms.get_room(&room_id) {
                        let text = msg
                            .payload
                            .as_ref()
                            .and_then(|p| p.get("message"))
                            .and_then(|v| v.as_str())
                            .unwrap_or("");
                        self.broadcast_to_room(
                            &room,
                            MessageType::Chat,
                            json!({
                                "fromId": session.connection_id,
                                "message": text
                            }),
                        )
                        .await;
                    }
                }
                None
            }
            MessageType::Disconnect => {
                session.kick();
                None
            }
            _ => None,
        }
    }

    async fn handle_login(
        &self,
        msg: &NetworkMessage,
        session: &Arc<ClientSession>,
    ) -> Option<String> {
        let req: Option<LoginRequest> = msg
            .payload
            .as_ref()
            .and_then(|p| serde_json::from_value(p.clone()).ok());
        let Some(req) = req else {
            return Some(response_json(
                MessageType::LoginAck,
                LoginResponse {
                    success: false,
                    message: "잘못된 요청".into(),
                    user: None,
                    connection_id: String::new(),
                },
            ));
        };
        if req.user_id.trim().is_empty() {
            return Some(response_json(
                MessageType::LoginAck,
                LoginResponse {
                    success: false,
                    message: "아이디를 입력하세요".into(),
                    user: None,
                    connection_id: String::new(),
                },
            ));
        }

        match self
            .users
            .validate_password(&req.user_id, &req.password)
            .await
        {
            Ok(false) => {
                return Some(response_json(
                    MessageType::LoginAck,
                    LoginResponse {
                        success: false,
                        message: "로그인 실패".into(),
                        user: None,
                        connection_id: String::new(),
                    },
                ));
            }
            Err(e) => {
                tracing::error!(error = %e, "DB 오류");
                return Some(response_json(
                    MessageType::LoginAck,
                    LoginResponse {
                        success: false,
                        message: "DB 연결 오류".into(),
                        user: None,
                        connection_id: String::new(),
                    },
                ));
            }
            Ok(true) => {}
        }

        let member = match self.users.get_by_uid(&req.user_id).await {
            Ok(Some(m)) => m,
            Ok(None) => {
                return Some(response_json(
                    MessageType::LoginAck,
                    LoginResponse {
                        success: false,
                        message: "사용자를 찾을 수 없음".into(),
                        user: None,
                        connection_id: String::new(),
                    },
                ));
            }
            Err(e) => {
                tracing::error!(error = %e, "DB 오류");
                return Some(response_json(
                    MessageType::LoginAck,
                    LoginResponse {
                        success: false,
                        message: "DB 연결 오류".into(),
                        user: None,
                        connection_id: String::new(),
                    },
                ));
            }
        };

        let nick = member.display_name().to_string();
        let user_info = UserInfo {
            id: member.id,
            user_id: member.uid.clone(),
            user_name: member.name.clone(),
            display_name: nick.clone(),
            gender: member.gender.code().to_string(),
        };

        if let Some(old_id) = self.lobby.connection_id_for_user(&member.uid) {
            if old_id != session.connection_id {
                if let Some(old) = self.clients.get(&old_id).map(|c| Arc::clone(c.value())) {
                    old.kick();
                    let notice = response_json(
                        MessageType::Disconnect,
                        json!({
                            "peerId": old_id,
                            "reason": "replaced"
                        }),
                    );
                    let data = encode_line(&self.crypto, &notice);
                    let _ = write_flushed(&old.writer, &data).await;
                    self.detach_session(&old).await;
                }
            }
        }

        *session.user_id.lock().unwrap() = Some(member.id);
        *session.username.lock().unwrap() = member.uid.clone();
        *session.display_name.lock().unwrap() = nick.clone();
        session.is_logged_in.store(true, Ordering::Relaxed);
        self.lobby.add_user(&session.connection_id, &user_info);
        let _ = self.users.update_last_login(member.id).await;
        info!(name = %nick, user_id = %member.uid, "로그인");

        Some(response_json(
            MessageType::LoginAck,
            LoginResponse {
                success: true,
                message: String::new(),
                user: Some(user_info),
                connection_id: session.connection_id.clone(),
            },
        ))
    }

    async fn handle_create_room(
        &self,
        msg: &NetworkMessage,
        session: &Arc<ClientSession>,
    ) -> Option<String> {
        if session.room_id().is_some() {
            return Some(response_json(
                MessageType::CreateRoomAck,
                json!({ "success": false, "message": "이미 룸에 있습니다" }),
            ));
        }
        let req: CreateRoomRequest = msg
            .payload
            .as_ref()
            .and_then(|p| serde_json::from_value(p.clone()).ok())
            .unwrap_or_default();
        let room_name = if req.room_name.is_empty() {
            "새 룸"
        } else {
            &req.room_name
        };
        let max_players = req.max_players.clamp(1, MAX_PLAYERS_PER_ROOM);
        let lobby_user = self.lobby.get_user(&session.connection_id)?;
        let Some(room) = self.rooms.create_room(
            &session.connection_id,
            lobby_user,
            room_name,
            max_players,
        ) else {
            return Some(response_json(
                MessageType::CreateRoomAck,
                json!({ "success": false, "message": "룸 생성 실패 (최대 룸 수 초과 또는 요청 거부)" }),
            ));
        };
        session.set_room(Some(room.room_id.clone()));
        self.broadcast_room_update(&room).await;
        Some(response_json(
            MessageType::CreateRoomAck,
            json!({ "success": true, "room": room.to_room_info() }),
        ))
    }

    async fn handle_join_room(
        &self,
        msg: &NetworkMessage,
        session: &Arc<ClientSession>,
    ) -> Option<String> {
        if session.room_id().is_some() {
            return Some(response_json(
                MessageType::JoinRoomAck,
                json!({ "success": false, "message": "이미 룸에 있습니다" }),
            ));
        }
        let req: JoinRoomRequest = msg
            .payload
            .as_ref()
            .and_then(|p| serde_json::from_value(p.clone()).ok())
            .unwrap_or_default();
        if req.room_id.is_empty() {
            return Some(response_json(
                MessageType::JoinRoomAck,
                json!({ "success": false, "message": "룸 ID 필요" }),
            ));
        }
        let lobby_user = self.lobby.get_user(&session.connection_id)?;
        if !self
            .rooms
            .join_room(&req.room_id, &session.connection_id, lobby_user)
        {
            return Some(response_json(
                MessageType::JoinRoomAck,
                json!({ "success": false, "message": "참가 실패" }),
            ));
        }
        session.set_room(Some(req.room_id.clone()));
        let room = self.rooms.get_room(&req.room_id);
        if let Some(ref room) = room {
            self.broadcast_room_update(room).await;
        }
        Some(response_json(
            MessageType::JoinRoomAck,
            json!({ "success": true, "room": room.map(|r| r.to_room_info()) }),
        ))
    }

    async fn handle_start_game(&self, session: &Arc<ClientSession>) {
        let Some(room_id) = session.room_id() else {
            return;
        };
        let Some(room) = self.rooms.get_room(&room_id) else {
            return;
        };
        if room.host_id() != session.connection_id {
            return;
        }
        room.set_playing(true);
        for p in room.players.iter() {
            let conn_id = p.key().clone();
            if let Some(s) = self.clients.get(&conn_id) {
                s.is_in_game.store(true, Ordering::Relaxed);
                self.game_state.insert(
                    conn_id.clone(),
                    PlayerState {
                        id: conn_id,
                        name: s.display_name(),
                        x: 0.0,
                        y: 0.0,
                        health: 100,
                        last_input_tick: 0,
                    },
                );
            }
        }
        self.broadcast_to_room(&room, MessageType::StartGame, json!({ "roomId": room.room_id }))
            .await;
        info!(room = %room.room_id, players = room.players.len(), "게임 시작");
    }

    async fn broadcast_room_update(&self, room: &crate::lobby::GameRoom) {
        let msg = response_json(MessageType::RoomUpdate, room.to_room_info());
        self.send_to_room_members(room, &msg).await;
    }

    async fn broadcast_to_room(
        &self,
        room: &crate::lobby::GameRoom,
        ty: MessageType,
        payload: serde_json::Value,
    ) {
        let msg = response_json(ty, payload);
        self.send_to_room_members(room, &msg).await;
    }

    async fn send_to_room_members(&self, room: &crate::lobby::GameRoom, msg: &str) {
        let data = encode_line(&self.crypto, msg);
        let writers: Vec<Writer> = room
            .players
            .iter()
            .filter_map(|p| {
                self.clients
                    .get(p.key())
                    .map(|c| Arc::clone(&c.writer))
            })
            .collect();
        for w in writers {
            let _ = write_flushed(&w, &data).await;
        }
    }
}

fn short_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()[..8].to_string()
}

async fn write_flushed(w: &Writer, data: &[u8]) -> anyhow::Result<()> {
    let mut g = w.lock().await;
    g.write_all(data).await?;
    g.flush().await?;
    Ok(())
}
