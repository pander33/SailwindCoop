using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using LiteNetLib;
using SailwindCoop.Net;
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
                Plugin.Logger.LogWarning("[SaveTransfer] Нечего отправлять клиенту (пустой сейв)");
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
            Plugin.Logger.LogInfo("[SaveTransfer] Отправлен сейв хоста клиенту: " + bytes.Length +
                                  " байт в " + count + " чанках");
        }

        /// <summary>Reads the host's current save file bytes (the world the client will join).</summary>
        public static byte[] ReadHostSaveBytes()
        {
            try
            {
                string path = SaveSlots.GetCurrentSavePath();
                if (!File.Exists(path))
                {
                    Plugin.Logger.LogWarning("[SaveTransfer] Файл сейва хоста не найден: " + path);
                    return null;
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[SaveTransfer] Не удалось прочитать сейв хоста: " + e);
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
            Plugin.Logger.LogInfo("[SaveTransfer] Приём сейва хоста: " + _totalBytes + " байт, " +
                                  _expectedChunks + " чанков (gameVersion=" + _hostGameVersion + ")");
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
                    Plugin.Logger.LogError("[SaveTransfer] Получено " + _receivedChunks + "/" +
                                           _expectedChunks + " чанков — приём сорван");
                    return;
                }

                byte[] bytes = Assemble();
                if (bytes == null || bytes.Length != _totalBytes)
                {
                    Plugin.Logger.LogError("[SaveTransfer] Размер собранного сейва не совпадает (" +
                                           (bytes?.Length ?? 0) + " != " + _totalBytes + ")");
                    return;
                }

                ApplyHostSave(bytes);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[SaveTransfer] Ошибка применения сейва хоста: " + e);
            }
            finally
            {
                _chunks = null;
            }
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
            Plugin.Logger.LogInfo("[SaveTransfer] Merged-сейв записан в слот " + slot + ": " + path);

            TriggerLoad(slot);
        }

        /// <summary>Drives the game's own load path so player controller, blackout and flags are set up
        /// exactly like a normal "Continue". Requires being at the title screen (<c>StartMenu</c>).</summary>
        private void TriggerLoad(int slot)
        {
            try
            {
                SaveSlots.currentSlot = slot;
                if (SaveSlots.slotsActive != null && slot < SaveSlots.slotsActive.Length)
                    SaveSlots.slotsActive[slot] = true;

                var menu = UnityEngine.Object.FindObjectOfType<StartMenu>();
                if (menu != null && !GameState.playing)
                {
                    var f = typeof(StartMenu).GetField("selectedContinue", BindingFlags.Instance | BindingFlags.NonPublic);
                    f?.SetValue(menu, true);
                    menu.ButtonClick(slot, 0);
                    Plugin.Logger.LogInfo("[SaveTransfer] Запущена загрузка мира хоста через меню (слот " + slot + ")");
                }
                else
                {
                    Plugin.Logger.LogWarning("[SaveTransfer] StartMenu недоступен или игра уже идёт — " +
                                             "клиент должен подключаться из главного меню. Прямая загрузка.");
                    SaveLoadManager.instance?.LoadGame(0);
                }

                OnSaveLoaded?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[SaveTransfer] Не удалось запустить загрузку: " + e);
            }
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
