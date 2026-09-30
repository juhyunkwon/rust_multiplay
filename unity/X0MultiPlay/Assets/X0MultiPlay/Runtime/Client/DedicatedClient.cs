using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using X0.MultiPlay.Protocol;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace X0.MultiPlay.Client
{
    // Dedicated 서버 클라 (로그인, 로비, 룸, 게임)
    public class DedicatedClient
    {
        readonly string _host;
        readonly int _port;
        readonly PacketCrypto _crypto;
        TcpClient _client;
        NetworkStream _stream;
        readonly TcpLineBuffer _lines = new TcpLineBuffer();
        string _connectionId = "";
        UserInfo _userInfo;
        long _inputTick;
        CancellationTokenSource _cts;
        TaskCompletionSource<NetworkMessage> _loginTcs;

        public event Action<GameStatePayload> OnGameStateReceived;
        public event Action<LoginResponse> OnLoginResult;
        public event Action<List<RoomInfo>> OnRoomListReceived;
        public event Action<List<string>> OnLobbyListReceived;
        public event Action<RoomInfo> OnRoomUpdate;
        public event Action<string> OnGameStarted;
        public event Action<int> OnPingUpdated;
        public event Action<string> OnDisconnected;
        public event Action<string> OnLogMessage;

        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public UserInfo UserInfo => _userInfo;
        public int? LastPingMs { get; private set; }
        public bool IsLoggedIn => _userInfo != null;
        public string ConnectionId => _connectionId;
        public bool IsConnected => _client?.Connected == true;

        public DedicatedClient(string host = "127.0.0.1", int port = 19060, PacketCrypto crypto = null)
        {
            _host = host;
            _port = port;
            _crypto = crypto ?? new PacketCrypto(null);
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_host, _port);
                _client.NoDelay = true;
                _stream = _client.GetStream();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _connectionId = "";
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DedicatedClient] 연결 실패 {_host}:{_port} {ex.Message}");
                return false;
            }
        }

        public async Task<LoginResponse> LoginAsync(string userId, string password, long shopId = 0, CancellationToken ct = default)
        {
            if (_stream == null) return null;
            await SendAsync(MessageType.Login, new LoginRequest { UserId = userId, Password = password, ShopId = shopId }, ct);
            var response = await WaitForLoginAckAsync(ct);
            if (response?.Payload == null) return null;
            var loginResp = MessageSerializer.Deserialize<LoginResponse>(response.Payload);
            if (loginResp?.Success == true && loginResp.User != null)
            {
                _userInfo = loginResp.User;
                if (!string.IsNullOrEmpty(loginResp.ConnectionId))
                    _connectionId = loginResp.ConnectionId;
            }
            UnityMainThread.Run(() => OnLoginResult?.Invoke(loginResp ?? new LoginResponse { Success = false }));
            return loginResp;
        }

        public Task RequestLobbyListAsync(CancellationToken ct = default) =>
            SendAsync(MessageType.LobbyList, new { }, ct);

        public Task RequestRoomListAsync(CancellationToken ct = default) =>
            SendAsync(MessageType.RoomList, new { }, ct);

        public Task CreateRoomAsync(string roomName, int maxPlayers = 4, CancellationToken ct = default) =>
            SendAsync(MessageType.CreateRoom, new CreateRoomRequest { RoomName = roomName, MaxPlayers = maxPlayers }, ct);

        public Task JoinRoomAsync(string roomId, CancellationToken ct = default) =>
            SendAsync(MessageType.JoinRoom, new JoinRoomRequest { RoomId = roomId }, ct);

        public Task LeaveRoomAsync(CancellationToken ct = default) =>
            SendAsync(MessageType.LeaveRoom, new { }, ct);

        public Task StartGameAsync(CancellationToken ct = default) =>
            SendAsync(MessageType.StartGame, new { }, ct);

        public Task SendPingAsync(CancellationToken ct = default) =>
            SendAsync(MessageType.PingRequest, new { clientTicks = DateTime.UtcNow.Ticks }, ct);

        public async Task SendInputAsync(float moveX, float moveY, string action = "", CancellationToken ct = default)
        {
            if (_stream == null) return;
            _inputTick++;
            await SendAsync(MessageType.PlayerInput, new PlayerInputPayload
            {
                PlayerId = _connectionId,
                InputTick = _inputTick,
                MoveX = moveX,
                MoveY = moveY,
                Action = action
            }, ct);
        }

        public void Disconnect()
        {
            try
            {
                if (_stream != null)
                {
                    var encoded = _crypto.EncodeForSend(new NetworkMessage
                    {
                        Type = MessageType.Disconnect,
                        Payload = MessageSerializer.ToToken(new { reason = "client" })
                    }.Serialize()) + "\n";
                    var data = Encoding.UTF8.GetBytes(encoded);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }
            }
            catch { /* ignore */ }
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _client?.Close(); } catch { /* ignore */ }
            _client = null;
            _stream = null;
            _userInfo = null;
            _connectionId = "";
        }

        async Task SendAsync(MessageType type, object payload, CancellationToken ct)
        {
            if (_stream == null) return;
            var msg = new NetworkMessage { Type = type, Payload = MessageSerializer.ToToken(payload) };
            var encoded = _crypto.EncodeForSend(msg.Serialize()) + "\n";
            var data = Encoding.UTF8.GetBytes(encoded);
            await _writeLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(data, 0, data.Length, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
            if (type != MessageType.PingRequest)
                OnLogMessage?.Invoke($"[pkt tx tcp] t={(int)type} {type}");
        }

        async Task<NetworkMessage> WaitForLoginAckAsync(CancellationToken ct)
        {
            _loginTcs = new TaskCompletionSource<NetworkMessage>();
            using (ct.Register(() => _loginTcs.TrySetResult(null)))
                return await _loginTcs.Task;
        }

        async Task ReceiveLoopAsync(CancellationToken ct)
        {
            if (_stream == null) return;
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && _client?.Connected == true)
            {
                try
                {
                    var n = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (n == 0) break;
                    if (_lines.Overflow) break;
                    foreach (var line in _lines.Push(buffer, n))
                    {
                        var plain = _crypto.DecodeFromReceive(line);
                        var msg = MessageSerializer.ParseNetworkMessage(plain);
                        if (msg == null) continue;
                        if (msg.Type != MessageType.PingResponse && msg.Type != MessageType.GameState)
                            OnLogMessage?.Invoke($"[pkt rx tcp] t={(int)msg.Type} {msg.Type}");
                        HandleMessage(msg);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[DedicatedClient] 수신 오류: {ex.Message}");
                    break;
                }
            }
        }

        void HandleMessage(NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.LoginAck:
                    _loginTcs?.TrySetResult(msg);
                    break;
                case MessageType.LobbyList:
                    if (msg.Payload is JObject lobbyRoot && lobbyRoot["users"] != null)
                    {
                        var names = new List<string>();
                        foreach (var u in lobbyRoot["users"])
                            names.Add(u.Value<string>("displayName") ?? u.Value<string>("userId") ?? "");
                        UnityMainThread.Run(() => OnLobbyListReceived?.Invoke(names));
                    }
                    break;
                case MessageType.RoomList:
                    if (msg.Payload is JObject roomRoot && roomRoot["rooms"] != null)
                    {
                        var rooms = roomRoot["rooms"].ToObject<List<RoomInfo>>() ?? new List<RoomInfo>();
                        UnityMainThread.Run(() => OnRoomListReceived?.Invoke(rooms));
                    }
                    break;
                case MessageType.CreateRoomAck:
                case MessageType.JoinRoomAck:
                    if (msg.Payload is JObject ack)
                    {
                        var success = ack.Value<bool>("success");
                        var room = ack["room"]?.ToObject<RoomInfo>();
                        if (success)
                            UnityMainThread.Run(() => OnRoomUpdate?.Invoke(room));
                    }
                    break;
                case MessageType.RoomUpdate:
                    {
                        var room = MessageSerializer.Deserialize<RoomInfo>(msg.Payload);
                        UnityMainThread.Run(() => OnRoomUpdate?.Invoke(room));
                    }
                    break;
                case MessageType.StartGame:
                    {
                        var roomId = msg.Payload?["roomId"]?.ToString() ?? "";
                        UnityMainThread.Run(() => OnGameStarted?.Invoke(roomId));
                    }
                    break;
                case MessageType.GameState:
                    {
                        var state = MessageSerializer.Deserialize<GameStatePayload>(msg.Payload);
                        if (state != null)
                            UnityMainThread.Run(() => OnGameStateReceived?.Invoke(state));
                    }
                    break;
                case MessageType.PingResponse:
                case MessageType.Pong:
                    if (msg.Payload != null)
                    {
                        var clientTicks = msg.Payload.Value<long>("clientTicks");
                        var elapsed = (int)((DateTime.UtcNow.Ticks - clientTicks) / 10000);
                        if (elapsed >= 0 && elapsed <= 60000)
                        {
                            LastPingMs = elapsed;
                            UnityMainThread.Run(() => OnPingUpdated?.Invoke(elapsed));
                        }
                    }
                    break;
                case MessageType.Chat:
                    break;
                case MessageType.Disconnect:
                    UnityMainThread.Run(() => OnDisconnected?.Invoke(msg.Payload?.Value<string>("reason") ?? "left"));
                    break;
            }
        }
    }
}
