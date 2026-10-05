using System;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    public sealed partial class ControlsSync
    {
        private ControlTrack[] _ropeTracks = Array.Empty<ControlTrack>(), _wheelTracks = Array.Empty<ControlTrack>();
        private bool[] _ropeDirty = Array.Empty<bool>(), _wheelDirty = Array.Empty<bool>();
        private float[] _wheelInput = Array.Empty<float>();
        // A held winch or wheel sends a request stream. Every snapshot already carries the latest
        // acknowledgement per control, so the guaranteed copy is sent at most this often instead of
        // one full reliable ControlState per request.
        private const float ReplyInterval = 0.25f;
        private bool _replyOwed;
        private float _replyTimer;
        private readonly ChangeStream _stream = new ChangeStream();
        private ControlStateMsg _sentState;
        private bool _resyncAsked;
        /// <summary>Host: a client just bound this hull and has no state for it yet.</summary>
        public void Resync(ushort boat) { var context = _fleet?.Get(boat); if (context != null) context._stream.Reset(); }
        private void AskResync()
        {
            if (_resyncAsked || _net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading) return;
            _resyncAsked = true;
            _net.Broadcast(new ResyncRequestMsg { BoatIndex = _boatId, Domain = ResyncDomain.Controls }, DeliveryMethod.ReliableOrdered);
        }
        private static bool Differs(ControlStateMsg a, ControlStateMsg b)
        {
            if (a == null || a.WheelInputs.Length != b.WheelInputs.Length || a.Lengths.Length != b.Lengths.Length ||
                a.Rotations.Length != b.Rotations.Length || a.WheelLocks.Length != b.WheelLocks.Length ||
                a.RopeEpochs.Length != b.RopeEpochs.Length || a.WheelEpochs.Length != b.WheelEpochs.Length) return true;
            for (int i = 0; i < a.WheelInputs.Length; i++) if (!a.WheelInputs[i].Equals(b.WheelInputs[i]) || a.WheelLocks[i] != b.WheelLocks[i]) return true;
            for (int i = 0; i < a.Lengths.Length; i++) if (!a.Lengths[i].Equals(b.Lengths[i])) return true;
            for (int i = 0; i < a.Rotations.Length; i++) if (UnityEngine.Quaternion.Angle(a.Rotations[i], b.Rotations[i]) > 0.05f) return true;
            return EpochsDiffer(a.RopeEpochs, b.RopeEpochs) || EpochsDiffer(a.WheelEpochs, b.WheelEpochs);
        }
        private static bool EpochsDiffer(ControlEpoch[] a, ControlEpoch[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i].Revision != b[i].Revision || a[i].Requester != b[i].Requester || a[i].RequestId != b[i].RequestId) return true;
            return false;
        }
        private static readonly FieldInfo WheelLocked = typeof(GPButtonSteeringWheel).GetField("locked", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static bool Locked(GPButtonSteeringWheel wheel)
        {
            if (WheelLocked == null) throw new MissingFieldException("GPButtonSteeringWheel", "locked");
            return (bool)WheelLocked.GetValue(wheel);
        }
        internal static bool WheelHeld(GPButtonSteeringWheel wheel)
            => wheel != null && (wheel.IsCliked() || ItemComponents.Read<GoPointer>(wheel, "stickyClickedBy") != null ||
                Grabbed(ItemComponents.Read<TouchRotateHandle>(wheel, "rotHandle")));
        // TouchRotateHandle creates its OVRGrabbable in Start. A handle that never started (inactive
        // object, no VR rig) has none, and the game's own IsGrabbed() throws on it.
        internal static bool Grabbed(TouchRotateHandle handle)
        {
            if (handle == null) return false;
            if (ItemComponents.Read<bool>(handle, "debugGrabbed")) return true;
            return ItemComponents.Read<UnityEngine.Object>(handle, "grabbable") != null && handle.IsGrabbed();
        }
        private static ControlTrack[] Tracks(int count)
        { var tracks = new ControlTrack[count]; for (int i = 0; i < count; i++) tracks[i] = new ControlTrack(); return tracks; }
        private void ResetControlTracks()
        {
            _ropeTracks = Tracks(_ropes.Length); _wheelTracks = Tracks(_wheels.Length);
            _ropeDirty = new bool[_ropes.Length]; _wheelDirty = new bool[_wheels.Length]; _wheelInput = new float[_wheels.Length];
        }
        private uint Request(ControlTrack track)
        { uint request = SessionCounters.NextRequest(); track.Gate.Begin(request); return request; }
        private void ApplyWheel(GPButtonSteeringWheel wheel, float input, bool locked)
        {
            wheel.currentInput = input;
            if (WheelLocked == null) throw new MissingFieldException("GPButtonSteeringWheel", "locked");
            WheelLocked.SetValue(wheel, locked);
            if (_miApplyRudder == null) _miApplyRudder = typeof(GPButtonSteeringWheel).GetMethod("ApplyRudderRotation", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_miApplyRudder == null) throw new MissingMethodException("GPButtonSteeringWheel", "ApplyRudderRotation");
            _miApplyRudder.Invoke(wheel, null);
        }
        internal void NotifyWheelInput(GPButtonSteeringWheel wheel)
        {
            if (InteractionContext.Suppressed || _net.Role != Role.Client || _net.State != LinkState.Connected) return;
            if (_fleet != null)
            { foreach (var c in _fleet.Values) if (Array.IndexOf(c._wheels, wheel) >= 0) { c.NotifyWheelInput(wheel); return; } return; }
            int i = Array.IndexOf(_wheels, wheel); if (i < 0) return;
            _wheelDirty[i] = true; _wheelInput[i] = wheel.currentInput;
        }
        internal void NotifyWheelLock(GPButtonSteeringWheel wheel)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected) return;
            if (_fleet != null)
            { foreach (var c in _fleet.Values) if (Array.IndexOf(c._wheels, wheel) >= 0) { c.NotifyWheelLock(wheel); return; } return; }
            int i = Array.IndexOf(_wheels, wheel); if (i < 0) return;
            if (_net.Role == Role.Host) SendState(null, false, true);
            else if (_net.Role == Role.Client)
            {
                _wheelDirty[i] = false; _wheelInput[i] = wheel.currentInput;
                _net.Broadcast(new WheelLockRequestMsg { BoatIndex = _boatId, LayoutHash = _layoutHash, Index = (ushort)i,
                    Locked = Locked(wheel), Input = wheel.currentInput, RequestId = Request(_wheelTracks[i]) }, DeliveryMethod.ReliableOrdered);
            }
        }
        public void OnWheelLockRequest(WheelLockRequestMsg msg, NetPeer peer)
        {
            if (_fleet != null)
            {
                if (!GameState.playing || GameState.currentlyLoading || _net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0) return;
                var c = _fleet.Get(msg.BoatIndex);
                if (c != null && BoatLayout.Matches(msg.BoatIndex, msg.LayoutHash, c._layoutHash, "ControlsSync")) c.OnWheelLockRequest(msg, peer);
                return;
            }
            if (_net.Role != Role.Host) return;
            RefreshNodes(); int i = msg.Index;
            if (i >= _wheels.Length || _wheels[i] == null) return;
            uint actor = _net.PlayerNetIdForPeer(peer);
            if (actor == 0 || !_wheelTracks[i].Requests.Accept(actor, msg.RequestId, false)) return;
            using (InteractionContext.Begin(InteractionSource.RemoteApply)) ApplyWheel(_wheels[i], msg.Input, msg.Locked);
            _wheelTracks[i].Acknowledge(actor, msg.RequestId);
            SendState(null, false, true);
        }
    }
}
