using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Steamworks;

namespace SailwindCoop.Net
{
    /// <summary>A Steam friend who is in the game right now.</summary>
    public struct SteamFriendInfo
    {
        public ulong Id;
        public string Name;
        /// <summary>The friend is hosting a co-op session.</summary>
        public bool Hosting;
        /// <summary>The hosted session uses this build's protocol.</summary>
        public bool SameVersion;
    }

    /// <summary>
    /// The only door to Steam. Nothing in its signatures or fields is a Steamworks type, and every call
    /// into <see cref="SteamNative"/> goes through a non-inlined method inside a try/catch: when the
    /// Steam wrapper or the native library is missing, the failure surfaces here as a message and the
    /// LAN transport never notices. Steam is not touched at all until <see cref="EnsureReady"/> is called.
    /// Main thread only.
    /// </summary>
    public static class SteamLink
    {
        /// <summary>Sailwind's Steam application id.</summary>
        public const uint AppId = 1764530;

        /// <summary>This build contains the Steam transport (the Thunderstore edition does not).</summary>
        public static readonly bool Available = true;

        public static bool Ready { get; private set; }
        /// <summary>Why Steam is unavailable, in the player's terms; empty when it is ready.</summary>
        public static string Error { get; private set; } = "";

        /// <summary>Starts the Steam client API on first use. Safe to call again after a failure.</summary>
        public static bool EnsureReady()
        {
            if (Ready) return true;
            try
            {
                InitCore();
                Ready = true;
                Error = "";
                Plugin.Logger.LogInfo("[Steam] Ready: " + MyName + " (" + MyId + ")");
            }
            catch (Exception e)
            {
                Error = Explain(e);
                Plugin.Logger.LogWarning("[Steam] Init failed: " + e.GetType().Name + ": " + e.Message);
            }
            return Ready;
        }

        /// <summary>Per-frame callback pump. Free when Steam was never started.</summary>
        public static void Tick()
        {
            if (!Ready) return;
            try { TickCore(); }
            catch (Exception e) { Plugin.Logger.ReportError("[Steam] Callback pump failed", e, ref _tickFailures); }
        }

        public static ulong MyId => Ready ? Guarded(MyIdCore, 0UL) : 0UL;
        public static string MyName => Ready ? Guarded(MyNameCore, "") : "";

        /// <summary>Friends currently in the game, hosts first.</summary>
        public static SteamFriendInfo[] FriendsInGame()
        {
            return Ready ? Guarded(FriendsCore, new SteamFriendInfo[0]) : new SteamFriendInfo[0];
        }

        public static string NameOf(ulong steamId)
        {
            return Ready ? Guarded(() => NameOfCore(steamId), "") : "";
        }

        /// <summary>Host: accept Steam peers and advertise the session to friends. Null on failure.</summary>
        public static IDatagramRelay OpenHost(bool friendsOnly)
        {
            if (!EnsureReady()) return null;
            try { return OpenCore(0UL, friendsOnly); }
            catch (Exception e) { Error = Explain(e); return null; }
        }

        /// <summary>Client: talk to one host. Null on failure.</summary>
        public static IDatagramRelay OpenClient(ulong hostId)
        {
            if (!EnsureReady()) return null;
            try { return OpenCore(hostId, false); }
            catch (Exception e) { Error = Explain(e); return null; }
        }

        public static void CloseRelay()
        {
            if (!Ready) return;
            try { CloseCore(); } catch { }
        }

        /// <summary>Host: change the admission rule of the open relay. Peers already in stay.</summary>
        public static void SetFriendsOnly(bool friendsOnly)
        {
            if (Ready) Guarded(() => { SetFriendsOnlyCore(friendsOnly); return true; }, false);
        }

        /// <summary>Host: show or hide the session in friends' menus (hidden while locked or full).</summary>
        public static void Advertise(bool open)
        {
            if (Ready) Guarded(() => { AdvertiseCore(open); return true; }, false);
        }

        /// <summary>Host: drop a peer and refuse it until this relay is closed.</summary>
        public static void Block(ulong steamId)
        {
            if (Ready) Guarded(() => { BlockCore(steamId); return true; }, false);
        }

        /// <summary>Client: Steam has said the host cannot be reached at all, so waiting is pointless.</summary>
        public static bool FailureIsFinal => Ready && Guarded(FailureIsFinalCore, false);

        /// <summary>The last peer-to-peer failure Steam reported, once.</summary>
        public static string TakeFailure()
        {
            return Ready ? Guarded(TakeFailureCore, null) : null;
        }

        public static void Shutdown()
        {
            if (!Ready) return;
            Ready = false;
            try { ShutdownCore(); } catch { }
        }

        private static Runtime.CoopLog.Repeat _tickFailures;

        private static T Guarded<T>(Func<T> call, T fallback)
        {
            try { return call(); }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[Steam] Call failed", e, ref _tickFailures);
                return fallback;
            }
        }

        private static string Explain(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            if (e is DllNotFoundException)
                return "steam_api64.dll was not found next to SailwindCoop.dll. Reinstall the mod with all its files.";
            if (e is FileNotFoundException || e is TypeLoadException || e is BadImageFormatException)
                return "Facepunch.Steamworks.Win64.dll was not found next to SailwindCoop.dll. Reinstall the mod with all its files.";
            if (e.Message != null && e.Message.IndexOf("SteamApi_Init", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Steam is not running, or this Steam account does not own Sailwind.";
            return "Steam is unavailable: " + e.Message;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] private static void InitCore() => SteamNative.Init();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void TickCore() => SteamNative.Tick();
        [MethodImpl(MethodImplOptions.NoInlining)] private static ulong MyIdCore() => SteamNative.MyId();
        [MethodImpl(MethodImplOptions.NoInlining)] private static string MyNameCore() => SteamNative.MyName();
        [MethodImpl(MethodImplOptions.NoInlining)] private static SteamFriendInfo[] FriendsCore() => SteamNative.FriendsInGame();
        [MethodImpl(MethodImplOptions.NoInlining)] private static string NameOfCore(ulong id) => SteamNative.NameOf(id);
        [MethodImpl(MethodImplOptions.NoInlining)] private static IDatagramRelay OpenCore(ulong host, bool friendsOnly) => SteamNative.Open(host, friendsOnly);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void CloseCore() => SteamNative.Close();
        [MethodImpl(MethodImplOptions.NoInlining)] private static string TakeFailureCore() => SteamNative.TakeFailure();
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool FailureIsFinalCore() => SteamNative.FailureIsFinal();
        [MethodImpl(MethodImplOptions.NoInlining)] private static void SetFriendsOnlyCore(bool friendsOnly) => SteamNative.SetFriendsOnly(friendsOnly);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void AdvertiseCore(bool open) => SteamNative.Advertise(open);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void BlockCore(ulong id) => SteamNative.Block(id);
        [MethodImpl(MethodImplOptions.NoInlining)] private static void ShutdownCore() => SteamNative.Shutdown();
    }

    /// <summary>Everything that names a Steamworks type. Reached only through <see cref="SteamLink"/>.</summary>
    internal static class SteamNative
    {
        /// <summary>Rich presence key a host publishes; the value is its protocol version.</summary>
        private const string PresenceKey = "sailwind_coop";

        private static bool _owned;     // we started the Steam client, so we shut it down
        private static bool _hooked;
        private static SteamRelay _relay;
        private static string _failure;
        private static bool _failureFinal;
        // Peers whose refusal is already in the log: a refused peer asks again with every packet.
        private static readonly HashSet<ulong> _refusalLogged = new HashSet<ulong>();
        // How long a quitting game waits for Steam to send what is queued (the disconnect packets).
        private const int QuitFlushMs = 300;
        // Sessions of a relay that was just closed. Closing a Steam session discards what is still
        // queued for it, and the last thing queued is LiteNetLib's disconnect packet.
        private static readonly List<ulong> _closing = new List<ulong>();
        private static readonly System.Diagnostics.Stopwatch _closingSince = new System.Diagnostics.Stopwatch();
        private const long CloseDelayMs = 1000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        public static void Init()
        {
            PreloadNative();
            // Another plugin may already have started the client through this same wrapper.
            if (!SteamClient.IsValid)
            {
                SteamClient.Init(SteamLink.AppId, false);
                _owned = true;
            }
            if (_hooked) return;
            SteamNetworking.AllowP2PPacketRelay(true);
            SteamNetworking.OnP2PSessionRequest += OnSessionRequest;
            SteamNetworking.OnP2PConnectionFailed += OnConnectionFailed;
            _hooked = true;
        }

        /// <summary>
        /// The wrapper's P/Invokes name "steam_api64", and Windows looks for it beside the executable,
        /// never in the plugin folder. Loading it once by full path makes every later call bind to it.
        /// A copy beside the executable wins.
        /// </summary>
        private static void PreloadNative()
        {
            const string name = "steam_api64.dll";
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir) && File.Exists(Path.Combine(exeDir, name))) return;
            string here = Path.GetDirectoryName(typeof(SteamNative).Assembly.Location);
            string candidate = Path.Combine(here ?? "", name);
            if (!File.Exists(candidate)) throw new DllNotFoundException(name);
            if (LoadLibraryW(candidate) == IntPtr.Zero)
                throw new DllNotFoundException(name + " (Windows error " + Marshal.GetLastWin32Error() + ")");
        }

        public static void Tick()
        {
            SteamClient.RunCallbacks();
            if (_closing.Count > 0 && _closingSince.ElapsedMilliseconds >= CloseDelayMs) FlushClosing();
        }

        private static void FlushClosing()
        {
            foreach (ulong peer in _closing) SteamNetworking.CloseP2PSessionWithUser(peer);
            _closing.Clear();
            _closingSince.Reset();
        }

        public static ulong MyId() => SteamClient.SteamId;
        public static string MyName() => SteamClient.Name ?? "";
        public static string NameOf(ulong id)
        {
            string name = new Friend(id).Name;
            // Steam answers "[unknown]" for an account it has no persona data for yet.
            return string.IsNullOrEmpty(name) || name == "[unknown]" ? "" : name;
        }

        public static SteamFriendInfo[] FriendsInGame()
        {
            string mine = Protocol.Version.ToString();
            var list = new List<SteamFriendInfo>();
            foreach (Friend friend in SteamFriends.GetFriends())
            {
                if (!friend.IsPlayingThisGame) continue;
                string presence = friend.GetRichPresence(PresenceKey);
                list.Add(new SteamFriendInfo
                {
                    Id = friend.Id,
                    Name = friend.Name ?? "",
                    Hosting = !string.IsNullOrEmpty(presence),
                    SameVersion = presence == mine,
                });
            }
            list.Sort((a, b) => a.Hosting != b.Hosting
                ? (a.Hosting ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list.ToArray();
        }

        public static IDatagramRelay Open(ulong hostId, bool friendsOnly)
        {
            Close();
            // A session left open would swallow the next request from that peer: Steam asks again only
            // for a closed one.
            FlushClosing();
            _failure = null;
            _failureFinal = false;
            _refusalLogged.Clear();
            _relay = new SteamRelay(hostId, friendsOnly);
            if (hostId == 0UL) SteamFriends.SetRichPresence(PresenceKey, Protocol.Version.ToString());
            return _relay;
        }

        public static void Close()
        {
            SteamRelay relay = _relay;
            _relay = null;
            if (relay == null) return;
            if (relay.HostId == 0UL) SteamFriends.SetRichPresence(PresenceKey, "");
            _closing.AddRange(relay.CloseAll());
            _closingSince.Restart();
        }

        public static string TakeFailure()
        {
            string failure = _failure;
            _failure = null;
            _failureFinal = false;
            return failure;
        }

        public static bool FailureIsFinal() => _failureFinal && _failure != null;

        public static void SetFriendsOnly(bool friendsOnly)
        {
            SteamRelay relay = _relay;
            if (relay == null || relay.HostId != 0UL) return;
            relay.FriendsOnly = friendsOnly;
            _refusalLogged.Clear();
        }

        public static void Advertise(bool open)
        {
            SteamRelay relay = _relay;
            if (relay == null || relay.HostId != 0UL) return;
            SteamFriends.SetRichPresence(PresenceKey, open ? Protocol.Version.ToString() : "");
        }

        public static void Block(ulong id)
        {
            SteamRelay relay = _relay;
            if (relay == null || relay.HostId != 0UL || id == 0UL) return;
            relay.Block(id);
            // The session is closed late for the same reason as in Close: the kick message is still queued.
            _closing.Add(id);
            _closingSince.Restart();
        }

        public static void Shutdown()
        {
            Close();
            if (_closing.Count > 0)
            {
                // The game is quitting and Tick will not run again. Closing the sessions now would discard
                // the disconnect packets, and the others would see this player freeze until the timeout.
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < QuitFlushMs)
                {
                    SteamClient.RunCallbacks();
                    System.Threading.Thread.Sleep(10);
                }
            }
            FlushClosing();
            if (_hooked)
            {
                SteamNetworking.OnP2PSessionRequest -= OnSessionRequest;
                SteamNetworking.OnP2PConnectionFailed -= OnConnectionFailed;
                _hooked = false;
            }
            if (_owned) SteamClient.Shutdown();
            _owned = false;
        }

        private static void OnSessionRequest(SteamId requester)
        {
            SteamRelay relay = _relay;
            if (relay == null) return;   // no session: nobody is let in
            ulong id = requester;
            bool blocked = relay.HostId == 0UL && relay.IsBlocked(id);
            bool allowed = relay.HostId != 0UL
                ? id == relay.HostId
                : !blocked && (!relay.FriendsOnly || new Friend(requester).IsFriend);
            if (!allowed)
            {
                if (_refusalLogged.Add(id))
                    Plugin.Logger.LogWarning("[Steam] Refused a connection from " + id +
                                             (relay.HostId != 0UL ? ": not the host"
                                              : blocked ? ": removed from this session by the host"
                                              : ": not a Steam friend (Steam/FriendsOnly)") +
                                             "; further attempts are not logged");
                return;
            }
            relay.Admit(id);   // before the accept: the tunnel thread may read the first packet at once
            SteamNetworking.AcceptP2PSessionWithUser(requester);
            Plugin.Logger.LogInfo("[Steam] Accepted a connection from " + new Friend(requester).Name + " (" + id + ")");
        }

        private static void OnConnectionFailed(SteamId peer, P2PSessionError error)
        {
            SteamRelay relay = _relay;
            if (relay == null) return;
            ulong id = peer;
            if (relay.HostId == 0UL)
            {
                // Revoking alone could leave the session half open: Steam asks again only for a closed
                // one, and without a new request this peer would stay unadmitted until the next Host.
                relay.Revoke(id);
                SteamNetworking.CloseP2PSessionWithUser(peer);
            }
            else
            {
                // A late report about the host of an earlier attempt must not end this one.
                if (id != relay.HostId) return;
                _failureFinal = error ==P2PSessionError.NotRunningApp ||
                                error == P2PSessionError.NoRightsToApp ||
                                error == P2PSessionError.DestinationNotLoggedIn;
            }
            _failure = "Steam could not reach " + id + ": " + Describe(error);
            Plugin.Logger.LogWarning("[Steam] " + _failure);
        }

        private static string Describe(P2PSessionError error)
        {
            switch (error)
            {
                case P2PSessionError.NotRunningApp: return "that player is not in the game";
                case P2PSessionError.NoRightsToApp: return "that account does not own Sailwind";
                case P2PSessionError.DestinationNotLoggedIn: return "that player is not signed in to Steam";
                case P2PSessionError.Timeout:
                    return "no answer (not hosting over Steam, hosting for Steam friends only and you are not " +
                           "on the host's friends list, or the connection is blocked)";
                default: return error.ToString();
            }
        }
    }

    /// <summary>
    /// Steam peer-to-peer datagrams on a channel of our own. Steam does the NAT traversal and falls back
    /// to its relay servers. Packets travel unreliable: LiteNetLib above already resends what matters.
    /// Send/TryReceive/Close run on the tunnel thread; the native API is thread-safe, and these calls
    /// avoid the wrapper's shared buffers.
    /// </summary>
    internal sealed class SteamRelay : IDatagramRelay
    {
        private const int Channel = 4703;

        private readonly HashSet<ulong> _admitted = new HashSet<ulong>();

        /// <summary>The host we talk to, or 0 when this side is the host.</summary>
        public readonly ulong HostId;
        /// <summary>Host: admit Steam friends only. Read when a session is requested, so it can change live.</summary>
        public volatile bool FriendsOnly;
        private readonly HashSet<ulong> _blocked = new HashSet<ulong>();   // under the _admitted lock

        public SteamRelay(ulong hostId, bool friendsOnly)
        {
            HostId = hostId;
            FriendsOnly = friendsOnly;
            if (hostId != 0UL) _admitted.Add(hostId);
        }

        public void Admit(ulong peer) { lock (_admitted) _admitted.Add(peer); }
        public void Revoke(ulong peer) { lock (_admitted) _admitted.Remove(peer); }
        /// <summary>Stops reading the peer's packets and refuses its next requests; sending still works.</summary>
        public void Block(ulong peer) { lock (_admitted) { _admitted.Remove(peer); _blocked.Add(peer); } }
        public bool IsBlocked(ulong peer) { lock (_admitted) return _blocked.Contains(peer); }

        public ulong[] CloseAll()
        {
            lock (_admitted)
            {
                var peers = new ulong[_admitted.Count];
                _admitted.CopyTo(peers);
                _admitted.Clear();
                return peers;
            }
        }

        public bool Send(ulong peer, byte[] data, int length)
        {
            return SteamNetworking.SendP2PPacket(peer, data, length, Channel, P2PSend.Unreliable);
        }

        public bool TryReceive(byte[] buffer, out ulong peer, out int length)
        {
            peer = 0UL;
            length = 0;
            uint size = 0;
            SteamId sender = default(SteamId);
            while (SteamNetworking.IsP2PPacketAvailable(Channel))
            {
                if (!SteamNetworking.ReadP2PPacket(buffer, ref size, ref sender, Channel)) return false;
                ulong id = sender;
                bool admitted;
                lock (_admitted) admitted = _admitted.Contains(id);
                if (!admitted || size == 0 || size > buffer.Length) continue;
                peer = id;
                length = (int)size;
                return true;
            }
            return false;
        }

        public void Close(ulong peer)
        {
            Revoke(peer);
            SteamNetworking.CloseP2PSessionWithUser(peer);
        }
    }
}
