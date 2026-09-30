using System.Drawing;
using System.Windows.Forms;

namespace X0.MultiPlay.Gui;

enum StepKind
{
    Idle,
    Ok,
    Warn,
    Fail
}

sealed class NotifyBar : Panel
{
    readonly Label _label;

    public NotifyBar()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(8, 0, 8, 0);
        MinimumSize = new Size(0, 26);
        BackColor = Color.FromArgb(245, 247, 250);
        _label = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "준비. Dedicated 연결 → 로그인 → 룸 / P2P 순으로 확인합니다."
        };
        Controls.Add(_label);
    }

    public void Show(StepKind kind, string text)
    {
        _label.Text = text ?? "";
        BackColor = kind switch
        {
            StepKind.Ok => Color.FromArgb(220, 245, 228),
            StepKind.Warn => Color.FromArgb(255, 243, 214),
            StepKind.Fail => Color.FromArgb(255, 228, 228),
            _ => Color.FromArgb(245, 247, 250)
        };
        _label.ForeColor = kind == StepKind.Fail
            ? Color.FromArgb(150, 30, 30)
            : Color.FromArgb(30, 36, 44);
    }
}

sealed class StepBoard : TableLayoutPanel
{
    readonly Dictionary<string, (Label lamp, Label text)> _rows = new();

    public StepBoard()
    {
        Dock = DockStyle.Fill;
        ColumnCount = 2;
        Padding = new Padding(6, 4, 6, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add("ded", "1. Dedicated TCP");
        Add("login", "2. 로그인");
        Add("rooms", "3. 룸 목록");
        Add("room", "4. 방 만들기 / 참가");
        Add("p2p", "5. 시그널링 등록");
        Add("peers", "6. 피어 목록");
        Add("punch", "7. 홀펀치 / UDP 직접");
        Add("chatTx", "8. 채팅 송신");
        Add("chatRx", "9. 채팅 수신");
    }

    void Add(string id, string title)
    {
        var row = RowCount;
        RowCount++;
        RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var lamp = new Label
        {
            Text = "●",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Silver
        };
        var text = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        Controls.Add(lamp, 0, row);
        Controls.Add(text, 1, row);
        _rows[id] = (lamp, text);
    }

    public void ResetGroup(params string[] ids)
    {
        foreach (var id in ids)
            Set(id, StepKind.Idle);
    }

    public void Set(string id, StepKind kind, string extra = null)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        row.lamp.ForeColor = kind switch
        {
            StepKind.Ok => Color.FromArgb(28, 168, 86),
            StepKind.Warn => Color.FromArgb(214, 150, 20),
            StepKind.Fail => Color.FromArgb(210, 55, 55),
            _ => Color.Silver
        };
        var title = id switch
        {
            "ded" => "1. Dedicated TCP",
            "login" => "2. 로그인",
            "rooms" => "3. 룸 목록",
            "room" => "4. 방 만들기 / 참가",
            "p2p" => "5. 시그널링 등록",
            "peers" => "6. 피어 목록",
            "punch" => "7. 홀펀치 / UDP 직접",
            "chatTx" => "8. 채팅 송신",
            "chatRx" => "9. 채팅 수신",
            _ => id
        };
        row.text.Text = string.IsNullOrWhiteSpace(extra) ? title : $"{title}  ·  {extra}";
        row.text.ForeColor = kind == StepKind.Fail
            ? Color.FromArgb(140, 30, 30)
            : Color.FromArgb(32, 38, 46);
    }
}
