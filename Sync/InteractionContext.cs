using System;

namespace SailwindCoop.Sync
{
    internal enum InteractionSource { WorldSimulation, LocalInput, ActiveHold, RemoteApply, Baseline }

    /// <summary>Main-thread operation origin. Remote/baseline scopes dominate nested input hooks.</summary>
    internal static class InteractionContext
    {
        private static InteractionSource _source;
        internal static InteractionSource Source => _source;
        internal static bool Suppressed => _source == InteractionSource.RemoteApply || _source == InteractionSource.Baseline;
        internal static bool HasInput => _source == InteractionSource.LocalInput || _source == InteractionSource.ActiveHold;
        internal static Scope Begin(InteractionSource source) => new Scope(source);

        internal sealed class Scope : IDisposable
        {
            private readonly InteractionSource _previous;
            private bool _disposed;
            internal Scope(InteractionSource source)
            {
                _previous = _source;
                if (!Suppressed) _source = source;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _source = _previous;
            }
        }
    }
}
