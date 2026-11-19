using System;
using System.Collections.Generic;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Owns the host's simulation clock while any co-op pause reason is active.</summary>
    public sealed class JoinPause
    {
        public float TimeoutSec = 120f;

        private readonly HashSet<uint> _pending = new HashSet<uint>();
        private readonly WorldPauseClock _clock = new WorldPauseClock();
        private float _heldAt;
        private float _settleUntil = -1f;
        private bool _manual;

        internal event Action Changed;
        internal WorldPauseReason Reasons => _clock.Reasons;
        internal uint Revision => _clock.Revision;
        public bool Active => Reasons != WorldPauseReason.None;
        public bool Settling => _settleUntil >= 0f;
        public int PendingCount => _pending.Count;
        internal bool Manual => _manual;
        internal bool Technical => PendingCount != 0 || Settling;

        internal void SetManual(bool paused)
        {
            _manual = paused;
            ApplyReasons();
        }

        public void HoldToSettle(float seconds)
        {
            _settleUntil = Time.realtimeSinceStartup + seconds;
            ApplyReasons();
        }

        public void Hold(uint netId)
        {
            if (_pending.Count == 0) _heldAt = Time.realtimeSinceStartup;
            _pending.Add(netId);
            ApplyReasons();
        }

        public void Release(uint netId)
        {
            if (_pending.Remove(netId)) ApplyReasons();
        }

        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (Settling && now >= _settleUntil)
            {
                _settleUntil = -1f;
                ApplyReasons();
            }

            if (_pending.Count == 0 || now - _heldAt <= TimeoutSec) return;
            _pending.Clear();
            ApplyReasons();
            Plugin.Logger.LogWarning("[JoinPause] Client load timed out after " + TimeoutSec + " s");
            CoopBehaviour.Notice("A joining player never finished loading. They may be out of sync; have them rejoin.");
        }

        internal void SetResumeTimeScale(float value) => _clock.SetResumeScale(value);

        public void Clear()
        {
            _pending.Clear();
            _settleUntil = -1f;
            _manual = false;
            ApplyReasons(notify: false);
        }

        private void ApplyReasons(bool notify = true)
        {
            WorldPauseReason reasons = _manual ? WorldPauseReason.Manual : WorldPauseReason.None;
            if (_pending.Count != 0) reasons |= WorldPauseReason.Loading;
            if (Settling) reasons |= WorldPauseReason.Settling;

            float scale = Time.timeScale;
            bool autoSync = Physics.autoSyncTransforms;
            if (!_clock.SetReasons(reasons, ref scale, ref autoSync)) return;
            if (Time.timeScale != scale) Time.timeScale = scale;
            if (Physics.autoSyncTransforms != autoSync) Physics.autoSyncTransforms = autoSync;
            Plugin.Logger.LogInfo("[WorldPause] reasons=" + reasons + ", timeScale=" + scale);
            if (notify) Changed?.Invoke();
        }
    }
}
