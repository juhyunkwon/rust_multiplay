using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using X0.MultiPlay.Client;
using X0.MultiPlay.Protocol;
using UnityEngine;

namespace X0.MultiPlay
{
    // Dedicated 랑 P2P 연결을 단계별로 처리
    // 이벤트는 전부 메인 스레드에서 호출됨
    public class MultiplaySession
    {
        public const string RoleMaster = "마스터(방장)";
        public const string RoleMember = "멤버";

        public string DedicatedHost = "127.0.0.1";
        public int DedicatedPort = 19060;
        public string SignalingHost = "127.0.0.1";
        public int SignalingTcpPort = 19050;
        public int SignalingUdpPort = 19051;
        public string EncryptionKey = "";
        public long ShopId;

        public DedicatedClient Dedicated { get; private set; }
        public P2pClient P2P { get; private set; }
        public RoomInfo CurrentRoom { get; private set; }
        public string SignalingRoomId { get; private set; }
        public string LoggedUserId { get; private set; } = "";
        public bool P2pBusy { get; private set; }

        public IReadOnlyList<PeerInfo> Peers => P2P?.LastPeerList ?? (IReadOnlyList<PeerInfo>)Array.Empty<PeerInfo>();

        public event Action<string> Log;
        public event Action Changed;
        public event Action<List<RoomInfo>> RoomListUpdated;
        public event Action PeersChanged;
        public event Action<string, int, int> RemoteObjMoved;
        public event Action<ChatEntry> ChatLine;
        // 성공 여부, 알림 문구 (버튼 결과 표시용)
        public event Action<bool, string> Notice;

        public sealed class ChatEntry
        {
            public DateTime Time = DateTime.Now;
            public string PeerId;
            public string Text;
            public bool Whisper;
            public bool Outgoing;
            public string Via;
            public bool System;
        }

        PacketCrypto Crypto() =>
            new PacketCrypto(string.IsNullOrWhiteSpace(EncryptionKey) ? null : EncryptionKey);

        public async Task ConnectDedicatedAsync(string host, int port)
        {
            DedicatedHost = host;
            DedicatedPort = port;
            var client = new DedicatedClient(host, port, Crypto());
            client.OnRoomListReceived += rooms => Main(() =>
            {
                RoomListUpdated?.Invoke(rooms);
                Changed?.Invoke();
            });
            client.OnLobbyListReceived += names =>
                Emit("[로비] " + names.Count + "명: " + string.Join(", ", names));
            client.OnDisconnected += reason => Main(() =>
            {
                Emit("[Dedicated] 종료 통지: " + reason);
                if (Dedicated == client)
                {
                    Dedicated = null;
                    LoggedUserId = "";
                    Notify(false, "Dedicated 서버와 연결이 끊겼습니다. (" + reason + ")");
                    Changed?.Invoke();
                }
            });
            client.OnLogMessage += Emit;
            client.OnRoomUpdate += room => Main(() => _ = OnRoomUpdateAsync(client, room));
            client.OnPingUpdated += _ => Main(() => Changed?.Invoke());
            client.OnGameStarted += id => Emit($"[게임 시작] {id}");

            if (!await client.ConnectAsync())
            {
                Emit("[Step 1] 실패 - server-host / dedicated-server 실행 여부 확인");
                Notify(false, $"Dedicated 연결 실패 {host}:{port}  (server-host 실행 여부 확인)");
                Main(() => Changed?.Invoke());
                return;
            }

            Dedicated = client;
            Emit($"[Step 1] 성공 {host}:{port}");
            Notify(true, $"Dedicated 연결 성공 {host}:{port}");
            _ = client.SendPingAsync();
            Main(() => Changed?.Invoke());
        }

        public async Task LoginAsync(string userId, string password)
        {
            if (Dedicated == null)
            {
                Emit("[Step 2] Dedicated 미연결");
                Notify(false, "먼저 Dedicated [연결]을 누르세요.");
                return;
            }
            var result = await Dedicated.LoginAsync(userId, password, ShopId);
            if (result?.Success != true)
            {
                Emit("[Step 2] 실패: " + (result?.Message ?? "오류"));
                Notify(false, "로그인 실패: " + (result?.Message ?? "응답 없음"));
                Main(() => Changed?.Invoke());
                return;
            }
            LoggedUserId = Dedicated.UserInfo?.UserId;
            if (string.IsNullOrEmpty(LoggedUserId)) LoggedUserId = userId;
            Emit("[Step 2] 성공: " + Dedicated.UserInfo?.DisplayName);
            Notify(true, $"로그인 성공: {LoggedUserId} ({Dedicated.UserInfo?.DisplayName})  ConnectionId={Dedicated.ConnectionId}");
            await Dedicated.RequestLobbyListAsync();
            await Dedicated.RequestRoomListAsync();
            _ = RefreshPeersAsync(true);
            Main(() => Changed?.Invoke());
        }

        public void RequestRoomList()
        {
            if (Dedicated?.IsLoggedIn == true)
                _ = Dedicated.RequestRoomListAsync();
            else
                Notify(false, "룸 목록: 먼저 로그인하세요.");
        }

        public void CreateRoom(string roomName)
        {
            if (Dedicated?.IsLoggedIn != true)
            {
                Notify(false, "방 만들기: 먼저 로그인하세요.");
                return;
            }
            _ = Dedicated.CreateRoomAsync(roomName);
        }

        public void JoinRoom(string roomId)
        {
            if (Dedicated?.IsLoggedIn != true)
            {
                Notify(false, "참가: 먼저 로그인하세요.");
                return;
            }
            _ = Dedicated.JoinRoomAsync(roomId);
        }

        // toPeerId 비었으면 방 전체, 있으면 귓속말
        public void SendChat(string text, string toPeerId = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (P2P is not { IsConnected: true })
            {
                Notify(false, "채팅: P2P 미접속. 방에 들어가거나 [P2P 접속]을 누르세요.");
                return;
            }
            if (string.IsNullOrEmpty(toPeerId))
            {
                if (Peers.Count == 0)
                {
                    Notify(false, "채팅: 같은 룸에 받을 피어가 없습니다.");
                    return;
                }
                _ = P2P.SendChatAsync(text);
                return;
            }
            if (P2P.FindPeer(toPeerId) == null)
            {
                Notify(false, "귓속말: 받는 사람이 룸에 없습니다. 다시 고르세요.");
                return;
            }
            _ = P2P.SendChatToAsync(toPeerId, text);
        }

        public void SendObj(int x, int y)
        {
            if (P2P == null) return;
            var packet = $"OBJ:{x},{y}";
            Emit($"[Step 4 OBJ] send data: {packet}");
            _ = P2P.SendChatAsync(packet);
        }

        public async Task RefreshPeersAsync(bool silent)
        {
            if (P2P is not { IsConnected: true })
            {
                if (!silent) Notify(false, "피어 목록: P2P 미접속");
                return;
            }
            SyncLinks();
            try
            {
                var peers = await P2P.GetPeerListAsync();
                if (!silent)
                {
                    Emit("[Step 4] 피어 목록: " + peers.Count + "명");
                    Notify(true, $"룸{P2P.MyRoomNumber} 피어 {peers.Count}명 · 나={(P2P.IsMaster ? RoleMaster : RoleMember)}");
                }
            }
            catch (Exception ex)
            {
                Emit("[Step 4] 피어 목록 실패: " + ex.Message);
                if (!silent) Notify(false, "피어 목록 실패: " + ex.Message);
            }
        }

        public async Task ConnectP2PAsync()
        {
            if (P2P is { IsConnected: true })
            {
                Notify(true, $"이미 P2P 접속 중 · 룸{P2P.MyRoomNumber} {(P2P.IsMaster ? RoleMaster : RoleMember)}");
                return;
            }
            if (CurrentRoom == null)
            {
                Notify(false, "P2P 접속: 먼저 방을 만들거나 참가하세요. (방 = 시그널링 룸)");
                return;
            }
            await StartSignalingAsync(CurrentRoom);
        }

        public void DisconnectP2P()
        {
            if (P2P == null)
            {
                Notify(false, "P2P 끊기: 접속 중이 아닙니다.");
                return;
            }
            P2P.Disconnect();
            P2P = null;
            SignalingRoomId = null;
            Emit("[Step 4] P2P 연결 해제");
            AddSystemLine("--- P2P 연결 해제 ---");
            Notify(true, "P2P 연결을 끊었습니다. 다시 붙으려면 [P2P 접속].");
            PeersChanged?.Invoke();
            Changed?.Invoke();
        }

        public void Disconnect()
        {
            P2P?.Disconnect();
            Dedicated?.Disconnect();
            P2P = null;
            Dedicated = null;
            CurrentRoom = null;
            SignalingRoomId = null;
            LoggedUserId = "";
            Emit("[연결 해제]");
            PeersChanged?.Invoke();
            Changed?.Invoke();
        }

        public async Task TickPingAsync()
        {
            if (Dedicated != null) _ = Dedicated.SendPingAsync();
            if (P2P != null) _ = P2P.SendSignalingPingAsync();
            Changed?.Invoke();
            await Task.CompletedTask;
        }

        async Task OnRoomUpdateAsync(DedicatedClient client, RoomInfo room)
        {
            if (room == null) return;
            CurrentRoom = room;
            if (SignalingRoomId == room.RoomId)
            {
                Emit("[Step 3] 룸 상태 갱신: " + room.Name);
                SyncLinks();
                Changed?.Invoke();
                return;
            }
            Emit("[Step 3] 룸 입장: " + room.Name + " (" + room.RoomId + ") → Step 4 시그널링");
            Notify(true, $"방 입장: {room.Name} ({room.RoomId}) → P2P 자동 접속");
            if (P2P != null)
            {
                P2P.Disconnect();
                P2P = null;
            }
            await StartSignalingAsync(room);
        }

        async Task StartSignalingAsync(RoomInfo room)
        {
            if (P2pBusy) return;
            P2pBusy = true;
            Changed?.Invoke();
            try
            {
                SignalingRoomId = room.RoomId;
                var p2p = new P2pClient(SignalingHost, SignalingTcpPort, SignalingUdpPort, Crypto())
                {
                    UserId = LoggedUserId ?? "",
                    DedicatedRoomId = room.RoomId ?? ""
                };
                p2p.OnLogMessage += Emit;
                p2p.OnChatMessage += OnChatMessage;
                p2p.OnChatSent += OnChatSent;
                p2p.OnPeerListUpdated += _ => { PeersChanged?.Invoke(); Changed?.Invoke(); };
                p2p.OnPeerConnected += (_, __) => { PeersChanged?.Invoke(); Changed?.Invoke(); };
                p2p.OnPeerDisconnected += id =>
                {
                    AddSystemLine($"--- {PeerName(id)} 퇴장 ---");
                    PeersChanged?.Invoke();
                    Changed?.Invoke();
                };
                p2p.OnSignalingPingUpdated += _ => Changed?.Invoke();
                p2p.OnError += msg => Emit("[P2P 오류] " + msg);

                var name = Dedicated?.UserInfo?.DisplayName ?? "Player";
                var signalingRoom = GetSignalingRoomNumber(room.RoomId);
                if (!await p2p.ConnectAsync(name, signalingRoom))
                {
                    Emit("[Step 4] 시그널링 연결 실패 (릴레이 불가)");
                    Notify(false, $"P2P 접속 실패 {SignalingHost}:{SignalingTcpPort}  (시그널링 서버 확인)");
                    P2P = null;
                    SignalingRoomId = null;
                    return;
                }
                P2P = p2p;
                _ = p2p.SendSignalingPingAsync();
                Emit("[Step 4] 시그널링 연결됨");
                Notify(true, $"P2P 접속 성공 · 룸{p2p.MyRoomNumber} · 나={(p2p.IsMaster ? RoleMaster : RoleMember)} · 피어 {p2p.LastPeerList.Count}명");
                PeersChanged?.Invoke();
            }
            finally
            {
                P2pBusy = false;
                Changed?.Invoke();
            }
        }

        void SyncLinks()
        {
            if (P2P == null) return;
            P2P.UserId = LoggedUserId ?? "";
            P2P.DedicatedRoomId = CurrentRoom?.RoomId ?? "";
        }

        void OnChatMessage(string fromId, string msg, string via, bool whisper)
        {
            if (msg != null && msg.StartsWith("OBJ:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = msg.Substring(4).Trim().Split(',');
                if (rest.Length >= 2
                    && int.TryParse(rest[0].Trim(), out var x)
                    && int.TryParse(rest[1].Trim(), out var y))
                {
                    Emit($"[Step 4 OBJ] recv {msg} from [{fromId}]");
                    RemoteObjMoved?.Invoke(fromId, x, y);
                }
                return;
            }
            Emit($"[Step 4 {(whisper ? "귓속말" : "전체")} {via}] [{fromId}] {msg}");
            ChatLine?.Invoke(new ChatEntry { PeerId = fromId, Text = msg, Whisper = whisper, Via = via });
        }

        void OnChatSent(bool udp, string msg, string dest)
        {
            if (msg != null && msg.StartsWith("OBJ:", StringComparison.OrdinalIgnoreCase)) return;
            var all = dest == "room";
            ChatLine?.Invoke(new ChatEntry
            {
                PeerId = all ? null : dest,
                Text = msg,
                Whisper = !all,
                Outgoing = true,
                Via = udp ? "udp" : "relay"
            });
        }

        void AddSystemLine(string text) =>
            ChatLine?.Invoke(new ChatEntry { Text = text, System = true });

        public string PeerName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            var p = P2P?.FindPeer(id);
            return string.IsNullOrEmpty(p?.Name) ? id : p.Name;
        }

        public string PeerPath(PeerInfo p) =>
            P2P?.HasDirectUdpTo(p?.Id) == true ? "UDP 직접" : "릴레이";

        public string PeerDedicated(PeerInfo p)
        {
            if (p == null || string.IsNullOrEmpty(p.DedicatedRoomId))
                return string.IsNullOrEmpty(p?.UserId) ? "미로그인" : "로비";
            if (CurrentRoom != null && p.DedicatedRoomId == CurrentRoom.RoomId)
                return "같은 방";
            return "다른 방 " + p.DedicatedRoomId;
        }

        public static string Addr(string ip, int port) =>
            string.IsNullOrEmpty(ip) || port <= 0 ? "-" : $"{ip}:{port}";

        static int GetSignalingRoomNumber(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return 1;
            var s = roomId.Replace("room_", "").Trim();
            return int.TryParse(s, out var n) && n > 0 ? n : Mathf.Abs(roomId.GetHashCode() % 10000);
        }

        void Notify(bool ok, string text) => Main(() => Notice?.Invoke(ok, text));

        static void Main(Action action) => UnityMainThread.Run(action);

        void Emit(string msg)
        {
            Main(() =>
            {
                Debug.Log(msg);
                Log?.Invoke(msg);
            });
        }

        public string BuildInfoText(int? pingMs, int? sigPingMs)
        {
            var d = Dedicated;
            var p = P2P;
            var peers = Peers;
            var udp = peers.Count(x => p?.HasDirectUdpTo(x.Id) == true);
            var lines = new List<string>
            {
                "=== Step 1. Dedicated (TCP) ===",
                "  주소: " + (d != null ? $"{DedicatedHost}:{DedicatedPort}" : "-"),
                "  연결: " + (d != null ? "연결됨" : "끊김") + "   Ping: " + (pingMs is int ms ? ms + "ms" : "???ms"),
                "",
                "=== Step 2. 로그인 ===",
                "  계정: " + (string.IsNullOrEmpty(LoggedUserId) ? "-" : LoggedUserId) + "   이름: " + (d?.UserInfo?.DisplayName ?? "-"),
                "  ConnectionId: " + (d?.ConnectionId ?? "-"),
                "",
                "=== Step 3. 방 (Dedicated) ===",
                "  방: " + (CurrentRoom != null ? $"{CurrentRoom.Name} ({CurrentRoom.RoomId})" : "-"),
                "",
                "=== Step 4. 시그널링 / P2P ===",
                "  시그널링: " + (p != null ? $"룸{p.MyRoomNumber} 연결됨" : "-") + "   Ping: " + (sigPingMs is int sms ? sms + "ms" : "???ms"),
                "  내 역할: " + (p != null ? (p.IsMaster ? RoleMaster : RoleMember) : "-"),
                "  내 외부(공인): " + (p?.MyPublicAddress ?? "-"),
                "  내 내부(LAN): " + (p?.MyLocalAddress ?? "-"),
                "  피어: " + peers.Count + "명  (UDP 직접 " + udp + " · 릴레이 " + (peers.Count - udp) + ")",
            };
            foreach (var x in peers)
            {
                lines.Add($"  · {x.Name} [{(x.IsMaster ? RoleMaster : RoleMember)}] 계정={(string.IsNullOrEmpty(x.UserId) ? "-" : x.UserId)}");
                lines.Add($"      외부 {Addr(x.PublicIp, x.PublicPort)}  내부 {Addr(x.LocalIp, x.LocalPort)}");
                lines.Add($"      P2P={PeerPath(x)}  Dedicated={PeerDedicated(x)}");
            }
            lines.Add("");
            lines.Add("마스터 = 룸에 가장 먼저 들어온 1명. 나가면 다음 입장자가 자동 승계.");
            return string.Join("\n", lines);
        }
    }
}
