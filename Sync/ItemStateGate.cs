using System;
using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    /// <summary>Orders complete semantic state independently of pose samples and own pending input.</summary>
    internal sealed class ItemStateGate
    {
        /// <summary>A request the host dropped without a reply must not shut the object off from host
        /// state for the rest of the session: after this long the pending request is abandoned and the
        /// next host state applies normally. Real time, so a paused world does not stretch it.</summary>
        internal const long PendingTimeoutMs = 5000;
        internal static Func<long> Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
        /// <summary>Diagnostics only: raised with the abandoned request id.</summary>
        internal static Action<uint> Expired;
        private uint _pending, _revision;
        private long _pendingSince;
        private long _poseTick = long.MinValue;
        private bool _haveState;
        internal bool Pending { get { Expire(); return _pending != 0; } }
        internal uint PendingRequest { get { Expire(); return _pending; } }
        internal uint Version => _revision;
        internal bool HasState => _haveState;
        internal ItemStateGate Copy() => new ItemStateGate { _pending = _pending, _pendingSince = _pendingSince, _revision = _revision, _poseTick = _poseTick, _haveState = _haveState };
        internal void Commit(ItemStateGate accepted)
        { _pending = accepted._pending; _pendingSince = accepted._pendingSince; _revision = accepted._revision; _poseTick = accepted._poseTick; _haveState = accepted._haveState; }
        internal ItemApplyStatus Apply(ItemStateGate accepted, Func<ItemApplyStatus> apply)
        {
            var status = apply();
            if (status == ItemApplyStatus.Applied) Commit(accepted);
            return status;
        }
        internal void Cancel(uint requester, uint request, uint localPlayer)
        { if (_pending != 0 && requester == localPlayer && request == _pending) _pending = 0; }
        internal void Clear() { _pending = _revision = 0; _haveState = false; _poseTick = long.MinValue; }
        internal void Begin(uint request) { if (request != 0) { _pending = request; _pendingSince = Clock(); } }
        private void Expire()
        {
            if (_pending == 0 || Clock() - _pendingSince < PendingTimeoutMs) return;
            uint abandoned = _pending; _pending = 0;
            try { Expired?.Invoke(abandoned); } catch { }
        }
        internal bool Receive(uint revision, long poseTick, uint requester, uint request, uint localPlayer,
            out bool newRevision, out bool newPose)
        {
            newRevision = newPose = false;
            Expire();
            bool ack = _pending != 0 && requester == localPlayer && request == _pending;
            if (_pending != 0 && !ack) return false;
            int delta = unchecked((int)(revision - _revision));
            if (_haveState && delta < 0) return false;
            if (ack) _pending = 0;
            newRevision = !_haveState || delta > 0;
            newPose = newRevision || poseTick > _poseTick;
            if (!newRevision && !newPose && !ack) return false;
            _haveState = true; _revision = revision;
            if (newPose) _poseTick = poseTick;
            return true;
        }
    }

    /// <summary>Host causal order per item/sender, without ownership or gameplay eligibility checks.</summary>
    internal sealed class ItemRequestOrder
    {
        private readonly Dictionary<uint, uint> _actions = new Dictionary<uint, uint>();
        internal bool Accept(uint actor, uint request, bool pose)
        {
            if (request == 0) return true; // manifest and older domain requests have no local prediction.
            uint previous;
            if (_actions.TryGetValue(actor, out previous))
            {
                int delta = unchecked((int)(request - previous));
                if (delta < 0 || (!pose && delta == 0)) return false;
            }
            if (!pose) _actions[actor] = request;
            return true;
        }
    }
}
