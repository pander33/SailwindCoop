using LiteNetLib;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Thunderstore edition: <see cref="ModSync"/> without the file transfer. Mods are compared and the
    /// player is told what is missing; nothing is sent, received or installed. Compiled instead of
    /// <c>ModSync.Download.cs</c> and <c>ModTransfer.cs</c>.
    /// </summary>
    public sealed partial class ModSync
    {
        /// <summary>This build can send and receive mod files.</summary>
        public static readonly bool DownloadSupported = false;

        private static bool HostOffersDownloads => false;

        /// <summary>A full-edition client asked for a file: answer at once, so it does not wait for a stall.</summary>
        public void OnFileRequest(ModFileRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0) return;
            if (msg.Mod == ushort.MaxValue && msg.File == ushort.MaxValue) return;   // a cancel
            peer.Send(new ModFileEndMsg { Mod = msg.Mod, File = msg.File, Ok = false }, DeliveryMethod.ReliableOrdered);
        }

        public void OnFileChunk(ModFileChunkMsg msg) { }
        public void OnFileEnd(ModFileEndMsg msg) { }
        public void Download() { }

        private bool CanDownload(out int mods, out long bytes)
        {
            mods = 0; bytes = 0;
            return false;
        }

        private void DropDownload() { }
        private void DropUploads() { }
        private void DropUpload(uint netId) { }
        private void TickTransfers() { }
        private void CheckStall() { }

        private bool DownloadProgress(out string text)
        {
            text = null;
            return false;
        }
    }
}
