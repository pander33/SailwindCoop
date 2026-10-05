using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>One independently ordered control; diagnostics are outside this state channel.</summary>
    internal sealed class ControlTrack
    {
        internal readonly ItemStateGate Gate = new ItemStateGate();
        internal readonly ItemRequestOrder Requests = new ItemRequestOrder();
        private readonly ControlEpoch _epoch = new ControlEpoch();
        private bool _sampled, _locked;
        private float _value;
        internal ControlEpoch Capture(float value, bool locked = false)
        {
            if (!_sampled || !_value.Equals(value) || _locked != locked)
            { _epoch.Revision = SessionCounters.NextRevision(); _value = value; _locked = locked; _sampled = true; }
            return new ControlEpoch { Revision = _epoch.Revision, Requester = _epoch.Requester, RequestId = _epoch.RequestId };
        }
        internal void Acknowledge(uint actor, uint request) { _epoch.Requester = actor; _epoch.RequestId = request; }
        internal bool Receive(ControlEpoch epoch, long tick, uint player)
        {
            bool semantic, pose;
            return Gate.Receive(epoch.Revision, tick, epoch.Requester, epoch.RequestId, player, out semantic, out pose);
        }
    }
}
