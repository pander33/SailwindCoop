using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Streams the host's world save to a joining client and loads it there with the client's own
    /// character profile overlaid (see <see cref="CoopProfile"/>). This is how the guest ends up in
    /// the HOST's world — the same islands, economy and (far-away) boat position — instead of needing
    /// a copy of the host's save on disk.
    ///
    /// <para><b>Host:</b> <see cref="SendSaveTo"/> serializes the host's current save file into
    /// reliable, ordered chunks (Begin → Chunk* → End).</para>
    /// <para><b>Client:</b> reassembles the bytes, deserializes the host <see cref="SaveContainer"/>,
    /// merges its profile, writes the merged save to the coop slot and triggers the game's normal
    /// load flow through <c>StartMenu</c>.</para>
    /// </summary>
    public sealed class SaveTransferSync
    {
        private const int ChunkSize = 16 * 1024;

        private readonly CoopNet _net;

        /// <summary>Which save slot (0..5) the client writes the merged host world into and loads.
        /// WARNING: this slot's local save is overwritten on the client. Default 5.</summary>
        public int CoopSlot = 5;

        // Client reassembly state.
        private byte[][] _chunks;
        private int _expectedChunks;
        private int _receivedChunks;
        private int _totalBytes;
        private int _hostGameVersion;
        private bool _receiving;

        /// <summary>Raised on the client right after the merged save has been loaded, so the runtime
        /// can flip into the "in host's world" state. Runs on the Unity main thread.</summary>
        public event Action OnSaveLoaded;

        public bool Receiving => _receiving;
        public float Progress => _expectedChunks > 0 ? (float)_receivedChunks / _expectedChunks : 0f;

        public SaveTransferSync(CoopNet net) { _net = net; }

        // -----------------------------------------------------------------
        // Host: serialize + stream the current save file.
        // -----------------------------------------------------------------

        /// <summary>Streams the given serialized SaveContainer bytes to one client.</summary>
        public void SendSaveTo(NetPeer peer, byte[] bytes)
        {
            if (peer == null || bytes == null || bytes.Length == 0)
            {
                Plugin.Logger.LogWarning("[SaveTransfer] Nothing to send to client (empty save)");
                return;
            }

            int count = (bytes.Length + ChunkSize - 1) / ChunkSize;
            peer.Send(new SaveSnapshotBeginMsg
            {
                TotalBytes = bytes.Length,
                ChunkCount = count,
                GameVersion = HostGameVersion(),
            }, DeliveryMethod.ReliableOrdered);

            for (int i = 0; i < count; i++)
            {
                int offset = i * ChunkSize;
                int len = Math.Min(ChunkSize, bytes.Length - offset);
                var slice = new byte[len];
                Buffer.BlockCopy(bytes, offset, slice, 0, len);
                peer.Send(new SaveSnapshotChunkMsg { Index = i, Data = slice }, DeliveryMethod.ReliableOrdered);
            }

            peer.Send(new SaveSnapshotEndMsg { Ok = true }, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogInfo("[SaveTransfer] Sent host save to client: " + bytes.Length +
                                  " bytes in " + count + " chunks");
        }

        /// <summary>Reads the host's current save file bytes (the world the client will join).</summary>
        public static byte[] ReadHostSaveBytes()
        {
            try
            {
                string path = SaveSlots.GetCurrentSavePath();
                if (!File.Exists(path))
                {
                    Plugin.Logger.LogWarning("[SaveTransfer] Host save file not found: " + path);
                    return null;
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[SaveTransfer] Failed to read host save: " + e);
                return null;
            }
        }

        /// <summary>True while <c>SaveLoadManager.DoSaveGame</c> is mid-write (its private <c>busy</c> flag).
        /// Used to wait for a forced host save to finish before reading the file off disk.</summary>
        public static bool HostSaveBusy()
        {
            try
            {
                var slm = SaveLoadManager.instance;
                if (slm == null) return false;
                var f = typeof(SaveLoadManager).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) return (bool)f.GetValue(slm);
            }
            catch { }
            return false;
        }

        private static int HostGameVersion()
        {
            try
            {
                var slm = SaveLoadManager.instance;
                if (slm == null) return 1;
                var f = typeof(SaveLoadManager).GetField("gameVersion", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) return (int)f.GetValue(slm);
            }
            catch { }
            return 1;
        }

        // -----------------------------------------------------------------
        // Client: reassemble, merge, load.
        // -----------------------------------------------------------------

        public void OnBegin(SaveSnapshotBeginMsg msg)
        {
            if (_net.Role != Role.Client) return;
            _expectedChunks = Mathf.Max(0, msg.ChunkCount);
            _totalBytes = Mathf.Max(0, msg.TotalBytes);
            _hostGameVersion = msg.GameVersion;
            _chunks = new byte[_expectedChunks][];
            _receivedChunks = 0;
            _receiving = true;
            Plugin.Logger.LogInfo("[SaveTransfer] Receiving host save: " + _totalBytes + " bytes, " +
                                  _expectedChunks + " chunks (gameVersion=" + _hostGameVersion + ")");
        }

        public void OnChunk(SaveSnapshotChunkMsg msg)
        {
            if (_net.Role != Role.Client || !_receiving) return;
            if (_chunks == null || msg.Index < 0 || msg.Index >= _chunks.Length) return;
            if (_chunks[msg.Index] == null) _receivedChunks++;
            _chunks[msg.Index] = msg.Data;
        }

        public void OnEnd(SaveSnapshotEndMsg msg)
        {
            if (_net.Role != Role.Client || !_receiving) return;
            _receiving = false;

            try
            {
                if (_receivedChunks != _expectedChunks)
                {
                    Plugin.Logger.LogError("[SaveTransfer] Received " + _receivedChunks + "/" +
                                           _expectedChunks + " chunks - receive aborted");
                    NotifyHostLoaded(false);
                    return;
                }

                byte[] bytes = Assemble();
                if (bytes == null || bytes.Length != _totalBytes)
                {
                    Plugin.Logger.LogError("[SaveTransfer] Assembled save size mismatch (" +
                                           (bytes?.Length ?? 0) + " != " + _totalBytes + ")");
                    NotifyHostLoaded(false);
                    return;
                }

                ApplyHostSave(bytes);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[SaveTransfer] Failed to apply host save: " + e);
                NotifyHostLoaded(false);
            }
            finally
            {
                _chunks = null;
            }
        }

        /// <summary>Client -> host: report the load outcome so the host can lift its join-pause
        /// without waiting for the safety timeout.</summary>
        private void NotifyHostLoaded(bool ok)
        {
            try { _net.Broadcast(new ClientWorldLoadedMsg { Ok = ok }, DeliveryMethod.ReliableOrdered); }
            catch (Exception e) { Plugin.Logger.LogWarning("[SaveTransfer] ClientWorldLoaded not sent: " + e.Message); }
        }

        private byte[] Assemble()
        {
            var outBytes = new byte[_totalBytes];
            int pos = 0;
            for (int i = 0; i < _chunks.Length; i++)
            {
                var c = _chunks[i];
                if (c == null) return null;
                Buffer.BlockCopy(c, 0, outBytes, pos, c.Length);
                pos += c.Length;
            }
            return outBytes;
        }

        /// <summary>Deserializes the host world, overlays the client's profile, writes the merged save
        /// to the coop slot and triggers the game's load flow.</summary>
        private void ApplyHostSave(byte[] bytes)
        {
            SaveContainer host;
            using (var ms = new MemoryStream(bytes))
            {
                host = (SaveContainer)new BinaryFormatter().Deserialize(ms);
            }

            CoopProfile.MergeInto(host);

            int slot = Mathf.Clamp(CoopSlot, 0, 5);
            string path = SaveSlots.GetSlotSavePath(slot);
            using (var fs = File.Create(path))
            {
                new BinaryFormatter().Serialize(fs, host);
            }
            Plugin.Logger.LogInfo("[SaveTransfer] Merged save written to slot " + slot + ": " + path);

            TriggerLoad(slot);
        }

        /// <summary>Drives the game's own load path so player controller, blackout and flags are set up
        /// exactly like a normal "lontinue". Requires being at the title screen (<c>StartMenu</c>):
        /// the menu silently ignores clicks while its fade animations play (<c>animsPlaying</c>), so the
        /// click is retried until the game's load coroutine actually starts (<c>GameState.currentlyLoading</c>).</summary>
        private void TriggerLoad(int slot)
        {
            var runner = CoopBehaviour.Instance;
            if (runner == null)
            {
                Plugin.Logger.LogError("[SaveTransfer] No CoopBehaviour - nothing can start the load");
                NotifyHostLoaded(false);
                return;
            }
            runner.StartCoroutine(LoadRoutine(slot));
        }

        private IEnumerator LoadRoutine(int slot)
        {
            if (GameState.playing || GameState.currentlyLoading)
            {
                // Loading a save over an already-loaded world duplicates every saved prefab — refuse.
                Plugin.Logger.LogError("[SaveTransfer] Client is already in-game - host world was not loaded. " +
                                       "Return to the main menu and reconnect.");
                NotifyHostLoaded(false);
                yield break;
            }

            SaveSlots.currentSlot = slot;
            if (SaveSlots.slotsActive != null && slot < SaveSlots.slotsActive.Length)
                SaveSlots.slotsActive[slot] = true;

            var fAnims = typeof(StartMenu).GetField("animsPlaying", BindingFlags.Instance | BindingFlags.NonPublic);

            for (float t = 0f; !GameState.currentlyLoading; t += Time.unscaledDeltaTime)
            {
                if (t >= 10f)
                {
                    Plugin.Logger.LogError("[SaveTransfer] Failed to start host world load within 10 s " +
                                           "(StartMenu busy or unavailable)");
                    NotifyHostLoaded(false);
                    yield break;
                }

                var menu = UnityEngine.Object.FindObjectOfType<StartMenu>();
                if (menu != null && AnimsPlaying(fAnims, menu) == 0)
                {
                    // Public field: without it ButtonClick treats the click as "New game" (island menu).
                    menu.selectedContinue = true;
                    try { menu.ButtonClick(slot, 0); }
                    catch (Exception e)
                    {
                        Plugin.Logger.LogError("[SaveTransfer] ButtonClick: " + e);
                        NotifyHostLoaded(false);
                        yield break;
                    }
                    // LoadGameAnimation sets currentlyLoading synchronously; if it didn't, retry next frame.
                    if (GameState.currentlyLoading) break;
                }

                yield return null;
            }
            Plugin.Logger.LogInfo("[SaveTransfer] Started host world load through menu (slot " + slot + ")");

            // The load itself takes a few seconds (blackout + LoadGame); report once the world is up.
            for (float t = 0f; !GameState.playing && t < 60f; t += Time.unscaledDeltaTime)
                yield return null;

            NotifyHostLoaded(GameState.playing);
            if (GameState.playing)
                OnSaveLoaded?.Invoke();
            else
                Plugin.Logger.LogWarning("[SaveTransfer] Load started, but the world still did not come up within 60 s");
        }

        private static int AnimsPlaying(FieldInfo f, StartMenu menu)
        {
            try { return f != null ? (int)f.GetValue(menu) : 0; }
            catch { return 0; }
        }

        public void Reset()
        {
            _chunks = null;
            _receiving = false;
            _receivedChunks = 0;
            _expectedChunks = 0;
            _totalBytes = 0;
        }
    }
}
