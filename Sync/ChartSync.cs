using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Committed chart data belongs to the host map instance; preview/camera stay local.</summary>
    public sealed class ChartSync
    {
        public static ChartSync Instance { get; private set; }
        private readonly CoopNet _net;
        private sealed class Document
        {
            internal MapChart Map;
            internal int Id, Prefab;
            internal uint Revision = 1, DrawOrder;
            internal readonly Dictionary<object, ulong> Ids = new Dictionary<object, ulong>();
            internal readonly Dictionary<ulong, ChartMark> Marks = new Dictionary<ulong, ChartMark>();
            internal readonly OperationLedger<ChartRequestMsg> Operations = new OperationLedger<ChartRequestMsg>();
            internal readonly ItemStateGate Gate = new ItemStateGate();
        }
        private sealed class Incoming
        {
            internal readonly ChunkAccumulator<ChartMark> Chunks = new ChunkAccumulator<ChartMark>();
            internal ChartStateMsg Header;
            internal ChartMark[] Ready;
            internal string Key;
        }
        private readonly Dictionary<int, Document> _documents = new Dictionary<int, Document>();
        private readonly Dictionary<int, Incoming> _incoming = new Dictionary<int, Incoming>();
        private readonly Dictionary<MapChart, Dictionary<object, ChartMark>> _waitingInput = new Dictionary<MapChart, Dictionary<object, ChartMark>>();
        private uint _nextMark, _nextRequest;
        private float _scanTimer;
        public string LastAction { get; private set; } = "—";
        public ChartSync(CoopNet net) { _net = net; Instance = this; }
        internal void Prepare(MapChart map) { if (_net.State == LinkState.Connected) Bind(map); }
        internal static Dictionary<object, ChartMark> Capture(MapChart map)
        {
            var result = new Dictionary<object, ChartMark>();
            if (map?.chartData == null) return result;
            if (map.chartData.lines != null) foreach (var line in map.chartData.lines) if (line != null)
                result[line] = new ChartMark { X = line.startX, Y = line.startY, EndX = line.endX, EndY = line.endY, Color = line.color };
            if (map.chartData.points != null) foreach (var point in map.chartData.points) if (point != null)
                result[point] = new ChartMark { Point = true, X = point.posX, Y = point.posY };
            return result;
        }
        private static bool Same(ChartMark a, ChartMark b) => a.Point == b.Point && a.X.Equals(b.X) && a.Y.Equals(b.Y) &&
            a.EndX.Equals(b.EndX) && a.EndY.Equals(b.EndY) && a.Color == b.Color;
        private Document Bind(MapChart map)
        {
            if (map == null || map.chartData == null) return null;
            var root = ItemComponents.Read<Transform>(map, "originalParent")?.GetComponent<ShipItem>() ?? map.GetComponentInParent<ShipItem>();
            int id, prefab;
            if (root == null || ItemSync.Instance == null || !ItemSync.Instance.TrySharedIdentity(root, out id, out prefab)) return null;
            if (_documents.TryGetValue(id, out var existing)) { existing.Map = map; return existing; }
            var doc = new Document { Map = map, Id = id, Prefab = prefab };
            var initial = _waitingInput.TryGetValue(map, out var waiting) ? waiting : Capture(map);
            foreach (var pair in initial)
            {
                var mark = pair.Value; mark.Id = ++doc.DrawOrder; mark.Order = doc.DrawOrder;
                doc.Ids[pair.Key] = mark.Id; doc.Marks[mark.Id] = mark;
            }
            _documents[id] = doc;
            if (_net.Role == Role.Host) Send(doc, null);
            else if (_net.Role == Role.Client)
                _net.Broadcast(new ChartRequestMsg { InstanceId = id, PrefabIndex = prefab, Action = ChartAction.Baseline }, DeliveryMethod.ReliableOrdered);
            return doc;
        }
        public void Tick(float dt)
        {
            if (_net.State != LinkState.Connected || !GameState.playing || GameState.currentlyLoading) return;
            _scanTimer += dt;
            if (_scanTimer < 0.5f) return;
            _scanTimer = 0;
            foreach (var map in ItemSync.Instance.SharedMaps())
            {
                var doc = Bind(map); if (doc == null) continue;
                if (_waitingInput.TryGetValue(map, out var before)) { _waitingInput.Remove(map); NotifyChanged(map, before); }
                if (_incoming.TryGetValue(doc.Id, out var incoming) && incoming.Ready != null)
                { Apply(doc, incoming.Header, incoming.Ready); _incoming.Remove(doc.Id); }
            }
        }
        public void SendBaseline(NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            foreach (var map in ItemSync.Instance.SharedMaps()) { var doc = Bind(map); if (doc != null) Send(doc, peer); }
        }
        private ulong NewMark()
        { if (++_nextMark == 0) ++_nextMark; return ((ulong)_net.MyNetId << 32) | _nextMark; }
        internal void NotifyChanged(MapChart map, Dictionary<object, ChartMark> before)
        {
            if (InteractionContext.Suppressed || _net.State != LinkState.Connected || map == null || before == null) return;
            var after = Capture(map);
            var doc = Bind(map);
            if (doc == null) { if (!_waitingInput.ContainsKey(map)) _waitingInput[map] = before; return; }
            bool changed = false;
            foreach (var pair in after)
            {
                if (before.TryGetValue(pair.Key, out var old) && Same(old, pair.Value)) continue;
                var mark = pair.Value;
                if (!before.ContainsKey(pair.Key) || !doc.Ids.TryGetValue(pair.Key, out mark.Id)) mark.Id = NewMark();
                doc.Ids[pair.Key] = mark.Id;
                Submit(doc, ChartAction.Upsert, mark); changed = true;
            }
            foreach (var pair in before)
                if (!after.ContainsKey(pair.Key) && doc.Ids.TryGetValue(pair.Key, out ulong id))
                { Submit(doc, ChartAction.Remove, new ChartMark { Id = id }); changed = true; }
            if (changed) LastAction = "out chart map=" + doc.Id;
        }
        private void Submit(Document doc, ChartAction action, ChartMark mark)
        {
            if (_net.Role == Role.Host)
            { Update(doc, action, mark); Send(doc, null); return; }
            if (_net.Role != Role.Client) return;
            if (++_nextRequest == 0) ++_nextRequest; doc.Gate.Begin(_nextRequest);
            _net.Broadcast(new ChartRequestMsg { InstanceId = doc.Id, PrefabIndex = doc.Prefab, RequestId = _nextRequest, Action = action, Mark = mark }, DeliveryMethod.ReliableOrdered);
        }
        private static bool Update(Document doc, ChartAction action, ChartMark mark)
        {
            if (action == ChartAction.Remove)
            { if (!doc.Marks.Remove(mark.Id)) return false; doc.Revision++; return true; }
            if (action != ChartAction.Upsert) return false;
            if (doc.Marks.TryGetValue(mark.Id, out var previous))
            { if (Same(previous, mark)) return false; mark.Order = previous.Order; }
            else mark.Order = ++doc.DrawOrder;
            doc.Marks[mark.Id] = mark; doc.Revision++; return true;
        }
        public void OnRequest(ChartRequestMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host) return;
            uint actor = _net.PlayerNetIdForPeer(peer); if (actor == 0) return;
            var item = ItemSync.Instance.HostFindItem(msg.InstanceId, msg.PrefabIndex) as ShipItemFoldable;
            var doc = item != null ? Bind(item.mapChart) : null;
            if (doc == null)
            { peer.Send(new ChartStateMsg { InstanceId = msg.InstanceId, PrefabIndex = msg.PrefabIndex, Requester = actor, RequestId = msg.RequestId, Missing = true }, DeliveryMethod.ReliableOrdered); return; }
            if (msg.Action == ChartAction.Baseline) { Send(doc, peer); return; }
            if (doc.Operations.TryBegin(actor, msg.RequestId, msg, out var previous) && Update(doc, msg.Action, msg.Mark))
            { Replace(doc, doc.Marks.Values.OrderBy(m => m.Order).ToArray()); LastAction = "in chart map=" + doc.Id + " actor=" + actor; }
            Send(doc, null, actor, msg.RequestId);
        }
        private void Send(Document doc, NetPeer peer, uint actor = 0, uint request = 0)
        {
            var marks = doc.Marks.Values.OrderBy(m => m.Order).ToArray();
            int chunks = Math.Max(1, (marks.Length + 127) / 128);
            if (chunks > ushort.MaxValue) throw new System.IO.InvalidDataException("chart document exceeds chunk format");
            for (int i = 0; i < chunks; i++)
            {
                var msg = new ChartStateMsg { InstanceId = doc.Id, PrefabIndex = doc.Prefab, Revision = doc.Revision, Requester = actor,
                    RequestId = request, Chunk = (ushort)i, Chunks = (ushort)chunks, Marks = marks.Skip(i * 128).Take(128).ToArray() };
                if (peer == null) _net.Broadcast(msg, DeliveryMethod.ReliableOrdered); else peer.Send(msg, DeliveryMethod.ReliableOrdered);
            }
        }
        public void OnState(ChartStateMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
            if (_documents.TryGetValue(msg.InstanceId, out var doc))
            {
                if (msg.Missing) { Apply(doc, msg, Array.Empty<ChartMark>()); _incoming.Remove(msg.InstanceId); return; }
                if (doc.Prefab != msg.PrefabIndex || unchecked((int)(msg.Revision - doc.Gate.Version)) < 0) return;
                if (doc.Gate.Pending && (msg.Requester != _net.MyNetId || msg.RequestId != doc.Gate.PendingRequest)) return;
            }
            if (!_incoming.TryGetValue(msg.InstanceId, out var incoming)) _incoming[msg.InstanceId] = incoming = new Incoming();
            string key = msg.Revision + ":" + msg.Requester + ":" + msg.RequestId;
            if (incoming.Header != null && unchecked((int)(msg.Revision - incoming.Header.Revision)) < 0) return;
            if (incoming.Key != key) { incoming.Key = key; incoming.Ready = null; }
            incoming.Header = msg;
            var complete = incoming.Chunks.Add(key, msg.Chunk, msg.Chunks, msg.Marks);
            if (complete == null) return;
            if (doc == null) { incoming.Header = msg; incoming.Ready = complete; return; }
            Apply(doc, msg, complete); _incoming.Remove(msg.InstanceId);
        }
        private void Apply(Document doc, ChartStateMsg msg, ChartMark[] marks)
        {
            if (msg.Missing)
            {
                // Clear the matching technical failure without inventing replacement chart contents.
                doc.Gate.Cancel(msg.Requester, msg.RequestId, _net.MyNetId);
                Plugin.Logger.LogWarning("[ChartSync] Missing host map id=" + msg.InstanceId + " request=" + msg.RequestId); return;
            }
            if (!doc.Gate.Receive(msg.Revision, msg.Revision, msg.Requester, msg.RequestId, _net.MyNetId, out var changed, out var pose)) return;
            doc.Revision = msg.Revision; doc.Marks.Clear(); foreach (var mark in marks) doc.Marks[mark.Id] = mark;
            Replace(doc, marks);
        }
        private static void Replace(Document doc, ChartMark[] marks)
        {
            var chart = doc.Map.chartData;
            var current = Capture(doc.Map).Values.ToArray(); bool visualChanged = current.Length != marks.Length;
            if (!visualChanged) for (int i = 0; i < marks.Length; i++) if (!Same(current[i], marks[i])) { visualChanged = true; break; }
            chart.lines = new List<ChartLine>(); chart.points = new List<ChartPoint>(); doc.Ids.Clear();
            foreach (var mark in marks.OrderBy(m => m.Order))
            {
                object value;
                if (mark.Point) { var point = new ChartPoint { posX = mark.X, posY = mark.Y }; chart.points.Add(point); value = point; }
                else { var line = new ChartLine { startX = mark.X, startY = mark.Y, endX = mark.EndX, endY = mark.EndY, color = mark.Color }; chart.lines.Add(line); value = line; }
                doc.Ids[value] = mark.Id; if (mark.Order > doc.DrawOrder) doc.DrawOrder = mark.Order;
            }
            if (ItemComponents.Read<ChartLine>(doc.Map, "currentLine") == null) chart.tempLine = null;
            if (visualChanged && doc.Map.chartRenderer?.material.mainTexture is RenderTexture)
            {
                doc.Map.UpdateTexture();
                // The game's chart renderer is shared. Finish this target before a second
                // document/preview reassigns its camera target in the same frame.
                MapChartTextureRenderer.instance?.renderCam?.Render();
            }
        }
        public void Clear()
        { _documents.Clear(); _incoming.Clear(); _waitingInput.Clear(); _nextMark = _nextRequest = 0; _scanTimer = 0; LastAction = "—"; }
    }
    internal static class ChartPatches
    {
        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(MapChart), "OnActivate", new[] { typeof(Vector3) }, method => harmony.Patch(method,
                prefix: Callback(nameof(PreEdit)), postfix: Callback(nameof(PostEdit))));
            PatchHealth.Report("Charts", hooks);
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(ChartPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        private static void Report(Exception error) { try { Plugin.Logger?.LogWarning("[ChartPatches] " + error); } catch { } }
        private static void PreEdit(MapChart __instance, out Dictionary<object, ChartMark> __state)
        {
            Dictionary<object, ChartMark> state = null;
            PatchGuard.Run(() => { if (!InteractionContext.Suppressed) { ChartSync.Instance?.Prepare(__instance); state = ChartSync.Capture(__instance); } }, Report); __state = state;
        }
        private static void PostEdit(MapChart __instance, Dictionary<object, ChartMark> __state)
            => PatchGuard.Run(() => { if (__state != null && !InteractionContext.Suppressed) ChartSync.Instance?.NotifyChanged(__instance, __state); }, Report);
    }
}
