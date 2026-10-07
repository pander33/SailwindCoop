using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The file transfer half of <see cref="ModSync"/>: the host streams the files of a mod a client
    /// lacks, the client stages, checks and installs them. Full edition only; the Thunderstore edition
    /// compiles <c>ModSync.NoDownload.cs</c> in its place and contains none of this.
    /// </summary>
    public sealed partial class ModSync
    {
        /// <summary>This build can send and receive mod files.</summary>
        public static readonly bool DownloadSupported = true;

        private static bool HostOffersDownloads => Plugin.Cfg.ShareMods.Value;

        private const int ChunksPerFrame = 8;
        private const float StallSeconds = 20f;
        private const ushort CancelIndex = ushort.MaxValue;

        private readonly Dictionary<uint, Upload> _uploads = new Dictionary<uint, Upload>();
        private readonly List<uint> _finished = new List<uint>();

        private ModDownload _download;
        private float _lastDataTime;
        private bool _leaveAfterInstall;

        private sealed class Upload
        {
            public NetPeer Peer;
            public ModUpload File;
        }

        private static string StagingRoot => Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "SailwindCoop"), "modsync");

        public void OnFileRequest(ModFileRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint netId = _net.PlayerNetIdForPeer(peer);
            if (netId == 0) return;
            if (_uploads.TryGetValue(netId, out Upload previous))
            {
                previous.File.Dispose();
                _uploads.Remove(netId);
            }
            if (msg.Mod == CancelIndex && msg.File == CancelIndex) return;
            try
            {
                if (!Plugin.Cfg.ShareMods.Value) throw new InvalidOperationException("mod sharing is off");
                if (msg.Mod >= _hostMods.Length) throw new IndexOutOfRangeException("mod index " + msg.Mod);
                ModEntry entry = _hostMods[msg.Mod];
                if (!entry.Downloadable || msg.File >= entry.Files.Length)
                    throw new IndexOutOfRangeException("file index " + msg.File + " of mod " + msg.Mod);
                string root = string.IsNullOrEmpty(entry.Folder) ? PluginsRoot : Path.Combine(PluginsRoot, entry.Folder);
                string path = ModPathRules.Resolve(root, entry.Files[msg.File].Path);
                _uploads[netId] = new Upload { Peer = peer, File = new ModUpload(path, entry.Files[msg.File], msg.Mod, msg.File) };
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[Mods] role=Host file request mod=" + msg.Mod + " file=" + msg.File +
                                         " from NetId=" + netId + " refused: " + e.Message);
                peer.Send(new ModFileEndMsg { Mod = msg.Mod, File = msg.File, Ok = false }, DeliveryMethod.ReliableOrdered);
            }
        }

        private void TickUploads()
        {
            _finished.Clear();
            foreach (var pair in _uploads)
            {
                Upload upload = pair.Value;
                ModUpload file = upload.File;
                try
                {
                    if (upload.Peer == null || upload.Peer.ConnectionState != ConnectionState.Connected)
                    {
                        _finished.Add(pair.Key);
                        continue;
                    }
                    for (int i = 0; i < ChunksPerFrame && !file.Done; i++)
                    {
                        byte[] data = file.NextChunk(out int index);
                        upload.Peer.Send(new ModFileChunkMsg { Mod = file.Mod, File = file.File, Index = index, Data = data },
                                         DeliveryMethod.ReliableOrdered);
                    }
                    if (!file.Done) continue;
                    upload.Peer.Send(new ModFileEndMsg { Mod = file.Mod, File = file.File, Ok = true }, DeliveryMethod.ReliableOrdered);
                    _finished.Add(pair.Key);
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[Mods] role=Host sending mod=" + file.Mod + " file=" + file.File +
                                             " to NetId=" + pair.Key + " failed: " + e.Message);
                    try { upload.Peer.Send(new ModFileEndMsg { Mod = file.Mod, File = file.File, Ok = false }, DeliveryMethod.ReliableOrdered); }
                    catch { }
                    _finished.Add(pair.Key);
                }
            }
            foreach (uint netId in _finished)
            {
                _uploads[netId].File.Dispose();
                _uploads.Remove(netId);
            }
        }

        /// <summary>Menu: download every missing mod the host offers.</summary>
        public void Download()
        {
            if ((_phase != Phase.Deciding && _phase != Phase.Failed) || !CanDownload(out _, out _)) return;
            try
            {
                var wanted = new List<KeyValuePair<int, ModEntry>>();
                foreach (ModDiff diff in _diffs)
                    if (diff.Kind == ModDiffKind.Missing && diff.Entry.Downloadable)
                        wanted.Add(new KeyValuePair<int, ModEntry>(diff.Index, diff.Entry));
                _download = new ModDownload(StagingRoot, PluginsRoot, wanted);
                _phase = Phase.Downloading;
                _message = "";
                RequestNext();
            }
            catch (Exception e) { Fail(e.Message); }
        }

        public void OnFileChunk(ModFileChunkMsg msg)
        {
            if (_phase != Phase.Downloading || _download == null) return;
            try
            {
                _download.OnChunk(msg.Mod, msg.File, msg.Index, msg.Data);
                _lastDataTime = Time.realtimeSinceStartup;
            }
            catch (Exception e) { Fail(e.Message); }
        }

        public void OnFileEnd(ModFileEndMsg msg)
        {
            if (_phase != Phase.Downloading || _download == null) return;
            try
            {
                _download.OnEnd(msg.Mod, msg.File, msg.Ok);
                RequestNext();
            }
            catch (Exception e) { Fail(e.Message); }
        }

        private void RequestNext()
        {
            _lastDataTime = Time.realtimeSinceStartup;
            if (_download.TryNext(out ushort mod, out ushort file))
            {
                _net.Broadcast(new ModFileRequestMsg { Mod = mod, File = file }, DeliveryMethod.ReliableOrdered);
                return;
            }

            List<string> installed = _download.Commit(_net.HostLabel);
            DropDownload();
            _phase = Phase.RestartRequired;
            _installedNames = string.Join(", ", installed.ToArray());
            _message = "Mods installed from the host: " + _installedNames +
                       ". You are not connected yet: quit the game, start it again, then join the host again.";
            Plugin.Logger.LogInfo("[Mods] role=Client " + _message);
            SendResult(ModSyncDecision.Abort);
            // This runs inside the receive callback; the session is torn down from Tick instead.
            _leaveAfterInstall = true;
        }

        private void Fail(string reason)
        {
            Plugin.Logger.LogError("[Mods] role=Client mod download failed, nothing was installed: " + reason);
            bool wasDownloading = _download != null;
            DropDownload();
            _phase = Phase.Failed;
            _message = "Mod download failed, nothing was installed: " + reason;
            // Stop the host's stream so a retry does not meet chunks of the abandoned file.
            if (wasDownloading)
            {
                try { _net.Broadcast(new ModFileRequestMsg { Mod = CancelIndex, File = CancelIndex }, DeliveryMethod.ReliableOrdered); }
                catch { }
            }
        }

        private void DropDownload()
        {
            if (_download == null) return;
            _download.Dispose();
            _download = null;
        }

        private bool CanDownload(out int mods, out long bytes)
        {
            mods = 0; bytes = 0;
            foreach (ModDiff diff in _diffs)
            {
                if (diff.Kind != ModDiffKind.Missing || !diff.Entry.Downloadable) continue;
                mods++; bytes += diff.Entry.TotalBytes;
            }
            return mods > 0 && _downloadAllowed && Plugin.Cfg.AllowModDownload.Value;
        }

        private void DropUploads()
        {
            foreach (Upload upload in _uploads.Values) upload.File.Dispose();
            _uploads.Clear();
        }

        private void DropUpload(uint netId)
        {
            if (!_uploads.TryGetValue(netId, out Upload upload)) return;
            upload.File.Dispose();
            _uploads.Remove(netId);
        }

        private void TickTransfers()
        {
            if (_uploads.Count > 0) TickUploads();

            if (_leaveAfterInstall)
            {
                _leaveAfterInstall = false;
                ClientLeave?.Invoke("mods-installed");
                CoopBehaviour.Notice(_message);
            }
        }

        private void CheckStall()
        {
            if (_phase == Phase.Downloading && Time.realtimeSinceStartup - _lastDataTime > StallSeconds)
                Fail("the host stopped sending data");
        }

        private bool DownloadProgress(out string text)
        {
            text = _download == null ? null
                : "Downloading mods from the host: " + Megabytes(_download.ReceivedBytes) + " of " +
                  Megabytes(_download.TotalBytes) + " MB";
            return text != null;
        }
    }
}
