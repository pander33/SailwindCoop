
namespace SailwindCoop.Sync
{
    /// <summary>
    /// Thunderstore edition: <see cref="ModSync"/> without the file transfer. Mods are compared and the
    /// player is told what is missing; nothing is sent, received or installed. Compiled instead of
    /// <c>ModSync.Download.cs</c> and <c>ModTransfer.cs</c>. The file messages (109-111) do not exist
    /// in this edition: one that arrives is dropped by <c>Protocol</c> as an unknown type.
    /// </summary>
    public sealed partial class ModSync
    {
        /// <summary>This build can send and receive mod files.</summary>
        public static readonly bool DownloadSupported = false;

        private static bool HostOffersDownloads => false;

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
