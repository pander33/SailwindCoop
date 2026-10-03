namespace SailwindCoop.Sync
{
    /// <summary>Snapshots cannot undo a local pickup/drop before its authenticated host reply.</summary>
    internal sealed class AnchorStateGate
    {
        private uint _pending;
        private uint _lastRevision;
        private bool _haveRevision;
        public bool HasPending => _pending != 0;

        public void Begin(uint request) { _pending = request; }

        public bool Receive(uint revision, uint requester, uint request, uint localPlayer)
        {
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
