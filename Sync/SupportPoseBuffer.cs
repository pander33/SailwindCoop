using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal sealed class SupportPoseBuffer
    {
        private struct Sample
        {
            internal long Tick;
            internal CoordFrame Frame;
            internal ushort Boat;
            internal Vector3 Pos, Vel;
            internal Quaternion Rot;
        }
        private sealed class Segment
        {
            internal Sample First, Last;
            internal readonly NetTransform Net = new NetTransform { SmoothUnderruns = true };
            internal Transform Boat;
            internal bool Resolve()
            {
                if (First.Frame != CoordFrame.Boat) return true;
                var boat = BoatLocator.FindByIndex(First.Boat);
                if (boat == null) return false;
                if (Boat != boat)
                { Boat = boat; Net.ToWorldPos = boat.TransformPoint; Net.ToWorldRot = r => boat.rotation * r; }
                return true;
            }
        }
        private readonly SupportHistory<Sample> _history = new SupportHistory<Sample>(s => s.Tick,
            (a, b) => a.Frame == b.Frame && (a.Frame != CoordFrame.Boat || a.Boat == b.Boat));
        private readonly List<Segment> _segments = new List<Segment>();
        internal bool HasData => _segments.Count > 0;
        internal float InterpDelayMs = 100f;
        internal void Apply(Transform target, long serverNow) => ApplyAt(target, serverNow - (long)InterpDelayMs);
        internal long PlayedTick => _history.PlayedTick;
        internal bool Push(long tick, CoordFrame frame, ushort boat, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            var sample = new Sample { Tick = tick, Frame = frame, Boat = boat, Pos = pos, Rot = rot, Vel = vel };
            bool forward = _history.Count == 0 || tick >= _history[_history.Count - 1].Tick;
            if (!_history.Push(sample)) return false;
            if (_history.Restarted) _segments.Clear();
            if (forward)
            {
                Segment last = _segments.Count > 0 ? _segments[_segments.Count - 1] : null;
                if (last == null || last.First.Frame != frame || (frame == CoordFrame.Boat && last.First.Boat != boat))
                { last = new Segment { First = sample }; _segments.Add(last); }
                last.Last = sample; last.Net.Push(tick, pos, rot, vel);
            }
            else
            {
                for (int i = _segments.Count - 1; i >= 0; i--)
                {
                    var existing = _segments[i];
                    if (tick < existing.First.Tick) continue;
                    if (existing.First.Frame == frame && (frame != CoordFrame.Boat || existing.First.Boat == boat) && tick >= existing.Net.OldestTick)
                    { existing.Net.Push(tick, pos, rot, vel); if (tick > existing.Last.Tick) existing.Last = sample; return true; }
                    break;
                }
                // Rebuild only on reordering: a late B can split one existing A run in two.
                var old = _segments.ToArray();
                _segments.Clear();
                for (int start = 0; start < _history.Count;)
                {
                    int end = _history.EndOfRun(start);
                    var first = _history[start]; var last = _history[end];
                    Segment segment = null;
                    foreach (var previous in old)
                        if (previous.First.Tick == first.Tick && previous.Last.Tick == last.Tick &&
                            previous.First.Frame == first.Frame && previous.First.Boat == first.Boat &&
                            (tick < first.Tick || tick > last.Tick)) { segment = previous; break; }
                    if (segment == null)
                    {
                        segment = new Segment { First = first, Last = last };
                        for (int i = start; i <= end; i++)
                        { var s = _history[i]; segment.Net.Push(s.Tick, s.Pos, s.Rot, s.Vel); }
                    }
                    _segments.Add(segment); start = end + 1;
                }
            }
            return true;
        }
        internal void ApplyAt(Transform target, long tick)
        {
            if (_segments.Count == 0) return;
            int index = 0;
            while (index + 1 < _segments.Count && _segments[index + 1].First.Tick <= tick) index++;
            var current = _segments[index];
            if (!current.Resolve()) return;
            var next = index + 1 < _segments.Count ? _segments[index + 1] : null;
            if (next == null || !next.Resolve() ||
                !current.Net.TryApplyTransition(target, next.Net, current.Last.Tick, next.First.Tick, tick))
                current.Net.ApplyAt(target, tick);
            if (index > 0) _segments.RemoveRange(0, index);
            _history.Prune(tick);
        }
        internal void Clear() { _segments.Clear(); _history.Clear(); }
    }
}
