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
        public string PlayerGuid = "";
        public string ModVersion = "";
        public string SelectedAvatar = ""; // avatar bundle file name chosen by this player
        public MemberJoinState JoinState = MemberJoinState.Handshaking;
        public int BoatIndex = -1;
        public bool Kicked;
        /// <summary>SteamID64 of a player who came over the Steam transport; 0 for a LAN player.</summary>
        public ulong SteamId;
    }

    public sealed class SessionMemberInfo
    {
        public uint NetId;
        public string Name = "";
        public bool IsHost;
        public MemberJoinState State;
        public int PingMs = -1;
        public int BoatIndex = -1;
        public string ModVersion = "";
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
        public string LastDisconnectReason { get; private set; } = "";
        public bool AcceptingClients { get; private set; } = true;
        public bool HasConnectedSuccessfully { get; private set; }

        /// <summary>This machine's own player NetId (host = 1, client = assigned by host).</summary>
        public uint MyNetId { get; private set; } = NetRegistry.HostPlayerNetId;

        public readonly NetClock Clock = new NetClock();
        public readonly NetRegistry Registry = new NetRegistry();

        // Host: connected client sessions, keyed by peer id. Client: single host peer.
        private readonly Dictionary<int, PeerSession> _sessions = new Dictionary<int, PeerSession>();
        private readonly Dictionary<uint, string> _playerNames = new Dictionary<uint, string>();
        private NetPeer _hostPeer;            // client side: the host
        private DatagramTunnel _tunnel;       // Steam transport: carries this session's datagrams
        private LagRelay _lag;                // Debug link simulation: the tunnel ends in a delaying UDP relay
        private bool _steamFriendsOnly;       // Steam host: Steam friends only, the LAN port refuses everyone
        private bool _steamAdvertised;        // Steam host: the session is shown in friends' menus
        // A Steam route can go quiet for longer than a LAN link while Steam switches relays.
        private const int SteamDisconnectTimeoutMs = 12000;
        private long _lastTimeSyncTick;
        private SessionMemberInfo[] _roster = new SessionMemberInfo[0];
        private int _rosterRevision;
        private int _hostBoatIndex = -1;

        // Identity supplied by the runtime layer.
        public string ModVersion = "0.0.1";
        public Func<string> WorldIdProvider = () => "";   // host's save identity ("" = unknown)
        public string PlayerName = "Player";
        public string PlayerGuid = "";
        public string GetPlayerGuid(uint netId)
        {
            if (netId == MyNetId) return PlayerGuid;
            foreach (var session in _sessions.Values)
                if (session.PlayerNetId == netId) return session.PlayerGuid;
            return "";
        }
        /// <summary>Host: true when a participant that joined earlier (the host first) presents the same
        /// PlayerGuid, as two copies started from one install folder do.</summary>
        public bool SharesPlayerGuid(uint netId)
        {
            string guid = GetPlayerGuid(netId);
            if (string.IsNullOrEmpty(guid) || netId == MyNetId) return false;
            if (guid == PlayerGuid) return true;
            foreach (var session in _sessions.Values)
                if (session.HandshakeDone && session.PlayerNetId < netId && session.PlayerGuid == guid) return true;
            return false;
        }

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
        // Raised on a client when another player is no longer in the host's roster.
        public event Action<uint> OnMemberGone;
        public event Action OnRosterChanged;
        public event Action<GameplayNoticeMsg> OnGameplayNotice;

        public CoopNet(Action<string> log)
        {
            _log = log ?? (_ => { });
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
            _listener.NetworkReceiveUnconnectedEvent += OnLanQuery;
        }

        /// <summary>Host: answer machines of the local network that look for a game (config Server/AnnounceOnLan).</summary>
        public bool AnnounceOnLan = true;
        private ulong _lanSessionId;
        private long _lanWindowTick;
        private int _lanReplies;
        private const int LanRepliesPerSecond = 20;

        /// <summary>
        /// Host: a machine on the local network asks who hosts a game. The answer is what the join
        /// screen shows: name, crew size, whether the session is open, and the protocol and mod
        /// version, so a player with another build sees the host and why it cannot join. A host that
        /// admits Steam friends only keeps its LAN port closed and does not answer.
        /// </summary>
        private void OnLanQuery(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type)
        {
            try
            {
                if (Role != Role.Host || _net == null || !AnnounceOnLan || SteamFriendsOnly) return;
                if (!LanBeacon.IsQuery(reader)) return;
                // The port may be reachable from outside the LAN: never more than a few small answers.
                long now = Clock.LocalTick;
                if (now - _lanWindowTick >= 1000) { _lanWindowTick = now; _lanReplies = 0; }
                if (++_lanReplies > LanRepliesPerSecond) return;
                int players = 1;
                foreach (PeerSession session in _sessions.Values) if (session.HandshakeDone) players++;
                var w = new NetDataWriter();
                LanBeacon.WriteReply(w, new LanHost
                {
                    SessionId = _lanSessionId,
                    ProtocolVersion = Protocol.Version,
                    ModVersion = ModVersion,
                    HostName = PlayerName,
                    Players = players,
                    MaxPlayers = Math.Max(1, MaxClients) + 1,
                    Accepting = AcceptingClients,
                });
                _net.SendUnconnectedMessage(w, remote);
            }
            catch (Exception e) { _log("[CoopNet] LAN query: " + e.Message); }
        }

        public int PeerCount => _sessions.Count;
        /// <summary>The session's datagrams also travel over Steam (host) or only over Steam (client).</summary>
        public bool OverSteam => _tunnel != null && _lag == null;
        /// <summary>Steam host: only Steam friends are admitted, and nobody through the LAN port.</summary>
        public bool SteamFriendsOnly => Role == Role.Host && OverSteam && _steamFriendsOnly;
        /// <summary>Client: this session's datagrams pass through the Debug link simulation.</summary>
        public bool LinkSimulated => _lag != null;
        /// <summary>Client: who we are joining, for messages — an address or a Steam name.</summary>
        public string HostLabel { get; private set; } = "";
        public string TransportStatus => !OverSteam ? "" :
            "steam out=" + _tunnel.SentToRelay + " in=" + _tunnel.ReceivedFromRelay +
            (Role == Role.Host ? " links=" + _tunnel.LinkCount : "") +
            (string.IsNullOrEmpty(_tunnel.LastError) ? "" : " err=" + _tunnel.LastError);
        public double RttMs => Clock.RttMs;
        public SessionMemberInfo[] RosterSnapshot => (SessionMemberInfo[])_roster.Clone();
        /// <summary>The latest mod version in the session when it is later than this machine's, else "".</summary>
        public string NewerModVersion { get; private set; } = "";
        /// <summary>Name of the member who runs <see cref="NewerModVersion"/>.</summary>
        public string NewerModVersionHolder { get; private set; } = "";

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

        public bool IsHostPeer(NetPeer peer) => Role == Role.Client && peer != null && peer == _hostPeer;

        public void SetAcceptingClients(bool accepting)
        {
            if (Role != Role.Host || AcceptingClients == accepting) return;
            AcceptingClients = accepting;
            BroadcastRoster();
            BroadcastNotice(accepting ? GameplayNoticeKind.SessionOpened : GameplayNoticeKind.SessionLocked, MyNetId);
        }

        /// <summary>Steam host: change the admission rule of the running session. Players already in stay.</summary>
        public void SetSteamFriendsOnly(bool friendsOnly)
        {
            if (Role != Role.Host || !OverSteam || _steamFriendsOnly == friendsOnly) return;
            _steamFriendsOnly = friendsOnly;
            SteamLink.SetFriendsOnly(friendsOnly);
            _log("[CoopNet] Host admission: " + (friendsOnly ? "Steam friends only, LAN port closed"
                                                             : "anyone, over Steam and on the LAN port"));
        }

        private static string Origin(PeerSession session) => session.SteamId != 0UL ? "Steam " + session.SteamId : "LAN";

        public void SetMemberState(uint netId, MemberJoinState state)
        {
            if (Role != Role.Host) return;
            foreach (PeerSession session in _sessions.Values)
            {
                if (!session.HandshakeDone || session.PlayerNetId != netId) continue;
                if (session.JoinState == state) return;
                session.JoinState = state;
                BroadcastRoster();
                return;
            }
        }

        public void SetMemberBoat(uint netId, int boatIndex)
        {
            if (Role != Role.Host) return;
            foreach (PeerSession session in _sessions.Values)
            {
                if (!session.HandshakeDone || session.PlayerNetId != netId) continue;
                session.BoatIndex = boatIndex;
                return;
            }
        }

        public bool DisconnectPlayer(uint netId, string reason)
        {
            if (Role != Role.Host || netId == NetRegistry.HostPlayerNetId) return false;
            foreach (PeerSession session in _sessions.Values)
            {
                if (!session.HandshakeDone || session.PlayerNetId != netId || session.Peer == null) continue;
                var msg = new DisconnectMsg { Reason = string.IsNullOrEmpty(reason) ? "Removed by host" : reason };
                session.Kicked = true;
                session.Peer.Send(msg, DeliveryMethod.ReliableOrdered);
                session.Peer.Disconnect(Protocol.Write(msg));
                // A removed Steam player stays out until the host starts a new session.
                if (session.SteamId != 0UL) SteamLink.Block(session.SteamId);
                return true;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        public void StartHost(int port)
        {
            Stop();
            _net = NewManager();
            // Only a host hears datagrams from outside a connection: the search of the local network.
            _net.UnconnectedMessagesEnabled = _net.BroadcastReceiveEnabled = AnnounceOnLan;
            byte[] session = Guid.NewGuid().ToByteArray();
            _lanSessionId = BitConverter.ToUInt64(session, 0) | 1UL;
            // Honor ListenIp: a concrete address binds to that interface only; "0.0.0.0"/empty = all.
            bool bindAll = string.IsNullOrEmpty(ListenIp) || ListenIp == "0.0.0.0";
            bool started = bindAll ? _net.Start(port) : _net.Start(ListenIp, "::", port);
            if (!started)
            {
                State = LinkState.Failed;
                LastError = "Failed to open UDP port " + port +
                            (bindAll ? "" : " on interface " + ListenIp);
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Host;
            State = LinkState.Connected;   // host is "up" immediately; clients join later
            AcceptingClients = true;
            LastError = "";
            LastDisconnectReason = "";
            MyNetId = NetRegistry.HostPlayerNetId;
            // Host registers its own player as NetId 1.
            Registry.Register(NetRegistry.HostPlayerNetId, NetObjKind.Player, NetRegistry.HostPlayerNetId);
            BroadcastRoster();
            _log("[CoopNet] Host listening on " + (bindAll ? "all interfaces" : ListenIp) +
                 " port " + port + " (protocol " + Protocol.Version + ")");
        }

        /// <summary>
        /// Host over Steam as well as the LAN port: the same LiteNetLib host, plus a tunnel that feeds it
        /// the datagrams of Steam peers through loopback. If Steam is unavailable the LAN host stays up.
        /// </summary>
        public void StartSteamHost(int port, bool friendsOnly)
        {
            StartHost(port);
            if (Role != Role.Host) return;
            IDatagramRelay relay = SteamLink.OpenHost(friendsOnly);
            if (relay == null)
            {
                LastError = SteamLink.Error + " Hosting on the LAN port only.";
                _log("[CoopNet] " + LastError);
                return;
            }
            bool bindAll = string.IsNullOrEmpty(ListenIp) || ListenIp == "0.0.0.0";
            IPAddress local = IPAddress.Loopback;
            if (!bindAll && !IPAddress.TryParse(ListenIp, out local)) local = IPAddress.Loopback;
            _tunnel = new DatagramTunnel(relay);
            _tunnel.StartHost(new IPEndPoint(local, port));
            _steamFriendsOnly = friendsOnly;
            _steamAdvertised = true;
            _net.DisconnectTimeout = Math.Max(_net.DisconnectTimeout, SteamDisconnectTimeoutMs);
            _log("[CoopNet] Host also reachable over Steam as " + SteamLink.MyId +
                 (friendsOnly ? " (Steam friends only, LAN port closed)" : ""));
        }

        /// <summary>Join a host by Steam id: LiteNetLib connects to a loopback tunnel instead of an address.</summary>
        public void StartSteamClient(ulong hostSteamId)
        {
            Stop();
            IDatagramRelay relay = hostSteamId == 0UL ? null : SteamLink.OpenClient(hostSteamId);
            if (relay == null)
            {
                State = LinkState.Failed;
                LastError = hostSteamId == 0UL ? "Invalid Steam ID" : SteamLink.Error;
                _log("[CoopNet] " + LastError);
                return;
            }
            _tunnel = new DatagramTunnel(relay);
            int port = _tunnel.StartClient(hostSteamId);
            string name = SteamLink.NameOf(hostSteamId);
            // Steam may need many seconds to open a session through NAT or its relays.
            int attempts = Math.Max(ConnectAttempts, 30000 / Math.Max(100, ReconnectDelayMs));
            Connect("127.0.0.1", port, attempts, string.IsNullOrEmpty(name) ? "Steam " + hostSteamId : name);
        }


        public void StartClient(string ip, int port)
        {
            Stop();
            if (LinkSimulation.Enabled)
            {
                // Development: put a delaying, lossy relay between this client and the host.
                LagRelay lag = LagRelay.Open(ip, port, out string lagError);
                if (lag != null)
                {
                    _lag = lag;
                    _tunnel = new DatagramTunnel(lag);
                    int local = _tunnel.StartClient(LagRelay.HostPeer);
                    _log("[CoopNet] Link simulation on: " + ip + ":" + port + " through 127.0.0.1:" + local +
                         " delay=" + LinkSimulation.DelayMs + "ms jitter=" + LinkSimulation.JitterMs +
                         "ms loss=" + LinkSimulation.LossPercent + "%");
                    Connect("127.0.0.1", local, ConnectAttempts, ip);
                    return;
                }
                _log("[CoopNet] Link simulation not started (" + lagError + "), connecting directly");
            }
            Connect(ip, port, ConnectAttempts, ip);
        }

        private void Connect(string ip, int port, int attempts, string hostLabel)
        {
            HostLabel = hostLabel ?? "";
            _net = NewManager();
            _net.MaxConnectAttempts = Math.Max(1, attempts);
            if (OverSteam) _net.DisconnectTimeout = Math.Max(_net.DisconnectTimeout, SteamDisconnectTimeoutMs);
            if (!_net.Start())
            {
                State = LinkState.Failed;
                LastError = "Failed to start network manager";
                _log("[CoopNet] " + LastError);
                return;
            }
            Role = Role.Client;
            State = LinkState.Connecting;
            LastError = "";
            LastDisconnectReason = "";
            _log("[CoopNet] Connecting to " + (OverSteam ? HostLabel + " over Steam" : ip + ":" + port) + " ...");
            _net.Connect(ip, port, ConnKey);   // Hello is sent once the peer connects
        }

        public void Stop()
        {
            BoatGenerationBook.Session.Clear();
            if (_net != null)
            {
                _net.Stop();
                _net = null;
            }
            if (_tunnel != null)
            {
                _tunnel.Stop();
                _tunnel = null;
                if (_lag != null) { _lag.Dispose(); _lag = null; }
                else SteamLink.CloseRelay();
            }
            _steamFriendsOnly = false;
            _steamAdvertised = false;
            _sessions.Clear();
            // Names and our own id belong to the session that just ended: leaving them behind made the
            // next session start with stale labels and, on a client, a NetId from the previous host.
            _playerNames.Clear();
            _hostPeer = null;
            _roster = new SessionMemberInfo[0];
            _rosterRevision = 0;
            NewerModVersion = "";
            NewerModVersionHolder = "";
            _hostBoatIndex = -1;
            AcceptingClients = true;
            OnRosterChanged?.Invoke();
            MyNetId = NetRegistry.HostPlayerNetId;
            _lastTimeSyncTick = 0;
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
            SteamLink.Tick();   // free unless Steam was started
            if (_net == null) return;
            if (OverSteam && TickSteam()) return;
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

        /// <summary>Steam bookkeeping of a session that runs over Steam. True when the session was ended here.</summary>
        private bool TickSteam()
        {
            if (Role == Role.Host)
            {
                // A locked or full session is not offered to friends as one they can join.
                bool open = AcceptingClients && _sessions.Count < Math.Max(1, MaxClients);
                if (open != _steamAdvertised)
                {
                    _steamAdvertised = open;
                    SteamLink.Advertise(open);
                }
                return false;
            }
            if (Role != Role.Client || State != LinkState.Connecting || !SteamLink.FailureIsFinal) return false;
            // Steam already knows the host cannot answer; the remaining connect attempts would only
            // make the player wait half a minute for the same message.
            string failure = SteamLink.TakeFailure();
            Stop();
            State = LinkState.Failed;
            LastError = string.IsNullOrEmpty(failure) ? "Steam could not reach the host" : failure;
            _log("[CoopNet] " + LastError);
            return true;
        }

        // -----------------------------------------------------------------
        // LiteNetLib callbacks
        // -----------------------------------------------------------------

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (Role != Role.Host) { request.Reject(); return; }
            // Friends only: the LAN port admits nobody. A Steam player arrives from one of the tunnel's
            // own loopback sockets, and Steam has already checked the friendship.
            if (SteamFriendsOnly && !_tunnel.TryGetPeer(request.RemoteEndPoint, out _))
            {
                _log("[CoopNet] Refused LAN connection from " + request.RemoteEndPoint + ": Steam friends only");
                request.Reject(Protocol.Write(new RejectMsg
                {
                    Reason = RejectReason.SessionLocked,
                    Detail = "the host accepts Steam friends only; join over Steam",
                }));
                return;
            }
            // Cap concurrent clients per config (Server/MaxClients).
            if (_sessions.Count >= Math.Max(1, MaxClients))
            {
                request.Reject(Protocol.Write(new RejectMsg { Reason = RejectReason.ServerFull, Detail = "the session is full" }));
                return;
            }
            string key = "";
            try { key = request.Data.GetString(); } catch { }
            if (key == ConnKey) { request.Accept(); return; }
            // A build with another protocol number: it cannot play here, and the player has to hear
            // which side must update. The reject travels in the refusal itself, where clients since
            // 0.4.3 already look for it.
            if (TryProtocolOfKey(key, out int theirs))
            {
                string detail = ModVersions.ProtocolMismatch(Protocol.Version, theirs, ModVersion);
                _log("[CoopNet] Refused " + request.RemoteEndPoint + ": " + detail);
                request.Reject(Protocol.Write(new RejectMsg { Reason = RejectReason.ProtocolMismatch, Detail = detail }));
                return;
            }
            request.Reject();
        }

        private static bool TryProtocolOfKey(string key, out int protocol)
        {
            protocol = 0;
            const string prefix = "SailwindCoop:";
            return key != null && key.StartsWith(prefix, StringComparison.Ordinal) &&
                   int.TryParse(key.Substring(prefix.Length), out protocol);
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
                _log("[CoopNet] Connected, sending Hello (avatar=" + mySelection + ")");
                peer.Send(new HelloMsg
                {
                    ProtocolVersion = Protocol.Version,
                    ModVersion = ModVersion,
                    WorldId = WorldIdProvider(),
                    PlayerName = PlayerName,
                    SelectedAvatar = mySelection,
                    PlayerGuid = PlayerGuid,
                }, DeliveryMethod.ReliableOrdered);
            }
            else // Host
            {
                var session = new PeerSession { Peer = peer };
                if (OverSteam) _tunnel.TryGetPeer(new IPEndPoint(peer.Address, peer.Port), out session.SteamId);
                _sessions[peer.Id] = session;
                _log("[CoopNet] Client connected (peer " + peer.Id + ", " + Origin(session) + "), waiting for Hello");
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (Role == Role.Host)
            {
                if (_sessions.TryGetValue(peer.Id, out var s) && s.HandshakeDone)
                {
                    BroadcastNotice(s.Kicked ? GameplayNoticeKind.PlayerKicked : GameplayNoticeKind.PlayerLeft,
                                    s.PlayerNetId);
                    Registry.Remove(s.PlayerNetId);
                    _playerNames.Remove(s.PlayerNetId);
                    OnPlayerLeft?.Invoke(s.PlayerNetId);
                }
                _sessions.Remove(peer.Id);
                BroadcastRoster();
                _log("[CoopNet] Client disconnected (peer " + peer.Id + "): " + info.Reason);
            }
            else
            {
                // Ignore a late disconnect from a previous attempt's peer. A null _hostPeer means the
                // current attempt failed to connect and must still end as Failed.
                if (_hostPeer != null && _hostPeer != peer) return;
                _hostPeer = null;
                TryReadRejectFromDisconnect(info);
                if (State != LinkState.Rejected)
                {
                    State = LinkState.Failed;
                    LastError = string.IsNullOrEmpty(LastDisconnectReason)
                        ? "Disconnected: " + info.Reason
                        : LastDisconnectReason;
                    // A host that says why it refuses was handled above. One that refuses in silence
                    // is a build up to 0.4.3 with another protocol number, or a full session of one.
                    if (info.Reason == DisconnectReason.ConnectionRejected && string.IsNullOrEmpty(LastDisconnectReason))
                        LastError = "The host refused the connection without a reason. Most likely the host runs " +
                                    "another version of the mod (yours is " + ModVersion + ", protocol " + Protocol.Version +
                                    "): both players need the same one.";
                    // Steam knows why the host could not be reached; "ConnectionFailed" does not.
                    string steamFailure = OverSteam ? SteamLink.TakeFailure() : null;
                    if (!string.IsNullOrEmpty(steamFailure) && string.IsNullOrEmpty(LastDisconnectReason))
                        LastError = steamFailure;
                }
                _log("[CoopNet] Disconnected from host: " + info.Reason);
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
                if (type == MsgType.Reject)
                {
                    if (Protocol.ReadBody(type, data) is RejectMsg rej) HandleReject(rej);
                    return;
                }
                if (type == MsgType.Disconnect && Protocol.ReadBody(type, data) is DisconnectMsg disconnect)
                    LastDisconnectReason = disconnect.Reason;
            }
            catch { }
        }

        private void OnNetworkError(IPEndPoint endPoint, SocketError error)
        {
            LastError = "Network error: " + error;
            _log("[CoopNet] " + LastError + " (" + endPoint + ")");
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            // This runs inside NetManager.PollEvents, i.e. inside CoopBehaviour.Update. An escaping
            // exception (malformed packet, a handler touching a destroyed object) would abort the whole
            // frame's tick, not just this packet — so nothing is allowed out of here.
            try
            {
                ReceivingUnreliable = method == DeliveryMethod.Unreliable;
                DispatchReceive(peer, reader);
            }
            catch (Exception e)
            {
                // A handler that throws usually throws for EVERY packet (a destroyed avatar, a boat that
                // went away). Unthrottled that is ~SnapshotHz x peers full stack traces per second, and
                // the disk I/O alone is the frame hitch this containment exists to avoid.
                //
                // Not routed through _log: that is the gated info sink, so with logging off (the default)
                // a receive path broken for the whole session left no trace at all. ReportError throttles
                // the same way and still writes a bounded number of lines while logging is off.
                Plugin.Logger.ReportError("[CoopNet] Receive handler failed", e, ref _receiveFailures);
            }
            // Packets replayed later from a deferred queue are not "arriving unreliable".
            finally { ReceivingUnreliable = false; }
        }

        private Runtime.CoopLog.Repeat _receiveFailures;
        private int _unknownTypeCount;
        private int _misdirectedCount;

        /// <summary>True while the packet being dispatched arrived Unreliable, i.e. its sender already
        /// tolerates losing it. Deferred queues use it to keep only the newest such snapshot.</summary>
        public bool ReceivingUnreliable { get; private set; }

        private void DispatchReceive(NetPeer peer, NetPacketReader reader)
        {
            MsgType type = Protocol.PeekType(reader);
            bool handshaked;
            if (Role == Role.Host)
            {
                if (!_sessions.TryGetValue(peer.Id, out var session)) return;
                handshaked = session.HandshakeDone;
            }
            else if (Role == Role.Client)
            {
                if (peer != _hostPeer) return;
                handshaked = State == LinkState.Connected;
            }
            else return;
            if (!MessageDirection.Accept(type, Role == Role.Host, handshaked))
            {
                // Not routed through _log: that sink is silent with logging off, and a wrong row in
                // MessageDirection would then look like a feature that simply does nothing.
                // Except a joining client: the host's unreliable snapshots routinely overtake the
                // reliable HelloAck, and dropping those is the expected outcome, not a fault.
                bool joining = Role == Role.Client && !handshaked;
                if (!joining && Plugin.Logger.ShouldReport(ref _misdirectedCount))
                    Plugin.Logger.LogWarning("[CoopNet] role=" + Role + " dropped " + type + " from peer " + peer.Id +
                         " handshaked=" + handshaked + " (occurrence #" + _misdirectedCount + ")");
                return;
            }

            INetMessage msg = Protocol.ReadBody(type, reader);
            if (msg == null)
            {
                // ReadBody already reported a malformed payload; only unknown types are news here, and a
                // spoofed/corrupt stream on the port can produce one per datagram.
                if (Plugin.Logger.ShouldReport(ref _unknownTypeCount))
                    _log("[CoopNet] Unknown or unreadable msgType " + (byte)type +
                         " - skipped (occurrence #" + _unknownTypeCount + ")");
                return;
            }

            switch (type)
            {
                case MsgType.Hello: HandleHello(peer, (HelloMsg)msg); break;
                case MsgType.HelloAck: HandleHelloAck((HelloAckMsg)msg); break;
                case MsgType.Reject: HandleReject((RejectMsg)msg); break;
                case MsgType.TimeSync: HandleTimeSync(peer, (TimeSyncMsg)msg); break;
                case MsgType.AvatarChange: HandleAvatarChange(peer, (AvatarChangeMsg)msg); break;
                case MsgType.SessionRoster: HandleSessionRoster((SessionRosterMsg)msg); break;
                case MsgType.GameplayNotice: HandleGameplayNotice((GameplayNoticeMsg)msg); break;
                case MsgType.Disconnect:
                    LastDisconnectReason = ((DisconnectMsg)msg).Reason;
                    _log("[CoopNet] Disconnect: " + LastDisconnectReason);
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
                _log("[CoopNet] Rejecting client: " + reason + " " + detail);
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
            session.PlayerGuid = hello.PlayerGuid;
            session.ModVersion = ModVersions.Clean(hello.ModVersion);
            session.PlayerName = string.IsNullOrEmpty(hello.PlayerName) ? ("Player" + assigned) : hello.PlayerName;
            session.SelectedAvatar = string.IsNullOrWhiteSpace(hello.SelectedAvatar) ? "" : hello.SelectedAvatar.Trim();
            session.JoinState = MemberJoinState.Queued;
            _playerNames[assigned] = session.PlayerName;
            _playerNames[NetRegistry.HostPlayerNetId] = PlayerName;

            peer.Send(new HelloAckMsg
            {
                AssignedNetId = assigned,
                ServerTick = Clock.ServerTick,
                HostPlayerName = PlayerName,
            }, DeliveryMethod.ReliableOrdered);

            _log("[CoopNet] Client accepted: " + session.PlayerName + " -> NetId " + assigned +
                 " (" + Origin(session) + ")");
            OnClientReady?.Invoke(session);
            SendKnownAvatarsTo(peer);
            BroadcastRoster();
            BroadcastNotice(GameplayNoticeKind.PlayerJoined, assigned);
        }

        /// <summary>
        /// Host: right after accepting a client, tell it everyone's CURRENT avatar choice —
        /// the host's own and every other connected client's. Without this a selection made
        /// BEFORE the join never reaches the new client (Hello carries only the client's own
        /// choice, and AvatarChange only fires on a change). Reuses the existing AvatarChange
        /// message, so the wire format is unchanged.
        /// </summary>
        private void SendKnownAvatarsTo(NetPeer peer)
        {
            try
            {
                string mine = "";
                try { mine = SailwindCoop.Avatar.AvatarCatalog.CurrentSelection; } catch { }
                if (!string.IsNullOrWhiteSpace(mine))
                    peer.Send(new AvatarChangeMsg { NetId = NetRegistry.HostPlayerNetId, BundleFile = mine.Trim() },
                              DeliveryMethod.ReliableOrdered);

                foreach (var s in _sessions.Values)
                {
                    if (!s.HandshakeDone || s.Peer == peer) continue;
                    if (string.IsNullOrWhiteSpace(s.SelectedAvatar)) continue;
                    peer.Send(new AvatarChangeMsg { NetId = s.PlayerNetId, BundleFile = s.SelectedAvatar },
                              DeliveryMethod.ReliableOrdered);
                }
                _log("[CoopNet] Sent known avatar selections to peer " + peer.Id +
                     " (host='" + mine + "')");
            }
            catch (Exception e)
            {
                _log("[CoopNet] SendKnownAvatarsTo: " + e.Message);
            }
        }

        private RejectReason ValidateHello(HelloMsg h, out string detail)
        {
            detail = "";
            if (h.ProtocolVersion != Protocol.Version)
            {
                detail = ModVersions.ProtocolMismatch(Protocol.Version, h.ProtocolVersion, ModVersion);
                return RejectReason.ProtocolMismatch;
            }
            // A different mod version is not a reason to refuse: the protocol number alone says whether
            // two builds understand each other (F1). Hosts up to 0.4.2 answered ModVersionMismatch here.
            if (!string.IsNullOrEmpty(ModVersion) && !string.IsNullOrEmpty(h.ModVersion) && h.ModVersion != ModVersion)
                _log("[CoopNet] Client runs mod " + h.ModVersion + ", host " + ModVersion + ": same protocol, accepted");
            if (!AcceptingClients)
            {
                detail = "the host closed this session to new players";
                return RejectReason.SessionLocked;
            }
            string hostWorld = WorldIdProvider() ?? "";
            // Only enforce when both sides actually know their world (Stage 0 may not).
            if (!string.IsNullOrEmpty(hostWorld) && !string.IsNullOrEmpty(h.WorldId) && h.WorldId != hostWorld)
            {
                detail = "different worlds/saves";
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
            LastError = "";
            LastDisconnectReason = "";
            MyNetId = ack.AssignedNetId;
            HasConnectedSuccessfully = true;
            // Seed the clock immediately with the host tick from the ack.
            Clock.OnReply(Clock.LocalTick, ack.ServerTick);
            Registry.Register(ack.AssignedNetId, NetObjKind.Player, ack.AssignedNetId);
            Registry.Register(NetRegistry.HostPlayerNetId, NetObjKind.Player, NetRegistry.HostPlayerNetId);
            _playerNames[ack.AssignedNetId] = PlayerName;
            _playerNames[NetRegistry.HostPlayerNetId] = string.IsNullOrEmpty(ack.HostPlayerName) ? "Host" : ack.HostPlayerName;
            _log("[CoopNet] Accepted by host. My NetId=" + ack.AssignedNetId + ", host='" + ack.HostPlayerName + "'");
            OnAccepted?.Invoke(ack);
        }

        private void HandleReject(RejectMsg rej)
        {
            if (Role != Role.Client) return;
            State = LinkState.Rejected;
            LastError = rej.Reason == RejectReason.ProtocolMismatch && !string.IsNullOrEmpty(rej.Detail)
                ? "Different mod versions: " + rej.Detail + "."
                : "Host rejected: " + rej.Reason + " (" + rej.Detail + ")";
            _log("[CoopNet] " + LastError);
        }

        public void BroadcastRoster(int? hostBoatIndex = null)
        {
            if (Role != Role.Host) return;
            if (hostBoatIndex.HasValue) _hostBoatIndex = hostBoatIndex.Value;
            var members = new List<SessionRosterMsg.Member>();
            members.Add(new SessionRosterMsg.Member
            {
                NetId = NetRegistry.HostPlayerNetId,
                Name = PlayerName,
                IsHost = true,
                State = MemberJoinState.Ready,
                PingMs = 0,
                BoatIndex = _hostBoatIndex,
                ModVersion = ModVersion,
            });
            foreach (PeerSession session in _sessions.Values)
            {
                if (!session.HandshakeDone) continue;
                members.Add(new SessionRosterMsg.Member
                {
                    NetId = session.PlayerNetId,
                    Name = session.PlayerName,
                    IsHost = false,
                    State = session.JoinState,
                    PingMs = session.Peer == null ? -1 : session.Peer.RoundTripTime,   // Ping is RTT/2
                    BoatIndex = session.BoatIndex,
                    ModVersion = session.ModVersion,
                });
            }
            members.Sort((a, b) => a.NetId.CompareTo(b.NetId));
            var msg = new SessionRosterMsg
            {
                Revision = ++_rosterRevision,
                AcceptingClients = AcceptingClients,
                Members = members.ToArray(),
            };
            ApplyRoster(msg);
            Broadcast(msg, DeliveryMethod.ReliableOrdered);
        }

        private void HandleSessionRoster(SessionRosterMsg msg)
        {
            if (Role != Role.Client || msg.Revision < _rosterRevision) return;
            ApplyRoster(msg);
        }

        private void ApplyRoster(SessionRosterMsg msg)
        {
            _rosterRevision = msg.Revision;
            AcceptingClients = msg.AcceptingClients;
            var next = new SessionMemberInfo[msg.Members == null ? 0 : msg.Members.Length];
            for (int i = 0; i < next.Length; i++)
            {
                SessionRosterMsg.Member m = msg.Members[i];
                next[i] = new SessionMemberInfo
                {
                    NetId = m.NetId,
                    Name = m.Name ?? "",
                    IsHost = m.IsHost,
                    State = m.State,
                    PingMs = m.PingMs,
                    BoatIndex = m.BoatIndex,
                    ModVersion = m.ModVersion ?? "",
                };
                _playerNames[m.NetId] = m.Name ?? "";
            }
            SessionMemberInfo[] previous = _roster;
            _roster = next;
            int newest = ModVersions.NewestAbove(ModVersion, Array.ConvertAll(next, m => m.ModVersion));
            NewerModVersion = newest < 0 ? "" : next[newest].ModVersion;
            NewerModVersionHolder = newest < 0 ? "" : next[newest].Name;
            // A client hears about a leaver only through the roster: OnPlayerLeft is raised on the host.
            if (Role == Role.Client)
                foreach (SessionMemberInfo old in previous)
                    if (old.NetId != MyNetId && Array.FindIndex(next, m => m.NetId == old.NetId) < 0) OnMemberGone?.Invoke(old.NetId);
            OnRosterChanged?.Invoke();
        }

        public void BroadcastNotice(GameplayNoticeKind kind, uint actorNetId, string detail = "")
        {
            if (Role != Role.Host) return;
            var msg = new GameplayNoticeMsg { Kind = kind, ActorNetId = actorNetId, Detail = detail ?? "" };
            OnGameplayNotice?.Invoke(msg);
            Broadcast(msg, DeliveryMethod.ReliableOrdered);
        }

        private void HandleGameplayNotice(GameplayNoticeMsg msg)
        {
            if (Role == Role.Client) OnGameplayNotice?.Invoke(msg);
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
            // Both sides: hand the message to the Sync layer so PlayerSync rebuilds the avatar.
            // (The switch above intercepts AvatarChange before the default OnGameMessage branch,
            // so without this explicit forward the change was never applied anywhere.)
            OnGameMessage?.Invoke(MsgType.AvatarChange, msg, peer);
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

        /// <summary>Host: the clients that have loaded the world.</summary>
        public uint[] ReadyClientIds()
        {
            var ids = new List<uint>();
            if (Role == Role.Host)
                foreach (var s in _sessions.Values)
                    if (s.HandshakeDone && s.JoinState == MemberJoinState.Ready) ids.Add(s.PlayerNetId);
            return ids.ToArray();
        }

        /// <summary>Host: send to one client that has loaded the world. False when there is no such client.</summary>
        public bool SendToPlayer(uint netId, INetMessage msg, DeliveryMethod method)
        {
            if (Role != Role.Host) return false;
            foreach (var s in _sessions.Values)
            {
                if (!s.HandshakeDone || s.PlayerNetId != netId || s.JoinState != MemberJoinState.Ready) continue;
                s.Peer.Send(msg, method);
                return true;
            }
            return false;
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
