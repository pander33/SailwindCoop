using System;
using System.Collections.Generic;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// What hangs in the other players' belts. A belt item is player-local: it leaves the shared item
    /// set when it goes into a slot (<see cref="ItemSync.OnLocalInventory"/>), so the others have no
    /// copy of it, and in the game the five slots are only a panel at the camera
    /// (<c>GPButtonInventorySlot</c>), not places on the body.
    /// <para>Each machine sends what its own player has in the slots (<see cref="BeltStateMsg"/>:
    /// prefab index per slot, a bit per slot for a burning lamp) on change and every few seconds
    /// for later arrivals. The others hang a visual copy of each item at the belt of that player's
    /// avatar: meshes and materials taken from the prefab, no game script, no collider, no physics.
    /// A burning lamp gets a light like the prefab's, so a lantern on the belt lights the deck for
    /// the crew as it does for its owner.</para>
    /// </summary>
    public sealed class PlayerBelt
    {
        public static PlayerBelt Instance { get; private set; }

        private const float ScanSeconds = 0.5f;
        private const float RepeatSeconds = 5f;
        /// <summary>How far the slots sit from the middle of the hips, and how much smaller a belt
        /// copy is than the item.</summary>
        private const float Radius = 0.2f, ItemScale = 0.7f;
        /// <summary>Slot places around the waist, degrees from straight ahead (right is positive).</summary>
        private static readonly float[] SlotAngles = { -100f, -55f, 55f, 100f, 180f };

        private readonly CoopNet _net;
        private readonly PlayerSync _players;

        private readonly int[] _sentPrefabs = new int[BeltStateMsg.Slots];
        private byte _sentLit;
        private bool _sentOnce;
        private float _scanTimer, _repeatTimer;

        private sealed class View
        {
            public readonly int[] Prefabs = new int[BeltStateMsg.Slots];
            public byte Lit;
            public bool Dirty = true;
            public GameObject Avatar;
            public Transform Hips;
            public GameObject Root;
            public readonly Copy[] Slots = new Copy[BeltStateMsg.Slots];
        }

        private sealed class Copy
        {
            public int Prefab;
            public GameObject Go;
            public Light Light;
            public Renderer Paper;
            public Material PaperOn, PaperOff;
        }

        private readonly Dictionary<uint, View> _views = new Dictionary<uint, View>();

        public PlayerBelt(CoopNet net, PlayerSync players)
        {
            _net = net;
            _players = players;
            Instance = this;
        }

        public void Tick(float dt)
        {
            if (_net.Role == Role.None || _net.State != LinkState.Connected) return;
            SendLocal(dt);
            foreach (var pair in _views) PlaceView(pair.Key, pair.Value);
        }

        // ---------------------------------------------------------------- local belt

        private void SendLocal(float dt)
        {
            _scanTimer += dt;
            _repeatTimer += dt;
            if (_scanTimer < ScanSeconds) return;
            _scanTimer = 0f;
            if (!GameState.playing) return;

            var prefabs = new int[BeltStateMsg.Slots];
            byte lit = 0;
            bool any = false;
            var slots = GPButtonInventorySlot.inventorySlots;
            for (int i = 0; i < BeltStateMsg.Slots && slots != null && i < slots.Length; i++)
            {
                var item = slots[i] != null ? slots[i].currentItem : null;
                if (item == null) continue;
                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable == null || saveable.prefabIndex <= 0) continue;
                prefabs[i] = saveable.prefabIndex;
                any = true;
                if (item is ShipItemLight && item.amount >= 1f && item.health > 0f) lit |= (byte)(1 << i);
            }

            bool changed = !_sentOnce || lit != _sentLit;
            for (int i = 0; i < BeltStateMsg.Slots && !changed; i++) changed = prefabs[i] != _sentPrefabs[i];
            if (!changed && !(any && _repeatTimer >= RepeatSeconds)) return;

            Array.Copy(prefabs, _sentPrefabs, prefabs.Length);
            _sentLit = lit;
            _sentOnce = true;
            _repeatTimer = 0f;
            _net.Broadcast(new BeltStateMsg { NetId = _net.MyNetId, Prefabs = prefabs, Lit = lit },
                           LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        // ---------------------------------------------------------------- remote belts

        public void OnBeltState(BeltStateMsg msg, LiteNetLib.NetPeer fromPeer)
        {
            try
            {
                if (_net.Role == Role.Host)
                {
                    uint sender = _net.PlayerNetIdForPeer(fromPeer);
                    if (sender == 0) return;
                    msg.NetId = sender;
                    _net.RelayExcept(msg, fromPeer, LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                if (msg.NetId == 0 || msg.NetId == _net.MyNetId || msg.Prefabs == null) return;
                if (!_views.TryGetValue(msg.NetId, out var view)) _views[msg.NetId] = view = new View();
                bool changed = view.Lit != msg.Lit;
                for (int i = 0; i < BeltStateMsg.Slots; i++)
                {
                    int prefab = i < msg.Prefabs.Length ? msg.Prefabs[i] : 0;
                    if (view.Prefabs[i] != prefab) { view.Prefabs[i] = prefab; changed = true; }
                }
                view.Lit = msg.Lit;
                if (changed) view.Dirty = true;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[PlayerBelt] OnBeltState: " + e.Message); }
        }

        private void PlaceView(uint netId, View view)
        {
            try
            {
                var avatar = _players.RemoteAvatarObject(netId);
                if (avatar != view.Avatar || view.Root == null)
                {
                    // A new model (the player changed avatar, or came back into range): start over.
                    Destroy(view);
                    view.Avatar = avatar;
                    view.Dirty = true;
                    if (avatar == null) return;
                    view.Hips = FindDeep(avatar.transform, "Hips") ?? FindDeep(avatar.transform, "Spine_01");
                    view.Root = new GameObject("CoopBelt");
                    view.Root.layer = avatar.layer;
                    view.Root.transform.SetParent(avatar.transform, false);
                }

                if (view.Dirty)
                {
                    view.Dirty = false;
                    for (int i = 0; i < BeltStateMsg.Slots; i++) Fill(view, i, avatar.layer);
                }

                // Meters, whatever the model's scale; at the hips, turned with the body.
                Transform root = view.Root.transform;
                Vector3 lossy = avatar.transform.lossyScale;
                root.localScale = new Vector3(1f / Mathf.Max(0.01f, lossy.x), 1f / Mathf.Max(0.01f, lossy.y), 1f / Mathf.Max(0.01f, lossy.z));
                root.position = view.Hips != null ? view.Hips.position : avatar.transform.position + avatar.transform.up * 0.95f;
                root.rotation = avatar.transform.rotation;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerBelt] NetId=" + netId + ": " + e.Message);
                Destroy(view);
                view.Avatar = null;
            }
        }

        private static void Fill(View view, int slot, int layer)
        {
            int prefab = view.Prefabs[slot];
            var copy = view.Slots[slot];
            if (copy != null && copy.Prefab != prefab)
            {
                if (copy.Go != null) UnityEngine.Object.Destroy(copy.Go);
                view.Slots[slot] = copy = null;
            }
            if (copy == null && prefab > 0)
            {
                copy = Build(prefab, layer);
                if (copy != null)
                {
                    float angle = SlotAngles[slot] * Mathf.Deg2Rad;
                    var t = copy.Go.transform;
                    t.SetParent(view.Root.transform, false);
                    t.localPosition = new Vector3(Mathf.Sin(angle) * Radius, -0.05f, Mathf.Cos(angle) * Radius);
                    t.localRotation = Quaternion.Euler(0f, SlotAngles[slot], 0f);
                    t.localScale = Vector3.one * ItemScale;
                }
                view.Slots[slot] = copy;
            }
            if (copy == null) return;
            bool lit = (view.Lit & (1 << slot)) != 0;
            if (copy.Light != null) copy.Light.enabled = lit;
            if (copy.Paper != null && copy.PaperOn != null && copy.PaperOff != null)
                copy.Paper.sharedMaterial = lit ? copy.PaperOn : copy.PaperOff;
        }

        private static AccessTools.FieldRef<ShipItemLight, Light> _lampLight;
        private static AccessTools.FieldRef<ShipItemLight, Renderer> _lampPaper;
        private static AccessTools.FieldRef<ShipItemLight, Material> _lampPaperOff;
        private static bool _lampFieldsTried;

        /// <summary>The lamp's private light and paper fields, looked up once; null when the game
        /// renamed them (the copy then has no light or stays with the prefab's paper).</summary>
        private static void ResolveLampFields()
        {
            if (_lampFieldsTried) return;
            _lampFieldsTried = true;
            try { _lampLight = AccessTools.FieldRefAccess<ShipItemLight, Light>("light"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[PlayerBelt] ShipItemLight.light: " + e.Message); }
            try { _lampPaper = AccessTools.FieldRefAccess<ShipItemLight, Renderer>("paperRenderer"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[PlayerBelt] ShipItemLight.paperRenderer: " + e.Message); }
            try { _lampPaperOff = AccessTools.FieldRefAccess<ShipItemLight, Material>("paperOffMat"); }
            catch (Exception e) { Plugin.Logger.LogWarning("[PlayerBelt] ShipItemLight.paperOffMat: " + e.Message); }
        }

        /// <summary>A visual copy of a prefab: its visible meshes with their materials, and the light
        /// of a lamp. Nothing of the game runs on it.</summary>
        private static Copy Build(int prefabIndex, int layer)
        {
            var dir = PrefabsDirectory.instance != null ? PrefabsDirectory.instance.directory : null;
            if (dir == null || prefabIndex <= 0 || prefabIndex >= dir.Length || dir[prefabIndex] == null) return null;
            GameObject prefab = dir[prefabIndex];
            Transform origin = prefab.transform;
            var copy = new Copy { Prefab = prefabIndex, Go = new GameObject(prefab.name + " (belt)") };
            copy.Go.layer = layer;

            var lamp = prefab.GetComponent<ShipItemLight>();
            Renderer paper = null;
            if (lamp != null)
            {
                ResolveLampFields();
                try
                {
                    paper = _lampPaper != null ? _lampPaper(lamp) : null;
                    copy.PaperOff = _lampPaperOff != null ? _lampPaperOff(lamp) : null;
                    copy.PaperOn = paper != null ? paper.sharedMaterial : null;
                }
                catch (Exception e) { Plugin.Logger.LogWarning("[PlayerBelt] lamp fields: " + e.Message); }
            }

            foreach (var renderer in VisibleRenderers(prefab))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var part = new GameObject(renderer.name);
                part.layer = layer;
                PlaceLike(part.transform, copy.Go.transform, origin, renderer.transform);
                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var mesh = part.AddComponent<MeshRenderer>();
                mesh.sharedMaterials = renderer.sharedMaterials;
                mesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (renderer == paper) copy.Paper = mesh;
            }

            Light source = null;
            if (lamp != null)
            {
                try { source = _lampLight != null ? _lampLight(lamp) : null; }
                catch { source = null; }
            }
            if (source == null) source = prefab.GetComponentInChildren<Light>(true);
            if (source != null && lamp != null)
            {
                var glow = new GameObject("light");
                glow.layer = layer;
                PlaceLike(glow.transform, copy.Go.transform, origin, source.transform);
                var light = glow.AddComponent<Light>();
                light.type = source.type;
                light.color = source.color;
                light.intensity = source.intensity;
                light.range = source.range;
                light.spotAngle = source.spotAngle;
                light.renderMode = source.renderMode;
                light.cullingMask = source.cullingMask;
                light.shadows = LightShadows.None;
                light.enabled = false;
                copy.Light = light;
            }
            return copy;
        }

        /// <summary>Renderers a player sees on the item: enabled, in active objects, and of the
        /// first level of detail only, so a lower level is not drawn over it.</summary>
        private static List<MeshRenderer> VisibleRenderers(GameObject prefab)
        {
            var result = new List<MeshRenderer>();
            HashSet<Renderer> lowerDetail = null;
            foreach (var group in prefab.GetComponentsInChildren<LODGroup>(false))
            {
                var levels = group.GetLODs();
                for (int i = 1; i < levels.Length; i++)
                    foreach (var r in levels[i].renderers)
                        if (r != null) (lowerDetail ?? (lowerDetail = new HashSet<Renderer>())).Add(r);
            }
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(false))
                if (r.enabled && (lowerDetail == null || !lowerDetail.Contains(r))) result.Add(r);
            return result;
        }

        /// <summary>Puts <paramref name="part"/> under <paramref name="parent"/> where
        /// <paramref name="source"/> sits relative to the prefab root <paramref name="origin"/>.</summary>
        private static void PlaceLike(Transform part, Transform parent, Transform origin, Transform source)
        {
            part.SetParent(parent, false);
            part.localPosition = origin.InverseTransformPoint(source.position);
            part.localRotation = Quaternion.Inverse(origin.rotation) * source.rotation;
            Vector3 o = origin.lossyScale, s = source.lossyScale;
            part.localScale = new Vector3(s.x / Mathf.Max(1e-4f, o.x), s.y / Mathf.Max(1e-4f, o.y), s.z / Mathf.Max(1e-4f, o.z));
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void Destroy(View view)
        {
            if (view.Root != null) UnityEngine.Object.Destroy(view.Root);
            view.Root = null;
            view.Hips = null;
            for (int i = 0; i < view.Slots.Length; i++) view.Slots[i] = null;
        }

        public void PlayerLeft(uint netId)
        {
            if (_views.TryGetValue(netId, out var view)) Destroy(view);
            _views.Remove(netId);
        }

        public void Clear()
        {
            foreach (var view in _views.Values) Destroy(view);
            _views.Clear();
            _sentOnce = false;
            _scanTimer = _repeatTimer = 0f;
            Array.Clear(_sentPrefabs, 0, _sentPrefabs.Length);
            _sentLit = 0;
        }
    }
}
