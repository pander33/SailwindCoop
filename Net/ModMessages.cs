using System;
using System.IO;
using LiteNetLib.Utils;

namespace SailwindCoop.Net
{
    public enum CrewNoteKind : byte { Strike, Voice, Horn }

    public enum HookAttachmentKind : byte { Item, Anchor, ClearAll }

    public sealed class HookAttachmentMsg : INetMessage
    {
        public HookAttachmentKind Kind;
        public bool Attached;
        public ushort BoatIndex;
        public int HookId, HookPrefab, ItemId, ItemPrefab;
        public MsgType Type => MsgType.HookAttachment;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind); w.Put(Attached); w.Put(BoatIndex);
            w.Put(HookId); w.Put(HookPrefab); w.Put(ItemId); w.Put(ItemPrefab);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (HookAttachmentKind)r.GetByte(); Attached = r.GetBool(); BoatIndex = r.GetUShort();
            HookId = r.GetInt(); HookPrefab = r.GetInt(); ItemId = r.GetInt(); ItemPrefab = r.GetInt();
        }
    }

    public sealed class SailDamageMsg : INetMessage
    {
        public bool ReplaceAll;
        public bool RepairAck;
        public SailDamageEntry[] Entries = Array.Empty<SailDamageEntry>();
        public MsgType Type => MsgType.SailDamage;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ReplaceAll);
            w.Put(RepairAck);
            w.Put(checked((ushort)Entries.Length));
            foreach (var entry in Entries)
            {
                w.Put(entry.Key);
                w.Put(entry.Mend);
                for (int i = 0; i < SailDamageEntry.ValueCount; i++) w.Put(entry.Values[i]);
            }
        }

        public void Deserialize(NetDataReader r)
        {
            ReplaceAll = r.GetBool();
            RepairAck = r.GetBool();
            int count = r.GetUShort();
            if (count * (sizeof(ushort) + sizeof(byte) + SailDamageEntry.ValueCount * sizeof(float)) > r.AvailableBytes)
                throw new InvalidDataException("Truncated sail damage entries");
            Entries = new SailDamageEntry[count];
            for (int i = 0; i < count; i++)
            {
                var entry = new SailDamageEntry { Key = r.GetString(512), Mend = r.GetBool() };
                for (int j = 0; j < SailDamageEntry.ValueCount; j++) entry.Values[j] = r.GetFloat();
                Entries[i] = entry;
            }
        }
    }

    public sealed class SailDamageEntry
    {
        public const int ValueCount = 6;
        public string Key = "";
        public bool Mend;
        public float[] Values = new float[ValueCount];
    }

    public abstract class PetMessage : INetMessage
    {
        public byte[] Data = Array.Empty<byte>();
        public abstract MsgType Type { get; }
        public void Serialize(NetDataWriter w) => w.PutBytesWithLength(Data);
        public void Deserialize(NetDataReader r) => Data = r.GetBytesWithLength();
    }

    public sealed class PetStateMsg : PetMessage { public override MsgType Type => MsgType.PetState; }
    public sealed class PetRequestMsg : PetMessage { public override MsgType Type => MsgType.PetRequest; }

    public sealed class PostalBoardRequestMsg : INetMessage
    {
        public uint Request;
        public int Port, Page;
        public byte Tab;
        public MsgType Type => MsgType.PostalBoardRequest;
        public void Serialize(NetDataWriter w) { w.Put(Request); w.Put(Port); w.Put(Page); w.Put(Tab); }
        public void Deserialize(NetDataReader r) { Request = r.GetUInt(); Port = r.GetInt(); Page = r.GetInt(); Tab = r.GetByte(); }
    }

    public sealed class PostalBoardMsg : INetMessage
    {
        public PostalBoardRequestMsg Address = new PostalBoardRequestMsg();
        public MissionJournalMsg Offers = new MissionJournalMsg();
        public int Total;
        public MsgType Type => MsgType.PostalBoard;
        public void Serialize(NetDataWriter w) { Address.Serialize(w); w.Put(Total); Offers.Serialize(w); }
        public void Deserialize(NetDataReader r) { Address.Deserialize(r); Total = r.GetInt(); Offers.Deserialize(r); }
    }

    public sealed class CrewNoteMsg : INetMessage
    {
        public CrewNoteKind Kind;
        public int ItemInstanceId, PrefabIndex, Note;
        public bool Playing;
        public uint Actor;
        public MsgType Type => MsgType.CrewNote;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind); w.Put(ItemInstanceId); w.Put(PrefabIndex);
            w.Put(Note); w.Put(Playing); w.Put(Actor);
        }

        public void Deserialize(NetDataReader r)
        {
            Kind = (CrewNoteKind)r.GetByte(); ItemInstanceId = r.GetInt(); PrefabIndex = r.GetInt();
            Note = r.GetInt(); Playing = r.GetBool(); Actor = r.GetUInt();
        }
    }

    [Flags]
    public enum ModFeatures : byte
    {
        None = 0,
        EconomyOverhaul = 1,
        SharedMarkets = 2,
        ShipyardExpansion = 4,
        HooksHangMore = 8,
        SailDamage = 16,
        FurstMate = 32,
        PostalExpansion = 64,
        EconomyTradeSales = 128,
    }

    public sealed class TradeSaleMsg : INetMessage
    {
        public Guid World, Sale, Author;
        public int Instance, Prefab;
        public MsgType Type => MsgType.TradeSale;

        public void Serialize(NetDataWriter w)
        {
            w.Put(World.ToByteArray()); w.Put(Sale.ToByteArray()); w.Put(Author.ToByteArray());
            w.Put(Instance); w.Put(Prefab);
        }

        public void Deserialize(NetDataReader r)
        {
            World = ReadId(r); Sale = ReadId(r); Author = ReadId(r);
            Instance = r.GetInt(); Prefab = r.GetInt();
        }

        internal static Guid ReadId(NetDataReader r)
        {
            var bytes = new byte[16];
            r.GetBytes(bytes, bytes.Length);
            return new Guid(bytes);
        }
    }

    public sealed class TradeSaleReceiptMsg : INetMessage
    {
        public Guid World, Sale;
        public MsgType Type => MsgType.TradeSaleReceipt;
        public void Serialize(NetDataWriter w) { w.Put(World.ToByteArray()); w.Put(Sale.ToByteArray()); }
        public void Deserialize(NetDataReader r) { World = TradeSaleMsg.ReadId(r); Sale = TradeSaleMsg.ReadId(r); }
    }

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
        public bool FilesOffered;
        public ModEntry[] Mods = new ModEntry[0];
        /// <summary>The host's values of settings that change the shared simulation (protocol 101).</summary>
        public ModSetting[] Settings = new ModSetting[0];
        public ModFeatures Features;

        public MsgType Type => MsgType.ModManifest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(FilesOffered);
            int mods = Math.Min(Mods.Length, ModLimits.MaxMods);
            w.Put((byte)mods);
            for (int i = 0; i < mods; i++)
            {
                ModEntry mod = Mods[i];
                w.Put(mod.Folder ?? "");
                w.Put(mod.Complete);
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
                    ModListFile file = mod.Files[f];
                    if (file.Hash == null || file.Hash.Length != ModLimits.HashBytes)
                        throw new InvalidDataException("mod list hash must be " + ModLimits.HashBytes + " bytes");
                    w.Put(file.Path ?? "");
                    w.Put(file.Size);
                    w.Put(file.Hash);
                }
            }
            int settings = Math.Min(Settings.Length, ModLimits.MaxSettings);
            w.Put((byte)settings);
            for (int s = 0; s < settings; s++)
            {
                ModSetting setting = Settings[s];
                w.Put(Clip(setting.Guid));
                w.Put(Clip(setting.Section));
                w.Put(Clip(setting.Key));
                w.Put(Clip(setting.Value));
            }
            w.Put((byte)Features);
        }

        public void Deserialize(NetDataReader r)
        {
            FilesOffered = r.GetBool();
            int mods = r.GetByte();
            if (mods > ModLimits.MaxMods) throw new InvalidDataException("too many mods");
            Mods = new ModEntry[mods];
            for (int i = 0; i < mods; i++)
            {
                var mod = new ModEntry { Folder = r.GetString(ModLimits.MaxPathChars), Complete = r.GetBool() };
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
                    throw new InvalidDataException("too many files in a listed mod");
                mod.Files = new ModListFile[files];
                for (int f = 0; f < files; f++)
                {
                    var file = new ModListFile { Path = r.GetString(ModLimits.MaxPathChars), Size = r.GetInt() };
                    if (r.AvailableBytes < ModLimits.HashBytes) throw new InvalidDataException("truncated mod list hash");
                    file.Hash = new byte[ModLimits.HashBytes];
                    r.GetBytes(file.Hash, ModLimits.HashBytes);
                    mod.Files[f] = file;
                }
                Mods[i] = mod;
            }
            int settings = r.GetByte();
            // Four strings of at least their length prefix each.
            if (settings > ModLimits.MaxSettings || settings * 4 * sizeof(ushort) > r.AvailableBytes)
                throw new InvalidDataException("too many mod settings");
            Settings = new ModSetting[settings];
            for (int s = 0; s < settings; s++)
                Settings[s] = new ModSetting
                {
                    Guid = r.GetString(ModLimits.MaxTextChars),
                    Section = r.GetString(ModLimits.MaxTextChars),
                    Key = r.GetString(ModLimits.MaxTextChars),
                    Value = r.GetString(ModLimits.MaxTextChars),
                };
            Features = (ModFeatures)r.GetByte();
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
        public ModFeatures Features;

        public MsgType Type => MsgType.ModSyncResult;

        public void Serialize(NetDataWriter w) { w.Put((byte)Decision); w.Put(Missing); w.Put(Different); w.Put((byte)Features); }
        public void Deserialize(NetDataReader r) { Decision = (ModSyncDecision)r.GetByte(); Missing = r.GetByte(); Different = r.GetByte(); Features = (ModFeatures)r.GetByte(); }
    }

#if !THUNDERSTORE
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
#endif
}
