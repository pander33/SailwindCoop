namespace SailwindCoop.Sync
{
    internal enum StreamSend { None, Unreliable, Reliable }

    /// <summary>Send-on-change policy for a host state stream. Nothing is sent while the value is
    /// unchanged. Because a silent stream no longer repairs a lost datagram, the first packet and the
    /// packet that ends a run of changes are reliable: the receiver always ends on the resting value.</summary>
    internal sealed class ChangeStream
    {
        private bool _sent, _active;
        internal StreamSend Next(bool changed)
        {
            if (!_sent) { _sent = true; _active = false; return StreamSend.Reliable; }
            if (changed) { _active = true; return StreamSend.Unreliable; }
            if (_active) { _active = false; return StreamSend.Reliable; }
            return StreamSend.None;
        }
        /// <summary>An out-of-band send (reply, transition) already delivered the current value.</summary>
        internal void Sent(bool reliable) { _sent = true; _active = !reliable; }
        /// <summary>A receiver asked for the current value: the next tick sends it reliably.</summary>
        internal void Reset() { _sent = false; _active = false; }
    }
}
