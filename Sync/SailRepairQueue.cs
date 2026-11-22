using System;

namespace SailwindCoop.Sync
{
    internal sealed class SailRepairQueue
    {
        internal const float RetryTimeoutSeconds = 30f;
        private readonly float[] _whole;
        private float[] _authority, _displayed, _sending;
        private bool _mend, _sendingMend;
        private float _sentAt = float.NaN;
        internal float[] Queued { get; private set; }

        internal SailRepairQueue(float[] whole)
        {
            _authority = _displayed = _whole = whole;
            Queued = new float[whole.Length];
        }

        internal void Observe(float[] actual)
        {
            for (int i = 0; i < Queued.Length; i++) Queued[i] += actual[i] - _displayed[i];
            _displayed = actual;
        }

        internal void MarkDisplayed(float[] actual) => _displayed = actual;

        internal void Mend(float[] actual)
        {
            Array.Clear(Queued, 0, Queued.Length);
            _mend = true;
            MarkDisplayed(actual);
        }

        internal void UpdateAuthority(float[] values, bool ack)
        {
            _authority = values;
            if (!ack) return;
            _sending = null;
            _sendingMend = false;
            _sentAt = float.NaN;
        }

        internal float[] Preview()
        {
            var values = new float[Queued.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float confirmed = _mend || _sendingMend ? _whole[i] : _authority[i] + (_sending?[i] ?? 0f);
                values[i] = Math.Max(0f, Math.Min(1f, confirmed + Queued[i]));
            }
            return values;
        }

        internal bool TrySend(bool sendDelta, out float[] values, out bool mend)
            => TrySend(sendDelta, float.NaN, out values, out mend);

        internal bool TrySend(bool sendDelta, float now, out float[] values, out bool mend)
        {
            values = null;
            mend = false;
            if (_sending != null)
            {
                if (float.IsNaN(now) || float.IsNaN(_sentAt) || now - _sentAt < RetryTimeoutSeconds)
                    return false;
                if (_mend)
                {
                    mend = _sendingMend = true;
                    values = _sending = _whole;
                    _mend = false;
                    _sentAt = now;
                    return true;
                }
                if (_sendingMend)
                {
                    values = _sending;
                    mend = true;
                    _sentAt = now;
                    return true;
                }
                // Non-mend deltas are additive on the host: never re-send a timed-out delta.
                // Clear the stalled in-flight slot so later repairs proceed and host authority reconciles.
                _sending = null;
                _sentAt = float.NaN;
                if (!sendDelta) return false;
            }
            if (!_mend && !sendDelta) return false;
            mend = _sendingMend = _mend;
            if (mend)
            {
                values = _sending = _whole;
                _mend = false;
            }
            else
            {
                values = _sending = Queued;
                Queued = new float[_whole.Length];
            }
            _sentAt = now;
            return true;
        }
    }
}
