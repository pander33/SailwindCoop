using System;

namespace SailwindCoop.Sync
{
    /// <summary>Keep the latest authoritative target until the game's animation accepts it.</summary>
    internal sealed class DeferredToggle
    {
        public bool Target { get; set; }
        public bool TryApply(Func<bool> read, Action activate)
        {
            if (read() == Target) return true;
            activate(); // Vanilla may ignore this while an earlier animation is running.
            return read() == Target;
        }
    }
}
