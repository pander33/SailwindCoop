namespace SailwindCoop.Sync
{
    internal static class PoseSupportOrder
    {
        // An older sample can move the boundary only while it is still unplayed and after
        // the last confirmed sample of the preceding support.
        internal static bool CanPrepend(long tick, long playedTick, long previousLastTick, long nextStartTick)
            => tick > playedTick && tick > previousLastTick && tick < nextStartTick;
    }
}
