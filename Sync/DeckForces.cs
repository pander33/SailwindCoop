using System;
using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Applies ordered guest batches over physics steps, with independent queues per actor and target.</summary>
    internal sealed class DeckForcePlayback
    {
        private readonly struct StreamKey : IEquatable<StreamKey>
        {
            internal readonly uint Actor;
            internal readonly Transform Hull;
            internal readonly Rigidbody Body;
            internal readonly int OarId;

            internal StreamKey(uint actor, Transform hull, Rigidbody body, int oarId)
            { Actor = actor; Hull = hull; Body = body; OarId = oarId; }

            public bool Equals(StreamKey other) => Actor == other.Actor && ReferenceEquals(Hull, other.Hull) &&
                ReferenceEquals(Body, other.Body) && OarId == other.OarId;
            public override bool Equals(object obj) => obj is StreamKey other && Equals(other);
            public override int GetHashCode() => unchecked(((int)Actor * 397 ^ Hull.GetHashCode()) * 397 ^ Body.GetHashCode()) * 397 ^ OarId;
        }

        private sealed class Stream
        {
            internal readonly DeckForceTimeline Timeline = new DeckForceTimeline();
            internal float OarMaxSpeed;
            internal ushort BoatIndex;
            internal float StartDelay = 0.04f;
            internal float IdleSeconds;
        }

        private readonly Dictionary<StreamKey, Stream> _streams = new Dictionary<StreamKey, Stream>();
        private readonly List<StreamKey> _finished = new List<StreamKey>();

        internal static void Accumulate(ref DeckForceBatch batch, Transform hull, Rigidbody body, Vector3 force, Vector3 point, float dt)
        {
            batch.Add(hull.InverseTransformDirection(force), hull.InverseTransformDirection(point - body.worldCenterOfMass), dt);
        }

        internal void Enqueue(uint actor, Transform hull, Rigidbody body, DeckForceRequest msg, ShipItemOar oar = null)
        {
            if (!(msg.Seconds > 0f)) return;
            var key = new StreamKey(actor, hull, body, msg is OarStrokeMsg row ? row.InstanceId : 0);
            if (!_streams.TryGetValue(key, out var stream)) _streams[key] = stream = new Stream { BoatIndex = msg.BoatIndex };
            if (oar != null) stream.OarMaxSpeed = oar.maxBoatSpeed;
            stream.Timeline.Enqueue(new DeckForceBatch { Seconds = msg.Seconds, Linear = msg.LinearImpulse, Angular = msg.AngularImpulse });
        }

        internal void Apply(float dt)
        {
            foreach (var pair in _streams)
            {
                var key = pair.Key;
                var stream = pair.Value;
                if (key.Hull == null || key.Body == null) { _finished.Add(key); continue; }
                if (ShipyardSync.Suspended(stream.BoatIndex)) continue;
                if (stream.Timeline.Empty)
                {
                    stream.IdleSeconds += dt;
                    if (stream.IdleSeconds >= 0.25f) _finished.Add(key);
                    continue;
                }
                stream.IdleSeconds = 0f;
                if (stream.StartDelay > 0f) { stream.StartDelay -= dt; continue; }
                stream.Timeline.Advance(dt, out var linear, out var angular);

                // Vanilla rowing adds Force * input delta per frame with ForceMode.Force.
                // Its impulse for a physics step therefore has one more fixedDeltaTime factor.
                float scale = key.OarId != 0 ? dt * Mathf.InverseLerp(stream.OarMaxSpeed, 0f, key.Body.velocity.magnitude) : 1f;
                key.Body.AddForce(key.Hull.TransformDirection(linear) * scale, ForceMode.Impulse);
                key.Body.AddTorque(key.Hull.TransformDirection(angular) * scale, ForceMode.Impulse);
            }
            RemoveFinished();
        }

        internal void RemoveActor(uint actor)
        {
            foreach (var key in _streams.Keys) if (key.Actor == actor) _finished.Add(key);
            RemoveFinished();
        }

        internal void RemoveBoat(ushort boat)
        {
            foreach (var pair in _streams) if (pair.Value.BoatIndex == boat) _finished.Add(pair.Key);
            RemoveFinished();
        }

        private void RemoveFinished()
        {
            foreach (var key in _finished) _streams.Remove(key);
            _finished.Clear();
        }

        internal void Clear() { _streams.Clear(); _finished.Clear(); }
    }
}
