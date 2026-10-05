namespace SailwindCoop.Sync
{
    /// <summary>Request ids and state revisions shared by every per-boat context. A context is
    /// rebuilt on one machine only (shipyard preview, late layout load), so a counter that restarted
    /// with it would fall behind what the other side already saw and be dropped as stale.</summary>
    internal static class SessionCounters
    {
        private static uint _request, _revision;
        internal static uint NextRequest() { if (++_request == 0) ++_request; return _request; }
        internal static uint NextRevision() { if (++_revision == 0) ++_revision; return _revision; }
    }
}
