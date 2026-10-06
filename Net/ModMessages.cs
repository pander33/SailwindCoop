using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    // ---------------------------------------------------------------------
    // Mod sharing. After HelloAck the host sends the list of its shared mods and
    // holds the world transfer until the client answers. A client that lacks some
    // of them may download the files (by manifest index, never by path), install
    // them and restart; nothing is installed without the player's consent.
    // ---------------------------------------------------------------------

    /// <summary>Host -> client: the host's shared mods. Sent once, right after HelloAck.</summary>
    public sealed class ModManifestMsg : INetMessage
    {
        /// <summary>The host lets clients download the listed files.</summary>
        public bool DownloadAllowed;
        public ModEntry[] Mods = new ModEntry[0];

        public MsgType Type => MsgType.ModManifest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(DownloadAllowed);
            int mods = Math.Min(Mods.Length, ModLimits.MaxMods);
            w.Put((byte)mods);
            for (int i = 0; i < mods; i++)
            {
                ModEntry mod = Mods[i];
                w.Put(mod.Folder ?? "");
                w.Put(mod.Downloadable);
                int plugins = Math.Min(mod.Plugins.Length, ModLimits.MaxPluginsPerMod);
                w.Put((byte)plugins);
                for (int p = 0; p < plugins; p++)
                {
                    w.Put(Clip(mod.Plugins[p].Guid));
                    w.Put(Clip(mod.Plugins[p].Name));
                    w.Put(Clip(mod.Plugins[p].Version));
                }
                int files = Math.Min(mod.Files.Length, ModLimits.MaxFilesPerMod);
                w.Put((ushort)files);
                for (int f = 0; f < files; f++)
                {
                    ModFile file = mod.Files[f];
                    if (file.Hash == null || file.Hash.Length != ModLimits.HashBytes)
                        throw new InvalidDataException("mod file hash must be " + ModLimits.HashBytes + " bytes");
                    w.Put(file.Path ?? "");
                    w.Put(file.Size);
                    w.Put(file.Hash);
                }
            }
        }

        public void Deserialize(NetDataReader r)
        {
            DownloadAllowed = r.GetBool();
            int mods = r.GetByte();
            if (mods > ModLimits.MaxMods) throw new InvalidDataException("too many mods");
            Mods = new ModEntry[mods];
            for (int i = 0; i < mods; i++)
            {
                var mod = new ModEntry { Folder = r.GetString(ModLimits.MaxPathChars), Downloadable = r.GetBool() };
                int plugins = r.GetByte();
                if (plugins > ModLimits.MaxPluginsPerMod) throw new InvalidDataException("too many plugins");
                mod.Plugins = new ModPlugin[plugins];
                for (int p = 0; p < plugins; p++)
                    mod.Plugins[p] = new ModPlugin
                    {
                        Guid = r.GetString(ModLimits.MaxTextChars),
                        Name = r.GetString(ModLimits.MaxTextChars),
                        Version = r.GetString(ModLimits.MaxTextChars),
                    };
                int files = r.GetUShort();
                // A file takes at least its hash on the wire: refuse a count the packet cannot hold
                // before sizing the array from it.
                if (files > ModLimits.MaxFilesPerMod || files * ModLimits.HashBytes > r.AvailableBytes)
                    throw new InvalidDataException("too many mod files");
                mod.Files = new ModFile[files];
                for (int f = 0; f < files; f++)
                {
                    var file = new ModFile { Path = r.GetString(ModLimits.MaxPathChars), Size = r.GetInt() };
                    if (r.AvailableBytes < ModLimits.HashBytes) throw new InvalidDataException("truncated mod file hash");
                    file.Hash = new byte[ModLimits.HashBytes];
                    r.GetBytes(file.Hash, ModLimits.HashBytes);
                    mod.Files[f] = file;
                }
                Mods[i] = mod;
            }
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= ModLimits.MaxTextChars ? text : text.Substring(0, ModLimits.MaxTextChars);
        }
    }

    public enum ModSyncDecision : byte
    {
        /// <summary>Go on with the join: send the world.</summary>
        Proceed = 0,
        /// <summary>The client is leaving (cancelled, or installed mods and must restart).</summary>
        Abort = 1,
    }

    /// <summary>Client -> host: the answer to <see cref="ModManifestMsg"/>. The counts are for the
    /// host's log and notice only; the host does not act on them.</summary>
    public sealed class ModSyncResultMsg : INetMessage
    {
        public ModSyncDecision Decision;
        public byte Missing;
        public byte Different;

        public MsgType Type => MsgType.ModSyncResult;

        public void Serialize(NetDataWriter w) { w.Put((byte)Decision); w.Put(Missing); w.Put(Different); }
        public void Deserialize(NetDataReader r) { Decision = (ModSyncDecision)r.GetByte(); Missing = r.GetByte(); Different = r.GetByte(); }
    }

    /// <summary>Client -> host: send me file <see cref="File"/> of mod <see cref="Mod"/>, both
    /// indices into the manifest the host sent.</summary>
    public sealed class ModFileRequestMsg : INetMessage
    {
        public ushort Mod;
        public ushort File;

        public MsgType Type => MsgType.ModFileRequest;

        public void Serialize(NetDataWriter w) { w.Put(Mod); w.Put(File); }
        public void Deserialize(NetDataReader r) { Mod = r.GetUShort(); File = r.GetUShort(); }
    }

    /// <summary>Host -> client: one ordered chunk of a requested mod file.</summary>
    public sealed class ModFileChunkMsg : INetMessage
    {
        public ushort Mod;
        public ushort File;
        public int Index;
        public byte[] Data = Array.Empty<byte>();

        public MsgType Type => MsgType.ModFileChunk;

        public void Serialize(NetDataWriter w)
        {
            if (Data.Length > ModLimits.ChunkSize) throw new InvalidDataException("mod chunk too large");
            w.Put(Mod); w.Put(File); w.Put(Index);
            w.Put((ushort)Data.Length); w.Put(Data);
        }

        public void Deserialize(NetDataReader r)
        {
            Mod = r.GetUShort(); File = r.GetUShort(); Index = r.GetInt();
            int size = r.GetUShort();
            if (size > ModLimits.ChunkSize || size > r.AvailableBytes) throw new InvalidDataException("truncated mod chunk");
            Data = new byte[size]; r.GetBytes(Data, size);
        }
    }

    /// <summary>Host -> client: the requested file is complete (<see cref="Ok"/>) or could not be
    /// sent (sharing is off, the file changed on disk, a bad index).</summary>
    public sealed class ModFileEndMsg : INetMessage
    {
        public ushort Mod;
        public ushort File;
        public bool Ok;

        public MsgType Type => MsgType.ModFileEnd;

        public void Serialize(NetDataWriter w) { w.Put(Mod); w.Put(File); w.Put(Ok); }
        public void Deserialize(NetDataReader r) { Mod = r.GetUShort(); File = r.GetUShort(); Ok = r.GetBool(); }
    }
}
