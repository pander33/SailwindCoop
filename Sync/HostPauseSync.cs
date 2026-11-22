using System;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed class HostPauseSync
    {
        private readonly CoopNet _net;
        private bool _restoreControl;
        private CoopLog.Repeat _controlReport;

        public HostPauseSync(CoopNet net) { _net = net; }

        public bool Frozen { get; private set; }
        internal WorldPauseReason Reasons { get; private set; }
        internal bool HasState { get; private set; }
        private uint _revision;

        internal void OnState(PauseStateMsg msg)
        {
            if (HasState && msg.Revision < _revision) return;
            _revision = msg.Revision;
            Reasons = msg.Reasons;
            HasState = true;
        }

        internal void Tick(EnvironmentSync env, bool hostManualPause)
        {
            bool remotePause = _net.Role == Role.Client && env.HasHostState &&
                               env.SecondsSinceHostState < SharedSleepTiming.HostSnapshotTimeout &&
                               env.HostTimeScale <= 0.0001f;
            Frozen = _net.State == LinkState.Connected && GameState.playing && !GameState.currentlyLoading &&
                     (remotePause || hostManualPause);
            UpdateControl();
        }

        public void Clear()
        {
            Frozen = HasState = false;
            Reasons = WorldPauseReason.None;
            _revision = 0;
            UpdateControl();
        }

        // A subsystem can release its own lock while both controllers are still held by the pause.
        internal void RestoreControlWhenReady() => _restoreControl = true;

        private void UpdateControl()
        {
            if (!Frozen && !_restoreControl) return;
            try
            {
                if (Refs.charController == null || Refs.ovrController == null) return;
                if (Frozen)
                {
                    if (!Refs.charController.enabled && !Refs.ovrController.enabled) return;
                    _restoreControl = true;
                    Refs.SetPlayerControl(false);
                }
                else if (GameState.playing && !GameState.currentlyLoading && !GameState.inCursorMenu &&
                         GameState.inBed == null && !GameState.recovering && !GameState.sleeping &&
                         SleepSync.Instance?.ClientAsleep != true &&
                         CoopBehaviour.Instance?.Knockdown?.Down != true &&
                         !FreeCamCompat.IsFreeCameraOn)
                {
                    Refs.SetPlayerControl(true);
                    _restoreControl = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[HostPauseSync] Could not toggle player control",
                                          e.Message, ref _controlReport);
            }
        }
    }
}
