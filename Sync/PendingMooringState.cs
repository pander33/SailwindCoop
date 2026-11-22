using System;

namespace SailwindCoop.Sync
{
    /// <summary>Ordered host state waits for the latest local request and for the hand to release.</summary>
    internal sealed class PendingMooringState<T> where T : class
    {
        internal const long PendingTimeoutMs = 5000;
        internal static Func<long> Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency;
        private uint _pendingRequest;
        private long _pendingSince;
        private T _state;
        public bool HasPending { get { Expire(); return _pendingRequest != 0; } }

        public void BeginRequest(uint requestId)
        {
            _pendingRequest = requestId;
            if (requestId != 0) _pendingSince = Clock();
            _state = null; // A state received before this request cannot undo its local effect.
        }

        private void Expire()
        {
            if (_pendingRequest == 0 || Clock() - _pendingSince < PendingTimeoutMs) return;
            _pendingRequest = 0;
        }

        public bool Receive(uint requester, uint requestId, uint localPlayer, T state, bool waitingTarget = false)
        {
            Expire();
            if (waitingTarget) return false; // Target loading never completes the local request.
            if (_pendingRequest != 0)
            {
                if (requester != localPlayer || requestId != _pendingRequest) return false;
                _pendingRequest = 0;
            }
            _state = state;
            return true;
        }

        public void TryApply(bool held, Func<T, bool> apply)
        {
            Expire();
            if (held || _pendingRequest != 0 || _state == null) return;
            if (apply(_state)) _state = null; // Keep an unresolved dock target for another frame.
        }
    }
}
