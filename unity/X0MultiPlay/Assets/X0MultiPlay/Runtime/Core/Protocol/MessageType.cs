namespace X0.MultiPlay.Protocol
{
    // 서버 protocol.rs 랑 번호 맞출 것
    public enum MessageType : byte
    {
        Unknown = 0,
        Register = 1,
        RegisterAck = 2,
        PeerList = 3,
        ConnectRequest = 4,
        ConnectResponse = 5,
        HolePunch = 6,
        RoomBroadcast = 7,
        SignalingPing = 8,
        SignalingPong = 9,
        Ping = 10,
        Pong = 11,
        Chat = 12,
        GameState = 13,
        PlayerInput = 14,
        Disconnect = 15,
        Login = 20,
        LoginAck = 21,
        LobbyList = 22,
        RoomList = 23,
        CreateRoom = 24,
        CreateRoomAck = 25,
        JoinRoom = 26,
        JoinRoomAck = 27,
        LeaveRoom = 28,
        RoomUpdate = 29,
        StartGame = 30,
        PingRequest = 31,
        PingResponse = 32,
    }
}
