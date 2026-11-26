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
        private static readonly System.Reflection.FieldInfo ExtraCleanables = typeof(SaveableObject).GetField("extraCleanables");
        private readonly CoopNet _net;
        private readonly Func<bool> _guestWorldLoaded;
        private sealed class Result { internal DirtOutcome Outcome = DirtOutcome.Fault; }
        private sealed class Surface
        {
            internal int Id;
            internal int Index;
            internal long Key => SurfaceKey(Id, Index);
            internal CleanableObject Target;
            internal byte[] Png;
            internal uint Revision = 1;
            internal bool Dirty;
            internal bool NeedsCapture;
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
        private readonly Dictionary<long, Surface> _surfaces = new Dictionary<long, Surface>();
        private readonly Dictionary<CleanableObject, Surface> _byTarget = new Dictionary<CleanableObject, Surface>();
        private readonly Dictionary<long, Incoming> _incoming = new Dictionary<long, Incoming>();
        private readonly HashSet<long> _pendingRefitCleans = new HashSet<long>();
        private uint _nextRequest;
        private float _scanTimer;
        public string LastAction { get; private set; } = "—";
        public DirtSync(CoopNet net, Func<bool> guestWorldLoaded)
        { _net = net; _guestWorldLoaded = guestWorldLoaded; Instance = this; }
        internal bool Connected => _net.State == LinkState.Connected;
        private bool WorldReady => Connected && GameState.playing && !GameState.currentlyLoading &&
            (_net.Role != Role.Client || _guestWorldLoaded());
        internal bool IsClient => _net.Role == Role.Client && WorldReady;
        private static long SurfaceKey(int sceneIndex, int surfaceIndex) => ((long)sceneIndex << 32) | (uint)surfaceIndex;
        internal static CleanableObject[] ExtraSurfaces(SaveableObject saveable) =>
            saveable != null ? ExtraCleanables?.GetValue(saveable) as CleanableObject[] ?? Array.Empty<CleanableObject>() :
            Array.Empty<CleanableObject>();
        private static CleanableObject Find(int sceneIndex, int surfaceIndex)
        {
            var objects = SaveLoadManager.instance?.GetCurrentObjects();
            if (objects == null || sceneIndex < 0 || sceneIndex >= objects.Length || objects[sceneIndex] == null) return null;
            var owner = objects[sceneIndex];
            if (surfaceIndex == -1) return owner.GetCleanable();
            var extra = ExtraSurfaces(owner);
            return surfaceIndex >= 0 && surfaceIndex < extra.Length ? extra[surfaceIndex] : null;
        }
        private static bool Address(CleanableObject target, out int id, out int index)
        {
            id = 0; index = -1;
            if (target == null) return false;
            var saveable = ItemComponents.Read<SaveableObject>(target, "saveable");
            if (saveable == null) return false;
            id = saveable.sceneIndex;
            if (saveable.GetCleanable() != target)
            {
                index = Array.IndexOf(ExtraSurfaces(saveable), target);
                if (index < 0) return false;
            }
            return Find(id, index) == target;
        }
        internal static bool Registered(CleanableObject target) => Address(target, out _, out _);
        private Surface Bind(CleanableObject target)
        {
            if (target == null) return null;
            if (_byTarget.TryGetValue(target, out var cached) && cached.Target == target) return cached;
            if (!Address(target, out int id, out int index) || ItemComponents.Read<Material>(target, "dirtMaterial") == null ||
                !DirtPainter.HasTexture(target)) return null;
            long key = SurfaceKey(id, index);
            if (_surfaces.TryGetValue(key, out var old))
            {
                if (old.Target != target)
                {
                    if (!ReferenceEquals(old.Target, null)) _byTarget.Remove(old.Target);
                    old.Target = target;
                    _byTarget[target] = old;
                    if (_net.Role == Role.Host) { Capture(old); old.Dirty = true; }
                    else RequestBaseline(old);
                }
                return old;
            }
            var surface = new Surface { Id = id, Index = index, Target = target, Png = DirtPainter.Capture(target), Dirty = _net.Role == Role.Host };
            _surfaces[key] = surface;
            _byTarget[target] = surface;
            if (_net.Role == Role.Client) RequestBaseline(surface);
            return surface;
        }
        private void RequestBaseline(Surface surface) => _net.Broadcast(new DirtRequestMsg
        {
            SceneIndex = surface.Id, SurfaceIndex = surface.Index, Action = DirtAction.Baseline
        }, DeliveryMethod.ReliableOrdered);
        internal bool BeforeStroke(CleanableObject target)
        {
            if (!WorldReady || !InteractionContext.HasInput || InteractionContext.Suppressed) return false;
            var surface = Bind(target);
            if (surface == null) return false;
            FinishPendingRefitClean(surface);
            return true;
        }
        internal void AfterStroke(CleanableObject target, Vector2 uv, bool colorPass)
        {
            if (InteractionContext.Suppressed || !WorldReady) return;
            var surface = Bind(target); if (surface == null) return;
            Submit(surface, DirtAction.Stroke, uv.x, uv.y, colorPass);
            LastAction = "out clean scene=" + surface.Id;
        }
        internal bool CleanFully(CleanableObject target)
        {
            if (!WorldReady || !Address(target, out int scene, out int index)) return false;
            if (ShipyardRefitPatches.RecordCleaning(scene, index))
            {
                if (_net.Role == Role.Host) ApplyRefitClean(scene, index);
                return true;
            }
            var surface = Bind(target); if (surface == null) return false;
            if (_net.Role == Role.Client && !InteractionContext.HasInput) return true;
            byte[] before = DirtPainter.Capture(target);
            DirtPainter.Clean(target);
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
            if (_net.Role == Role.Host) { surface.NeedsCapture = true; surface.Dirty = true; return; } // Tick captures & sends the coalesced texture.
            if (_net.Role != Role.Client) return;
            if (++_nextRequest == 0) ++_nextRequest; surface.Gate.Begin(_nextRequest);
            _net.Broadcast(new DirtRequestMsg { SceneIndex = surface.Id, SurfaceIndex = surface.Index, Action = action, U = u, V = v,
                ColorPass = colorPass, RequestId = _nextRequest }, DeliveryMethod.ReliableOrdered);
        }
        internal void TextureChanged(CleanableObject target)
        {
            if (!Connected || _net.Role != Role.Host) return;
            var surface = Bind(target);
            if (surface == null) return;
            surface.NeedsCapture = true;
            surface.Dirty = true;
        }
        private static bool Capture(Surface surface)
        {
            if (surface == null || !DirtPainter.HasTexture(surface.Target)) return false;
            surface.NeedsCapture = false;
            byte[] png = DirtPainter.Capture(surface.Target);
            if (surface.Png != null && surface.Png.SequenceEqual(png)) return false;
            surface.Png = png; surface.Revision++; surface.Dirty = true; return true;
        }
        public void Tick(float dt)
        {
            if (!WorldReady) return;
            _scanTimer += dt;
            if (_scanTimer >= 0.5f)
            {
                _scanTimer = 0;
                if (_pendingRefitCleans.Count != 0) _pendingRefitCleans.RemoveWhere(RetryRefitClean);
                foreach (var obj in SaveLoadManager.instance?.GetCurrentObjects() ?? Array.Empty<SaveableObject>())
                {
                    if (obj == null) continue;
                    Bind(obj.GetCleanable());
                    foreach (var target in ExtraSurfaces(obj)) Bind(target);
                }
            }
            foreach (var surface in _surfaces.Values)
            {
                if (surface.Target == null) continue;
                if (_net.Role == Role.Host && surface.Dirty && Time.unscaledTime - surface.LastSent >= SendInterval) Flush(surface);
                if (_net.Role == Role.Client && _incoming.TryGetValue(surface.Key, out var incoming) && incoming.Ready != null &&
                    Apply(surface, incoming.Header, incoming.Ready)) _incoming.Remove(surface.Key);
            }
        }
        private const float SendInterval = 0.25f;
        private void Flush(Surface surface)
        {
            if (surface.NeedsCapture)
            {
                bool changed;
                try { changed = Capture(surface); }
                catch (Exception error)
                {
                    surface.NeedsCapture = false;
                    surface.Dirty = false;
                    surface.LastSent = Time.unscaledTime;
                    if (surface.Acks.Count > 0)
                    {
                        foreach (var ack in new List<KeyValuePair<uint, KeyValuePair<uint, DirtOutcome>>>(surface.Acks))
                            _net.Broadcast(new DirtStateMsg { SceneIndex = surface.Id, SurfaceIndex = surface.Index,
                                Requester = ack.Key, RequestId = ack.Value.Key, Outcome = DirtOutcome.Fault }, DeliveryMethod.ReliableOrdered);
                        surface.Acks.Clear();
                    }
                    Plugin.Logger.LogWarning("[DirtSync] host scene=" + surface.Id + " flush capture fault: " + error);
                    return;
                }
                if (!changed && surface.Acks.Count == 0)
                {
                    surface.Dirty = false;
                    surface.LastSent = Time.unscaledTime;
                    return;
                }
            }
            if (surface.Acks.Count == 0) { Send(surface, null); return; }
            foreach (var ack in new List<KeyValuePair<uint, KeyValuePair<uint, DirtOutcome>>>(surface.Acks))
                Send(surface, null, ack.Key, ack.Value.Key, ack.Value.Value);
            surface.Acks.Clear();
        }
        public void OnRequest(DirtRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            var surface = Bind(Find(msg.SceneIndex, msg.SurfaceIndex));
            if (surface == null)
            { peer.Send(new DirtStateMsg { SceneIndex = msg.SceneIndex, SurfaceIndex = msg.SurfaceIndex, Requester = actor, RequestId = msg.RequestId,
                Outcome = DirtOutcome.MissingObject }, DeliveryMethod.ReliableOrdered); return; }
            FinishPendingRefitClean(surface);
            if (msg.Action == DirtAction.Baseline) { Capture(surface); Send(surface, peer); return; }
            var result = new Result();
            if (!surface.Operations.TryBegin(actor, msg.RequestId, result, out var previous))
            { if (surface.NeedsCapture) Capture(surface); Send(surface, peer, actor, msg.RequestId, previous.Outcome); return; }
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
                // Defer PNG encoding to Tick/Flush (4 Hz) so per-frame scrubbing never stalls the main thread.
                surface.NeedsCapture = true;
                LastAction = "in clean scene=" + surface.Id + " actor=" + actor;
                surface.Acks[actor] = new KeyValuePair<uint, DirtOutcome>(msg.RequestId, result.Outcome);
                surface.Dirty = true;
            }
        }
        private void Send(Surface surface, NetPeer peer, uint actor = 0, uint request = 0, DirtOutcome outcome = DirtOutcome.Applied)
        {
            int total = surface.Png.Length;
            int chunks = Math.Max(1, (total + 4095) / 4096);
            if (chunks > DirtStateMsg.MaxChunks) throw new System.IO.InvalidDataException("dirt texture exceeds chunk format");
            for (int i = 0; i < chunks; i++)
            {
                int offset = i * 4096;
                int len = Math.Max(0, Math.Min(4096, total - offset));
                byte[] slice = len == 0 ? Array.Empty<byte>() : new byte[len];
                if (len > 0) Buffer.BlockCopy(surface.Png, offset, slice, 0, len);
                var msg = new DirtStateMsg { SceneIndex = surface.Id, SurfaceIndex = surface.Index, Revision = surface.Revision, Requester = actor, RequestId = request,
                    Outcome = outcome, Chunk = (ushort)i, Chunks = (ushort)chunks, Png = slice };
                if (peer == null) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered); else peer.Send(msg, DeliveryMethod.ReliableOrdered);
            }
            if (peer == null) { surface.Dirty = false; surface.LastSent = Time.unscaledTime; }
        }
        public void OnState(DirtStateMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
            long address = SurfaceKey(msg.SceneIndex, msg.SurfaceIndex);
            _surfaces.TryGetValue(address, out var surface);
            if (msg.Outcome == DirtOutcome.MissingObject || (msg.Outcome == DirtOutcome.Fault && msg.Png.Length == 0))
            {
                surface?.Gate.Cancel(msg.Requester, msg.RequestId, _net.MyNetId);
                // A failed lookup or request does not supersede a complete authoritative texture.
                Plugin.Logger.LogWarning("[DirtSync] host outcome=" + msg.Outcome + " scene=" + msg.SceneIndex + " request=" + msg.RequestId); return;
            }
            if (surface != null && !Eligible(surface, msg)) return;
            if (!_incoming.TryGetValue(address, out var incoming)) _incoming[address] = incoming = new Incoming();
            if (incoming.Header != null && unchecked((int)(msg.Revision - incoming.Header.Revision)) < 0) return;
            string key = msg.Revision + ":" + msg.Requester + ":" + msg.RequestId + ":" + (byte)msg.Outcome;
            if (incoming.Key != key) { incoming.Key = key; incoming.Ready = null; }
            incoming.Header = msg;
            byte[] complete = incoming.Chunks.Add(key, msg.Chunk, msg.Chunks, msg.Png);
            if (complete == null) return;
            incoming.Ready = complete;
            if (WorldReady && surface != null && Apply(surface, msg, complete)) _incoming.Remove(address);
        }
        private bool Eligible(Surface surface, DirtStateMsg msg) => unchecked((int)(msg.Revision - surface.Gate.Version)) >= 0 &&
            (!surface.Gate.Pending || (msg.Requester == _net.MyNetId && msg.RequestId == surface.Gate.PendingRequest));
        private bool Apply(Surface surface, DirtStateMsg msg, byte[] png)
        {
            if (!Eligible(surface, msg)) return true; // Finished obsolete transfer cannot replace prediction.
            if (surface.Target == null || ItemComponents.Read<Material>(surface.Target, "dirtMaterial") == null ||
                !DirtPainter.HasTexture(surface.Target)) return false;
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
        { _surfaces.Clear(); _byTarget.Clear(); _incoming.Clear(); _pendingRefitCleans.Clear(); _nextRequest = 0; _scanTimer = 0; LastAction = "—"; }

        internal void ApplyRefitClean(int scene, int index)
        {
            if (_net.Role != Role.Host) return;
            // An option enabled by this very order can still be waiting for CleanableObject.Start.
            long address = SurfaceKey(scene, index);
            _pendingRefitCleans.Add(address);
            if (RetryRefitClean(address)) _pendingRefitCleans.Remove(address);
        }
        private bool TryApplyRefitClean(long address)
        {
            var surface = Bind(Find((int)(address >> 32), unchecked((int)address)));
            if (surface == null) return false;
            ApplyRefitClean(surface);
            return true;
        }
        private bool RetryRefitClean(long address)
        {
            try { return TryApplyRefitClean(address); }
            catch (Exception error) { ShipyardSync.Report("deferred refit clean", error); return false; }
        }
        private void FinishPendingRefitClean(Surface surface)
        {
            if (!_pendingRefitCleans.Contains(surface.Key)) return;
            ApplyRefitClean(surface);
            _pendingRefitCleans.Remove(surface.Key);
        }
        private static void ApplyRefitClean(Surface surface)
        {
            using (InteractionContext.Begin(InteractionSource.RemoteApply)) DirtPainter.Clean(surface.Target);
            Capture(surface);
            surface.Dirty = true;
        }
    }
}
