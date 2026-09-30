using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using X0.MultiPlay.Protocol;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace X0.MultiPlay.Client
{
    // P2P 클라. 홀펀칭 되면 UDP 로, 안 되면 시그널링 서버 릴레이
    // 이벤트는 전부 메인 스레드에서 호출됨
    public class P2pClient
    {
        readonly string _signalingHost;
        readonly int _signalingPort;
        readonly PacketCrypto _crypto;
        TcpClient _signalingClient;
        NetworkStream _signalingStream;
        readonly TcpLineBuffer _signalingLines = new TcpLineBuffer();
        readonly SemaphoreSlim _signalWrite = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim _peerListLock = new SemaphoreSlim(1, 1);
        UdpClient _udpClient;
        int _localPort;
        string _myId = "";
        string _udpToken = "";
        string _myName = "Player";
        string _myPublicIp = "";
        int _myPublicPort;
        bool _isMaster;
        int _myRoomNumber = 1;
        string _myLocalIp = "";
        readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
        CancellationTokenSource _cts;
        TaskCompletionSource<NetworkMessage> _peerListTcs;
        List<PeerInfo> _lastPeerList = new();
        bool _acceptRoomBroadcast = true;

        // fromId, message
        public event Action<string, string> OnChatReceived;
        // fromId, message, via(udp/relay), 귓속말 여부
        public event Action<string, string, string, bool> OnChatMessage;
        // UDP 직접 여부, message, 대상(room 또는 peerId)
        public event Action<bool, string, string> OnChatSent;
        public event Action<string, PeerInfo> OnPeerConnected;
        public event Action<string> OnPeerDisconnected;
        public event Action<PeerInfo> OnConnectRequested;
        public event Action<IReadOnlyList<PeerInfo>> OnPeerListUpdated;
        public event Action<string> OnLogMessage;
        public event Action<string> OnError;
        public event Action<int> OnSignalingPingUpdated;

        public int SignalingUdpPort { get; }
        // Dedicated 로그인 계정. PeerList 요청할 때 같이 보냄
        public string UserId { get; set; } = "";
        // Dedicated 방 ID. PeerList 요청할 때 같이 보냄
        public string DedicatedRoomId { get; set; } = "";
        public int? LastSignalingPingMs { get; private set; }
        public string MyId => _myId;
        public string MyName => _myName;
        public bool IsMaster => _isMaster;
        public IReadOnlyList<PeerInfo> LastPeerList => _lastPeerList;
        public int MyRoomNumber => _myRoomNumber;
        public IReadOnlyList<string> ConnectedPeerIds => _peers.Keys.ToList();
        public bool IsConnected => _signalingClient?.Connected == true;
        public bool HasDirectUdp => _peers.Values.Any(p => p.Punched && p.Info.PublicPort > 0);

        public string GetConnectedPeersDisplay() =>
            string.Join(", ", _peers.Select(kv => $"{kv.Key} ({kv.Value.Info.PublicIp}:{kv.Value.Info.PublicPort})"));

        public string MyLocalAddress =>
            !string.IsNullOrEmpty(_myLocalIp) && _localPort != 0
                ? $"{_myLocalIp}:{_localPort}"
                : "-:-";

        public string MyPublicAddress =>
            string.IsNullOrEmpty(_myPublicIp) ? "-:-" : $"{_myPublicIp}:{_myPublicPort}";

        public class PeerConnection
        {
            public PeerInfo Info;
            public UdpClient Udp;
            public DateTime ConnectedAt;
            public bool Punched;
            public IPEndPoint Observed;
        }

        public P2pClient(string signalingHost = "127.0.0.1", int signalingTcpPort = 19050, int signalingUdpPort = 19051, PacketCrypto crypto = null)
        {
            _signalingHost = signalingHost;
            _signalingPort = signalingTcpPort;
            SignalingUdpPort = signalingUdpPort;
            _crypto = crypto ?? new PacketCrypto(null);
        }

        public async Task<bool> ConnectAsync(string playerName = "Player", int roomNumber = 1, CancellationToken ct = default)
        {
            if (IsConnected) return true;
            _myName = playerName;
            _acceptRoomBroadcast = true;
            _peers.Clear();
            _lastPeerList = new List<PeerInfo>();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                _signalingClient = new TcpClient();
                await _signalingClient.ConnectAsync(_signalingHost, _signalingPort);
                _signalingClient.NoDelay = true;
                _signalingStream = _signalingClient.GetStream();
                _myLocalIp = "";
                if (_signalingClient.Client.LocalEndPoint is IPEndPoint localEp)
                {
                    var ip = localEp.Address.IsIPv4MappedToIPv6 ? localEp.Address.MapToIPv4() : localEp.Address;
                    if (!IPAddress.IsLoopback(ip))
                        _myLocalIp = ip.ToString();
                }

                _udpClient = new UdpClient(0);
                _localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;
                if (string.IsNullOrEmpty(_myLocalIp))
                    _myLocalIp = GetFallbackLocalIp();

                var registerReq = new RegisterRequest
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = _myName,
                    LocalPort = _localPort,
                    RoomNumber = roomNumber,
                    LocalIp = _myLocalIp,
                    UserId = UserId ?? "",
                    DedicatedRoomId = DedicatedRoomId ?? ""
                };
                await SendToSignalingAsync(MessageType.Register, registerReq, ct);
                var response = await ReadSignalingResponseAsync(MessageType.RegisterAck, ct);
                if (response?.Type != MessageType.RegisterAck) return false;

                if (response.Payload != null)
                {
                    var regResp = MessageSerializer.Deserialize<RegisterResponse>(response.Payload);
                    if (regResp != null)
                    {
                        _myId = regResp.RegisteredId;
                        _myPublicIp = regResp.PublicIp ?? "";
                        _myPublicPort = regResp.PublicPort;
                        _isMaster = regResp.IsMaster;
                        _udpToken = regResp.UdpToken ?? "";
                        _myRoomNumber = roomNumber;
                        var role = _isMaster ? " [마스터]" : "";
                        Log($"[연결] 시그널링 등록 완료 | ID: {_myId} | 룸: {roomNumber}{role}");
                    }
                }

                var udpRegister = Encoding.UTF8.GetBytes($"REGISTER_UDP:{_myId}:{_localPort}:{_udpToken}");
                await _udpClient.SendAsync(udpRegister, udpRegister.Length, _signalingHost, SignalingUdpPort);
                Pkt("tx", "udp", $"REGISTER_UDP:{_myId}:{_localPort}:token → {_signalingHost}:{SignalingUdpPort}");

                _ = Task.Run(() => SignalingReceiveLoopAsync(_cts.Token));
                _ = Task.Run(() => UdpReceiveLoopAsync(_cts.Token));

                try
                {
                    await GetPeerListAsync(ct);
                }
                catch (Exception ex)
                {
                    Log($"[이벤트] 피어 목록 조회 실패: {ex.Message}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"[연결 실패] {ex.Message}");
                return false;
            }
        }

        public async Task<List<PeerInfo>> GetPeerListAsync(CancellationToken ct = default)
        {
            if (_signalingStream == null) return new List<PeerInfo>();
            await _peerListLock.WaitAsync(ct);
            try
            {
                _peerListTcs = new TaskCompletionSource<NetworkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(5000);
                using (cts.Token.Register(() => _peerListTcs.TrySetResult(null)))
                {
                    await SendToSignalingAsync(MessageType.PeerList, new
                    {
                        userId = UserId ?? "",
                        dedicatedRoomId = DedicatedRoomId ?? ""
                    }, ct);
                    var response = await _peerListTcs.Task;
                    if (response?.Type != MessageType.PeerList || response.Payload == null)
                        return new List<PeerInfo>();
                    ApplyPeerList(response);
                    return _lastPeerList;
                }
            }
            finally
            {
                _peerListLock.Release();
            }
        }

        public async Task<bool> ConnectToPeerByIndexAsync(int roomIndex, CancellationToken ct = default)
        {
            if (roomIndex < 0 || roomIndex >= _lastPeerList.Count) return false;
            await ConnectToPeerAsync(_lastPeerList[roomIndex].Id, ct);
            return true;
        }

        public Task ConnectToPeerAsync(string peerId, CancellationToken ct = default) =>
            SendToSignalingAsync(MessageType.ConnectRequest, new ConnectRequestPayload
            {
                PeerId = peerId,
                RequesterId = _myId
            }, ct);

        public bool HasDirectUdpTo(string peerId) =>
            !string.IsNullOrEmpty(peerId)
            && _peers.TryGetValue(peerId, out var p)
            && p.Punched
            && p.Info?.PublicPort > 0;

        public PeerInfo FindPeer(string peerId) =>
            _lastPeerList.FirstOrDefault(p => p.Id == peerId)
            ?? (_peers.TryGetValue(peerId ?? "", out var c) ? c.Info : null);

        public Task SendChatAsync(string message, CancellationToken ct = default) =>
            SendChatToAsync(null, message, ct);

        public Task SendChatToAsync(string toPeerId, string message, CancellationToken ct = default) =>
            SendP2pOrRelayAsync(toPeerId, message, ct);

        public Task SendRoomBroadcastAsync(string message, CancellationToken ct = default) =>
            SendToSignalingAsync(MessageType.RoomBroadcast, new { message }, ct);

        public Task SendSignalingPingAsync(CancellationToken ct = default) =>
            SendToSignalingAsync(MessageType.SignalingPing, new { clientTicks = DateTime.UtcNow.Ticks }, ct);

        public void Disconnect()
        {
            try
            {
                if (_signalingStream != null)
                {
                    var encoded = _crypto.EncodeForSend(new NetworkMessage
                    {
                        Type = MessageType.Disconnect,
                        Payload = MessageSerializer.ToToken(new { peerId = _myId, reason = "client" })
                    }.Serialize()) + "\n";
                    var data = Encoding.UTF8.GetBytes(encoded);
                    _signalingStream.Write(data, 0, data.Length);
                    _signalingStream.Flush();
                }
            }
            catch { /* ignore */ }
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _signalingClient?.Close(); } catch { /* ignore */ }
            try { _udpClient?.Close(); } catch { /* ignore */ }
            _signalingClient = null;
            _signalingStream = null;
            _udpClient = null;
            _myId = "";
            _udpToken = "";
            _isMaster = false;
            _peers.Clear();
            _lastPeerList = new List<PeerInfo>();
            _peerListTcs?.TrySetResult(null);
        }

        public void DisconnectAllPeers()
        {
            _acceptRoomBroadcast = false;
            foreach (var p in _peers.Values)
            {
                if (p.Udp != null && !ReferenceEquals(p.Udp, _udpClient))
                {
                    try { p.Udp.Close(); } catch { /* ignore */ }
                }
            }
            _peers.Clear();
        }

        async Task SendP2pOrRelayAsync(string toPeerId, string message, CancellationToken ct)
        {
            toPeerId = string.IsNullOrWhiteSpace(toPeerId) ? "" : toPeerId.Trim();
            var payload = string.IsNullOrEmpty(toPeerId)
                ? (object)new { fromId = _myId, message }
                : new { fromId = _myId, toId = toPeerId, message };
            var sent = string.IsNullOrEmpty(toPeerId)
                ? await SendUdpToPeersAsync(MessageType.Chat, payload, ct)
                : await SendUdpToPeerAsync(toPeerId, MessageType.Chat, payload, ct);
            if (!sent)
            {
                await SendToSignalingAsync(
                    MessageType.Chat,
                    string.IsNullOrEmpty(toPeerId)
                        ? (object)new { message }
                        : (object)new { message, toId = toPeerId },
                    ct);
            }
            var dest = string.IsNullOrEmpty(toPeerId) ? "room" : toPeerId;
            if (sent)
                Pkt("tx", "udp", $"Chat UDP-direct → {dest} '{message}'");
            else
                Pkt("tx", "tcp", $"Chat relay → {dest} '{message}'");
            Main(() => OnChatSent?.Invoke(sent, message, dest));
        }

        async Task<bool> SendUdpToPeerAsync(string peerId, MessageType type, object payload, CancellationToken ct)
        {
            if (_udpClient == null || string.IsNullOrEmpty(peerId)) return false;
            if (!_peers.TryGetValue(peerId, out var peer) || peer.Info == null || !peer.Punched)
                return false;
            var ep = peer.Observed;
            if (ep == null)
            {
                if (peer.Info.PublicPort <= 0) return false;
                var ip = string.IsNullOrEmpty(peer.Info.PublicIp) || peer.Info.PublicIp == "0.0.0.0"
                    ? "127.0.0.1"
                    : peer.Info.PublicIp;
                if (!IPAddress.TryParse(ip, out var addr)) return false;
                ep = new IPEndPoint(addr, peer.Info.PublicPort);
            }
            var encoded = _crypto.EncodeForSend(new NetworkMessage
            {
                Type = type,
                Payload = MessageSerializer.ToToken(payload)
            }.Serialize());
            var bytes = Encoding.UTF8.GetBytes(encoded);
            try
            {
                await _udpClient.SendAsync(bytes, bytes.Length, ep);
                Pkt("tx", "udp", $"{type} {bytes.Length}B → {ep} peer={peer.Info.Id}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[UDP 송신 실패] {peer.Info.Id}: {ex.Message}");
                return false;
            }
        }

        async Task<bool> SendUdpToPeersAsync(MessageType type, object payload, CancellationToken ct)
        {
            if (_udpClient == null) return false;
            var targets = _peers.Values.Where(p => p.Info != null && !string.IsNullOrEmpty(p.Info.Id)).ToList();
            // UDP 안 붙은 사람 하나라도 있으면 전부 릴레이로 (중복/누락 방지)
            if (targets.Count == 0 || targets.Any(p => !p.Punched)) return false;
            var all = true;
            foreach (var peer in targets)
            {
                if (!await SendUdpToPeerAsync(peer.Info.Id, type, payload, ct))
                    all = false;
            }
            return all;
        }

        async Task PunchPeerAsync(PeerInfo info, CancellationToken ct)
        {
            if (info == null || string.IsNullOrEmpty(info.Id)) return;
            await SendToSignalingAsync(MessageType.HolePunch, new { peerId = info.Id }, ct);
            await SendUdpPunchAsync(info);
        }

        async Task SendUdpPunchAsync(PeerInfo info)
        {
            if (_udpClient == null || info == null || info.PublicPort <= 0) return;
            var ip = string.IsNullOrEmpty(info.PublicIp) || info.PublicIp == "0.0.0.0" ? "127.0.0.1" : info.PublicIp;
            try
            {
                var punch = Encoding.UTF8.GetBytes("HOLEPUNCH");
                await _udpClient.SendAsync(punch, punch.Length, ip, info.PublicPort);
                Pkt("tx", "udp", $"HOLEPUNCH → {ip}:{info.PublicPort} peer={info.Id}");
                var ping = _crypto.EncodeForSend(new NetworkMessage
                {
                    Type = MessageType.Ping,
                    Payload = MessageSerializer.ToToken(new { fromId = _myId, clientTicks = DateTime.UtcNow.Ticks })
                }.Serialize());
                var bytes = Encoding.UTF8.GetBytes(ping);
                await _udpClient.SendAsync(bytes, bytes.Length, ip, info.PublicPort);
                Pkt("tx", "udp", $"Ping {bytes.Length}B → {ip}:{info.PublicPort}");
            }
            catch (Exception ex)
            {
                Log($"[홀펀치 UDP] {ex.Message}");
            }
        }

        async Task SendToSignalingAsync(MessageType type, object payload, CancellationToken ct)
        {
            if (_signalingStream == null) return;
            var msg = new NetworkMessage { Type = type, Payload = MessageSerializer.ToToken(payload) };
            var encoded = _crypto.EncodeForSend(msg.Serialize()) + "\n";
            var data = Encoding.UTF8.GetBytes(encoded);
            await _signalWrite.WaitAsync(ct);
            try
            {
                await _signalingStream.WriteAsync(data, 0, data.Length, ct);
                await _signalingStream.FlushAsync(ct);
                if (type != MessageType.SignalingPing && type != MessageType.Ping)
                    Pkt("tx", "tcp", $"t={(int)type} {type} {Clip(MessageSerializer.ToToken(payload)?.ToString())}");
            }
            finally
            {
                _signalWrite.Release();
            }
        }

        async Task<NetworkMessage> ReadSignalingResponseAsync(MessageType expected, CancellationToken ct)
        {
            if (_signalingStream == null) return null;
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                var n = await _signalingStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (n == 0) return null;
                if (_signalingLines.Overflow) return null;
                foreach (var line in _signalingLines.Push(buffer, n))
                {
                    var msg = MessageSerializer.ParseNetworkMessage(_crypto.DecodeFromReceive(line));
                    if (msg == null) continue;
                    if (msg.Type == MessageType.PeerList)
                        ApplyPeerList(msg);
                    if (msg.Type == expected)
                        return msg;
                }
            }
            return null;
        }

        async Task SignalingReceiveLoopAsync(CancellationToken ct)
        {
            if (_signalingStream == null) return;
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && _signalingClient?.Connected == true)
            {
                try
                {
                    var n = await _signalingStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (n == 0) break;
                    if (_signalingLines.Overflow) break;
                    foreach (var line in _signalingLines.Push(buffer, n))
                    {
                        var msg = MessageSerializer.ParseNetworkMessage(_crypto.DecodeFromReceive(line));
                        if (msg == null) continue;
                        if (msg.Type != MessageType.SignalingPong && msg.Type != MessageType.Pong)
                            Pkt("rx", "tcp", $"t={(int)msg.Type} {msg.Type} {Clip(msg.Payload?.ToString())}");
                        switch (msg.Type)
                        {
                            case MessageType.ConnectRequest:
                                if (msg.Payload != null)
                                {
                                    var requesterId = msg.Payload.Value<string>("requesterId") ?? "";
                                    var requesterInfo = msg.Payload["requesterInfo"]?.ToObject<PeerInfo>();
                                    if (!string.IsNullOrEmpty(requesterId) && requesterInfo != null)
                                    {
                                        TryAddPeer(requesterInfo);
                                        Main(() => OnConnectRequested?.Invoke(requesterInfo));
                                        _ = PunchPeerAsync(requesterInfo, ct);
                                    }
                                    await SendToSignalingAsync(MessageType.ConnectResponse, new ConnectResponsePayload
                                    {
                                        Accepted = true,
                                        PeerId = requesterId
                                    }, ct);
                                }
                                break;
                            case MessageType.ConnectResponse:
                                if (msg.Payload != null)
                                {
                                    var resp = MessageSerializer.Deserialize<ConnectResponsePayload>(msg.Payload);
                                    if (resp?.Accepted == true && resp.RequesterInfo != null)
                                    {
                                        TryAddPeer(resp.RequesterInfo);
                                        Log($"[이벤트] P2P 피어 연결됨: {resp.RequesterInfo.Name} ({resp.RequesterInfo.Id})");
                                        _ = PunchPeerAsync(resp.RequesterInfo, ct);
                                    }
                                }
                                break;
                            case MessageType.HolePunch:
                                if (msg.Payload != null && _udpClient != null)
                                {
                                    var ip = msg.Payload.Value<string>("publicIp");
                                    var port = msg.Payload.Value<int?>("publicPort") ?? 0;
                                    var fromId = msg.Payload.Value<string>("fromId") ?? msg.Payload.Value<string>("peerId");
                                    if (!string.IsNullOrEmpty(ip) && port > 0)
                                    {
                                        var punch = Encoding.UTF8.GetBytes("HOLEPUNCH");
                                        await _udpClient.SendAsync(punch, punch.Length, ip, port);
                                    }
                                    if (!string.IsNullOrEmpty(fromId) && _peers.TryGetValue(fromId, out var punched))
                                        punched.Punched = true;
                                }
                                break;
                            case MessageType.PeerList:
                                ApplyPeerList(msg);
                                _peerListTcs?.TrySetResult(msg);
                                break;
                            case MessageType.RoomBroadcast:
                            case MessageType.Chat:
                                if (!_acceptRoomBroadcast) break;
                                if (msg.Payload != null)
                                {
                                    var toId = msg.Payload.Value<string>("toId") ?? "";
                                    if (!string.IsNullOrEmpty(toId) && toId != _myId) break;
                                    var fromId = msg.Payload.Value<string>("fromId") ?? "";
                                    var text = msg.Payload.Value<string>("message") ?? "";
                                    RaiseChat(fromId, text, "relay", !string.IsNullOrEmpty(toId));
                                }
                                break;
                            case MessageType.Disconnect:
                                {
                                    var peerId = msg.Payload?.Value<string>("peerId")
                                        ?? msg.Payload?.Value<string>("fromId")
                                        ?? "";
                                    if (!string.IsNullOrEmpty(peerId))
                                    {
                                        _peers.TryRemove(peerId, out _);
                                        Main(() => OnPeerDisconnected?.Invoke(peerId));
                                        Log($"[이벤트] 피어 퇴장: {peerId}");
                                    }
                                }
                                break;
                            case MessageType.SignalingPong:
                            case MessageType.Pong:
                                if (msg.Payload != null)
                                {
                                    var clientTicks = msg.Payload.Value<long>("clientTicks");
                                    var elapsed = (int)((DateTime.UtcNow.Ticks - clientTicks) / 10000);
                                    if (elapsed >= 0 && elapsed <= 60000)
                                    {
                                        LastSignalingPingMs = elapsed;
                                        Main(() => OnSignalingPingUpdated?.Invoke(elapsed));
                                    }
                                }
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"[오류] 시그널링 수신: {ex.Message}");
                    Main(() => OnError?.Invoke("시그널링 수신: " + ex.Message));
                }
            }
        }

        async Task UdpReceiveLoopAsync(CancellationToken ct)
        {
            if (_udpClient == null) return;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    var raw = Encoding.UTF8.GetString(result.Buffer);
                    if (raw.StartsWith("HOLEPUNCH", StringComparison.Ordinal))
                    {
                        Pkt("rx", "udp", $"HOLEPUNCH from {result.RemoteEndPoint} punched=true");
                        MarkPunched(result.RemoteEndPoint);
                        continue;
                    }
                    var plain = _crypto.DecodeFromReceive(raw);
                    var msg = MessageSerializer.ParseNetworkMessage(plain);
                    if (msg?.Payload == null) continue;
                    var fromId = msg.Payload.Value<string>("fromId") ?? msg.Payload.Value<string>("peerId") ?? "";
                    switch (msg.Type)
                    {
                        case MessageType.Ping:
                            MarkPunched(result.RemoteEndPoint, fromId);
                            if (_udpClient != null)
                            {
                                var pong = _crypto.EncodeForSend(new NetworkMessage
                                {
                                    Type = MessageType.Pong,
                                    Payload = MessageSerializer.ToToken(new
                                    {
                                        fromId = _myId,
                                        clientTicks = msg.Payload.Value<long?>("clientTicks") ?? 0
                                    })
                                }.Serialize());
                                var bytes = Encoding.UTF8.GetBytes(pong);
                                await _udpClient.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                            }
                            break;
                        case MessageType.Pong:
                            MarkPunched(result.RemoteEndPoint, fromId);
                            break;
                        case MessageType.Chat:
                            var toId = msg.Payload.Value<string>("toId") ?? "";
                            if (!string.IsNullOrEmpty(toId) && toId != _myId) break;
                            Pkt("rx", "udp", $"Chat from {result.RemoteEndPoint} {fromId} {Clip(msg.Payload?.ToString())}");
                            if (string.IsNullOrEmpty(fromId)) break;
                            if (!_peers.ContainsKey(fromId))
                                TryAddPeer(new PeerInfo
                                {
                                    Id = fromId,
                                    PublicIp = result.RemoteEndPoint.Address.ToString(),
                                    PublicPort = result.RemoteEndPoint.Port
                                });
                            MarkPunched(result.RemoteEndPoint, fromId);
                            RaiseChat(fromId, msg.Payload.Value<string>("message") ?? "", "udp", !string.IsNullOrEmpty(toId));
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { /* NAT ICMP 등 무시 */ }
                catch (Exception ex)
                {
                    Log($"[오류] UDP 수신: {ex.Message}");
                    Main(() => OnError?.Invoke("UDP 수신: " + ex.Message));
                }
            }
        }

        void RaiseChat(string fromId, string text, string via, bool whisper)
        {
            Main(() =>
            {
                OnChatReceived?.Invoke(fromId, text);
                OnChatMessage?.Invoke(fromId, text, via, whisper);
            });
        }

        void ApplyPeerList(NetworkMessage msg)
        {
            if (msg?.Payload == null) return;
            var peers = msg.Payload["peers"]?.ToObject<List<PeerInfo>>() ?? new List<PeerInfo>();
            _lastPeerList = peers.Where(p => !string.IsNullOrEmpty(p.Id) && p.Id != _myId).ToList();
            var roomMasterId = msg.Payload.Value<string>("roomMasterId") ?? "";
            if (!string.IsNullOrEmpty(roomMasterId))
                _isMaster = roomMasterId == _myId;
            else if (_lastPeerList.Any(p => p.IsMaster))
                _isMaster = false;
            var yourPort = msg.Payload.Value<int?>("yourPublicPort") ?? 0;
            if (yourPort > 0)
            {
                _myPublicIp = msg.Payload.Value<string>("yourPublicIp") ?? _myPublicIp;
                _myPublicPort = yourPort;
            }
            foreach (var peer in _lastPeerList)
            {
                var known = _peers.ContainsKey(peer.Id);
                TryAddPeer(peer);
                if (!known)
                    _ = ConnectToPeerAsync(peer.Id);
            }
            var snapshot = _lastPeerList.ToList();
            Main(() => OnPeerListUpdated?.Invoke(snapshot));
        }

        void MarkPunched(IPEndPoint ep, string fromId = null)
        {
            if (ep == null) return;
            if (!string.IsNullOrEmpty(fromId) && _peers.TryGetValue(fromId, out var known))
            {
                known.Observed = ep;
                known.Punched = true;
                return;
            }
            foreach (var p in _peers.Values)
            {
                if (p.Observed?.Port == ep.Port || (p.Observed == null && p.Info != null && p.Info.PublicPort == ep.Port))
                {
                    p.Observed ??= ep;
                    p.Punched = true;
                }
            }
        }

        void TryAddPeer(PeerInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.Id) || info.Id == _myId) return;
            if (_peers.ContainsKey(info.Id))
            {
                _peers[info.Id].Info = info;
                return;
            }
            _acceptRoomBroadcast = true;
            var conn = new PeerConnection
            {
                Info = info,
                Udp = _udpClient,
                ConnectedAt = DateTime.UtcNow
            };
            _peers[info.Id] = conn;
            Main(() => OnPeerConnected?.Invoke(info.Id, info));
        }

        static void Main(Action action) => UnityMainThread.Run(action);

        void Log(string message)
        {
            Main(() => OnLogMessage?.Invoke(message));
        }

        void Pkt(string dir, string proto, string detail)
        {
            Log($"[pkt {dir} {proto}] {detail}");
        }

        static string Clip(string s, int max = 220)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        static string GetFallbackLocalIp()
        {
            try
            {
                var entry = Dns.GetHostEntry(Dns.GetHostName());
                var ip = entry.AddressList.FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                return ip?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }
    }
}
