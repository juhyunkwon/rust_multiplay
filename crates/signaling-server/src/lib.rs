//! 시그널링 서버
//! TCP 는 시그널링 메시지, UDP 는 공인 IP/포트 확인용 (STUN 비슷하게)

use dashmap::DashMap;
use x0_core::net::{encode_line, response_json};
use x0_core::protocol::{
    ConnectRequestPayload, ConnectResponsePayload, MessageType, NetworkMessage, PeerInfo,
    RegisterRequest, RegisterResponse,
};
use x0_core::recv_buffer::RecvLineBuffer;
use x0_core::{MessageSerializer, PacketCrypto};
use serde_json::json;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::tcp::OwnedWriteHalf;
use tokio::net::{TcpListener, TcpStream, UdpSocket};
use tokio::sync::Mutex;
use tracing::{debug, info, warn};

pub type Writer = Arc<Mutex<OwnedWriteHalf>>;

pub struct SignalingServerHost {
    tcp_port: u16,
    udp_port: u16,
    crypto: PacketCrypto,
    clients: DashMap<String, Arc<ClientSession>>,
    roster: Mutex<()>,
}

pub struct ClientSession {
    pub id: String,
    pub name: String,
    pub room_number: i32,
    pub udp_token: String,
    pub public_ip: std::sync::Mutex<String>,
    pub public_port: std::sync::Mutex<i32>,
    pub local_ip: String,
    pub local_port: i32,
    pub user_id: std::sync::Mutex<String>,
    pub dedicated_room_id: std::sync::Mutex<String>,
    pub writer: Writer,
    pub connected_at: std::time::Instant,
    last_broadcast_ms: AtomicU64,
}

impl ClientSession {
    /// 로그인 계정이나 방이 바뀌었으면 true
    fn update_links(&self, user_id: Option<&str>, room_id: Option<&str>) -> bool {
        let mut changed = false;
        if let Some(u) = user_id {
            let mut cur = self.user_id.lock().unwrap();
            if *cur != u {
                *cur = u.to_string();
                changed = true;
            }
        }
        if let Some(r) = room_id {
            let mut cur = self.dedicated_room_id.lock().unwrap();
            if *cur != r {
                *cur = r.to_string();
                changed = true;
            }
        }
        changed
    }

    fn public_ip(&self) -> String {
        self.public_ip.lock().unwrap().clone()
    }
    fn public_port(&self) -> i32 {
        *self.public_port.lock().unwrap()
    }
    fn set_public(&self, ip: String, port: i32) {
        *self.public_ip.lock().unwrap() = ip;
        *self.public_port.lock().unwrap() = port;
    }

    fn allow_broadcast(&self) -> bool {
        let now = now_ms();
        let prev = self.last_broadcast_ms.load(Ordering::Relaxed);
        if now.saturating_sub(prev) < 50 {
            return false;
        }
        self.last_broadcast_ms.store(now, Ordering::Relaxed);
        true
    }
}

impl SignalingServerHost {
    pub fn new(tcp_port: u16, udp_port: u16, crypto: PacketCrypto) -> Arc<Self> {
        Arc::new(Self {
            tcp_port,
            udp_port,
            crypto,
            clients: DashMap::new(),
            roster: Mutex::new(()),
        })
    }

    pub fn connection_count(&self) -> usize {
        self.clients.len()
    }

    pub async fn start(self: Arc<Self>) -> anyhow::Result<()> {
        let udp = UdpSocket::bind(("0.0.0.0", self.udp_port)).await?;
        let listener = TcpListener::bind(("0.0.0.0", self.tcp_port)).await?;
        self.run(listener, udp).await
    }

    pub async fn run(self: Arc<Self>, listener: TcpListener, udp: UdpSocket) -> anyhow::Result<()> {
        info!(
            tcp = self.tcp_port,
            udp = self.udp_port,
            "시그널링 시작"
        );

        let udp = Arc::new(udp);
        let this = Arc::clone(&self);
        let udp_s = Arc::clone(&udp);
        tokio::spawn(async move { this.udp_loop(udp_s).await });

        loop {
            match listener.accept().await {
                Ok((stream, addr)) => {
                    let _ = stream.set_nodelay(true);
                    let this = Arc::clone(&self);
                    tokio::spawn(async move {
                        if let Err(e) = this.handle_tcp(stream, addr).await {
                            debug!("[SignalingServer] 클라이언트 종료: {e}");
                        }
                    });
                }
                Err(e) => warn!("[SignalingServer] accept 오류: {e}"),
            }
        }
    }

    async fn udp_loop(self: Arc<Self>, udp: Arc<UdpSocket>) {
        let mut buf = [0u8; 2048];
        loop {
            match udp.recv_from(&mut buf).await {
                Ok((n, remote)) => {
                    let msg = String::from_utf8_lossy(&buf[..n]);
                    if let Some(rest) = msg.strip_prefix("REGISTER_UDP:") {
                        let parts: Vec<&str> = rest.trim().split(':').collect();
                        if parts.len() < 3 {
                            warn!(from = %remote, "UDP REGISTER 형식 오류");
                            continue;
                        }
                        let client_id = parts[0].trim();
                        let token = parts[2].trim();
                        let Some(session) = self.clients.get(client_id) else {
                            warn!(client_id, "UDP REGISTER 미등록 id");
                            continue;
                        };
                        if session.udp_token != token {
                            warn!(client_id, "UDP REGISTER 토큰 불일치");
                            continue;
                        }
                        let room = session.room_number;
                        session.set_public(remote.ip().to_string(), remote.port() as i32);
                        drop(session);
                        info!(
                            client_id,
                            ip = %remote.ip(),
                            port = remote.port(),
                            "UDP 공인 주소"
                        );
                        self.broadcast_peer_list(room).await;
                    }
                }
                Err(e) => warn!("[UDP 오류] {e}"),
            }
        }
    }

    async fn handle_tcp(self: Arc<Self>, stream: TcpStream, addr: SocketAddr) -> anyhow::Result<()> {
        let (reader, writer) = stream.into_split();
        let writer: Writer = Arc::new(Mutex::new(writer));
        let mut session: Option<Arc<ClientSession>> = None;
        let result = self.serve_conn(reader, &writer, addr, &mut session).await;
        if let Some(s) = session.take() {
            self.leave_and_notify(&s).await;
            match &result {
                Ok(()) => info!(name = %s.name, id = %s.id, "접속 해제"),
                Err(e) => info!(name = %s.name, id = %s.id, error = %e, "접속 해제 (소켓 오류)"),
            }
        }
        result
    }

    async fn serve_conn(
        &self,
        mut reader: tokio::net::tcp::OwnedReadHalf,
        writer: &Writer,
        addr: SocketAddr,
        session: &mut Option<Arc<ClientSession>>,
    ) -> anyhow::Result<()> {
        let mut line_buf = RecvLineBuffer::new(8192);
        let mut buf = [0u8; 4096];
        let enc_str = if self.crypto.is_enabled() { "Y" } else { "N" };
        let remote_ip = addr.ip().to_string();
        let remote_port = addr.port() as i32;

        loop {
            let n = reader.read(&mut buf).await?;
            if n == 0 {
                break;
            }
            let id = session.as_ref().map(|s| s.id.as_str()).unwrap_or("미등록");
            debug!(
                kind = "packet",
                conn = %id,
                dir = "rx",
                bytes = n,
                encrypted = enc_str,
                "수신"
            );
            if !line_buf.on_write(&buf[..n]) {
                warn!("수신 버퍼 상한 초과, 연결 종료");
                break;
            }
            let mut shut = false;
            while let Some(line) = line_buf.read_line() {
                let line = line.trim().to_string();
                if line.is_empty() {
                    continue;
                }
                let plain = self.crypto.decode_from_receive(&line);
                let Some(net_msg) = MessageSerializer::parse_network_message(&plain) else {
                    continue;
                };
                if net_msg.msg_type == MessageType::Disconnect {
                    shut = true;
                    break;
                }
                if let Some(resp) = self
                    .process_message(&net_msg, writer, &remote_ip, remote_port, session)
                    .await
                {
                    let data = encode_line(&self.crypto, &resp);
                    let mut w = writer.lock().await;
                    w.write_all(&data).await?;
                    w.flush().await?;
                    let sid = session.as_ref().map(|s| s.id.as_str()).unwrap_or("미등록");
                    debug!(
                        kind = "packet",
                        conn = %sid,
                        dir = "tx",
                        bytes = data.len(),
                        encrypted = enc_str,
                        "송신"
                    );
                }
            }
            if shut {
                break;
            }
        }
        Ok(())
    }

    async fn process_message(
        &self,
        msg: &NetworkMessage,
        writer: &Writer,
        remote_ip: &str,
        remote_port: i32,
        session: &mut Option<Arc<ClientSession>>,
    ) -> Option<String> {
        match msg.msg_type {
            MessageType::Register => {
                let req: RegisterRequest = msg
                    .payload
                    .as_ref()
                    .and_then(|p| serde_json::from_value(p.clone()).ok())
                    .unwrap_or_default();
                let id = if req.id.is_empty() {
                    short_id()
                } else {
                    req.id
                };
                let name = if req.name.is_empty() {
                    "Player".into()
                } else {
                    req.name
                };
                let room_number = if req.room_number == 0 {
                    1
                } else {
                    req.room_number
                };

                let (is_master, udp_token, stale) = {
                    let _g = self.roster.lock().await;
                    let stale_ids: Vec<String> = self
                        .clients
                        .iter()
                        .filter(|c| {
                            c.id == id || (c.name == name && c.room_number == room_number)
                        })
                        .map(|c| c.id.clone())
                        .collect();
                    let stale: Vec<Arc<ClientSession>> = stale_ids
                        .iter()
                        .filter_map(|k| self.clients.remove(k).map(|(_, s)| s))
                        .collect();
                    let is_master = !self.clients.iter().any(|c| c.room_number == room_number);
                    let udp_token = short_id();
                    let client = Arc::new(ClientSession {
                        id: id.clone(),
                        name: name.clone(),
                        room_number,
                        udp_token: udp_token.clone(),
                        public_ip: std::sync::Mutex::new(remote_ip.to_string()),
                        public_port: std::sync::Mutex::new(remote_port),
                        local_ip: req.local_ip.clone(),
                        local_port: req.local_port,
                        user_id: std::sync::Mutex::new(req.user_id.clone()),
                        dedicated_room_id: std::sync::Mutex::new(req.dedicated_room_id.clone()),
                        writer: Arc::clone(writer),
                        connected_at: std::time::Instant::now(),
                        last_broadcast_ms: AtomicU64::new(0),
                    });
                    self.clients.insert(id.clone(), Arc::clone(&client));
                    *session = Some(client);
                    (is_master, udp_token, stale)
                };
                for old in &stale {
                    warn!(
                        name = %old.name,
                        id = %old.id,
                        "같은 이름/룸 잔여 세션 교체"
                    );
                    self.notify_peers_left(old).await;
                }

                info!(
                    name = %name,
                    id = %id,
                    room = room_number,
                    ip = remote_ip,
                    port = remote_port,
                    is_master,
                    "시그널링 등록"
                );
                self.broadcast_peer_list_except(room_number, &id).await;

                Some(response_json(
                    MessageType::RegisterAck,
                    RegisterResponse {
                        registered_id: id,
                        public_ip: remote_ip.to_string(),
                        public_port: remote_port,
                        is_master,
                        udp_token,
                    },
                ))
            }
            MessageType::PeerList => {
                let s = session.as_ref().map(Arc::clone)?;
                let p = msg.payload.as_ref();
                let user_id = p.and_then(|p| p.get("userId")).and_then(|v| v.as_str());
                let room_id = p.and_then(|p| p.get("dedicatedRoomId")).and_then(|v| v.as_str());
                if s.update_links(user_id, room_id) {
                    info!(
                        id = %s.id,
                        user_id = user_id.unwrap_or(""),
                        dedicated_room = room_id.unwrap_or(""),
                        "피어 링크 갱신"
                    );
                    self.broadcast_peer_list_except(s.room_number, &s.id).await;
                }
                Some(self.peer_list_json(&s.id, s.room_number))
            }
            MessageType::ConnectRequest => {
                let conn_req: ConnectRequestPayload = msg
                    .payload
                    .as_ref()
                    .and_then(|p| serde_json::from_value(p.clone()).ok())?;
                let s = session.as_ref()?;
                let Some(peer) = self.clients.get(&conn_req.peer_id).map(|c| Arc::clone(c.value()))
                else {
                    return Some(response_json(
                        MessageType::ConnectResponse,
                        ConnectResponsePayload {
                            accepted: false,
                            peer_id: conn_req.peer_id,
                            requester_info: None,
                        },
                    ));
                };
                let requester_info = self.peer_info_of(s);
                let forward = response_json(
                    MessageType::ConnectRequest,
                    json!({
                        "peerId": s.id,
                        "requesterId": conn_req.requester_id,
                        "requesterInfo": requester_info
                    }),
                );
                self.forward_to(&peer, &forward).await;
                None
            }
            MessageType::ConnectResponse => {
                let conn_resp: ConnectResponsePayload = msg
                    .payload
                    .as_ref()
                    .and_then(|p| serde_json::from_value(p.clone()).ok())?;
                let s = session.as_ref()?;
                let payload = ConnectResponsePayload {
                    accepted: conn_resp.accepted,
                    peer_id: conn_resp.peer_id.clone(),
                    requester_info: Some(self.peer_info_of(s)),
                };
                if let Some(target) = self
                    .clients
                    .get(&conn_resp.peer_id)
                    .map(|c| Arc::clone(c.value()))
                {
                    let json = response_json(MessageType::ConnectResponse, payload);
                    self.forward_to(&target, &json).await;
                }
                None
            }
            MessageType::HolePunch => {
                let s = session.as_ref()?;
                let peer_id = msg
                    .payload
                    .as_ref()
                    .and_then(|p| {
                        p.get("peerId")
                            .or_else(|| p.get("peer_id"))
                            .and_then(|v| v.as_str())
                    })
                    .unwrap_or("")
                    .to_string();
                if peer_id.is_empty() {
                    warn!("HolePunch peerId 없음");
                    return None;
                }
                let peer = self.clients.get(&peer_id).map(|c| Arc::clone(c.value()));
                let Some(peer) = peer else {
                    warn!(peer_id, "HolePunch 대상 없음");
                    return None;
                };
                let forward = response_json(
                    MessageType::HolePunch,
                    json!({
                        "peerId": s.id,
                        "fromId": s.id,
                        "publicIp": s.public_ip(),
                        "publicPort": s.public_port()
                    }),
                );
                self.forward_to(&peer, &forward).await;
                None
            }
            MessageType::SignalingPing | MessageType::Ping => {
                let client_ticks = msg
                    .payload
                    .as_ref()
                    .and_then(|p| p.get("clientTicks"))
                    .and_then(|v| v.as_i64())
                    .unwrap_or(0);
                let reply = if msg.msg_type == MessageType::Ping {
                    MessageType::Pong
                } else {
                    MessageType::SignalingPong
                };
                Some(response_json(reply, json!({ "clientTicks": client_ticks })))
            }
            MessageType::Chat => {
                let s = session.as_ref()?;
                let message_text = msg
                    .payload
                    .as_ref()
                    .and_then(|p| p.get("message"))
                    .and_then(|v| v.as_str())
                    .unwrap_or("")
                    .to_string();
                let to_id = msg
                    .payload
                    .as_ref()
                    .and_then(|p| {
                        p.get("toId")
                            .or_else(|| p.get("peerId"))
                            .and_then(|v| v.as_str())
                    })
                    .unwrap_or("")
                    .to_string();
                let payload = json!({
                    "fromId": s.id,
                    "fromName": s.name,
                    "toId": to_id,
                    "message": message_text
                });
                let forward = response_json(MessageType::Chat, payload);
                if !to_id.is_empty() {
                    if let Some(peer) = self.clients.get(&to_id).map(|c| Arc::clone(c.value())) {
                        if peer.room_number == s.room_number {
                            self.forward_to(&peer, &forward).await;
                        }
                    }
                    return None;
                }
                if !s.allow_broadcast() {
                    return None;
                }
                for t in self.room_peers(&s.id, s.room_number) {
                    self.forward_to(&t, &forward).await;
                }
                None
            }
            MessageType::RoomBroadcast => {
                let s = session.as_ref()?;
                if !s.allow_broadcast() {
                    return None;
                }
                let message_text = msg
                    .payload
                    .as_ref()
                    .and_then(|p| p.get("message"))
                    .and_then(|v| v.as_str())
                    .unwrap_or("")
                    .to_string();
                let room_forward = response_json(
                    MessageType::RoomBroadcast,
                    json!({
                        "fromId": s.id,
                        "fromName": s.name,
                        "message": message_text
                    }),
                );
                for t in self.room_peers(&s.id, s.room_number) {
                    self.forward_to(&t, &room_forward).await;
                }
                None
            }
            MessageType::Disconnect => None,
            _ => None,
        }
    }

    fn peer_info_of(&self, s: &ClientSession) -> PeerInfo {
        Self::info_for(s, self.room_master_id(s.room_number).as_deref())
    }

    fn info_for(s: &ClientSession, master_id: Option<&str>) -> PeerInfo {
        PeerInfo {
            id: s.id.clone(),
            name: s.name.clone(),
            public_ip: s.public_ip(),
            public_port: s.public_port(),
            is_master: master_id == Some(s.id.as_str()),
            local_ip: s.local_ip.clone(),
            local_port: s.local_port,
            user_id: s.user_id.lock().unwrap().clone(),
            dedicated_room_id: s.dedicated_room_id.lock().unwrap().clone(),
        }
    }

    fn room_master_id(&self, room: i32) -> Option<String> {
        self.clients
            .iter()
            .filter(|c| c.room_number == room)
            .min_by_key(|c| c.connected_at)
            .map(|c| c.id.clone())
    }

    fn peer_list_json(&self, self_id: &str, room: i32) -> String {
        let (my_ip, my_port) = self
            .clients
            .get(self_id)
            .map(|c| (c.public_ip(), c.public_port()))
            .unwrap_or_default();
        response_json(
            MessageType::PeerList,
            json!({
                "peers": self.peer_infos(self_id, room),
                "roomMasterId": self.room_master_id(room),
                "yourPublicIp": my_ip,
                "yourPublicPort": my_port
            }),
        )
    }

    async fn broadcast_peer_list(&self, room: i32) {
        self.broadcast_peer_list_except(room, "").await;
    }

    async fn broadcast_peer_list_except(&self, room: i32, except_id: &str) {
        let ids: Vec<String> = self
            .clients
            .iter()
            .filter(|c| c.room_number == room && (except_id.is_empty() || c.id != except_id))
            .map(|c| c.id.clone())
            .collect();
        for id in ids {
            if let Some(s) = self.clients.get(&id).map(|c| Arc::clone(c.value())) {
                self.forward_to(&s, &self.peer_list_json(&s.id, room)).await;
            }
        }
    }

    fn peer_infos(&self, self_id: &str, room: i32) -> Vec<PeerInfo> {
        let mut room_peers: Vec<Arc<ClientSession>> = self
            .clients
            .iter()
            .filter(|c| c.room_number == room && c.id != self_id)
            .map(|c| Arc::clone(c.value()))
            .collect();
        room_peers.sort_by_key(|c| c.connected_at);
        let master_id = self.room_master_id(room);
        room_peers
            .into_iter()
            .map(|c| Self::info_for(&c, master_id.as_deref()))
            .collect()
    }

    fn room_peers(&self, self_id: &str, room: i32) -> Vec<Arc<ClientSession>> {
        self.clients
            .iter()
            .filter(|c| c.room_number == room && c.id != self_id)
            .map(|c| Arc::clone(c.value()))
            .collect()
    }

    async fn leave_and_notify(&self, s: &Arc<ClientSession>) {
        let removed = {
            let _g = self.roster.lock().await;
            self.clients
                .remove_if(&s.id, |_, cur| Arc::ptr_eq(cur, s))
                .is_some()
        };
        if removed {
            self.notify_peers_left(s).await;
        }
    }

    async fn notify_peers_left(&self, s: &ClientSession) {
        let forward = response_json(
            MessageType::Disconnect,
            json!({
                "peerId": s.id,
                "fromId": s.id,
                "name": s.name,
                "reason": "left"
            }),
        );
        for t in self.room_peers(&s.id, s.room_number) {
            self.forward_to(&t, &forward).await;
            self.forward_to(&t, &self.peer_list_json(&t.id, s.room_number))
                .await;
        }
    }

    async fn forward_to(&self, target: &ClientSession, message: &str) {
        let data = encode_line(&self.crypto, message);
        let mut w = target.writer.lock().await;
        if let Err(e) = w.write_all(&data).await {
            drop(w);
            let evicted = self
                .clients
                .remove_if(&target.id, |_, cur| std::ptr::eq(cur.as_ref(), target))
                .is_some();
            warn!(id = %target.id, error = %e, evicted, "전달 실패");
            return;
        }
        let _ = w.flush().await;
    }
}

fn short_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()[..8].to_string()
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}
