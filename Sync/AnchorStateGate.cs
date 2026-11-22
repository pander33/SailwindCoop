using System;

namespace SailwindCoop.Sync
{
    /// <summary>Snapshots cannot undo a local pickup/drop before its authenticated host reply.</summary>
    internal sealed class AnchorStateGate
    {
        internal const long PendingTimeoutMs = 5000;
        internal static Func<long> Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
        private uint _pending;
        private long _pendingSince;
        private uint _lastRevision;
        private bool _haveRevision;
        public bool HasPending { get { Expire(); return _pending != 0; } }

        public void Begin(uint request)
        {
            _pending = request;
            if (request != 0) _pendingSince = Clock();
        }

        private void Expire()
        {
            if (_pending == 0 || Clock() - _pendingSince < PendingTimeoutMs) return;
            _pending = 0;
        }

        public bool Receive(uint revision, uint requester, uint request, uint localPlayer)
        {
            Expire();
            if (_pending != 0)
            {
                if (requester != localPlayer || request != _pending) return false;
                _pending = 0;
            }
            // Millisecond host ticks can coincide for a reply and a periodic snapshot.
            if (_haveRevision && unchecked((int)(revision - _lastRevision)) <= 0) return false;
            _lastRevision = revision;
            _haveRevision = true;
            return true;
        }
    }
}
