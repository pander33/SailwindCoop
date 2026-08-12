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
        private bool _prevAutoSync = true;
        private bool _ownsTimeScale;
        private bool _ownsAutoSync;
        private float _pausedAt;

        /// <summary>
        /// Armed whenever we release a freeze we actually took. Disarms on the first frame the world is
        /// seen running again, which in the healthy case is immediately — see <see cref="Tick"/>.
        /// </summary>
        private bool _repairArmed;

        /// <summary>A game menu was seen open during the current freeze — the precondition for the
        /// ownership clash the watcher repairs.</summary>
        private bool _menuSeenDuringFreeze;

        /// <summary>When the game menu was first seen closed while the watcher was armed, or -1 while it
        /// is (or reads as) open. See <see cref="RepairSettleSec"/>.</summary>
        private float _menuGoneAt = -1f;

        /// <summary>How long after the menu closes the watcher waits before judging the world state.
        /// The menu clears its "open" flag and restores the two globals; if those are not one atomic
        /// step, there is a window where it already reads closed but has not written yet. Judging inside
        /// that window would see the world still running, disarm, and miss the write that follows.
        /// Disarming late costs nothing, so wait it out.</summary>
        private const float RepairSettleSec = 0.25f;

        public bool Active => _paused;
        public int PendingCount => _pending.Count;

        /// <summary>Host: freeze the world until <paramref name="netId"/> reports its load done.</summary>
        public void Hold(uint netId)
        {
            _pending.Add(netId);
            if (_paused) return;

            // Track each global independently. If another owner (notably the settings menu) already set
            // the required value, leave it alone and do not restore it when this join finishes.
            _prevTimeScale = Time.timeScale;
            _prevAutoSync = Physics.autoSyncTransforms;
            _ownsTimeScale = Time.timeScale != 0f;
            _ownsAutoSync = Physics.autoSyncTransforms;
            if (_ownsTimeScale) Time.timeScale = 0f;
            if (_ownsAutoSync) Physics.autoSyncTransforms = false;
            _pausedAt = Time.realtimeSinceStartup;
            _paused = true;
            // Fresh freeze, fresh observation window. A menu open right now is already accounted for by
            // the ownership check above (we simply did not take what it holds).
            _menuSeenDuringFreeze = false;

            if (_ownsTimeScale)
            {
                Plugin.Logger.LogInfo("[JoinPause] Host world paused: waiting for client load NetId=" + netId);
                return;
            }

            // We did NOT take the clock, because something else already had it stopped. The join then
            // rides on THAT pause, and whoever owns it can lift it at any moment — at which point the
            // world runs on while the guest is still loading, which is the drift this class exists to
            // prevent. Tolerable (it is the behaviour from before JoinPause existed) but never silent:
            // the log line is gated off by default, so this also goes to the menu.
            Plugin.Logger.LogWarning("[JoinPause] World was already stopped by something else (likely the " +
                                     "game's own menu) - NetId=" + netId + " joins WITHOUT our freeze");
            Runtime.CoopBehaviour.Notice("A player is joining while the game menu holds the world paused. " +
                                         "Keep the menu open until they are in, or their world may drift.");
        }

        /// <summary>Host: this client finished loading (or left) — lift the pause if it was the last one.</summary>
        public void Release(uint netId)
        {
            if (!_pending.Remove(netId)) return;
            if (_pending.Count == 0) Unpause("client NetId=" + netId + " loaded");
        }

        /// <summary>Lift the pause unconditionally (disconnect/teardown).</summary>
        public void Clear()
        {
            _pending.Clear();
            if (_paused) Unpause("session reset");
        }

        /// <summary>Call every frame on the host: enforces the safety timeout and repairs a world left
        /// frozen by the ownership clash described on <see cref="TryRepairFrozenWorld"/>.</summary>
        public void Tick()
        {
            if (!_paused)
            {
                TryRepairFrozenWorld();
                return;
            }

            // Sampled for the WHOLE freeze, not just at its start: a game menu opening mid-freeze is
            // precisely the case ownership tracking cannot see, and it is the only thing that arms the
            // repair watcher below.
            if (ForeignCursorMenuOpen()) _menuSeenDuringFreeze = true;

            if (Time.realtimeSinceStartup - _pausedAt > TimeoutSec)
            {
                Plugin.Logger.LogWarning("[JoinPause] Client did not report loaded within " + TimeoutSec +
                                         " s - force releasing pause");
                // The warning above is on the gated path, so with logging off (the default) a host who
                // just sat frozen for two minutes would get no explanation anywhere.
                Runtime.CoopBehaviour.Notice("A joining player never finished loading the world - the " +
                                             "host was un-paused after " + TimeoutSec + " s. They may be " +
                                             "out of sync; have them rejoin.");
                _pending.Clear();
                Unpause("timeout");
            }
        }

        private void Unpause(string why)
        {
            _paused = false;
            if (_ownsTimeScale && Time.timeScale == 0f)
                Time.timeScale = _prevTimeScale;
            if (_ownsAutoSync && !Physics.autoSyncTransforms)
                Physics.autoSyncTransforms = _prevAutoSync;
            // Arm the watcher only where the clash is possible at all: we held the clock down AND a game
            // menu was open at some point while we did, so its saved "restore to" values may be ours.
            if (_ownsTimeScale && _menuSeenDuringFreeze)
            {
                _repairArmed = true;
                _menuGoneAt = -1f;   // fresh observation: this arming's settle window starts now
            }
            _menuSeenDuringFreeze = false;
            _ownsTimeScale = false;
            _ownsAutoSync = false;
            Plugin.Logger.LogInfo("[JoinPause] Pause released (" + why + ")");
        }

        /// <summary>
        /// Undo a world left frozen with nobody owning the freeze.
        ///
        /// Ownership tracking fixes the order "menu open, then client joins". It cannot fix the reverse,
        /// which is the likelier one — the host sees the game stuck for up to <see cref="TimeoutSec"/>
        /// and presses Escape:
        ///
        ///   1. we freeze and record (1, true) as the gameplay state to go back to;
        ///   2. the game's menu opens and records OUR (0, false) as ITS state to go back to;
        ///   3. the guest finishes loading, we restore (1, true) — the world now runs behind an open menu;
        ///   4. the host closes the menu, which restores the (0, false) it captured from us.
        ///
        /// The world is then stopped and <c>autoSyncTransforms</c> off with no owner left to lift either,
        /// which needs killing the process. Nothing at step 1–3 can detect this: both parties use the
        /// same two globals with the same values, so there is no signal to read at freeze time. What IS
        /// unambiguous is the END state — the world sitting at exactly the value we wrote, with no cursor
        /// menu open to explain it. In the healthy case this disarms on the very next frame after
        /// <see cref="Unpause"/>, so it costs one comparison.
        /// </summary>
        private void TryRepairFrozenWorld()
        {
            if (!_repairArmed) return;

            // Wait for the menu to CLOSE before judging anything. Checking "is the world running?" first
            // would disarm at step 3 above — where the world does run, behind the still-open menu — and
            // so miss the damage that only lands at step 4. Our own F8 menu does not stop the clock, so
            // it is never an explanation and must not hold the watcher.
            if (ForeignCursorMenuOpen())
            {
                _menuGoneAt = -1f;
                return;
            }

            // Then let the close finish landing before reading the globals (see RepairSettleSec).
            if (_menuGoneAt < 0f)
            {
                _menuGoneAt = Time.realtimeSinceStartup;
                return;
            }
            if (Time.realtimeSinceStartup - _menuGoneAt < RepairSettleSec) return;

            // Menu gone and the world is running: whoever owned what, the outcome is fine.
            if (Time.timeScale > 0f)
            {
                _repairArmed = false;
                _menuGoneAt = -1f;
                return;
            }

            // Force the gameplay defaults rather than the captured values. This path only runs once the
            // state is known to be corrupt and unowned, and a later Hold may have captured the corrupt
            // values themselves (_prevTimeScale = 0, _prevAutoSync = false) — restoring those would
            // repair nothing. autoSyncTransforms=true is also Unity's own default, and leaving it off is
            // the silent-raycast failure this whole ownership question is about.
            Time.timeScale = _prevTimeScale > 0f ? _prevTimeScale : 1f;
            Physics.autoSyncTransforms = true;
            _repairArmed = false;
            _menuGoneAt = -1f;
            Plugin.Logger.LogWarning("[JoinPause] World was still frozen with no menu open after a join - " +
                                     "restored timeScale=" + Time.timeScale +
                                     ", autoSyncTransforms=" + Physics.autoSyncTransforms);
            Runtime.CoopBehaviour.Notice("The world stayed paused after a player joined (the game menu and " +
                                         "co-op both held it). It has been un-paused automatically.");
        }

        /// <summary>A cursor menu belonging to the GAME is open — ours does not stop the clock, so it is
        /// never an explanation for a stopped world.</summary>
        private static bool ForeignCursorMenuOpen()
        {
            try
            {
                if (!GameState.inCursorMenu) return false;
                var coop = Runtime.CoopBehaviour.Instance;
                return coop == null || !coop.CoopMenuOpen;
            }
            catch
            {
                // Unreadable engine state: assume a menu IS open. Failing this way leaves the world
                // frozen (recoverable by closing the menu) instead of stamping over a legitimate pause.
                return true;
            }
        }
    }
}
