using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Host-owned, saved dirt textures. Input requests and quiet texture updates are separate.</summary>
    public sealed class DirtSync
    {
        public static DirtSync Instance { get; private set; }
        private readonly CoopNet _net;
        private sealed class Result { internal DirtOutcome Outcome = DirtOutcome.Fault; }
        private sealed class Surface
        {
            internal int Id;
            internal CleanableObject Target;
            internal byte[] Png;
            internal uint Revision = 1;
            internal bool Dirty;
            internal float LastSent = float.NegativeInfinity;
            // Latest request per actor still owed an acknowledgement in the next coalesced texture.
            internal readonly Dictionary<uint, KeyValuePair<uint, DirtOutcome>> Acks = new Dictionary<uint, KeyValuePair<uint, DirtOutcome>>();
            internal readonly ItemStateGate Gate = new ItemStateGate();
            internal readonly OperationLedger<Result> Operations = new OperationLedger<Result>();
        }
        private sealed class Incoming
        {
            internal readonly ChunkAccumulator<byte> Chunks = new ChunkAccumulator<byte>();
            internal DirtStateMsg Header;
            internal string Key;
            internal byte[] Ready;
        }
        private readonly Dictionary<int, Surface> _surfaces = new Dictionary<int, Surface>();
        private readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>();
        private uint _nextRequest;
        private float _scanTimer;
        public string LastAction { get; private set; } = "—";
        public DirtSync(CoopNet net) { _net = net; Instance = this; }
        internal bool Connected => _net.State == LinkState.Connected;
        internal bool IsClient => Connected && _net.Role == Role.Client;
        internal static CleanableObject Find(int sceneIndex)
        {
            var objects = SaveLoadManager.instance?.GetCurrentObjects();
            return objects != null && sceneIndex >= 0 && sceneIndex < objects.Length ? objects[sceneIndex]?.GetCleanable() : null;
        }
        internal static bool Registered(CleanableObject target)
        {
            if (target == null) return false;
            var saveable = ItemComponents.Read<SaveableObject>(target, "saveable");
            return saveable != null && Find(saveable.sceneIndex) == target;
        }
        private Surface Bind(CleanableObject target)
        {
            if (!Registered(target) || ItemComponents.Read<Material>(target, "dirtMaterial") == null) return null;
            int id = ItemComponents.Read<SaveableObject>(target, "saveable").sceneIndex;
            if (_surfaces.TryGetValue(id, out var old))
            {
                if (old.Target != target) { old.Target = target; if (_net.Role == Role.Host) Capture(old); }
                return old;
            }
            var surface = new Surface { Id = id, Target = target, Png = DirtPainter.Capture(target), Dirty = _net.Role == Role.Host };
            _surfaces[id] = surface;
            if (_net.Role == Role.Client)
                _net.Broadcast(new DirtRequestMsg { SceneIndex = id, Action = DirtAction.Baseline }, DeliveryMethod.ReliableOrdered);
            return surface;
        }
        internal byte[] BeforeStroke(CleanableObject target)
        {
            if (!Connected || !InteractionContext.HasInput || InteractionContext.Suppressed || Bind(target) == null) return null;
            return DirtPainter.Capture(target);
        }
        internal void AfterStroke(CleanableObject target, byte[] before, Vector2 uv, bool colorPass)
        {
            if (before == null || InteractionContext.Suppressed || !Connected) return;
            var after = DirtPainter.Capture(target);
            if (before.SequenceEqual(after)) return;
            var surface = Bind(target); if (surface == null) return;
            Submit(surface, DirtAction.Stroke, uv.x, uv.y, colorPass);
            LastAction = "out clean scene=" + surface.Id;
        }
        internal bool CleanFully(CleanableObject target)
        {
            if (!Connected || !Registered(target)) return false;
            var surface = Bind(target); if (surface == null) return false;
            if (_net.Role == Role.Client && !InteractionContext.HasInput) return true;
            byte[] before = DirtPainter.Capture(target);
            DirtPainter.Clean(target);
            if (ShipyardSync.Instance?.Confirming == true) return true; // One compound refit result, not an independent clean request.
            if (InteractionContext.HasInput && !InteractionContext.Suppressed && !before.SequenceEqual(DirtPainter.Capture(target)))
            {
                Submit(surface, DirtAction.CleanFully, 0, 0, false);
                LastAction = "out clean fully scene=" + surface.Id;
            }
            else if (_net.Role == Role.Host) Capture(surface);
            return true;
        }
        private void Submit(Surface surface, DirtAction action, float u, float v, bool colorPass)
        {
            if (_net.Role == Role.Host) { Capture(surface); return; } // Tick sends the coalesced texture.
            if (_net.Role != Role.Client) return;
            if (++_nextRequest == 0) ++_nextRequest; surface.Gate.Begin(_nextRequest);
            _net.Broadcast(new DirtRequestMsg { SceneIndex = surface.Id, Action = action, U = u, V = v,
                ColorPass = colorPass, RequestId = _nextRequest }, DeliveryMethod.ReliableOrdered);
        }
        internal void TextureChanged(CleanableObject target)
        {
            if (!Connected || _net.Role != Role.Host) return;
            var surface = Bind(target); if (surface != null) Capture(surface);
        }
        private static bool Capture(Surface surface)
        {
            byte[] png = DirtPainter.Capture(surface.Target);
            if (surface.Png != null && surface.Png.SequenceEqual(png)) return false;
            surface.Png = png; surface.Revision++; surface.Dirty = true; return true;
        }
        public void Tick(float dt)
        {
            if (!Connected || !GameState.playing || GameState.currentlyLoading) return;
            _scanTimer += dt;
            if (_scanTimer >= 0.5f)
            {
                _scanTimer = 0;
                foreach (var obj in SaveLoadManager.instance?.GetCurrentObjects() ?? Array.Empty<SaveableObject>())
                    if (obj?.GetCleanable() != null) Bind(obj.GetCleanable());
            }
            foreach (var surface in _surfaces.Values)
            {
                if (surface.Target == null) continue;
                if (_net.Role == Role.Host && surface.Dirty && Time.unscaledTime - surface.LastSent >= SendInterval) Flush(surface);
                if (_net.Role == Role.Client && _incoming.TryGetValue(surface.Id, out var incoming) && incoming.Ready != null &&
                    Apply(surface, incoming.Header, incoming.Ready)) _incoming.Remove(surface.Id);
            }
        }
        private const float SendInterval = 0.25f;
        private void Flush(Surface surface)
        {
            if (surface.Acks.Count == 0) { Send(surface, null); return; }
            foreach (var ack in new List<KeyValuePair<uint, KeyValuePair<uint, DirtOutcome>>>(surface.Acks))
                Send(surface, null, ack.Key, ack.Value.Key, ack.Value.Value);
            surface.Acks.Clear();
        }
        public void OnRequest(DirtRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            var surface = Bind(Find(msg.SceneIndex));
            if (surface == null)
            { peer.Send(new DirtStateMsg { SceneIndex = msg.SceneIndex, Requester = actor, RequestId = msg.RequestId,
                Outcome = DirtOutcome.MissingObject }, DeliveryMethod.ReliableOrdered); return; }
            if (msg.Action == DirtAction.Baseline) { Capture(surface); Send(surface, peer); return; }
            var result = new Result();
            if (!surface.Operations.TryBegin(actor, msg.RequestId, result, out var previous))
            { Send(surface, peer, actor, msg.RequestId, previous.Outcome); return; }
            byte[] before = surface.Png;
            try
            {
                using (InteractionContext.Begin(InteractionSource.RemoteApply))
                {
                    if (msg.Action == DirtAction.Stroke) DirtPainter.Stroke(surface.Target, msg.U, msg.V, msg.ColorPass);
                    else if (msg.Action == DirtAction.CleanFully) DirtPainter.Clean(surface.Target);
                }
                result.Outcome = DirtOutcome.Applied;
            }
            catch (Exception error) { Plugin.Logger.LogWarning("[DirtSync] host scene=" + surface.Id + " request=" + msg.RequestId + " effect fault: " + error); }
            finally
            {
                // Even a partially applied Unity operation is acknowledged with its actual current texture.
                try
                {
                    Capture(surface);
                    if (!before.SequenceEqual(surface.Png)) LastAction = "in clean scene=" + surface.Id + " actor=" + actor;
                    // A scrubbing guest sends a stroke per frame; the texture goes out coalesced from Tick.
                    surface.Acks[actor] = new KeyValuePair<uint, DirtOutcome>(msg.RequestId, result.Outcome); surface.Dirty = true;
                }
                catch (Exception error)
                {
                    result.Outcome = DirtOutcome.Fault;
                    peer.Send(new DirtStateMsg { SceneIndex = surface.Id, Requester = actor, RequestId = msg.RequestId,
                        Outcome = DirtOutcome.Fault }, DeliveryMethod.ReliableOrdered);
                    Plugin.Logger.LogWarning("[DirtSync] host scene=" + surface.Id + " result fault: " + error);
                }
            }
        }
        private void Send(Surface surface, NetPeer peer, uint actor = 0, uint request = 0, DirtOutcome outcome = DirtOutcome.Applied)
        {
            int chunks = Math.Max(1, (surface.Png.Length + 4095) / 4096);
            if (chunks > ushort.MaxValue) throw new System.IO.InvalidDataException("dirt texture exceeds chunk format");
            for (int i = 0; i < chunks; i++)
            {
                var msg = new DirtStateMsg { SceneIndex = surface.Id, Revision = surface.Revision, Requester = actor, RequestId = request,
                    Outcome = outcome, Chunk = (ushort)i, Chunks = (ushort)chunks, Png = surface.Png.Skip(i * 4096).Take(4096).ToArray() };
                if (peer == null) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered); else peer.Send(msg, DeliveryMethod.ReliableOrdered);
            }
            if (peer == null) { surface.Dirty = false; surface.LastSent = Time.unscaledTime; }
        }
        public void OnState(DirtStateMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
            _surfaces.TryGetValue(msg.SceneIndex, out var surface);
            if (msg.Outcome == DirtOutcome.MissingObject || (msg.Outcome == DirtOutcome.Fault && msg.Png.Length == 0))
            {
                surface?.Gate.Cancel(msg.Requester, msg.RequestId, _net.MyNetId);
                if (surface == null || !surface.Gate.Pending) _incoming.Remove(msg.SceneIndex);
                Plugin.Logger.LogWarning("[DirtSync] host outcome=" + msg.Outcome + " scene=" + msg.SceneIndex + " request=" + msg.RequestId); return;
            }
            if (surface != null && !Eligible(surface, msg)) return;
            if (!_incoming.TryGetValue(msg.SceneIndex, out var incoming)) _incoming[msg.SceneIndex] = incoming = new Incoming();
            if (incoming.Header != null && unchecked((int)(msg.Revision - incoming.Header.Revision)) < 0) return;
            string key = msg.Revision + ":" + msg.Requester + ":" + msg.RequestId + ":" + (byte)msg.Outcome;
            if (incoming.Key != key) { incoming.Key = key; incoming.Ready = null; }
            incoming.Header = msg;
            byte[] complete = incoming.Chunks.Add(key, msg.Chunk, msg.Chunks, msg.Png);
            if (complete == null) return;
            incoming.Ready = complete;
            if (surface != null && Apply(surface, msg, complete)) _incoming.Remove(msg.SceneIndex);
        }
        private bool Eligible(Surface surface, DirtStateMsg msg) => unchecked((int)(msg.Revision - surface.Gate.Version)) >= 0 &&
            (!surface.Gate.Pending || (msg.Requester == _net.MyNetId && msg.RequestId == surface.Gate.PendingRequest));
        private bool Apply(Surface surface, DirtStateMsg msg, byte[] png)
        {
            if (ShipyardSync.PendingClean(surface.Target)) return false;
            if (!Eligible(surface, msg)) return true; // Finished obsolete transfer cannot replace prediction.
            if (surface.Target == null || ItemComponents.Read<Material>(surface.Target, "dirtMaterial") == null) return false;
            if (!DirtPainter.Capture(surface.Target).SequenceEqual(png))
            {
                var texture = DirtPainter.Decode(png);
                try { using (InteractionContext.Begin(InteractionSource.RemoteApply)) surface.Target.ApplyNewDirtTexture(texture); texture = null; }
                finally { if (texture != null) UnityEngine.Object.Destroy(texture); }
            }
            surface.Gate.Receive(msg.Revision, msg.Revision, msg.Requester, msg.RequestId, _net.MyNetId, out var semantic, out var pose);
            surface.Revision = msg.Revision; surface.Png = png; // No interaction diagnostics for state/echo/baseline.
            return true;
        }
        public void Clear()
        { _surfaces.Clear(); _incoming.Clear(); _nextRequest = 0; _scanTimer = 0; LastAction = "—"; }

        internal void ApplyRefit(CleanableObject target, byte[] png)
        {
            if (target == null) return;
            var texture = DirtPainter.Decode(png);
            try { using (InteractionContext.Begin(InteractionSource.RemoteApply)) target.ApplyNewDirtTexture(texture); texture = null; }
            finally { if (texture != null) UnityEngine.Object.Destroy(texture); }
            var surface = Bind(target); if (surface == null) return;
            _incoming.Remove(surface.Id); surface.Png = png;
            if (_net.Role == Role.Host) { surface.Revision++; surface.Dirty = true; }
        }
    }
}
