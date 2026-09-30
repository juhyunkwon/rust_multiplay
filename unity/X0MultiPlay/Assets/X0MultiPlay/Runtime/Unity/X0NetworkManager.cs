using System.Collections.Generic;
using X0.MultiPlay.Client;
using X0.MultiPlay.Protocol;
using UnityEngine;

namespace X0.MultiPlay
{
    // 인스펙터에서 쓰는 간단 래퍼. 실제 화면은 MultiplayClient 씬의 ClientHud 사용
    public class X0NetworkManager : MonoBehaviour
    {
        [Header("Dedicated / Authority")]
        public string dedicatedHost = "127.0.0.1";
        public int dedicatedPort = 19060;

        [Header("Signaling / P2P")]
        public string signalingHost = "127.0.0.1";
        public int signalingTcpPort = 19050;
        public int signalingUdpPort = 19051;

        [Header("Auth")]
        public string userId = "";
        public string password = "";
        public long shopId;

        [Header("Encryption (서버 appsettings Encryption:Key 와 동일)")]
        public string encryptionKey = "";

        [Header("Sync")]
        public Transform localSyncTarget;
        public float syncSendInterval = 0.1f;

        public DedicatedClient Dedicated { get; private set; }
        public P2pClient P2P { get; private set; }
        public RoomInfo CurrentRoom { get; private set; }

        float _nextSync;
        Vector3 _lastSent;
        readonly Dictionary<string, Transform> _remotes = new();

        public event System.Action<string> Log;
        public event System.Action<List<RoomInfo>> RoomListUpdated;
        public event System.Action<string, string> RoomChat;

        void OnDestroy()
        {
            Disconnect();
        }

        public async void ConnectAndLogin()
        {
            var crypto = new PacketCrypto(string.IsNullOrWhiteSpace(encryptionKey) ? null : encryptionKey);
            Dedicated = new DedicatedClient(dedicatedHost, dedicatedPort, crypto);
            Dedicated.OnRoomListReceived += rooms => RoomListUpdated?.Invoke(rooms);
            Dedicated.OnRoomUpdate += OnRoomEntered;
            Dedicated.OnGameStateReceived += OnGameState;
            Dedicated.OnPingUpdated += ms => Emit($"[Dedicated ping] {ms}ms");

            if (!await Dedicated.ConnectAsync())
            {
                Emit("[Step 1] Dedicated 연결 실패 — server-host 실행 여부 확인");
                return;
            }
            Emit($"[Step 1] Dedicated 연결 {dedicatedHost}:{dedicatedPort}");

            var login = await Dedicated.LoginAsync(userId, password, shopId);
            if (login?.Success != true)
            {
                Emit($"[Step 2] 로그인 실패: {login?.Message}");
                return;
            }
            Emit($"[Step 2] 로그인: {Dedicated.UserInfo?.DisplayName}");
            await Dedicated.RequestRoomListAsync();
            _ = Dedicated.SendPingAsync();
        }

        public void CreateRoom(string roomName, int maxPlayers = 4) =>
            _ = Dedicated?.CreateRoomAsync(roomName, maxPlayers);

        public void JoinRoom(string roomId) =>
            _ = Dedicated?.JoinRoomAsync(roomId);

        public void SendChat(string text) =>
            _ = P2P?.SendChatAsync(text);

        public void StartGame() =>
            _ = Dedicated?.StartGameAsync();

        public void SendMove(float x, float y) =>
            _ = Dedicated?.SendInputAsync(x, y);

        public void Disconnect()
        {
            P2P?.Disconnect();
            Dedicated?.Disconnect();
            P2P = null;
            Dedicated = null;
            CurrentRoom = null;
        }

        async void OnRoomEntered(RoomInfo room)
        {
            if (room == null) return;
            if (CurrentRoom != null && CurrentRoom.RoomId == room.RoomId)
            {
                Emit($"[Step 3] 룸 상태 갱신: {room.Name}");
                return;
            }
            CurrentRoom = room;
            Emit($"[Step 3] 룸 입장: {room.Name} ({room.RoomId}) → Step 4 시그널링");

            var crypto = new PacketCrypto(string.IsNullOrWhiteSpace(encryptionKey) ? null : encryptionKey);
            P2P = new P2pClient(signalingHost, signalingTcpPort, signalingUdpPort, crypto);
            P2P.OnLogMessage += Emit;
            P2P.OnChatReceived += (from, msg) => OnRoomMessage(from, msg);
            P2P.OnSignalingPingUpdated += ms => Emit($"[Signaling ping] {ms}ms");

            var name = Dedicated?.UserInfo?.DisplayName ?? "Player";
            var signalingRoom = GetSignalingRoomNumber(room.RoomId);
            if (!await P2P.ConnectAsync(name, signalingRoom))
            {
                Emit("[Step 4] 시그널링 연결 실패 (릴레이 불가)");
                return;
            }
            _ = P2P.SendSignalingPingAsync();
            Emit("[Step 4] 시그널링 연결됨");
        }

        void OnRoomMessage(string fromId, string msg)
        {
            if (msg.StartsWith("OBJ:", System.StringComparison.OrdinalIgnoreCase))
            {
                var rest = msg.Substring(4).Trim().Split(',');
                if (rest.Length >= 2
                    && int.TryParse(rest[0].Trim(), out var x)
                    && int.TryParse(rest[1].Trim(), out var y))
                {
                    Emit($"[OBJ] {fromId} → ({x},{y})");
                    ApplyRemotePosition(fromId, x, y);
                }
                return;
            }
            Emit($"[채팅] [{fromId}] {msg}");
            RoomChat?.Invoke(fromId, msg);
        }

        void ApplyRemotePosition(string fromId, int x, int y)
        {
            if (!_remotes.TryGetValue(fromId, out var t) || t == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Remote_{fromId}";
                t = go.transform;
                _remotes[fromId] = t;
            }
            t.position = new Vector3(x, 0f, y);
        }

        void OnGameState(GameStatePayload state)
        {
            if (state?.Players == null) return;
            foreach (var p in state.Players)
            {
                if (p.Id == Dedicated?.ConnectionId) continue;
                ApplyRemotePosition(p.Id, Mathf.RoundToInt(p.X), Mathf.RoundToInt(p.Y));
            }
        }

        void Update()
        {
            if (P2P == null || localSyncTarget == null) return;
            if (Time.unscaledTime < _nextSync) return;
            var pos = localSyncTarget.position;
            if ((pos - _lastSent).sqrMagnitude < 0.01f) return;
            _lastSent = pos;
            _nextSync = Time.unscaledTime + syncSendInterval;
            _ = P2P.SendChatAsync($"OBJ:{(int)pos.x},{(int)pos.z}");
        }

        static int GetSignalingRoomNumber(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return 1;
            var s = roomId.Replace("room_", "").Trim();
            return int.TryParse(s, out var n) && n > 0 ? n : Mathf.Abs(roomId.GetHashCode() % 10000);
        }

        void Emit(string msg)
        {
            Debug.Log(msg);
            Log?.Invoke(msg);
        }
    }
}
