using Newtonsoft.Json.Linq;
using X0.MultiPlay;
using X0.MultiPlay.Client;
using X0.MultiPlay.Protocol;

var settings = CliSettings.Load();
var crypto = new PacketCrypto(string.IsNullOrWhiteSpace(settings.Encryption.Key) ? null : settings.Encryption.Key);
var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

if (mode is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (mode == "selftest")
{
    var room = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 777;
    var tcp = args.Length > 2 && int.TryParse(args[2], out var t) ? t : settings.SignalingServer.TcpPort;
    var udp = args.Length > 2 ? tcp + 1 : settings.SignalingServer.UdpPort;
    return await SelfTest.RunAsync(settings.SignalingServer.Host, tcp, udp, crypto, room);
}

DedicatedClient dedicated = null;
P2pClient p2p = null;

try
{
    if (mode is "dedicated" or "both")
    {
        dedicated = new DedicatedClient(settings.DedicatedServer.Host, settings.DedicatedServer.Port, crypto);
        dedicated.OnPingUpdated += ms => Console.WriteLine($"[dedicated ping] {ms}ms");
        dedicated.OnLobbyListReceived += names => Console.WriteLine("[lobby] " + string.Join(", ", names));
        dedicated.OnRoomListReceived += rooms =>
        {
            foreach (var r in rooms)
                Console.WriteLine($"[room] {r.Name} {r.RoomId} {r.PlayerCount}/{r.MaxPlayers}");
        };
        dedicated.OnRoomUpdate += room => Console.WriteLine($"[room update] {room?.Name} {room?.RoomId}");
        dedicated.OnGameStarted += id => Console.WriteLine($"[game start] {id}");
        dedicated.OnDisconnected += reason => Console.WriteLine($"[dedicated disconnect] {reason}");
        if (!await dedicated.ConnectAsync())
        {
            Console.Error.WriteLine("Dedicated 연결 실패. server-host 를 먼저 실행하세요.");
            return 1;
        }
        Console.WriteLine($"Dedicated 연결 {settings.DedicatedServer.Host}:{settings.DedicatedServer.Port}");
        await dedicated.SendPingAsync();
        if (!string.IsNullOrEmpty(settings.Login.UserId))
        {
            var login = await dedicated.LoginAsync(settings.Login.UserId, settings.Login.Password, settings.Login.ShopId);
            Console.WriteLine(login?.Success == true
                ? $"로그인 {login.User?.DisplayName} conn={dedicated.ConnectionId}"
                : $"로그인 실패 {login?.Message}");
            if (login?.Success == true)
                await dedicated.RequestRoomListAsync();
        }
    }

    if (mode is "p2p" or "both")
    {
        var name = args.Length > 1 ? args[1] : "CsPlayer";
        var room = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 1;
        p2p = new P2pClient(settings.SignalingServer.Host, settings.SignalingServer.TcpPort, settings.SignalingServer.UdpPort, crypto);
        p2p.OnLogMessage += Console.WriteLine;
        p2p.OnChatReceived += (from, msg, via) => Console.WriteLine($"[chat {via} {from}] {msg}");
        p2p.OnPeerConnected += (id, info) => Console.WriteLine($"[peer] {info.Name} {id} {info.PublicIp}:{info.PublicPort}");
        p2p.OnPeerDisconnected += id => Console.WriteLine($"[peer left] {id}");
        if (!await p2p.ConnectAsync(name, room))
        {
            Console.Error.WriteLine("시그널링 연결 실패.");
            return 1;
        }
        Console.WriteLine($"P2P 등록 id={p2p.MyId} room={room} udp={p2p.HasDirectUdp}");
        await p2p.SendSignalingPingAsync();
    }

    Console.WriteLine("명령: ping | login <id> <pw> | rooms | create <name> | join <id> | peers | chat <text> | quit");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line == null) break;
        var parts = Split(line);
        if (parts.Length == 0) continue;
        var cmd = parts[0].ToLowerInvariant();
        if (cmd is "quit" or "exit" or "q") break;
        switch (cmd)
        {
            case "ping":
                if (dedicated != null) await dedicated.SendPingAsync();
                if (p2p != null) await p2p.SendSignalingPingAsync();
                break;
            case "login" when dedicated != null && parts.Length >= 3:
                {
                    var login = await dedicated.LoginAsync(parts[1], parts[2], settings.Login.ShopId);
                    Console.WriteLine(login?.Success == true
                        ? $"로그인 {login.User?.DisplayName} conn={dedicated.ConnectionId}"
                        : $"실패 {login?.Message}");
                    if (login?.Success == true)
                        await dedicated.RequestRoomListAsync();
                }
                break;
            case "rooms" when dedicated != null:
                await dedicated.RequestRoomListAsync();
                break;
            case "create" when dedicated != null && parts.Length >= 2:
                await dedicated.CreateRoomAsync(string.Join(" ", parts.Skip(1)));
                break;
            case "join" when dedicated != null && parts.Length >= 2:
                await dedicated.JoinRoomAsync(parts[1]);
                break;
            case "leave" when dedicated != null:
                await dedicated.LeaveRoomAsync();
                break;
            case "start" when dedicated != null:
                await dedicated.StartGameAsync();
                break;
            case "peers" when p2p != null:
                {
                    var peers = await p2p.GetPeerListAsync();
                    foreach (var p in peers)
                        Console.WriteLine($"  {p.Name} {p.Id} {p.PublicIp}:{p.PublicPort} master={p.IsMaster}");
                }
                break;
            case "connect" when p2p != null && parts.Length >= 2:
                await p2p.ConnectToPeerAsync(parts[1]);
                break;
            case "chat" when p2p != null && parts.Length >= 2:
                await p2p.SendChatAsync(string.Join(" ", parts.Skip(1)));
                break;
            default:
                Console.WriteLine("알 수 없는 명령");
                break;
        }
        await Task.Delay(80);
        if (p2p != null)
            Console.WriteLine($"udp={p2p.HasDirectUdp} peers={p2p.ConnectedPeerIds.Count}");
    }
}
finally
{
    p2p?.Disconnect();
    dedicated?.Disconnect();
}

return 0;

static void PrintHelp()
{
    Console.WriteLine("""
      Unity 없는 C# 클라. server-host 가 떠 있어야 합니다.

        dotnet run --project csharp/X0.MultiPlay.Cli -- dedicated
        dotnet run --project csharp/X0.MultiPlay.Cli -- p2p [이름] [룸번호]
        dotnet run --project csharp/X0.MultiPlay.Cli -- both

      설정: config/appsettings.json (서버랑 같은 파일)
      """);
}

static string[] Split(string line) =>
    line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

sealed class CliSettings
{
    public DedicatedEndpoint DedicatedServer { get; set; } = new();
    public SignalingEndpoint SignalingServer { get; set; } = new();
    public EncryptionSettings Encryption { get; set; } = new();
    public LoginSettings Login { get; set; } = new();

    public static CliSettings Load()
    {
        var path = ConfigFile.Find();
        if (path == null)
        {
            Console.WriteLine("[config] config/appsettings.json 없음, 기본값 사용");
            return new CliSettings();
        }
        return JObject.Parse(File.ReadAllText(path)).ToObject<CliSettings>() ?? new CliSettings();
    }
}

sealed class DedicatedEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 19060;
}

sealed class SignalingEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 19050;
    public int UdpPort { get; set; } = 19051;
}

sealed class EncryptionSettings
{
    public string Key { get; set; } = "";
}

sealed class LoginSettings
{
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public long ShopId { get; set; }
}
