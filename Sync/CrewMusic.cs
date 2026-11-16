using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;

namespace SailwindCoop.Sync
{
    internal static class CrewMusic
    {
        internal static CoopNet Net => CoopBehaviour.Instance?.Net;
        internal static bool Active => CoopBehaviour.Instance?.PlayingCoop == true;

        internal static void Apply(Harmony harmony)
        {
            VikingHornCompat.Init();
            VikingHornCompat.ApplyPatches(harmony);
            MusicModCompat.Init();
            MusicModCompat.ApplyPatches(harmony);
        }

        internal static bool Send(ShipItem item, CrewNoteKind kind, int note, bool playing)
        {
            if (!Active || ItemSync.Instance == null || !ItemSync.Instance.TrySharedIdentity(item, out int id, out int prefab))
                return false;
            Net.Broadcast(new CrewNoteMsg
            {
                Kind = kind, Actor = Net.MyNetId, ItemInstanceId = id, PrefabIndex = prefab,
                Note = note, Playing = playing,
            }, DeliveryMethod.ReliableOrdered);
            return true;
        }

        internal static void Receive(CrewNoteMsg packet, NetPeer from)
        {
            if (!Active) return;
            if (Net.Role == Role.Host)
            {
                packet.Actor = Net.PlayerNetIdForPeer(from);
                Net.RelayExcept(packet, from, DeliveryMethod.ReliableOrdered);
            }
            if (packet.Actor != Net.MyNetId) MusicModCompat.OnRemoteNote(packet);
        }

        internal static void Tick()
        {
            VikingHornCompat.Tick();
            MusicModCompat.Tick();
        }

        internal static void Clear()
        {
            VikingHornCompat.Reset();
            MusicModCompat.Reset();
        }
    }
}
