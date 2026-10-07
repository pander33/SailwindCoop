using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>What the co-op menu shows about mods. Built once per GUI layout pass.</summary>
    public struct ModSyncView
    {
        public string Text;
        /// <summary>The join is waiting for the player's answer: show the decision buttons.</summary>
        public bool Deciding;
        public bool CanDownload;
        public string DownloadLabel;
    }

    /// <summary>
    /// Mod sharing between host and guests. The step sits between the handshake and the world
    /// transfer: the host sends the list of its shared mods and starts streaming the save only after
    /// the client answers. A client with the same mods answers by itself; one that lacks some is asked
    /// in the co-op menu and may download them, which ends the session — the loader reads plugins
    /// once at start-up, so the new mods need a game restart.
    ///
    /// <para>Downloaded DLLs run with the player's full rights, so nothing is installed without an
    /// explicit click, and never over an existing file. The host does not enforce its mod list: a
    /// client that declines still joins.</para>
    ///
    /// <para>The file transfer lives in <c>ModSync.Download.cs</c> and is not part of the Thunderstore
    /// edition, which compiles <c>ModSync.NoDownload.cs</c> instead: that edition only compares the
    /// lists and tells the player what is missing.</para>
    /// </summary>
    public sealed partial class ModSync
    {
        private enum Phase { Idle, Deciding, Downloading, Failed, RestartRequired }

        private static readonly string[] BuiltInExcludes = { Plugin.Guid, "com.sailwind.coop.devconsole" };

        private readonly CoopNet _net;

        /// <summary>Host: the client answered — start its world transfer.</summary>
        public Action<NetPeer, uint> HostProceed;
        /// <summary>Client: leave the session (the argument is the log reason).</summary>
        public Action<string> ClientLeave;
        /// <summary>Client: a decision is waiting in the co-op menu.</summary>
        public Action NeedsAttention;

        // Host.
        private ModEntry[] _hostMods = new ModEntry[0];
        private readonly HashSet<uint> _awaiting = new HashSet<uint>();

        // Client.
        private Phase _phase;
        private bool _downloadAllowed;
        private List<ModDiff> _diffs = new List<ModDiff>();
        private List<string> _localOnly = new List<string>();
        private string _message = "";
        private string _installedNames = "";

        /// <summary>Client: mods were installed in this run of the game. Joining is pointless until the
        /// game is restarted — the loader has not read them, so the host would offer them again.</summary>
        public bool RestartRequired => _phase == Phase.RestartRequired;
        /// <summary>The mods installed in this run, for the restart notice.</summary>
        public string InstalledNames => _installedNames;


        public ModSync(CoopNet net) { _net = net; }

        private static string PluginsRoot => BepInEx.Paths.PluginPath;

        // -----------------------------------------------------------------
        // Session lifecycle
        // -----------------------------------------------------------------

        /// <summary>Host: hash the shared mods once, when the session starts.</summary>
        public void BeginHost()
        {
            Reset();
            _phase = Phase.Idle; _message = "";
            try
            {
                var mods = ModCatalog.Build(PluginsRoot, LoadedPlugins(), Excluded(),
                    text => Plugin.Logger.LogWarning("[Mods] role=Host " + text));
                _hostMods = mods.ToArray();
#if !THUNDERSTORE
                Plugin.Logger.LogInfo("[Mods] role=Host manifest built: " + _hostMods.Length + " mods, sharing " +
                                      (HostOffersDownloads ? "on" : "off"));
#else
                Plugin.Logger.LogInfo("[Mods] role=Host manifest built: " + _hostMods.Length + " mods");
#endif
            }
            catch (Exception e)
            {
                _hostMods = new ModEntry[0];
                Plugin.Logger.LogError("[Mods] role=Host could not list mods, clients will not be told about them: " + e);
            }
        }

        public void BeginClient()
        {
            Reset();
            if (_phase != Phase.RestartRequired) { _phase = Phase.Idle; _message = ""; }
        }

        /// <summary>Session teardown. A finished install keeps its "restart the game" text.</summary>
        public void Reset()
        {
            DropUploads();
            _awaiting.Clear();
            _hostMods = new ModEntry[0];
            DropDownload();
            _diffs = new List<ModDiff>();
            _localOnly = new List<string>();
            if (_phase != Phase.RestartRequired) { _phase = Phase.Idle; _message = ""; }
        }

        public void ClearRemoteActor(uint netId)
        {
            _awaiting.Remove(netId);
            DropUpload(netId);
        }

        // -----------------------------------------------------------------
        // Host
        // -----------------------------------------------------------------

        /// <summary>Host: a client finished the handshake. Sends the manifest; the world transfer
        /// starts when the client answers. Any failure here falls through to the transfer, so a
        /// broken mod list can never block a join.</summary>
        public void OnClientReady(PeerSession session)
        {
            try
            {
                session.Peer.Send(new ModManifestMsg { DownloadAllowed = HostOffersDownloads, Mods = _hostMods },
                                  DeliveryMethod.ReliableOrdered);
                _awaiting.Add(session.PlayerNetId);
                _net.SetMemberState(session.PlayerNetId, MemberJoinState.CheckingMods);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[Mods] role=Host manifest not sent to NetId=" + session.PlayerNetId +
                                       ", joining without the mod check: " + e);
                _awaiting.Remove(session.PlayerNetId);
                HostProceed?.Invoke(session.Peer, session.PlayerNetId);
            }
        }

        public void OnResult(ModSyncResultMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint netId = _net.PlayerNetIdForPeer(peer);
            // One answer per join: a repeated Proceed must not start a second world transfer.
            if (netId == 0 || !_awaiting.Remove(netId)) return;
            ClearRemoteActor(netId);
            if (msg.Decision != ModSyncDecision.Proceed)
            {
                Plugin.Logger.LogInfo("[Mods] role=Host NetId=" + netId + " left at the mod check");
                return;
            }
            if (msg.Missing > 0 || msg.Different > 0)
            {
                Plugin.Logger.LogWarning("[Mods] role=Host NetId=" + netId + " joins without " + msg.Missing +
                                         " shared mods, " + msg.Different + " differ");
                CoopBehaviour.Notice(_net.GetPlayerName(netId) + " joined with a different mod set (" + msg.Missing +
                                     " missing, " + msg.Different + " different). Some things may not match.");
            }
            HostProceed?.Invoke(peer, netId);
        }

        // -----------------------------------------------------------------
        // Per-frame
        // -----------------------------------------------------------------

        public void Tick()
        {
            TickTransfers();

            if (_phase == Phase.Deciding || _phase == Phase.Downloading || _phase == Phase.Failed)
            {
                if (_net.Role != Role.Client || _net.State != LinkState.Connected)
                {
                    DropDownload();
                    _phase = Phase.Idle;
                    _message = "The connection ended before the mod check finished.";
                }
                else CheckStall();
            }
        }

        // -----------------------------------------------------------------
        // Client
        // -----------------------------------------------------------------

        /// <summary>Client: the host's mod list arrived. Answers at once when nothing differs.
        /// Any failure answers Proceed too — the check must never be the reason a join hangs.</summary>
        public void OnManifest(ModManifestMsg msg)
        {
            if (_net.Role != Role.Client) return;
            try
            {
                DropDownload();
                _downloadAllowed = msg.DownloadAllowed;
                var mods = new List<ModEntry>(msg.Mods);
#if !THUNDERSTORE
                foreach (ModEntry entry in mods)
                {
                    string problem = ModPathRules.Validate(entry);
                    if (problem == null) continue;
                    Plugin.Logger.LogWarning("[Mods] role=Client host mod '" + entry.DisplayName +
                                             "' has an invalid " + problem + "; it cannot be downloaded");
                    entry.Downloadable = false;
                }
#endif
                List<LoadedPlugin> local = LoadedPlugins();
                _diffs = ModCatalog.Compare(mods, PluginsRoot, local);
                _localOnly = ModCatalog.LocalOnly(mods, local, Excluded());

                Count(out int missing, out int different);
                Plugin.Logger.LogInfo("[Mods] role=Client host lists " + mods.Count + " mods: " + missing + " missing, " +
                                      different + " different, " + _localOnly.Count + " only here");
                if (missing == 0 && different == 0)
                {
                    _phase = Phase.Idle;
                    _message = _localOnly.Count == 0 ? ""
                        : "You have mods the host does not share: " + string.Join(", ", _localOnly.ToArray()) + ".";
                    SendResult(ModSyncDecision.Proceed);
                    return;
                }
                _phase = Phase.Deciding;
                _message = "";
                NeedsAttention?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[Mods] role=Client mod check failed, joining without it: " + e);
                _phase = Phase.Idle;
                _diffs = new List<ModDiff>();
                SendResult(ModSyncDecision.Proceed);
            }
        }

        /// <summary>Menu: join with the mods this machine already has.</summary>
        public void JoinAnyway()
        {
            if (_phase != Phase.Deciding && _phase != Phase.Failed) return;
            DropDownload();
            Count(out int missing, out int different);
            _phase = Phase.Idle;
            _message = "Joined with a different mod set (" + missing + " missing, " + different +
                       " different). Some things may not match the host.";
            SendResult(ModSyncDecision.Proceed);
        }

        /// <summary>Menu: do not join.</summary>
        public void Cancel()
        {
            if (_phase != Phase.Deciding && _phase != Phase.Failed) return;
            DropDownload();
            _phase = Phase.Idle;
            _message = "";
            SendResult(ModSyncDecision.Abort);
            ClientLeave?.Invoke("mods-cancelled");
        }

        private void SendResult(ModSyncDecision decision)
        {
            Count(out int missing, out int different);
            try
            {
                _net.Broadcast(new ModSyncResultMsg
                {
                    Decision = decision,
                    Missing = (byte)Math.Min(missing, 255),
                    Different = (byte)Math.Min(different, 255),
                }, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Mods] role=Client answer not sent: " + e.Message); }
        }

        private void Count(out int missing, out int different)
        {
            missing = 0; different = 0;
            foreach (ModDiff diff in _diffs)
            {
                if (diff.Kind == ModDiffKind.Missing) missing++;
                else if (diff.Kind == ModDiffKind.Different) different++;
            }
        }

        // -----------------------------------------------------------------
        // Menu
        // -----------------------------------------------------------------

        public ModSyncView BuildView()
        {
#if !THUNDERSTORE
            var view = new ModSyncView { Text = _message ?? "", DownloadLabel = "Download" };
#else
            var view = new ModSyncView { Text = _message ?? "" };
#endif
            if (_net.Role == Role.Host)
            {
#if !THUNDERSTORE
                view.Text = _hostMods.Length == 0 ? "No mods to tell joining players about."
                    : (HostOffersDownloads ? "Joining players can download: "
                       : "Joining players are told about (downloads off): ") + Names(_hostMods);
#else
                view.Text = _hostMods.Length == 0 ? "No mods to tell joining players about."
                    : "Joining players are told which of these they lack: " + Names(_hostMods);
#endif
                return view;
            }
            if (_phase == Phase.Downloading && DownloadProgress(out string progress))
            {
                view.Text = progress;
                return view;
            }
#if !THUNDERSTORE
            if (_phase == Phase.RestartRequired)
            {
                // The restart notice at the top of the menu carries the instructions.
                view.Text = "Installed, waiting for a game restart: " + _installedNames;
                return view;
            }
#endif
            if (_phase != Phase.Deciding && _phase != Phase.Failed) return view;

            var text = new StringBuilder();
            if (_phase == Phase.Failed) text.Append(_message).Append('\n');
            text.Append("Host ").Append(_net.HostLabel).Append(" uses mods that differ from yours:");
            foreach (ModDiff diff in _diffs)
            {
                if (diff.Kind == ModDiffKind.Same) continue;
                ModEntry entry = diff.Entry;
                text.Append('\n');
                if (diff.Kind == ModDiffKind.Missing)
                {
                    text.Append("  + ").Append(entry.DisplayName).Append(' ')
                        .Append(entry.Plugins.Length > 0 ? entry.Plugins[0].Version : "");
                    if (DownloadSupported && entry.Downloadable)
                        text.Append(" (").Append(Megabytes(entry.TotalBytes)).Append(" MB, ").Append(entry.Files.Length)
                            .Append(" files, sha256 ").Append(MainHash(entry)).Append(')');
                    else text.Append(" (install it yourself)");
                }
                else text.Append("  ~ ").Append(entry.DisplayName).Append(": ").Append(diff.Detail).Append(" (update it yourself)");
            }
            if (_localOnly.Count > 0)
                text.Append("\nOnly you have: ").Append(string.Join(", ", _localOnly.ToArray()));

            view.Deciding = true;
            view.CanDownload = CanDownload(out int mods, out long bytes);
#if THUNDERSTORE
            text.Append("\nInstall the missing mods yourself, restart the game and join again. " +
                        "\"Join anyway\" joins with the mods you have now.");
#else
            if (mods > 0)
            {
                view.DownloadLabel = "Download " + mods + " (" + Megabytes(bytes) + " MB)";
                text.Append(view.CanDownload
                    ? "\nMods run code on your PC with your rights. Download only from a host you trust.\nAfter the download you must RESTART THE GAME and join again."
                    : !_downloadAllowed ? "\nThe host does not offer downloads (ShareMods is off on the host)."
                    : "\nDownloads are disabled in your config (AllowModDownload).");
            }
#endif
            view.Text = text.ToString();
            return view;
        }

        private static string MainHash(ModEntry entry)
        {
            foreach (ModFile file in entry.Files)
                if (ModCatalog.IsDll(file.Path)) return ModCatalog.ShortHash(file.Hash);
            return entry.Files.Length > 0 ? ModCatalog.ShortHash(entry.Files[0].Hash) : "";
        }

        private static string Names(ModEntry[] mods)
        {
            var names = new string[mods.Length];
            for (int i = 0; i < mods.Length; i++) names[i] = mods[i].DisplayName;
            return string.Join(", ", names);
        }

        private static string Megabytes(long bytes) =>
            (bytes / (1024f * 1024f)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        // -----------------------------------------------------------------
        // Loader
        // -----------------------------------------------------------------

        private static List<LoadedPlugin> LoadedPlugins()
        {
            var list = new List<LoadedPlugin>();
            foreach (var pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo info = pair.Value;
                if (info == null || info.Metadata == null || string.IsNullOrEmpty(info.Location)) continue;
                list.Add(new LoadedPlugin
                {
                    Guid = info.Metadata.GUID ?? "",
                    Name = info.Metadata.Name ?? "",
                    Version = info.Metadata.Version != null ? info.Metadata.Version.ToString() : "",
                    Location = info.Location,
                });
            }
            return list;
        }

        /// <summary>GUIDs that are never listed, compared or offered: this mod itself and the
        /// personal ones named in the config (a UI translator is nobody else's business).</summary>
        private static List<string> Excluded()
        {
            var list = new List<string>(BuiltInExcludes);
            string configured = Plugin.Cfg.ModSyncExclude.Value ?? "";
            foreach (string part in configured.Split(',', ';', ' ', '\t'))
                if (part.Length > 0) list.Add(part);
            return list;
        }
    }
}
