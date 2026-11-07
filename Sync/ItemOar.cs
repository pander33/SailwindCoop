using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class ItemSync
    {
        private const float OarBatchSeconds = 1f / 15f;
        private DeckForceBatch _oarForce;
        private ItemEntry _rowingOar;
        private ushort _rowingBoat;
        private int _lastRowFrame;

        internal void CaptureOarStroke(ShipItemOar oar)
        {
            if (InteractionContext.Suppressed || ItemOperationCapture.Absorb() || _net.Role != Role.Client ||
                _net.State != LinkState.Connected || oar == null || !LocalHand(oar) || Time.deltaTime <= 0f ||
                !_byItem.TryGetValue(oar, out var entry) || !_hostIds.Contains(entry.InstanceId)) return;
            Transform hull = GameState.currentBoat;
            ushort boat = BoatLocator.IndexOf(hull);
            var body = InteractionSync.BoatForceBody(hull);
            if (boat == BoatLocator.NoBoat || body == null || oar.waterPos == null) return;

            if (_rowingOar != entry || _rowingBoat != boat) FlushOarStroke();
            _rowingOar = entry;
            _rowingBoat = boat;
            _lastRowFrame = Time.frameCount;
            DeckForcePlayback.Accumulate(ref _oarForce, hull, body, -oar.waterPos.forward * oar.rowForce, oar.waterPos.position, Time.deltaTime);
            if (_oarForce.Seconds >= OarBatchSeconds) FlushOarStroke();
        }

        private void FlushOarStroke()
        {
            if (_rowingOar == null || _oarForce.Seconds <= 0f) return;
            var msg = new OarStrokeMsg { BoatIndex = _rowingBoat, InstanceId = _rowingOar.InstanceId, PrefabIndex = _rowingOar.PrefabIndex };
            _oarForce.Write(msg);
            _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
            ClearOarStroke();
        }

        private void ClearOarStroke() { _oarForce = default; _rowingOar = null; }

        internal ShipItemOar FindOar(int id, int prefabIndex)
        {
            var entry = HostLookup(id, prefabIndex);
            if (entry == null) { RefreshItems(force: true); entry = HostLookup(id, prefabIndex); }
            return entry?.Item as ShipItemOar;
        }
    }
}
