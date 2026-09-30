using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using X0.MultiPlay.Protocol;

namespace X0.MultiPlay
{
    // 화면 UI. config/appsettings.json 읽어서 Step 1~4 진행
    public class ClientHud : MonoBehaviour
    {
        [Header("설정")]
        [FormerlySerializedAs("loadStreamingAssets")]
        public bool loadConfigFile = true;
        public string dedicatedAddress = "127.0.0.1:19060";
        public string signalingHost = "127.0.0.1";
        public int signalingTcpPort = 19050;
        public int signalingUdpPort = 19051;
        public string encryptionKey = "";
        public string userId = "boy1";
        public string password = "1111";
        public long shopId;
        ClientSettings.LoginSettings _loginA = new() { UserId = "boy1", Password = "1111" };
        ClientSettings.LoginSettings _loginB = new() { UserId = "boy2", Password = "1111" };

        [Header("UI (비어 있으면 Play 시 자동 생성)")]
        public InputField dedicatedInput;
        public Button connectButton;
        public Button disconnectButton;
        public Text step1Status;
        public Text dedicatedPing;
        public Text signalingPing;
        public InputField userIdInput;
        public InputField passwordInput;
        public Button loginButton;
        public Button logoutButton;
        public Button accountAButton;
        public Button accountBButton;
        public Text step2Status;
        public RectTransform roomContent;
        public Button refreshRoomsButton;
        public InputField roomNameInput;
        public Button createRoomButton;
        public InputField joinRoomIdInput;
        public Button joinRoomButton;
        public Text step3Status;
        public Text commStatus;
        public Button connectP2PButton;
        public Button disconnectP2PButton;
        public Button refreshPeersButton;
        public RectTransform peerContent;
        public Dropdown recipientDropdown;
        public Button allRecipientButton;
        public RectTransform chatContent;
        public InputField chatInput;
        public Text chatPlaceholder;
        public Button sendChatButton;
        public RectTransform syncArea;
        public RectTransform localBox;
        public SyncBoxDrag syncDrag;
        public Toggle syncAutoToggle;
        public Image noticeBg;
        public Text noticeText;
        public Text infoText;
        public Text logText;
        public Button clearLogButton;

        static readonly Color RowBg = new(0.16f, 0.17f, 0.20f, 1f);
        static readonly Color RowSelf = new(0.26f, 0.27f, 0.30f, 1f);
        static readonly Color RowWhisper = new(0.36f, 0.26f, 0.52f, 1f);
        static readonly Color RowSelected = new(0.28f, 0.46f, 0.76f, 0.9f);
        static readonly Color TextNormal = Color.white;
        static readonly Color TextRelay = new(1f, 0.78f, 0.35f, 1f);
        static readonly Color TextSystem = new(0.65f, 0.67f, 0.72f, 1f);
        static readonly Color NoticeOk = new(0.13f, 0.36f, 0.22f, 1f);
        static readonly Color NoticeFail = new(0.50f, 0.16f, 0.16f, 1f);
        static readonly Color InputNormal = new(0.10f, 0.11f, 0.14f, 1f);

        MultiplaySession _session;
        readonly List<string> _log = new();
        readonly object _logLock = new();
        readonly List<string> _rooms = new();
        readonly Dictionary<string, RectTransform> _remoteBoxes = new();
        readonly List<MultiplaySession.ChatEntry> _chat = new();
        readonly List<string> _recipientIds = new();
        string _recipientId = "";
        string _peerSignature = "";
        int _selectedRoom = -1;
        bool _busy;
        float _nextPing;
        float _nextAutoSync;
        float _nextPeerRefresh;
        float _nextPeerRedraw;

        sealed class Row
        {
            public string Text;
            public Color Bg = RowBg;
            public Color Fg = TextNormal;
            public float Height = 20;
            public bool Bold;
            public Action Click;
        }

        void Awake()
        {
            if (loadConfigFile)
                ApplySettings(ClientSettings.LoadOrDefault());
            var canvas = GetComponentInChildren<Canvas>();
            if (canvas != null && recipientDropdown == null)
            {
                // 예전 레이아웃으로 저장된 씬이면 새로 만듦
                DestroyImmediate(canvas.gameObject);
                canvas = null;
            }
            if (canvas == null)
                ClientUiLayout.Build(this);
            BindFieldsFromInputs();
            _session = new MultiplaySession
            {
                SignalingHost = signalingHost,
                SignalingTcpPort = signalingTcpPort,
                SignalingUdpPort = signalingUdpPort,
                EncryptionKey = encryptionKey,
                ShopId = shopId
            };
            _session.Log += AppendLog;
            _session.Changed += RefreshStatus;
            _session.RoomListUpdated += list =>
            {
                _rooms.Clear();
                foreach (var r in list)
                    _rooms.Add($"{r.Name} ({r.RoomId})");
                RebuildRoomList();
            };
            _session.PeersChanged += RedrawPeers;
            _session.ChatLine += OnChatLine;
            _session.Notice += ShowNotice;
            _session.RemoteObjMoved += OnRemoteObj;
            WireButtons();
            if (syncDrag != null)
                syncDrag.Released = (x, y) => _session.SendObj(x, y);
            AppendLog("통합 클라이언트 시작. Step 1 → 2 → 3 → 4. F1=계정 A, F2=계정 B.");
            RedrawPeers();
            RefreshStatus();
        }

        void OnDestroy() => _session?.Disconnect();

        void Update()
        {
            if (_session == null) return;
            if (Input.GetKeyDown(KeyCode.F1)) FillLogin(_loginA, "A");
            if (Input.GetKeyDown(KeyCode.F2)) FillLogin(_loginB, "B");
            if (Input.GetKeyDown(KeyCode.Escape) && !string.IsNullOrEmpty(_recipientId))
                SetRecipient("");
            if (Time.unscaledTime >= _nextPing)
            {
                _nextPing = Time.unscaledTime + 10f;
                _ = _session.TickPingAsync();
            }
            if (Time.unscaledTime >= _nextPeerRefresh)
            {
                _nextPeerRefresh = Time.unscaledTime + 3f;
                if (_session.P2P is { IsConnected: true } && !_session.P2pBusy)
                    _ = _session.RefreshPeersAsync(true);
            }
            if (Time.unscaledTime >= _nextPeerRedraw)
            {
                _nextPeerRedraw = Time.unscaledTime + 1f;
                RedrawPeers();
                RefreshStatus();
            }
            if (syncAutoToggle != null && syncAutoToggle.isOn && Time.unscaledTime >= _nextAutoSync && localBox != null)
            {
                _nextAutoSync = Time.unscaledTime + 1.5f;
                _session.SendObj(Mathf.RoundToInt(localBox.anchoredPosition.x), Mathf.RoundToInt(-localBox.anchoredPosition.y));
            }
        }

        void ApplySettings(ClientSettings s)
        {
            if (s == null) return;
            dedicatedAddress = s.DedicatedAddress;
            signalingHost = s.SignalingServer.Host;
            signalingTcpPort = s.SignalingServer.TcpPort;
            signalingUdpPort = s.SignalingServer.UdpPort;
            encryptionKey = s.Encryption.Key ?? "";
            if (!string.IsNullOrEmpty(s.Login.UserId)) userId = s.Login.UserId;
            if (!string.IsNullOrEmpty(s.Login.Password)) password = s.Login.Password;
            shopId = s.Login.ShopId;
            if (s.Login != null) _loginA = s.Login;
            if (s.LoginB != null && !string.IsNullOrEmpty(s.LoginB.UserId)) _loginB = s.LoginB;
        }

        void FillLogin(ClientSettings.LoginSettings login, string slot)
        {
            if (login == null) return;
            userId = login.UserId ?? "";
            password = login.Password ?? "";
            shopId = login.ShopId;
            if (userIdInput != null) userIdInput.text = userId;
            if (passwordInput != null) passwordInput.text = password;
            AppendLog($"[Step 2] 계정 {slot} 채움: {userId}");
        }

        void BindFieldsFromInputs()
        {
            if (dedicatedInput != null) dedicatedInput.text = dedicatedAddress;
            if (userIdInput != null) userIdInput.text = userId;
            if (passwordInput != null) passwordInput.text = password;
            if (roomNameInput != null && string.IsNullOrEmpty(roomNameInput.text)) roomNameInput.text = "새 룸";
        }

        void WireButtons()
        {
            connectButton?.onClick.AddListener(() => _ = ConnectAsync());
            disconnectButton?.onClick.AddListener(() =>
            {
                _session.Disconnect();
                ShowNotice(true, "Dedicated / P2P 연결을 모두 끊었습니다.");
            });
            loginButton?.onClick.AddListener(() => _ = LoginAsync());
            accountAButton?.onClick.AddListener(() => FillLogin(_loginA, "A"));
            accountBButton?.onClick.AddListener(() => FillLogin(_loginB, "B"));
            logoutButton?.onClick.AddListener(() =>
            {
                _session.Disconnect();
                ShowNotice(true, "로그아웃했습니다.");
            });
            refreshRoomsButton?.onClick.AddListener(() => _session.RequestRoomList());
            createRoomButton?.onClick.AddListener(() =>
            {
                var name = roomNameInput != null ? roomNameInput.text.Trim() : "";
                if (string.IsNullOrEmpty(name)) ShowNotice(false, "방 이름을 입력하세요.");
                else _session.CreateRoom(name);
            });
            joinRoomButton?.onClick.AddListener(() =>
            {
                var id = SelectedRoomId();
                if (string.IsNullOrEmpty(id)) ShowNotice(false, "방을 선택하거나 방 ID를 입력하세요.");
                else _session.JoinRoom(id);
            });
            connectP2PButton?.onClick.AddListener(() => _ = _session.ConnectP2PAsync());
            disconnectP2PButton?.onClick.AddListener(() => _session.DisconnectP2P());
            refreshPeersButton?.onClick.AddListener(() => _ = _session.RefreshPeersAsync(false));
            allRecipientButton?.onClick.AddListener(() => SetRecipient(""));
            recipientDropdown?.onValueChanged.AddListener(i =>
                SetRecipient(i > 0 && i - 1 < _recipientIds.Count ? _recipientIds[i - 1] : ""));
            sendChatButton?.onClick.AddListener(SendChat);
            chatInput?.onEndEdit.AddListener(_ =>
            {
                if (!Input.GetKeyDown(KeyCode.Return) && !Input.GetKeyDown(KeyCode.KeypadEnter)) return;
                SendChat();
                chatInput.ActivateInputField();
            });
            clearLogButton?.onClick.AddListener(() =>
            {
                lock (_logLock) _log.Clear();
                if (logText != null) logText.text = "";
            });
        }

        void SendChat()
        {
            var text = chatInput != null ? chatInput.text.Trim() : "";
            if (string.IsNullOrEmpty(text)) return;
            _session.SendChat(text, _recipientId);
            if (chatInput != null) chatInput.text = "";
        }

        async System.Threading.Tasks.Task ConnectAsync()
        {
            dedicatedAddress = dedicatedInput != null ? dedicatedInput.text.Trim() : dedicatedAddress;
            var parts = dedicatedAddress.Split(':');
            var host = parts.Length > 0 ? parts[0].Trim() : "127.0.0.1";
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 19060;
            _busy = true;
            SetText(step1Status, "상태: 연결 중...");
            AppendLog("[Step 1] Dedicated 서버 연결 시도: " + host + ":" + port);
            try { await _session.ConnectDedicatedAsync(host, port); }
            finally
            {
                _busy = false;
                UnityMainThread.Run(RefreshStatus);
            }
        }

        async System.Threading.Tasks.Task LoginAsync()
        {
            userId = userIdInput != null ? userIdInput.text.Trim() : userId;
            password = passwordInput != null ? passwordInput.text : password;
            if (string.IsNullOrWhiteSpace(userId))
            {
                ShowNotice(false, "아이디를 입력하세요.");
                return;
            }
            _busy = true;
            SetText(step2Status, "상태: 로그인 중...");
            AppendLog("[Step 2] 로그인 시도: " + userId);
            try { await _session.LoginAsync(userId, password); }
            finally
            {
                _busy = false;
                UnityMainThread.Run(RefreshStatus);
            }
        }

        void RefreshStatus()
        {
            var connected = _session.Dedicated != null;
            var loggedIn = _session.Dedicated?.IsLoggedIn == true;
            var p2p = _session.P2P;
            var hasP2P = p2p is { IsConnected: true };
            if (connectButton != null) connectButton.interactable = !_busy && !connected;
            if (disconnectButton != null) disconnectButton.interactable = !_busy && connected;
            if (loginButton != null) loginButton.interactable = !_busy && connected && !loggedIn;
            if (logoutButton != null) logoutButton.interactable = !_busy && connected;
            if (refreshRoomsButton != null) refreshRoomsButton.interactable = loggedIn;
            if (createRoomButton != null) createRoomButton.interactable = loggedIn;
            if (joinRoomButton != null) joinRoomButton.interactable = loggedIn;
            if (connectP2PButton != null) connectP2PButton.interactable = !hasP2P && !_session.P2pBusy && _session.CurrentRoom != null;
            if (disconnectP2PButton != null) disconnectP2PButton.interactable = p2p != null;
            if (refreshPeersButton != null) refreshPeersButton.interactable = hasP2P;
            if (sendChatButton != null) sendChatButton.interactable = hasP2P;

            if (!connected)
            {
                SetText(step1Status, "상태: 대기");
                SetText(step2Status, "상태: Step 1 완료 후 가능");
                SetText(step3Status, "상태: Step 2 완료 후");
                foreach (var kv in _remoteBoxes)
                    if (kv.Value != null) Destroy(kv.Value.gameObject);
                _remoteBoxes.Clear();
            }
            else
            {
                SetText(step1Status, "상태: 연결됨 " + dedicatedAddress);
                SetText(step2Status, loggedIn
                    ? "상태: 로그인: " + _session.LoggedUserId + " (" + (_session.Dedicated.UserInfo?.DisplayName ?? userId) + ")"
                    : "상태: 로그인 가능");
                SetText(step3Status, _session.CurrentRoom != null
                    ? "상태: 방 입장: " + _session.CurrentRoom.Name
                    : loggedIn ? "상태: 방 만들기 / 참가하면 P2P 자동 접속" : "상태: Step 2 완료 후");
            }

            if (_session.P2pBusy)
                SetText(commStatus, "P2P: 접속 중...");
            else if (!hasP2P)
                SetText(commStatus, _session.CurrentRoom != null
                    ? "P2P: 미접속  ·  [P2P 접속]을 누르세요"
                    : "P2P: 미접속  ·  방에 들어가면 자동 접속");
            else
            {
                var peers = _session.Peers;
                var udp = peers.Count(x => p2p.HasDirectUdpTo(x.Id));
                SetText(commStatus,
                    $"나 = {(p2p.IsMaster ? MultiplaySession.RoleMaster : MultiplaySession.RoleMember)}  ·  룸{p2p.MyRoomNumber}  ·  피어 {peers.Count}명 (UDP 직접 {udp} · 릴레이 {peers.Count - udp})\n" +
                    $"외부 {p2p.MyPublicAddress}   내부 {p2p.MyLocalAddress}");
            }

            SetText(dedicatedPing, PingLabel("게임서버", _session.Dedicated?.LastPingMs));
            SetText(signalingPing, PingLabel("시그널링", _session.P2P?.LastSignalingPingMs));
            if (infoText != null)
                infoText.text = _session.BuildInfoText(_session.Dedicated?.LastPingMs, _session.P2P?.LastSignalingPingMs);
        }

        void RedrawPeers()
        {
            if (_session == null) return;
            var p2p = _session.P2P;
            var peers = _session.Peers.ToList();
            RebuildRecipients(peers);

            var rows = new List<Row>();
            if (p2p is { IsConnected: true })
            {
                rows.Add(new Row
                {
                    Text = $"(나) {p2p.MyName}  [{Role(p2p.IsMaster)}]  계정 {Dash(_session.LoggedUserId)}\n" +
                           $"외부 {p2p.MyPublicAddress}   내부 {p2p.MyLocalAddress}",
                    Bg = string.IsNullOrEmpty(_recipientId) ? RowSelected : RowSelf,
                    Height = 36,
                    Bold = true,
                    Click = () => SetRecipient("")
                });
                foreach (var p in peers)
                {
                    var id = p.Id;
                    var udp = p2p.HasDirectUdpTo(id);
                    rows.Add(new Row
                    {
                        Text = $"{p.Name}  [{Role(p.IsMaster)}]  계정 {Dash(p.UserId)}  ·  {(udp ? "UDP 직접" : "릴레이")}\n" +
                               $"외부 {MultiplaySession.Addr(p.PublicIp, p.PublicPort)}   내부 {MultiplaySession.Addr(p.LocalIp, p.LocalPort)}   Dedicated {_session.PeerDedicated(p)}",
                        Bg = id == _recipientId ? RowWhisper : RowBg,
                        Fg = udp ? TextNormal : TextRelay,
                        Height = 36,
                        Click = () => SetRecipient(id)
                    });
                }
            }
            else
            {
                rows.Add(new Row { Text = "P2P 미접속", Fg = TextSystem });
            }

            var sig = string.Join("|", rows.Select(r => r.Text + r.Bg));
            if (sig == _peerSignature) return;
            _peerSignature = sig;
            BuildRows(peerContent, rows, false);
        }

        void RebuildRecipients(List<PeerInfo> peers)
        {
            if (recipientDropdown == null) return;
            var ids = peers.Select(p => p.Id).ToList();
            var labels = new List<string> { "전체 (룸 전원)" };
            labels.AddRange(peers.Select(p => $"{p.Name} ({Role(p.IsMaster)})"));
            if (!string.IsNullOrEmpty(_recipientId) && !ids.Contains(_recipientId))
            {
                _recipientId = "";
                if (_session.P2P != null)
                    ShowNotice(false, "귓속말 상대가 룸에서 나가 받는 사람이 [전체]로 바뀌었습니다.");
            }
            var same = ids.SequenceEqual(_recipientIds)
                       && recipientDropdown.options.Select(o => o.text).SequenceEqual(labels);
            if (!same)
            {
                _recipientIds.Clear();
                _recipientIds.AddRange(ids);
                recipientDropdown.ClearOptions();
                recipientDropdown.AddOptions(labels);
            }
            recipientDropdown.SetValueWithoutNotify(string.IsNullOrEmpty(_recipientId) ? 0 : _recipientIds.IndexOf(_recipientId) + 1);
            recipientDropdown.RefreshShownValue();
            ApplyRecipientVisual();
        }

        void SetRecipient(string peerId)
        {
            _recipientId = peerId ?? "";
            if (recipientDropdown != null)
            {
                recipientDropdown.SetValueWithoutNotify(string.IsNullOrEmpty(_recipientId) ? 0 : _recipientIds.IndexOf(_recipientId) + 1);
                recipientDropdown.RefreshShownValue();
            }
            ApplyRecipientVisual();
            _peerSignature = "";
            RedrawPeers();
            if (chatInput != null)
                chatInput.ActivateInputField();
        }

        void ApplyRecipientVisual()
        {
            var whisper = !string.IsNullOrEmpty(_recipientId);
            var name = whisper ? _session.PeerName(_recipientId) : "";
            if (recipientDropdown != null)
                recipientDropdown.GetComponent<Image>().color = whisper ? RowWhisper : InputNormal;
            if (chatPlaceholder != null)
                chatPlaceholder.text = whisper ? $"Enter → {name} 에게만 (귓속말)" : "메시지 입력 후 Enter → 전체에게";
        }

        void OnChatLine(MultiplaySession.ChatEntry e)
        {
            _chat.Add(e);
            if (_chat.Count > 200) _chat.RemoveRange(0, _chat.Count - 200);
            if (!e.System && !e.Outgoing)
                ShowNotice(true, $"{(e.Whisper ? "귓속말" : "전체")} 수신 ({Path(e.Via)})  {_session.PeerName(e.PeerId)}: {e.Text}");
            RedrawChat();
        }

        void RedrawChat()
        {
            var rows = new List<Row>();
            foreach (var e in _chat.Skip(Math.Max(0, _chat.Count - 120)))
            {
                var time = e.Time.ToString("HH:mm:ss");
                if (e.System)
                {
                    rows.Add(new Row { Text = $"{time}  {e.Text}", Fg = TextSystem });
                    continue;
                }
                var kind = e.Whisper ? "[귓속말]" : "[전체]";
                var who = e.Outgoing
                    ? $"나 → {(e.Whisper ? _session.PeerName(e.PeerId) : "전체")}"
                    : $"{_session.PeerName(e.PeerId)} → 나";
                var peerId = e.PeerId;
                rows.Add(new Row
                {
                    Text = $"{time}  {kind} {who} : {e.Text}   ({Path(e.Via)})",
                    Bg = e.Whisper ? new Color(0.24f, 0.19f, 0.34f, 1f) : RowBg,
                    Fg = e.Via == "udp" ? TextNormal : TextRelay,
                    Click = string.IsNullOrEmpty(peerId) ? null : () =>
                    {
                        if (_session.P2P?.FindPeer(peerId) == null)
                            ShowNotice(false, "그 상대는 지금 룸에 없습니다.");
                        else
                            SetRecipient(peerId);
                    }
                });
            }
            BuildRows(chatContent, rows, true);
        }

        void ShowNotice(bool ok, string text)
        {
            if (noticeText != null) noticeText.text = (ok ? "[완료] " : "[실패] ") + text;
            if (noticeBg != null) noticeBg.color = ok ? NoticeOk : NoticeFail;
            AppendLog((ok ? "[알림] " : "[알림 실패] ") + text);
        }

        void OnRemoteObj(string peerId, int x, int y)
        {
            if (syncAutoToggle == null || !syncAutoToggle.isOn || syncArea == null) return;
            if (!_remoteBoxes.TryGetValue(peerId, out var box) || box == null)
            {
                var go = new GameObject(peerId, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(syncArea, false);
                box = go.GetComponent<RectTransform>();
                box.anchorMin = box.anchorMax = new Vector2(0, 1);
                box.pivot = new Vector2(0, 1);
                box.sizeDelta = new Vector2(28, 28);
                go.GetComponent<Image>().color = new Color(1f, 0.45f, 0.1f, 1f);
                _remoteBoxes[peerId] = box;
            }
            var maxX = Mathf.Max(0, syncArea.rect.width - 28);
            var maxY = Mathf.Max(0, syncArea.rect.height - 28);
            box.anchoredPosition = new Vector2(Mathf.Clamp(x, 0, maxX), -Mathf.Clamp(y, 0, maxY));
        }

        string SelectedRoomId()
        {
            if (_selectedRoom >= 0 && _selectedRoom < _rooms.Count)
            {
                var sel = _rooms[_selectedRoom];
                var open = sel.LastIndexOf('(');
                if (open >= 0) return sel.Substring(open + 1).TrimEnd(')').Trim();
            }
            return joinRoomIdInput != null ? joinRoomIdInput.text.Trim() : "";
        }

        void RebuildRoomList()
        {
            var rows = _rooms.Select((r, i) => new Row
            {
                Text = r,
                Bg = i == _selectedRoom ? RowSelected : RowBg,
                Click = () =>
                {
                    _selectedRoom = i;
                    RebuildRoomList();
                }
            }).ToList();
            BuildRows(roomContent, rows, false);
        }

        void BuildRows(RectTransform content, List<Row> rows, bool scrollToEnd)
        {
            if (content == null) return;
            for (var i = content.childCount - 1; i >= 0; i--)
                Destroy(content.GetChild(i).gameObject);
            foreach (var row in rows)
            {
                var go = new GameObject("Row", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                go.transform.SetParent(content, false);
                go.GetComponent<Image>().color = row.Bg;
                go.GetComponent<LayoutElement>().preferredHeight = row.Height;
                if (row.Click != null)
                {
                    var click = row.Click;
                    go.AddComponent<Button>().onClick.AddListener(() => click());
                }
                var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                textGo.transform.SetParent(go.transform, false);
                var trt = textGo.GetComponent<RectTransform>();
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(6, 0);
                trt.offsetMax = new Vector2(-4, 0);
                var t = textGo.GetComponent<Text>();
                t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                         ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                t.fontSize = 12;
                t.color = row.Fg;
                t.alignment = TextAnchor.MiddleLeft;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Truncate;
                t.fontStyle = row.Bold ? FontStyle.Bold : FontStyle.Normal;
                t.text = row.Text;
                t.raycastTarget = false;
            }
            if (scrollToEnd)
            {
                var scroll = content.GetComponentInParent<ScrollRect>();
                if (scroll != null)
                {
                    Canvas.ForceUpdateCanvases();
                    scroll.verticalNormalizedPosition = 0f;
                }
            }
        }

        static string Role(bool master) => master ? MultiplaySession.RoleMaster : MultiplaySession.RoleMember;

        static string Dash(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        static string Path(string via) => via == "udp" ? "UDP 직접" : "릴레이";

        static void SetText(Text label, string value)
        {
            if (label != null) label.text = value;
        }

        static string PingLabel(string name, int? ms) =>
            ms.HasValue ? $"{name}: {ms}ms" : $"{name}: ???ms";

        void AppendLog(string text)
        {
            lock (_logLock)
            {
                _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
                if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
                if (logText == null) return;
                var sb = new StringBuilder();
                var start = Math.Max(0, _log.Count - 15);
                for (var i = start; i < _log.Count; i++)
                    sb.AppendLine(_log[i]);
                logText.text = sb.ToString();
            }
        }
    }
}
