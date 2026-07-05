using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public enum Role { None, Host, Client }

    public enum LinkState { Idle, Connecting, Handshaking, Connected, Rejected, Failed }

    /// <summary>Per-peer session info the host keeps for each connected client.</summary>
    public sealed class PeerSession
    {
        public NetPeer Peer;
        public bool HandshakeDone;
        public uint PlayerNetId;
        public string PlayerName = "";
        public string SelectedAvatar = ""; // avatar bundle file name chosen by this player
    }

    /// <summary>
    /// Transport + session layer (F1/F5). Wraps LiteNetLib, drives the Hello
    /// handshake and TimeSync, and routes decoded gameplay messages to a callback.
    /// Pure transport/session here — no game-object access (that lives in Sync/).
    ///
    /// All public methods are expected to be called from the Unity main thread;
    /// <see cref="PollEvents"/> must run every frame (F5).
    /// </summary>
    public sealed class CoopNet
    {
        // Connection key gates wrong builds out at the LiteNetLib layer too.
        private static string ConnKey => "SailwindCoop:" + Protocol.Version;

        private readonly Action<string> _log;
        private readonly EventBasedNetListener _listener = new EventBasedNetListener();
        private NetManager _net;

        public Role Role { get; private set; } = Role.None;
        public LinkState State { get; private set; } = LinkState.Idle;
        public string LastError { get; private set; } = "";

        /// <summary>This machine's own player NetId (host = 1, client = assigned by host).</summary>
        public uint MyNetId { get; private set; } = NetRegistry.HostPlayerNetId;

        public readonly NetClock Clock = new NetClock();
        public readonly NetRegistry Registry = new NetRegistry();

        // Host: connected client sessions, keyed by peer id. Client: single host peer.
        private readonly Dictionary<int, PeerSession> _sessions = new Dictionary<int, PeerSession>();
        private readonly Dictionary<uint, string> _playerNames = new Dictionary<uint, string>();
        private NetPeer _hostPeer;            // client side: the host
        private long _lastTimeSyncTick;

        // Identity supplied by the runtime layer.
        public string ModVersion = "0.0.1";
        public Func<string> WorldIdProvider = () => "";   // host's save identity ("" = unknown)
        public string PlayerName = "Player";

        // Server (host) tuning, supplied by the runtime layer from config.
        public int MaxClients = 4;
        public int DisconnectTimeoutMs = 5000;
        public int UpdateTimeMs = 15;
        public int PingIntervalMs = 1000;

        // Connection tuning (host bind + client connect), supplied by the runtime layer from config.
        public string ListenIp = "0.0.0.0";   // host: interface to bind ("0.0.0.0"/empty = all)
        public int ConnectAttempts = 10;       // client: connect packets before giving up
        public int ReconnectDelayMs = 500;     // client: delay between connect attempts

        // Raised for non-session gameplay messages (Stage 1+). (type, message, fromPeer)
        public event Action<MsgType, INetMessage, NetPeer> OnGameMessage;
        // Raised when a client finishes handshake on the host (host side) — for spawn bookkeeping.
        public event Action<PeerSession> OnClientReady;
        // Raised on the client when its own handshake is accepted.
        public event Action<HelloAckMsg> OnAccepted;
        // Raised when a remote player leaves (host side, with the leaver's NetId).
        public event Action<uint> OnPlayerLeft;

        public CoopNet(Action<string> log)
        {
            _log = log ?? (_ => { });
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
        }

        public int PeerCount => _sessions.Count;
        public double RttMs => Clock.RttMs;

        public string GetPlayerName(uint netId)
        {
            if (_playerNames.TryGetValue(netId, out string name) && !string.IsNullOrEmpty(name))
                return name;
            if (netId == MyNetId && !string.IsNullOrEmpty(PlayerName))
                return PlayerName;
            return "Player " + netId;
        }

        public uint PlayerNetIdForPeer(NetPeer peer)
        {
            if (peer != null && _sessions.TryGetValue(peer.Id, out var session) && session.HandshakeDone)
                return session.PlayerNetId;
            return 0;
        }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        public void StartHost(int port)
        {
            Stop();
            _net = NewManager();
            // Honor ListenIp: a concrete address binds to that interface only; "0.0.0.0"/empty = all.
            bool bindAll = string.IsNullOrEmpty(ListenIp) || ListenIp == "0.0.0.0";
            bool started = bindAll ? _net.Start(port) : _net.Start(ListenIp, "::", port);
            if (!started)
            {
                State = LinkState.Failed;
                LastError = "Не удалось открыть UDP-порт " + port +
                            (bindAll ? "" : " на интерфейсе " + ListenIp);
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Host;
            State = LinkState.Connected;   // host is "up" immediately; clients join later
            MyNetId = NetRegistry.HostPlayerNetId;
            // Host registers its own player as NetId 1.
            Registry.Register(NetRegistry.HostPlayerNetId, NetObjKind.Player, NetRegistry.HostPlayerNetId);
            _log("[CoopNet] Хост слушает " + (bindAll ? "все интерфейсы" : ListenIp) +
                 " порт " + port + " (protocol " + Protocol.Version + ")");
        }

        public void StartClient(string ip, int port)
        {
            Stop();
            _net = NewManager();
            if (!_net.Start())
            {
                State = LinkState.Failed;
                LastError = "Не удалось запустить сетевой менеджер";
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Client;
            State = LinkState.Connecting;
            _log("[CoopNet] Подключение к " + ip + ":" + port + " ...");
            _net.Connect(ip, port, ConnKey);   // Hello is sent once the peer connects
        }

        public void Stop()
        {
            if (_net != null)
            {
                _net.Stop();
                _net = null;
            }
            _sessions.Clear();
            _hostPeer = null;
            Registry.Clear();
            Clock.Reset();
            Role = Role.None;
            State = LinkState.Idle;
        }

        private NetManager NewManager()
        {
            return new NetManager(_listener)
            {
                AutoRecycle = true,
                UpdateTime = Math.Max(1, UpdateTimeMs),
                DisconnectTimeout = Math.Max(1000, DisconnectTimeoutMs),
                PingInterval = Math.Max(100, PingIntervalMs),
                MaxConnectAttempts = Math.Max(1, ConnectAttempts),
                ReconnectDelay = Math.Max(100, ReconnectDelayMs),
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,
            };
        }

        // -----------------------------------------------------------------
        // Per-frame pump (F5: main thread only)
        // -----------------------------------------------------------------

        public void PollEvents()
        {
            if (_net == null) return;
            _net.PollEvents();

            // Client drives TimeSync at ~3 Hz once connected.
            if (Role == Role.Client && _hostPeer != null && State == LinkState.Connected)
            {
                long now = Clock.LocalTick;
                if (now - _lastTimeSyncTick >= 333)
                {
                    _lastTimeSyncTick = now;
                    _hostPeer.Send(new TimeSyncMsg { IsReply = false, ClientSendTick = now },
                                   DeliveryMethod.Unreliable);
                }
            }
        }

        // -----------------------------------------------------------------
        // LiteNetLib callbacks
        // -----------------------------------------------------------------

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (Role != Role.Host) { request.Reject(); return; }
            // Cap concurrent clients per config (Server/MaxClients).
            if (_sessions.Count >= Math.Max(1, MaxClients)) { request.Reject(); return; }
            request.AcceptIfKey(ConnKey);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (Role == Role.Client)
            {
                _hostPeer = peer;
                State = LinkState.Handshaking;
                string mySelection = "";
                try { mySelection = SailwindCoop.Avatar.AvatarCatalog.CurrentSelection; }
                catch { mySelection = ""; }
                _log("[CoopNet] Соединение установлено, отправляю Hello (avatar=" + mySelection + ")");
                peer.Send(new HelloMsg
                {
                    ProtocolVersion = Protocol.Version,
                    ModVersion = ModVersion,
                    WorldId = WorldIdProvider(),
                    PlayerName = PlayerName,
                    SelectedAvatar = mySelection,
                }, DeliveryMethod.ReliableOrdered);
            }
            else // Host
            {
                _sessions[peer.Id] = new PeerSession { Peer = peer };
                _log("[CoopNet] Клиент подключился (peer " + peer.Id + "), жду Hello");
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (Role == Role.Host)
            {
                if (_sessions.TryGetValue(peer.Id, out var s) && s.HandshakeDone)
                {
                    Registry.Remove(s.PlayerNetId);
                    _playerNames.Remove(s.PlayerNetId);
                    OnPlayerLeft?.Invoke(s.PlayerNetId);
                }
                _sessions.Remove(peer.Id);
                _log("[CoopNet] Клиент отключился (peer " + peer.Id + "): " + info.Reason);
            }
            else
            {
                _hostPeer = null;
                TryReadRejectFromDisconnect(info);
                if (State != LinkState.Rejected)
                {
                    State = LinkState.Failed;
                    LastError = "Отключено: " + info.Reason;
                }
                _log("[CoopNet] Отключено от хоста: " + info.Reason);
            }
        }

        /// <summary>The host duplicates a handshake RejectMsg into the disconnect packet (the plain
        /// reliable send may not flush before the shutdown) — recover the reason from there.</summary>
        private void TryReadRejectFromDisconnect(DisconnectInfo info)
        {
            try
            {
                var data = info.AdditionalData;
                if (data == null || data.EndOfData) return;
                MsgType type = Protocol.PeekType(data);
                if (type != MsgType.Reject) return;
                if (Protocol.ReadBody(type, data) is RejectMsg rej) HandleReject(rej);
            }
            catch { }
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError error)
        {
            LastError = "Сетевая ошибка: " + error;
            _log("[CoopNet] " + LastError + " (" + endPoint + ")");
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            MsgType type = Protocol.PeekType(reader);
            INetMessage msg = Protocol.ReadBody(type, reader);
            if (msg == null)
            {
                _log("[CoopNet] Неизвестный msgType " + (byte)type + " — пропущен");
                return;
            }

            switch (type)
            {
                case MsgType.Hello: HandleHello(peer, (HelloMsg)msg); break;
                case MsgType.HelloAck: HandleHelloAck((HelloAckMsg)msg); break;
                case MsgType.Reject: HandleReject((RejectMsg)msg); break;
                case MsgType.TimeSync: HandleTimeSync(peer, (TimeSyncMsg)msg); break;
                case MsgType.AvatarChange: HandleAvatarChange(peer, (AvatarChangeMsg)msg); break;
                case MsgType.Disconnect:
                    _log("[CoopNet] Disconnect: " + ((DisconnectMsg)msg).Reason);
                    break;
                default:
                    // Gameplay message — hand to the Sync layer (Stage 1+).
                    OnGameMessage?.Invoke(type, msg, peer);
                    break;
            }
        }

        // -----------------------------------------------------------------
        // Handshake (host side)
        // -----------------------------------------------------------------

        private void HandleHello(NetPeer peer, HelloMsg hello)
        {
            if (Role != Role.Host) return;
            if (!_sessions.TryGetValue(peer.Id, out var session)) return;

            RejectReason reason = ValidateHello(hello, out string detail);
            if (reason != RejectReason.None)
            {
                _log("[CoopNet] Отказ клиенту: " + reason + " " + detail);
                var rej = new RejectMsg { Reason = reason, Detail = detail };
                peer.Send(rej, DeliveryMethod.ReliableOrdered);
                // A reliable send may not flush before the disconnect — duplicate the reason into the
                // disconnect packet itself, which LiteNetLib delivers as part of the shutdown handshake.
                peer.Disconnect(Protocol.Write(rej));
                _sessions.Remove(peer.Id);
                return;
            }

            uint assigned = Registry.AllocateId();
            Registry.Register(assigned, NetObjKind.Player, assigned);
            session.HandshakeDone = true;
            session.PlayerNetId = assigned;
            session.PlayerName = string.IsNullOrEmpty(hello.PlayerName) ? ("Player" + assigned) : hello.PlayerName;
            session.SelectedAvatar = string.IsNullOrWhiteSpace(hello.SelectedAvatar) ? "" : hello.SelectedAvatar.Trim();
            _playerNames[assigned] = session.PlayerName;
            _playerNames[NetRegistry.HostPlayerNetId] = PlayerName;

            peer.Send(new HelloAckMsg
            {
                AssignedNetId = assigned,
                ServerTick = Clock.ServerTick,
                HostPlayerName = PlayerName,
            }, DeliveryMethod.ReliableOrdered);

            _log("[CoopNet] Клиент принят: " + session.PlayerName + " -> NetId " + assigned);
            OnClientReady?.Invoke(session);
        }

        private RejectReason ValidateHello(HelloMsg h, out string detail)
        {
            detail = "";
            if (h.ProtocolVersion != Protocol.Version)
            {
                detail = "сервер " + Protocol.Version + ", клиент " + h.ProtocolVersion;
                return RejectReason.ProtocolMismatch;
            }
            if (!string.IsNullOrEmpty(ModVersion) && !string.IsNullOrEmpty(h.ModVersion) && h.ModVersion != ModVersion)
            {
                detail = "сервер " + ModVersion + ", клиент " + h.ModVersion;
                return RejectReason.ModVersionMismatch;
            }
            string hostWorld = WorldIdProvider() ?? "";
            // Only enforce when both sides actually know their world (Stage 0 may not).
            if (!string.IsNullOrEmpty(hostWorld) && !string.IsNullOrEmpty(h.WorldId) && h.WorldId != hostWorld)
            {
                detail = "разные миры/сейвы";
                return RejectReason.WorldMismatch;
            }
            return RejectReason.None;
        }

        // -----------------------------------------------------------------
        // Handshake (client side)
        // -----------------------------------------------------------------

        private void HandleHelloAck(HelloAckMsg ack)
        {
            if (Role != Role.Client) return;
            State = LinkState.Connected;
            MyNetId = ack.AssignedNetId;
            // Seed the clock immediately with the host tick from the ack.
            Clock.OnReply(Clock.LocalTick, ack.ServerTick);
            Registry.Register(ack.AssignedNetId, NetObjKind.Player, ack.AssignedNetId);
            Registry.Register(NetRegistry.HostPlayerNetId, NetObjKind.Player, NetRegistry.HostPlayerNetId);
            _playerNames[ack.AssignedNetId] = PlayerName;
            _playerNames[NetRegistry.HostPlayerNetId] = string.IsNullOrEmpty(ack.HostPlayerName) ? "Host" : ack.HostPlayerName;
            _log("[CoopNet] Принят хостом. Мой NetId=" + ack.AssignedNetId + ", хост='" + ack.HostPlayerName + "'");
            OnAccepted?.Invoke(ack);
        }

        private void HandleReject(RejectMsg rej)
        {
            if (Role != Role.Client) return;
            State = LinkState.Rejected;
            LastError = "Хост отклонил: " + rej.Reason + " (" + rej.Detail + ")";
            _log("[CoopNet] " + LastError);
        }

        // -----------------------------------------------------------------
        // TimeSync (both sides)
        // -----------------------------------------------------------------

        private void HandleTimeSync(NetPeer peer, TimeSyncMsg ts)
        {
            if (!ts.IsReply)
            {
                // Responder: echo with our clock.
                peer.Send(new TimeSyncMsg
                {
                    IsReply = true,
                    ClientSendTick = ts.ClientSendTick,
                    ServerTick = Clock.ServerTick,
                }, DeliveryMethod.Unreliable);
            }
            else
            {
                // Originator: complete the round trip.
                Clock.OnReply(ts.ClientSendTick, ts.ServerTick);
            }
        }

        // -----------------------------------------------------------------
        // Avatar change (both sides)
        // -----------------------------------------------------------------

        /// <summary>Called by the client to tell the host (and other clients) the local player
        /// switched avatar bundle. The host rebroadcasts to everyone except the originator.</summary>
        public void SendAvatarChange(string bundleFile)
        {
            if (string.IsNullOrWhiteSpace(bundleFile)) return;
            var msg = new AvatarChangeMsg { NetId = MyNetId, BundleFile = bundleFile.Trim() };
            if (Role == Role.Host)
                Broadcast(msg, DeliveryMethod.ReliableOrdered);
            else if (_hostPeer != null)
                _hostPeer.Send(msg, DeliveryMethod.ReliableOrdered);
        }

        private void HandleAvatarChange(NetPeer peer, AvatarChangeMsg msg)
        {
            if (Role == Role.Host)
            {
                // Refresh the session's known choice before relaying.
                if (_sessions.TryGetValue(peer.Id, out var s) && s.HandshakeDone)
                    s.SelectedAvatar = msg.BundleFile ?? "";
                // Forward to other clients.
                RelayExcept(msg, peer, DeliveryMethod.ReliableOrdered);
            }
            // Both sides: the Sync layer reads the new bundle file via OnGameMessage.
        }

        // -----------------------------------------------------------------
        // Outbound helpers for Sync layers (Stage 1+).
        // -----------------------------------------------------------------

        /// <summary>Host: send to every handshaked client. Client: send to host.</summary>
        public void Broadcast(INetMessage msg, DeliveryMethod method)
        {
            if (Role == Role.Host)
            {
                foreach (var s in _sessions.Values)
                    if (s.HandshakeDone) s.Peer.Send(msg, method);
            }
            else if (_hostPeer != null)
            {
                _hostPeer.Send(msg, method);
            }
        }

        /// <summary>Host-only: forward a message to every handshaked client except the sender.</summary>
        public void RelayExcept(INetMessage msg, NetPeer except, DeliveryMethod method)
        {
            if (Role != Role.Host) return;
            foreach (var s in _sessions.Values)
                if (s.HandshakeDone && s.Peer != except) s.Peer.Send(msg, method);
        }
    }
}
