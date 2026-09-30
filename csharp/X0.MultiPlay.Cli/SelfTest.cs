using System.Collections.Concurrent;
using X0.MultiPlay;
using X0.MultiPlay.Client;

// 실제 서버에 클라 둘 붙여서 P2P 흐름 확인
static class SelfTest
{
    sealed class Probe
    {
        public P2pClient Client;
        public string Name;
        public readonly ConcurrentQueue<(string from, string msg, string via)> Chats = new();
        public readonly ConcurrentQueue<string> Left = new();
        public readonly ConcurrentQueue<(string msg, bool whisper)> Kinds = new();
    }

    static int _fail;

    public static async Task<int> RunAsync(string host, int tcp, int udp, PacketCrypto crypto, int room)
    {
        Console.WriteLine($"== selftest  signaling={host}:{tcp}/{udp}  room={room}");
        var a = await Join(host, tcp, udp, crypto, "ST-A", room);
        var b = await Join(host, tcp, udp, crypto, "ST-B", room);
        try
        {
            if (a == null || b == null) return 1;

            Check("A 마스터", a.Client.IsMaster);
            Check("B 대기", !b.Client.IsMaster);

            await WaitFor(() => a.Client.LastPeerList.Any(p => p.Id == b.Client.MyId), 2000);
            await WaitFor(() => b.Client.LastPeerList.Any(p => p.Id == a.Client.MyId), 2000);
            Check("A 목록에 B(대기)", a.Client.LastPeerList.Any(p => p.Id == b.Client.MyId && !p.IsMaster));
            Check("B 목록에 A(마스터)", b.Client.LastPeerList.Any(p => p.Id == a.Client.MyId && p.IsMaster));
            var aInB = b.Client.LastPeerList.FirstOrDefault(p => p.Id == a.Client.MyId);
            Console.WriteLine($"       A 정보 (B가 봄): 외부 {aInB?.PublicIp}:{aInB?.PublicPort}  내부 {aInB?.LocalIp}:{aInB?.LocalPort}");
            Check("피어 외부 IP:포트 전달", aInB is { PublicPort: > 0 } && !string.IsNullOrEmpty(aInB.PublicIp));
            Check("피어 내부 IP:포트 전달", aInB is { LocalPort: > 0 } && !string.IsNullOrEmpty(aInB.LocalIp));
            Check("내가 보는 내 외부 주소 = 상대가 보는 내 외부 주소", await WaitFor(() =>
                a.Client.MyPublicAddress == $"{aInB?.PublicIp}:{aInB?.PublicPort}", 4000));

            a.Client.UserId = "st-user-a";
            a.Client.DedicatedRoomId = "room_st";
            await a.Client.GetPeerListAsync();
            Check("A 계정/Dedicated 방 → B에 자동 동기화", await WaitFor(() =>
                b.Client.LastPeerList.FirstOrDefault(p => p.Id == a.Client.MyId) is { UserId: "st-user-a", DedicatedRoomId: "room_st" }, 2000));

            b.Client.ResetPunch(a.Client.MyId);
            await b.Client.ConnectToPeerAsync(a.Client.MyId);
            Check("B→A 선택 연결 후 UDP 직접", await WaitFor(() => b.Client.HasDirectUdpTo(a.Client.MyId), 2000));
            Check("A→B UDP 직접", await WaitFor(() => a.Client.HasDirectUdpTo(b.Client.MyId), 2000));

            await ChatCase("A→B UDP 채팅", a, b, "udp", () => a.Client.SendChatToAsync(b.Client.MyId, "hello-udp-a"), "hello-udp-a");
            await ChatCase("B→A UDP 채팅", b, a, "udp", () => b.Client.SendChatToAsync(a.Client.MyId, "hello-udp-b"), "hello-udp-b");
            await ChatCase("A→B 릴레이 채팅", a, b, "relay", () => a.Client.SendRelayChatToAsync(b.Client.MyId, "hello-relay"), "hello-relay");
            Check("귓속말은 whisper=true 로 수신", b.Kinds.Any(k => k.msg == "hello-udp-a" && k.whisper));
            await ChatCase("A→전체 메시지", a, b, "udp", () => a.Client.SendChatAsync("hello-all"), "hello-all");
            Check("전체 메시지는 whisper=false 로 수신", b.Kinds.Any(k => k.msg == "hello-all" && !k.whisper));

            for (var i = 1; i <= 3; i++)
            {
                var oldId = b.Client.MyId;
                b.Client.Disconnect();
                Check($"#{i} B 끊기 → A 퇴장 통지", await WaitFor(() => a.Left.Contains(oldId), 2000));
                Check($"#{i} A 목록 비움", await WaitFor(() => a.Client.LastPeerList.Count == 0, 2000));
                await Task.Delay(150);
                if (!await b.Client.ConnectAsync("ST-B", room))
                {
                    Check($"#{i} B 재접속", false);
                    break;
                }
                Check($"#{i} 재접속 후 A 마스터 유지", a.Client.IsMaster);
                Check($"#{i} 재접속 후 B 대기", !b.Client.IsMaster);
                Check($"#{i} 재접속 후 UDP 직접", await WaitFor(() => b.Client.HasDirectUdpTo(a.Client.MyId) && a.Client.HasDirectUdpTo(b.Client.MyId), 2000));
                await ChatCase($"#{i} 재접속 후 채팅", a, b, "udp", () => a.Client.SendChatToAsync(b.Client.MyId, $"again-{i}"), $"again-{i}");
            }

            var abortedId = b.Client.MyId;
            b.Client.Abort();
            Check("B 강제 종료(RST) → A 퇴장 통지", await WaitFor(() => a.Left.Contains(abortedId), 3000));
            Check("B 강제 종료 → 유령 세션 없음", await WaitFor(() => a.Client.LastPeerList.Count == 0, 3000));
            await Task.Delay(150);
            var b2 = await Join(host, tcp, udp, crypto, "ST-B", room);
            if (b2 != null)
            {
                b = b2;
                Check("강제 종료 후 재입장 A 마스터 유지", a.Client.IsMaster);
                Check("강제 종료 후 재입장 B 대기", !b.Client.IsMaster);
                Check("강제 종료 후 재입장 목록 1명", await WaitFor(() => b.Client.LastPeerList.Count == 1, 2000));
            }

            var c = await Join(host, tcp, udp, crypto, "ST-C", room);
            Probe[] three = { a, b, c };
            Check("3명 → 모두 서로 2명씩 보임", await WaitFor(() => three.All(x => x.Client.LastPeerList.Count == 2), 3000));
            Check("3명 → 마스터는 A 하나", three.Count(x => x.Client.IsMaster) == 1 && a.Client.IsMaster);
            Check("3명 → 목록상 마스터 표시도 하나(A)", three.All(x =>
                x.Client.LastPeerList.Count(p => p.IsMaster) == (x == a ? 0 : 1)));

            a.Client.Disconnect();
            Check("A 퇴장 → 다음 입장자 B 마스터 승계", await WaitFor(() => b.Client.IsMaster, 2000));
            Check("A 퇴장 → C는 멤버 유지, B를 마스터로 봄", await WaitFor(() =>
                !c.Client.IsMaster && c.Client.LastPeerList.Any(p => p.Id == b.Client.MyId && p.IsMaster), 2000));

            b.Client.Disconnect();
            Check("B 퇴장 → C 마스터 승계", await WaitFor(() => c.Client.IsMaster, 2000));
            c.Client.Disconnect();
        }
        finally
        {
            a?.Client.Disconnect();
            b?.Client.Disconnect();
        }

        Console.WriteLine(_fail == 0 ? "== RESULT: ALL PASS" : $"== RESULT: FAIL {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    static async Task<Probe> Join(string host, int tcp, int udp, PacketCrypto crypto, string name, int room)
    {
        var p = new Probe { Name = name, Client = new P2pClient(host, tcp, udp, crypto) };
        p.Client.OnChatReceived += (from, msg, via) => p.Chats.Enqueue((from, msg, via));
        p.Client.OnPeerDisconnected += id => p.Left.Enqueue(id);
        p.Client.OnChatMessage += (_, msg, _, whisper) => p.Kinds.Enqueue((msg, whisper));
        var ok = await p.Client.ConnectAsync(name, room);
        Check($"{name} 시그널링 등록", ok);
        return ok ? p : null;
    }

    static async Task ChatCase(string title, Probe from, Probe to, string via, Func<Task> send, string text)
    {
        while (to.Chats.TryDequeue(out _)) { }
        await send();
        var got = await WaitFor(() => to.Chats.Any(c => c.msg == text && c.from == from.Client.MyId && c.via == via), 2000);
        Check($"{title} ({via})", got);
        if (!got)
            Console.WriteLine($"       수신 내역: {string.Join(" | ", to.Chats.Select(c => $"{c.from}/{c.via}/{c.msg}"))}  from={from.Client.MyId}");
    }

    static async Task<bool> WaitFor(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    static void Check(string title, bool ok)
    {
        if (!ok) _fail++;
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}");
    }
}
