using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using X0.MultiPlay;
using X0.MultiPlay.Client;
using X0.MultiPlay.Protocol;

namespace X0.MultiPlay.Gui;

sealed class MainForm : Form
{
    readonly GuiSettings _settings;
    readonly PacketCrypto _crypto;
    DedicatedClient _dedicated;
    P2pClient _p2p;

    readonly TextBox _dedHost;
    readonly TextBox _user;
    readonly TextBox _pass;
    readonly TextBox _roomName;
    readonly TextBox _p2pName;
    readonly NumericUpDown _p2pRoom;
    readonly ListBox _rooms;
    readonly ListView _peers;
    readonly Label _targetBar;
    readonly TextBox _chat;
    readonly ComboBox _to;
    readonly TextBox _log;
    readonly Label _chatStatus;
    const string RoleMaster = "마스터(방장)";
    const string RoleMember = "멤버";

    string _selectedPeerId = "";
    readonly NotifyBar _notify;
    readonly StepBoard _steps;
    readonly System.Windows.Forms.Timer _statusTimer;
    readonly System.Windows.Forms.Timer _refreshTimer;
    bool _refreshing;
    bool _applyingPeers;
    bool _p2pBusy;
    string _pendingRoom;
    string _loggedUser = "";
    readonly ListBox _chatLog;
    readonly Label _linkBar;
    string _joinedRoomId = "";

    public MainForm()
    {
        _settings = GuiSettings.Load();
        _crypto = new PacketCrypto(string.IsNullOrWhiteSpace(_settings.Encryption.Key) ? null : _settings.Encryption.Key);

        Text = "X0 MultiPlay";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(1220, 820);
        MinimumSize = new Size(960, 680);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Font = UiFont();
        PlaceOnPrimaryScreen();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 8
        };
        Controls.Add(split);

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8, 8, 4, 8)
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        split.Panel1.Controls.Add(left);

        _dedHost = Input($"{_settings.DedicatedServer.Host}:{_settings.DedicatedServer.Port}");
        _user = Input(_settings.Login.UserId);
        _pass = Input(_settings.Login.Password);
        _pass.UseSystemPasswordChar = true;
        left.Controls.Add(BuildDedicated(), 0, 0);

        _rooms = List();
        _roomName = Input("seq-room");
        _roomName.Width = 140;
        _roomName.Anchor = AnchorStyles.Left;
        _roomName.Dock = DockStyle.None;
        left.Controls.Add(BuildRooms(), 0, 1);

        _p2pName = Input($"{Environment.MachineName}-{Environment.ProcessId}");
        _p2pName.Width = 140;
        _p2pName.Anchor = AnchorStyles.Left;
        _p2pName.Dock = DockStyle.None;
        _p2pRoom = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 9999,
            Value = 1,
            Width = 80,
            Height = 28,
            Font = Font,
            Margin = new Padding(4, 4, 4, 4)
        };
        _peers = PeerView();
        _to = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
            Font = Font,
            Margin = new Padding(4, 6, 4, 4),
            Name = "recipient",
            AccessibleName = "받는 사람"
        };
        _to.Items.Add(AllRecipient);
        _to.SelectedIndex = 0;
        _to.SelectedIndexChanged += (_, _) =>
        {
            if (_applyingPeers) return;
            OnRecipientChanged();
        };
        _peers.ItemSelectionChanged += (_, e) =>
        {
            if (_applyingPeers || !e.IsSelected) return;
            SelectRecipient((e.Item.Tag as PeerInfo)?.Id);
        };
        _peers.ItemActivate += (_, _) =>
        {
            if (_peers.SelectedItems.Count == 0) return;
            SelectRecipient((_peers.SelectedItems[0].Tag as PeerInfo)?.Id);
            _chat.Focus();
        };
        _peers.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _peers.HitTest(e.Location).Item;
            if (hit?.Tag is not PeerInfo p) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add($"{p.Name} 에게 귓속말", null, (_, _) => { SelectRecipient(p.Id); _chat.Focus(); });
            menu.Items.Add("전체로 보내기", null, (_, _) => { SelectRecipient(null); _chat.Focus(); });
            menu.Show(_peers, e.Location);
        };
        _targetBar = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(235, 240, 248),
            Padding = new Padding(8, 0, 8, 0),
            Text = "P2P 접속 전  —  받는 사람 기본값은 [전체], 목록 클릭/더블클릭으로 귓속말 상대 지정"
        };
        _chat = Input("");
        _chat.PlaceholderText = "메시지 입력 후 Enter → 전체에게";
        _chat.Name = "chatInput";
        _chat.AccessibleName = "메시지 입력";
        _chat.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                SelectRecipient(null);
                return;
            }
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SendToRecipientAsync();
        };
        _linkBar = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(242, 244, 248),
            Padding = new Padding(8, 0, 8, 0),
            Text = "Dedicated: 끊김   |   시그널링: 미접속   |   P2P: -"
        };
        _chatLog = List();
        _chatLog.HorizontalScrollbar = true;
        _chatLog.DoubleClick += (_, _) =>
        {
            if (_chatLog.SelectedItem is not ChatLine { PeerId: { Length: > 0 } id }) return;
            if (_p2p?.FindPeer(id) == null)
            {
                Notify(StepKind.Warn, "그 상대는 지금 룸에 없습니다.");
                return;
            }
            SelectRecipient(id);
            _chat.Focus();
        };
        _chatStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(80, 88, 98),
            Text = "채팅: 대기"
        };
        left.Controls.Add(BuildP2p(), 0, 2);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4, 8, 8, 8)
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 268));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        split.Panel2.Controls.Add(right);

        _notify = new NotifyBar();
        var notifyBox = Group("알림");
        notifyBox.Controls.Add(_notify);
        right.Controls.Add(notifyBox, 0, 0);

        _steps = new StepBoard();
        var stepBox = Group("동작 확인 (녹색 = 정상)");
        stepBox.Controls.Add(_steps);
        right.Controls.Add(stepBox, 0, 1);

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 9f)
        };
        var logBox = Group("패킷 / 이벤트 로그");
        logBox.Controls.Add(_log);
        right.Controls.Add(logBox, 0, 2);

        Load += (_, _) =>
        {
            PlaceOnPrimaryScreen();
            WindowState = FormWindowState.Normal;
            Visible = true;
            ShowInTaskbar = true;
        };
        Shown += (_, _) =>
        {
            if (split.Width > 480)
            {
                split.Panel1MinSize = 200;
                split.Panel2MinSize = 240;
            }
            PlaceSplitter(split);
            Activate();
            BringToFront();
        };
        FormClosing += (_, _) =>
        {
            _refreshTimer.Stop();
            _statusTimer.Stop();
            DisconnectP2p();
            DisconnectDedicated();
        };

        _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _statusTimer.Tick += (_, _) => TickStatus();
        _statusTimer.Start();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += async (_, _) => await AutoRefreshAsync();
        _refreshTimer.Start();

        Log("설정: " + _settings.LoadedFrom + "  계정 A=" + _settings.Login.UserId + " / B=" + _settings.LoginB.UserId);
        Notify(StepKind.Idle, "서버를 켠 뒤 연결 → 로그인 → 룸 / P2P. 목록은 2초마다 자동 갱신됩니다.");
    }

    GroupBox BuildDedicated()
    {
        var box = Group("Dedicated (TCP 19060)");
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 3,
            Padding = new Padding(4)
        };
        for (var i = 0; i < 6; i++)
            grid.ColumnStyles.Add(new ColumnStyle(i is 1 or 2 ? SizeType.Percent : SizeType.AutoSize, i is 1 or 2 ? 50 : 0));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.4f));

        grid.Controls.Add(Lbl("주소"), 0, 0);
        grid.Controls.Add(_dedHost, 1, 0);
        grid.SetColumnSpan(_dedHost, 3);
        grid.Controls.Add(Btn("연결", async (_, _) => await Safe(ConnectDedicatedAsync, "Dedicated 연결")), 4, 0);
        grid.Controls.Add(Btn("끊기", (_, _) => DisconnectDedicatedByUser("Dedicated 끊기", "서버 연결을 끊었습니다.")), 5, 0);

        grid.Controls.Add(Lbl("아이디"), 0, 1);
        grid.Controls.Add(_user, 1, 1);
        grid.SetColumnSpan(_user, 3);
        grid.Controls.Add(Btn("계정 A", (_, _) => FillLogin(_settings.Login)), 4, 1);
        grid.Controls.Add(Btn("계정 B", (_, _) => FillLogin(_settings.LoginB)), 5, 1);

        grid.Controls.Add(Lbl("비밀번호"), 0, 2);
        grid.Controls.Add(_pass, 1, 2);
        grid.SetColumnSpan(_pass, 3);
        grid.Controls.Add(Btn("로그인", async (_, _) => await Safe(LoginAsync, "로그인")), 4, 2);
        grid.Controls.Add(Btn("로그아웃", (_, _) => DisconnectDedicatedByUser("로그아웃", "로그아웃했습니다. (서버 연결도 끊김)")), 5, 2);

        box.Controls.Add(grid);
        return box;
    }

    GroupBox BuildRooms()
    {
        var box = Group("룸");
        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(4)
        };
        lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        lay.Controls.Add(_rooms, 0, 0);
        var btns = Flow();
        btns.Controls.Add(Btn("목록", async (_, _) =>
        {
            if (_dedicated is not { IsLoggedIn: true })
            {
                Fail("룸 목록", "먼저 Dedicated 연결 후 로그인하세요.");
                return;
            }
            _pendingRoom = "룸 목록";
            await _dedicated.RequestRoomListAsync();
        }));
        btns.Controls.Add(Lbl("이름"));
        btns.Controls.Add(_roomName);
        btns.Controls.Add(Btn("만들기", async (_, _) =>
        {
            if (_dedicated is not { IsLoggedIn: true })
            {
                Fail("방 만들기", "먼저 Dedicated 연결 후 로그인하세요.");
                return;
            }
            var name = _roomName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Fail("방 만들기", "방 이름을 입력하세요.");
                return;
            }
            _pendingRoom = "방 만들기";
            await _dedicated.CreateRoomAsync(name);
        }));
        btns.Controls.Add(Btn("참가", async (_, _) =>
        {
            if (_dedicated is not { IsLoggedIn: true })
            {
                Fail("방 참가", "먼저 Dedicated 연결 후 로그인하세요.");
                return;
            }
            if (_rooms.SelectedItem == null)
            {
                Fail("방 참가", "목록에서 방을 선택하세요.");
                return;
            }
            var id = RoomIdFromItem(_rooms.SelectedItem.ToString());
            if (string.IsNullOrEmpty(id)) return;
            _pendingRoom = "방 참가";
            await _dedicated.JoinRoomAsync(id);
        }));
        lay.Controls.Add(btns, 0, 1);
        box.Controls.Add(lay);
        return box;
    }

    GroupBox BuildP2p()
    {
        var box = Group("시그널링 / 홀펀치 (실제 UDP 패킷)");
        var lay = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(4)
        };
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        lay.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        lay.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        var top = Flow();
        top.Controls.Add(Lbl("이름"));
        top.Controls.Add(_p2pName);
        top.Controls.Add(Lbl("룸#"));
        top.Controls.Add(_p2pRoom);
        top.Controls.Add(Btn("P2P 접속", async (_, _) => await Safe(ConnectP2pAsync, "P2P 접속")));
        top.Controls.Add(Btn("P2P 끊기", (_, _) => DisconnectP2pByUser()));
        top.Controls.Add(Btn("피어 목록", async (_, _) => await Safe(RefreshPeersByUserAsync, "피어 목록")));
        lay.Controls.Add(top, 0, 0);
        lay.Controls.Add(_linkBar, 0, 1);
        lay.Controls.Add(_targetBar, 0, 2);
        lay.Controls.Add(_peers, 0, 3);
        lay.Controls.Add(_chatLog, 0, 4);

        var chatRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        chatRow.Controls.Add(Lbl("받는 사람"), 0, 0);
        chatRow.Controls.Add(_to, 1, 0);
        chatRow.Controls.Add(_chat, 2, 0);
        chatRow.Controls.Add(Btn("보내기", async (_, _) => await SendToRecipientAsync()), 3, 0);
        lay.Controls.Add(chatRow, 0, 5);
        lay.Controls.Add(_chatStatus, 0, 6);

        box.Controls.Add(lay);
        return box;
    }

    void FillLogin(LoginSettings login)
    {
        if (login == null) return;
        _user.Text = login.UserId ?? "";
        _pass.Text = login.Password ?? "";
        Notify(StepKind.Idle, "계정 입력: " + _user.Text);
    }

    async System.Threading.Tasks.Task ConnectDedicatedAsync()
    {
        DisconnectDedicated();
        var parts = _dedHost.Text.Trim().Split(':');
        var host = parts.Length > 0 ? parts[0] : "127.0.0.1";
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 19060;
        _dedicated = new DedicatedClient(host, port, _crypto);
        _dedicated.OnPingUpdated += ms => Ui(() => Log($"[dedicated ping] {ms}ms"));
        _dedicated.OnLogMessage += msg => Ui(() => Log(msg));
        var mine = _dedicated;
        _dedicated.OnError += msg => Ui(() =>
        {
            var pending = _pendingRoom;
            _pendingRoom = null;
            if (pending != null)
            {
                _steps.Set("room", StepKind.Fail, msg);
                Fail(pending, msg);
            }
            else
                Fail("Dedicated", msg, popup: false);
        });
        _dedicated.OnLobbyListReceived += names => Ui(() => Log("[lobby] " + string.Join(", ", names)));
        _dedicated.OnRoomListReceived += rooms => Ui(() =>
        {
            if (!ReferenceEquals(_dedicated, mine)) return;
            ApplyRooms(rooms, true);
            if (_pendingRoom == "룸 목록")
            {
                _pendingRoom = null;
                Alert(true, "룸 목록", $"룸 {rooms?.Count ?? 0}개를 받았습니다.");
            }
        });
        _dedicated.OnRoomUpdate += room => Ui(() =>
        {
            if (!ReferenceEquals(_dedicated, mine)) return;
            if (room == null)
            {
                Fail("방", "RoomUpdate 없음", popup: false);
                return;
            }
            var roomChanged = _joinedRoomId != (room.RoomId ?? "");
            _joinedRoomId = room.RoomId ?? "";
            if (roomChanged)
                _ = RefreshPeersAsync(true);
            _steps.Set("room", StepKind.Ok, $"{room.Name} {room.RoomId} {room.PlayerCount}/{room.MaxPlayers}");
            Notify(StepKind.Ok, $"방 갱신  {room.Name} ({room.PlayerCount}/{room.MaxPlayers})");
            Log($"[room update] {room.Name} {room.RoomId}");
            if (_pendingRoom is "방 만들기" or "방 참가")
            {
                var what = _pendingRoom;
                _pendingRoom = null;
                Alert(true, what, $"{what} 성공\n\n방: {room.Name}\nID: {room.RoomId}\n인원: {room.PlayerCount}/{room.MaxPlayers}");
            }
            _ = mine.RequestRoomListAsync();
        });
        _dedicated.OnDisconnected += reason => Ui(() =>
        {
            if (!ReferenceEquals(_dedicated, mine)) return;
            Log("[dedicated disconnect] " + reason);
            _steps.Set("ded", StepKind.Fail, reason);
            _steps.ResetGroup("login", "room");
            Fail("Dedicated 끊김", $"서버와의 연결이 끊겼습니다. ({reason})");
        });
        if (!await _dedicated.ConnectAsync())
        {
            _steps.Set("ded", StepKind.Fail, "연결 실패");
            _dedicated = null;
            Fail("Dedicated 연결", $"{host}:{port} 연결 실패.\nserver-host 가 켜져 있는지 확인하세요.");
            return;
        }
        _steps.Set("ded", StepKind.Ok, $"{host}:{port}");
        Notify(StepKind.Ok, $"Dedicated TCP {host}:{port}");
        Log($"Dedicated TCP 연결 {host}:{port}");
        await _dedicated.SendPingAsync();
        Alert(true, "Dedicated 연결", $"{host}:{port} 연결 성공.\n이제 로그인하세요.");
    }

    async System.Threading.Tasks.Task LoginAsync()
    {
        if (_dedicated is not { IsConnected: true })
        {
            Fail("로그인", "먼저 Dedicated [연결] 버튼을 누르세요.");
            return;
        }
        var shop = string.Equals(_user.Text.Trim(), _settings.LoginB?.UserId, StringComparison.OrdinalIgnoreCase)
            ? (_settings.LoginB?.ShopId ?? 0)
            : _settings.Login.ShopId;
        var login = await _dedicated.LoginAsync(_user.Text.Trim(), _pass.Text, shop);
        if (login?.Success == true)
        {
            _steps.Set("login", StepKind.Ok, login.User?.DisplayName ?? _user.Text);
            Notify(StepKind.Ok, $"로그인 OK  {login.User?.DisplayName}  conn={_dedicated.ConnectionId}");
            _loggedUser = _user.Text.Trim();
            _ = RefreshPeersAsync(true);
            Log($"로그인 OK {login.User?.DisplayName} conn={_dedicated.ConnectionId}");
            await _dedicated.RequestRoomListAsync();
            Alert(true, "로그인", $"로그인 성공\n\n아이디: {_user.Text.Trim()}\n이름: {login.User?.DisplayName}\n접속 ID: {_dedicated.ConnectionId}");
        }
        else
        {
            var msg = string.IsNullOrWhiteSpace(login?.Message) ? "서버 응답 없음" : login.Message;
            _steps.Set("login", StepKind.Fail, msg);
            Fail("로그인", $"로그인 실패\n\n{msg}");
        }
    }

    void DisconnectDedicatedByUser(string title, string text)
    {
        if (_dedicated == null)
        {
            Fail(title, "연결되어 있지 않습니다.");
            return;
        }
        DisconnectDedicated();
        Notify(StepKind.Idle, text);
        Log($"[{title}] {text}");
        Alert(true, title, text);
    }

    void DisconnectDedicated()
    {
        _pendingRoom = null;
        _loggedUser = "";
        _joinedRoomId = "";
        _dedicated?.Disconnect();
        _dedicated = null;
        _rooms.Items.Clear();
        _steps.ResetGroup("ded", "login", "rooms", "room");
    }

    async System.Threading.Tasks.Task ConnectP2pAsync()
    {
        if (_p2pBusy)
        {
            Fail("P2P 접속", "접속 처리 중입니다. 잠시 후 다시 시도하세요.");
            return;
        }
        if (_p2p is { IsConnected: true })
        {
            Fail("P2P 접속", $"이미 접속 중입니다. (id={_p2p.MyId}, 룸 {_p2p.MyRoomNumber})\n다시 하려면 먼저 [P2P 끊기]를 누르세요.");
            return;
        }
        _p2pBusy = true;
        try
        {
            if (_p2p != null)
            {
                DisconnectP2p();
                await System.Threading.Tasks.Task.Delay(400);
            }
            var s = _settings.SignalingServer;
        _p2p = new P2pClient(s.Host, s.TcpPort, s.UdpPort, _crypto);
        _p2p.OnLogMessage += msg => Ui(() => Log(msg));
        _p2p.OnError += msg => Ui(() => Fail("P2P", msg, popup: false));
        _p2p.OnChatMessage += (from, msg, via, whisper) => Ui(() =>
        {
            var path = via == "udp" ? "UDP 직접" : "릴레이";
            var kind = whisper ? "귓속말" : "전체";
            var who = PeerName(from);
            AddChatLine($"[{kind}] {who} → 나 : {msg}   ({path})", from);
            _chatStatus.Text = $"수신  [{kind} · {path}]  {who}: {msg}";
            _steps.Set("chatRx", via == "udp" ? StepKind.Ok : StepKind.Warn, $"{kind} · {path}");
            if (via == "udp")
                _steps.Set("punch", StepKind.Ok, "UDP 채팅 수신");
            Notify(via == "udp" ? StepKind.Ok : StepKind.Warn, $"{kind} 메시지 수신 ({path})  {who}: {msg}");
            Log($"[chat {kind} {via} {from}] {msg}");
        });
        _p2p.OnChatSent += (udp, msg, to) => Ui(() =>
        {
            var path = udp ? "UDP 직접" : "릴레이";
            var all = to == "room";
            var target = all ? "전체" : PeerName(to);
            AddChatLine($"[{(all ? "전체" : "귓속말")}] 나 → {target} : {msg}   ({path})", all ? null : to);
            _chatStatus.Text = $"송신 완료  [{path}] → {target}  {msg}";
            _steps.Set("chatTx", udp ? StepKind.Ok : StepKind.Warn, $"{path} → {target}");
            Notify(udp ? StepKind.Ok : StepKind.Warn, $"{target} 에게 전송 완료 ({path})");
        });
        _p2p.OnPeerListUpdated += peers => Ui(() => ApplyPeers(peers, true));
        _p2p.OnPeerConnected += (id, info) => Ui(() =>
        {
            Log($"[peer] {info.Name} {id} {info.PublicIp}:{info.PublicPort}");
            Notify(StepKind.Ok, $"피어 연결  {info.Name}");
        });
        _p2p.OnConnectRequested += info => Ui(() =>
        {
            Log($"[connect request] {info.Name} {info.Id} → HolePunch 응답");
            _steps.Set("punch", StepKind.Ok, $"연결 요청 받음 ← {info.Name} · 홀펀치 응답");
            Notify(StepKind.Ok, $"{info.Name} 가 연결 요청 → 자동으로 홀펀치 응답했습니다.");
        });
        _p2p.OnPeerDisconnected += id => Ui(() =>
        {
            Log("[peer left] " + id);
            Notify(StepKind.Warn, "피어 퇴장 " + id);
        });
        var name = string.IsNullOrWhiteSpace(_p2pName.Text) ? "CsPlayer" : _p2pName.Text.Trim();
        var room = (int)_p2pRoom.Value;
        SyncLinks();
        if (!await _p2p.ConnectAsync(name, room))
        {
            _steps.Set("p2p", StepKind.Fail, "시그널링 실패");
            _p2p = null;
            Fail("P2P 접속", $"시그널링 서버({s.Host}:{s.TcpPort}) 연결 실패.\nserver-host 가 켜져 있는지 확인하세요.");
            return;
        }
        var role = _p2p.IsMaster ? RoleMaster : RoleMember;
        _steps.Set("p2p", StepKind.Ok, $"{role}  id={_p2p.MyId}  room={room}");
        Notify(StepKind.Ok, $"P2P 등록  id={_p2p.MyId}  public={_p2p.MyPublicAddress}");
        Log($"P2P 등록 id={_p2p.MyId} room={room} local={_p2p.MyLocalAddress} public={_p2p.MyPublicAddress}");
        await _p2p.SendSignalingPingAsync();
        await RefreshPeersAsync(true);
        var peerCount = _p2p?.LastPeerList.Count ?? 0;
        Alert(true, "P2P 접속", $"P2P 접속 성공\n\n내 역할: {role}\nID: {_p2p?.MyId}\n룸: {room}\n외부(공인): {_p2p?.MyPublicAddress}\n내부(LAN): {_p2p?.MyLocalAddress}\n같은 룸 피어: {peerCount}명" +
            (peerCount > 0 ? "\n\n피어와 자동으로 연결 + 홀펀치합니다." : "\n\n다른 창에서 같은 룸#으로 접속하면 자동 연결됩니다."));
        }
        finally
        {
            _p2pBusy = false;
        }
    }

    void DisconnectP2pByUser()
    {
        if (_p2p == null)
        {
            Fail("P2P 끊기", "P2P에 접속되어 있지 않습니다.");
            return;
        }
        var id = _p2p.MyId;
        DisconnectP2p();
        AddChatLine("--- P2P 연결 해제 ---");
        Notify(StepKind.Idle, "P2P 연결을 끊었습니다.");
        Log($"[P2P 끊기] id={id}");
        Alert(true, "P2P 끊기", $"P2P 연결을 끊었습니다. (id={id})");
    }

    async System.Threading.Tasks.Task RefreshPeersByUserAsync()
    {
        if (_p2p is not { IsConnected: true })
        {
            Fail("피어 목록", "먼저 [P2P 접속]을 누르세요.");
            return;
        }
        SyncLinks();
        var peers = await _p2p.GetPeerListAsync();
        ApplyPeers(peers, true);
        var lines = peers.Count == 0
            ? "같은 룸에 다른 피어가 없습니다."
            : string.Join("\n\n", peers.Select(p =>
                $"· {p.Name}  [{(p.IsMaster ? RoleMaster : RoleMember)}]  계정={(string.IsNullOrEmpty(p.UserId) ? "-" : p.UserId)}\n" +
                $"   외부 {Addr(p.PublicIp, p.PublicPort)}   내부 {Addr(p.LocalIp, p.LocalPort)}\n" +
                $"   P2P={PeerPath(p)}   Dedicated={PeerDedicated(p)}"));
        Alert(true, "피어 목록",
            $"나: {(_p2p.IsMaster ? RoleMaster : RoleMember)}   외부 {_p2p.MyPublicAddress}   내부 {_p2p.MyLocalAddress}\n피어 {peers.Count}명\n\n{lines}");
    }

    void DisconnectP2p()
    {
        _p2p?.Disconnect();
        _p2p = null;
        _selectedPeerId = "";
        _peers.Items.Clear();
        RebuildRecipients(null);
        UpdateTargetBar();
        _chatStatus.Text = "채팅: 대기";
        _steps.ResetGroup("p2p", "peers", "punch", "chatTx", "chatRx");
    }

    async System.Threading.Tasks.Task RefreshPeersAsync(bool silent)
    {
        if (_p2p is not { IsConnected: true }) return;
        SyncLinks();
        var peers = await _p2p.GetPeerListAsync();
        Ui(() => ApplyPeers(peers, silent));
    }

    void ApplyPeers(IReadOnlyList<PeerInfo> peers, bool silent)
    {
        if (IsDisposed || _peers.IsDisposed) return;
        _applyingPeers = true;
        try
        {
            RebuildRecipients(peers);
            var keep = _selectedPeerId;
            _peers.BeginUpdate();
            _peers.Items.Clear();
            ListViewItem pick = null;
            if (_p2p != null && !string.IsNullOrEmpty(_p2p.MyId))
            {
                _peers.Items.Add(new ListViewItem(new[]
                {
                    "(나) " + (_p2pName.Text ?? ""),
                    string.IsNullOrEmpty(_loggedUser) ? "-" : _loggedUser,
                    _p2p.MyPublicAddress,
                    _p2p.MyLocalAddress,
                    _p2p.IsMaster ? RoleMaster : RoleMember,
                    "-",
                    string.IsNullOrEmpty(_joinedRoomId) ? (string.IsNullOrEmpty(_loggedUser) ? "미로그인" : "로비") : _joinedRoomId
                })
                { BackColor = Color.FromArgb(236, 236, 236), Font = new Font(_peers.Font, FontStyle.Bold) });
            }
            if (peers != null)
            {
                foreach (var p in peers)
                {
                    var item = new ListViewItem(new[]
                    {
                        p.Name ?? "",
                        string.IsNullOrEmpty(p.UserId) ? "-" : p.UserId,
                        Addr(p.PublicIp, p.PublicPort),
                        Addr(p.LocalIp, p.LocalPort),
                        p.IsMaster ? RoleMaster : RoleMember,
                        PeerPath(p),
                        PeerDedicated(p)
                    })
                    { Tag = p };
                    if (_p2p?.HasDirectUdpTo(p.Id) != true)
                        item.ForeColor = Color.FromArgb(150, 100, 0);
                    _peers.Items.Add(item);
                    if (!string.IsNullOrEmpty(keep) && p.Id == keep)
                        pick = item;
                }
            }
            if (pick != null)
                pick.Selected = true;
            _peers.EndUpdate();
        }
        finally
        {
            _applyingPeers = false;
        }
        UpdateTargetBar();
        if (_p2p != null)
        {
            var role = _p2p.IsMaster ? RoleMaster : RoleMember;
            _steps.Set("p2p", StepKind.Ok, $"{role}  id={_p2p.MyId}  room={_p2p.MyRoomNumber}");
            if (!string.IsNullOrEmpty(_selectedPeerId))
            {
                _chatStatus.Text = _p2p.HasDirectUdpTo(_selectedPeerId)
                    ? "채팅 준비  ·  UDP 직접"
                    : "채팅 준비  ·  릴레이 (홀펀치 전이면 정상)";
            }
        }
        var n = peers?.Count ?? 0;
        _steps.Set("peers", n > 0 ? StepKind.Ok : StepKind.Warn, n > 0 ? $"{n}명 · 자동 연결됨" : "0명 (창 2개, 같은 룸#)");
        if (!silent)
        {
            Log($"[peer list] {n}명");
            Notify(n > 0 ? StepKind.Ok : StepKind.Warn, $"룸 피어 {n}명. Enter = 전체, 목록 더블클릭 = 귓속말 상대 지정.");
        }
    }

    sealed class Recipient
    {
        public PeerInfo Peer;
        public string Text;
        public override string ToString() => Text;
    }

    static readonly Recipient AllRecipient = new() { Text = "전체 (룸 전원)" };

    PeerInfo CurrentPeer() => (_to.SelectedItem as Recipient)?.Peer;

    void RebuildRecipients(IReadOnlyList<PeerInfo> peers)
    {
        var keep = _selectedPeerId;
        _to.BeginUpdate();
        _to.Items.Clear();
        _to.Items.Add(AllRecipient);
        var idx = 0;
        foreach (var p in peers ?? Array.Empty<PeerInfo>())
        {
            var acct = string.IsNullOrEmpty(p.UserId) ? "" : $" · {p.UserId}";
            _to.Items.Add(new Recipient { Peer = p, Text = $"{p.Name} ({(p.IsMaster ? RoleMaster : RoleMember)}{acct})" });
            if (!string.IsNullOrEmpty(keep) && p.Id == keep)
                idx = _to.Items.Count - 1;
        }
        _to.SelectedIndex = idx;
        _to.EndUpdate();
        if (!string.IsNullOrEmpty(keep) && idx == 0)
            Notify(StepKind.Warn, "귓속말 상대가 룸에서 나가 받는 사람이 [전체]로 바뀌었습니다.");
        _selectedPeerId = CurrentPeer()?.Id ?? "";
    }

    void SelectRecipient(string peerId)
    {
        for (var i = 0; i < _to.Items.Count; i++)
        {
            if (((Recipient)_to.Items[i]).Peer?.Id == (string.IsNullOrEmpty(peerId) ? null : peerId))
            {
                _to.SelectedIndex = i;
                return;
            }
        }
        _to.SelectedIndex = 0;
    }

    void OnRecipientChanged()
    {
        _selectedPeerId = CurrentPeer()?.Id ?? "";
        _applyingPeers = true;
        try
        {
            foreach (ListViewItem it in _peers.Items)
                it.Selected = it.Tag is PeerInfo x && x.Id == _selectedPeerId;
        }
        finally
        {
            _applyingPeers = false;
        }
        UpdateTargetBar();
    }

    void UpdateTargetBar()
    {
        if (IsDisposed || _targetBar.IsDisposed || _applyingPeers) return;
        var p = CurrentPeer();
        var me = _p2p == null ? "미접속" : (_p2p.IsMaster ? RoleMaster : RoleMember);
        if (p == null || string.IsNullOrEmpty(p.Id))
        {
            _targetBar.BackColor = Color.FromArgb(235, 240, 248);
            _targetBar.Text = $"나={me}  ·  받는 사람: 전체 (룸 전원)  ·  귓속말은 목록 더블클릭 또는 [받는 사람]에서 선택";
            _to.BackColor = SystemColors.Window;
            _chat.PlaceholderText = "메시지 입력 후 Enter → 전체에게";
            return;
        }
        var udp = _p2p?.HasDirectUdpTo(p.Id) == true ? "UDP 직접" : "릴레이";
        _targetBar.BackColor = Color.FromArgb(236, 226, 250);
        _targetBar.Text = $"나={me}  ·  귓속말 → {p.Name}  ({Addr(p.PublicIp, p.PublicPort)})  ·  전달={udp}  ·  Esc = 전체로";
        _to.BackColor = Color.FromArgb(236, 226, 250);
        _chat.PlaceholderText = $"메시지 입력 후 Enter → {p.Name} 에게만 (귓속말)";
    }

    void ApplyRooms(List<RoomInfo> rooms, bool silent)
    {
        var keep = RoomIdFromItem(_rooms.SelectedItem?.ToString());
        _rooms.BeginUpdate();
        _rooms.Items.Clear();
        if (rooms != null)
        {
            foreach (var r in rooms)
                _rooms.Items.Add($"{r.Name}  {r.RoomId}  {r.PlayerCount}/{r.MaxPlayers}");
        }
        if (!string.IsNullOrEmpty(keep))
        {
            for (var i = 0; i < _rooms.Items.Count; i++)
            {
                if (RoomIdFromItem(_rooms.Items[i].ToString()) == keep)
                {
                    _rooms.SelectedIndex = i;
                    break;
                }
            }
        }
        _rooms.EndUpdate();
        var n = rooms?.Count ?? 0;
        _steps.Set("rooms", StepKind.Ok, $"{n}개");
        if (!silent)
        {
            Log($"[room list] {n}개");
            Notify(StepKind.Ok, $"룸 목록 {n}개");
        }
    }

    System.Threading.Tasks.Task SendToRecipientAsync() =>
        CurrentPeer() == null
            ? Safe(SendChatAllAsync, "전체 메시지")
            : Safe(SendChatAsync, "귓속말");

    async System.Threading.Tasks.Task SendChatAllAsync()
    {
        if (_p2p is not { IsConnected: true })
        {
            Fail("전체 메시지", "먼저 [P2P 접속]을 누르세요.");
            return;
        }
        var text = _chat.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Fail("전체 메시지", "메시지를 입력하세요.");
            return;
        }
        if (_p2p.LastPeerList.Count == 0)
        {
            Fail("전체 메시지", "같은 룸에 받을 피어가 없습니다.");
            return;
        }
        await _p2p.SendChatAsync(text);
        Log($"전체 → {_p2p.LastPeerList.Count}명 : {text}");
        _chat.Clear();
    }

    async System.Threading.Tasks.Task SendChatAsync()
    {
        if (_p2p is not { IsConnected: true })
        {
            Fail("귓속말", "먼저 [P2P 접속]을 누르세요.");
            return;
        }
        var peer = CurrentPeer();
        if (peer == null || string.IsNullOrEmpty(peer.Id) || _p2p.FindPeer(peer.Id) == null)
        {
            Fail("귓속말", "받는 사람이 룸에 없습니다. [받는 사람]에서 다시 고르세요.");
            SelectRecipient(null);
            return;
        }
        var text = _chat.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Fail("귓속말", "메시지를 입력하세요.");
            return;
        }
        await _p2p.SendChatToAsync(peer.Id, text);
        Log($"귓속말 → {peer.Name} {peer.Id} udp={_p2p.HasDirectUdpTo(peer.Id)} : {text}");
        _chat.Clear();
    }

    static string Addr(string ip, int port) =>
        string.IsNullOrEmpty(ip) || port <= 0 ? "-" : $"{ip}:{port}";

    string PeerPath(PeerInfo p) =>
        _p2p?.HasDirectUdpTo(p.Id) == true ? "UDP 직접(홀펀치)" : "시그널링 릴레이";

    string PeerDedicated(PeerInfo p)
    {
        if (string.IsNullOrEmpty(p.DedicatedRoomId))
            return string.IsNullOrEmpty(p.UserId) ? "미로그인" : "로비";
        if (!string.IsNullOrEmpty(_joinedRoomId) && p.DedicatedRoomId == _joinedRoomId)
            return $"같은 방 {p.DedicatedRoomId}";
        return $"다른 방 {p.DedicatedRoomId}";
    }

    // 로그인 계정/방 정보를 P2P 쪽에도 넣어 둠 (다음 PeerList 때 서버로 감)
    void SyncLinks()
    {
        if (_p2p == null) return;
        _p2p.UserId = _dedicated is { IsLoggedIn: true } ? _loggedUser : "";
        _p2p.DedicatedRoomId = _dedicated is { IsLoggedIn: true } ? _joinedRoomId : "";
    }

    void UpdateLinkBar()
    {
        if (IsDisposed || _linkBar.IsDisposed) return;
        var ded = _dedicated is not { IsConnected: true }
            ? "끊김"
            : _dedicated.IsLoggedIn
                ? $"{_loggedUser}" + (string.IsNullOrEmpty(_joinedRoomId) ? " · 로비" : $" · 방 {_joinedRoomId}")
                : "연결됨 · 미로그인";
        string sig, p2p;
        if (_p2p is { IsConnected: true })
        {
            sig = $"룸{_p2p.MyRoomNumber} {(_p2p.IsMaster ? RoleMaster : RoleMember)}  외부 {_p2p.MyPublicAddress}  내부 {_p2p.MyLocalAddress}";
            var peers = _p2p.LastPeerList;
            var direct = peers.Count(x => _p2p.HasDirectUdpTo(x.Id));
            p2p = peers.Count == 0 ? "피어 없음" : $"UDP 직접 {direct} · 릴레이 {peers.Count - direct}";
        }
        else
        {
            sig = "미접속";
            p2p = "-";
        }
        _linkBar.Text = $"Dedicated: {ded}   |   시그널링: {sig}   |   P2P: {p2p}";
    }

    string PeerName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "?";
        var p = _p2p?.FindPeer(id);
        return string.IsNullOrEmpty(p?.Name) ? id : p.Name;
    }

    sealed class ChatLine
    {
        public string Text;
        public string PeerId;
        public override string ToString() => Text;
    }

    void AddChatLine(string line, string peerId = null)
    {
        if (IsDisposed || _chatLog.IsDisposed) return;
        _chatLog.Items.Add(new ChatLine { Text = $"{DateTime.Now:HH:mm:ss}  {line}", PeerId = peerId });
        if (_chatLog.Items.Count > 500)
            _chatLog.Items.RemoveAt(0);
        _chatLog.TopIndex = Math.Max(0, _chatLog.Items.Count - 1);
    }

    void Alert(bool ok, string title, string text)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            MessageBox.Show(this, text, title, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        });
    }

    async System.Threading.Tasks.Task AutoRefreshAsync()
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        try
        {
            if (_dedicated is { IsLoggedIn: true, IsConnected: true })
                await _dedicated.RequestRoomListAsync();
            if (_p2p is { IsConnected: true } && !_p2pBusy)
                await RefreshPeersAsync(true);
        }
        catch (Exception ex)
        {
            Fail("자동 갱신", ex.Message, popup: false);
        }
        finally
        {
            _refreshing = false;
        }
    }

    void TickStatus()
    {
        try
        {
            UpdateLinkBar();
            if (IsDisposed || _p2p == null || _applyingPeers) return;
            var target = _selectedPeerId;
            if (!string.IsNullOrEmpty(target) && _p2p.HasDirectUdpTo(target))
                _steps.Set("punch", StepKind.Ok, $"자동 연결 · UDP 직접 → {PeerName(target)}");
            else if (_p2p.HasDirectUdp)
                _steps.Set("punch", StepKind.Ok, "자동 연결 · UDP 직접");
            else if (_p2p.IsConnected && _p2p.ConnectedPeerIds.Count > 0)
                _steps.Set("punch", StepKind.Warn, "UDP 응답 대기 · 메시지는 릴레이로 전달");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    void Notify(StepKind kind, string text) => _notify.Show(kind, text);

    void Fail(string title, string detail, bool popup = true)
    {
        Notify(StepKind.Fail, $"{title}: {detail.Replace('\n', ' ')}");
        Log($"[ERR] {title}: {detail.Replace('\n', ' ')}");
        if (popup)
            Alert(false, title, detail);
    }

    async System.Threading.Tasks.Task Safe(Func<System.Threading.Tasks.Task> work, string title)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Fail(title, ex.Message);
        }
    }

    void PlaceOnPrimaryScreen()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var w = Math.Min(Width, area.Width);
        var h = Math.Min(Height, area.Height);
        Left = area.Left + Math.Max(0, (area.Width - w) / 2);
        Top = area.Top + Math.Max(0, (area.Height - h) / 2);
        if (Width > area.Width || Height > area.Height)
            Bounds = new Rectangle(area.Left + 20, area.Top + 20, area.Width - 40, area.Height - 40);
    }

    static void PlaceSplitter(SplitContainer split)
    {
        if (split.Width < 80) return;
        var min = Math.Max(1, split.Panel1MinSize);
        var max = split.Width - split.Panel2MinSize - split.SplitterWidth;
        if (max < min) return;
        split.SplitterDistance = Math.Clamp((int)(split.Width * 0.52), min, max);
    }

    void Log(string line)
    {
        try
        {
            if (IsDisposed || _log.IsDisposed) return;
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _log.AppendText($"[{stamp}] {line}{Environment.NewLine}");
            SessionLog.Write($"[{stamp}] {line}");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a);
        else a();
    }

    static string RoomIdFromItem(string item)
    {
        if (string.IsNullOrEmpty(item)) return "";
        var parts = item.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : "";
    }

    static Font UiFont()
    {
        try { return new Font("Malgun Gothic", 9.5f); }
        catch { return new Font("Segoe UI", 9.75f); }
    }

    static Label Lbl(string t) => new()
    {
        Text = t,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(4, 8, 6, 0),
        Anchor = AnchorStyles.Left
    };

    static TextBox Input(string t) => new()
    {
        Text = t,
        Dock = DockStyle.Fill,
        Margin = new Padding(4),
        Height = 28
    };

    static ListBox List() => new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        Margin = new Padding(4)
    };

    static ListView PeerView()
    {
        var v = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Margin = new Padding(4)
        };
        v.Columns.Add("이름", 150);
        v.Columns.Add("계정", 70);
        v.Columns.Add("외부(공인) IP:포트", 140);
        v.Columns.Add("내부(LAN) IP:포트", 140);
        v.Columns.Add("역할", 60);
        v.Columns.Add("P2P 경로", 110);
        v.Columns.Add("Dedicated", 110);
        return v;
    }

    static Button Btn(string t, EventHandler h)
    {
        var b = new Button
        {
            Text = t,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(72, 30),
            Height = 30,
            Margin = new Padding(4),
            Padding = new Padding(8, 2, 8, 2),
            UseVisualStyleBackColor = true
        };
        b.Click += h;
        return b;
    }

    static GroupBox Group(string t) => new()
    {
        Text = t,
        Dock = DockStyle.Fill,
        Padding = new Padding(10, 8, 10, 10)
    };

    static FlowLayoutPanel Flow() => new()
    {
        Dock = DockStyle.Fill,
        AutoSize = false,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        Padding = new Padding(0),
        Margin = new Padding(0)
    };
}
