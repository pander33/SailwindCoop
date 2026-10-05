using System;

namespace SailwindCoop.Sync
{
    /// <summary>Ordered host state waits for the latest local request and for the hand to release.</summary>
    internal sealed class PendingMooringState<T> where T : class
    {
        private uint _pendingRequest;
        private T _state;

        public void BeginRequest(uint requestId)
        {
            _pendingRequest = requestId;
            _state = null; // A state received before this request cannot undo its local effect.
        }

        public bool Receive(uint requester, uint requestId, uint localPlayer, T state, bool waitingTarget = false)
        {
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
            if (held || _pendingRequest != 0 || _state == null) return;
            if (apply(_state)) _state = null; // Keep an unresolved dock target for another frame.
        }
    }
}
