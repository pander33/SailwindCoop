using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Host-side "join freeze": while a client downloads and loads the streamed save, the host's
    /// world keeps running, so by the time the client is in, items/anchor/moorings/waves no longer
    /// match the snapshot it loaded. To prevent that the host pauses itself the way the game's own
    /// settings menu does (<c>Time.timeScale = 0</c> + <c>Physics.autoSyncTransforms = false</c>,
    /// see <c>StartMenu.GameToSettings</c>) from the moment a client is accepted until that client
    /// reports <c>ClientWorldLoaded</c> (or leaves, or a safety timeout fires).
    ///
    /// Multiple simultaneous joiners are refcounted by player NetId — the pause lifts when the
    /// last pending client checks in.
    /// </summary>
    public sealed class JoinPause
    {
        /// <summary>Safety net: never hold the pause longer than this (seconds, unscaled).</summary>
        public float TimeoutSec = 120f;

        private readonly HashSet<uint> _pending = new HashSet<uint>();
        private bool _paused;
        private float _prevTimeScale = 1f;
        private float _pausedAt;

        public bool Active => _paused;
        public int PendingCount => _pending.Count;

        /// <summary>Host: freeze the world until <paramref name="netId"/> reports its load done.</summary>
        public void Hold(uint netId)
        {
            _pending.Add(netId);
            if (_paused) return;

            _prevTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            Physics.autoSyncTransforms = false;
            _pausedAt = Time.realtimeSinceStartup;
            _paused = true;
            Plugin.Logger.LogInfo("[JoinPause] Мир хоста на паузе: ждём загрузку клиента NetId=" + netId);
        }

        /// <summary>Host: this client finished loading (or left) — lift the pause if it was the last one.</summary>
        public void Release(uint netId)
        {
            if (!_pending.Remove(netId)) return;
            if (_pending.Count == 0) Unpause("клиент NetId=" + netId + " загрузился");
        }

        /// <summary>Lift the pause unconditionally (disconnect/teardown).</summary>
        public void Clear()
        {
            _pending.Clear();
            if (_paused) Unpause("сброс сессии");
        }

        /// <summary>Call every frame on the host: enforces the safety timeout.</summary>
        public void Tick()
        {
            if (_paused && Time.realtimeSinceStartup - _pausedAt > TimeoutSec)
            {
                Plugin.Logger.LogWarning("[JoinPause] Клиент не отчитался о загрузке за " + TimeoutSec +
                                         " с — снимаю паузу принудительно");
                _pending.Clear();
                Unpause("таймаут");
            }
        }

        private void Unpause(string why)
        {
            _paused = false;
            // Restore only if nobody else (e.g. the game's own settings menu) re-pinned the timescale.
            if (Time.timeScale == 0f)
                Time.timeScale = _prevTimeScale > 0f ? _prevTimeScale : 1f;
            Physics.autoSyncTransforms = true;
            Plugin.Logger.LogInfo("[JoinPause] Пауза снята (" + why + ")");
        }
    }
}
