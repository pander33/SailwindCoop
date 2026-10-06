using System;
using System.Reflection;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The F8 "Teleport to boat" action: puts the local player back on a deck — after falling
    /// overboard, or when the boat sailed off without them. Purely local: the pose reaches the others
    /// through <see cref="PlayerSync"/> like any other step, so there is no message and nothing for
    /// the host to decide.
    ///
    /// <para>Boarding goes through the game's own <c>PlayerEmbarkerNew</c>, in the order it uses when
    /// the player jumps aboard: name the boat, move the embark capsule onto the boat's static walk
    /// copy, <c>PlayerEmbark</c>. The character controller is the master of the player's position
    /// (<c>PlayerControllerMirror</c> copies its local position to the observer every frame), and on
    /// board it lives under that walk copy, so a deck spot is the same local point in both.</para>
    /// </summary>
    public sealed class BoatTeleport
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly CoopNet _net;
        private readonly PlayerSync _players;

        private PlayerEmbarkerNew _emb;
        private float _nextEmbarkerScan;
        private bool _pending;

        // The last place the player stood on a deck, in the hull's own space.
        private Transform _deckBoat;
        private Vector3 _deckPos;

        private FieldInfo _fCurrentBoat;
        private MethodInfo _mToWalkCol, _mEmbark;
        private bool _reflected;

        /// <summary>Called once per request: success flag and a line for the menu.</summary>
        public Action<bool, string> Done;

        public BoatTeleport(CoopNet net, PlayerSync players)
        {
            _net = net;
            _players = players;
        }

        public bool Available => GameState.playing && !GameState.currentlyLoading;

        /// <summary>Runs on the next <see cref="Tick"/>, not from OnGUI: the game recomputes its
        /// swimming flags in LateUpdate, and they must see the new position before the next physics
        /// step — a player still flagged as swimming is put off the boat again.</summary>
        public void Request() { _pending = true; }

        public void Tick()
        {
            if (!Available) { _pending = false; return; }
            RememberDeck();
            if (!_pending) return;
            _pending = false;

            bool ok = false;
            string text;
            try { ok = Run(out text); }
            catch (Exception e)
            {
                text = "Teleport failed: " + e.Message;
                Plugin.Logger.LogWarning("[BoatTeleport] " + e);
            }
            Plugin.Logger.LogInfo("[BoatTeleport] " + (ok ? "done: " : "refused: ") + text);
            Done?.Invoke(ok, text);
        }

        private PlayerEmbarkerNew Embarker()
        {
            if (_emb != null) return _emb;
            if (Time.unscaledTime < _nextEmbarkerScan) return null;
            _nextEmbarkerScan = Time.unscaledTime + 1f;
            _emb = UnityEngine.Object.FindObjectOfType<PlayerEmbarkerNew>();
            return _emb;
        }

        private void RememberDeck()
        {
            var emb = Embarker();
            CharacterController cc = Refs.charController;
            if (emb == null || cc == null) return;
            Transform boat = emb.debugOutCurrentBoat;
            // Standing, and boarded as far as the game is concerned: a fall past the rail or a climb
            // up the rigging must not become the place to come back to.
            if (boat == null || GameState.currentBoat != boat || !cc.enabled || !cc.isGrounded) return;
            _deckBoat = boat;
            _deckPos = cc.transform.localPosition;
        }

        private bool Run(out string text)
        {
            var emb = Embarker();
            CharacterController cc = Refs.charController;
            if (emb == null || cc == null || emb.playerObserver == null) { text = "The player is not ready yet"; return false; }
            if (GameState.inBed || !cc.enabled) { text = "Not available while asleep or paused"; return false; }

            Transform hull = PickBoat(out bool tooFar);
            if (hull == null)
            {
                text = tooFar ? "The boat is too far away to board" : "No boat to teleport to";
                return false;
            }

            Transform walk = WalkCopy(hull);
            if (walk == null) { text = "This boat cannot be boarded"; return false; }
            if (!PickSpot(hull, walk, cc, out Vector3 pos, out string spot))
            {
                text = "No place to stand found on the boat";
                return false;
            }

            ReleaseHands();

            Transform ctrl = cc.transform;
            bool aboard = GameState.currentBoat == hull && ctrl.parent == walk;
            // A character controller that stays enabled can put itself back where it was.
            cc.enabled = false;
            try
            {
                if (aboard)
                {
                    ctrl.localPosition = pos;
                    emb.playerObserver.localPosition = pos;
                }
                else if (Reflect())
                {
                    emb.playerObserver.position = hull.TransformPoint(pos);
                    _fCurrentBoat.SetValue(emb, new EmbarkBoat(hull, walk));
                    _mToWalkCol.Invoke(emb, null);
                    _mEmbark.Invoke(emb, null);
                }
                else
                {
                    // The game's embark members are gone: stand the player on the deck and leave the
                    // boarding to the boat's own trigger.
                    ctrl.position = hull.TransformPoint(pos);
                    emb.playerObserver.position = ctrl.position;
                }
            }
            finally { cc.enabled = true; }
            Physics.SyncTransforms();

            text = "Teleported to the boat (" + spot + ")";
            return true;
        }

        private bool Reflect()
        {
            if (!_reflected)
            {
                _reflected = true;
                Type t = typeof(PlayerEmbarkerNew);
                _fCurrentBoat = t.GetField("currentBoat", Private);
                _mToWalkCol = t.GetMethod("TeleportToWalkCol", Private, null, Type.EmptyTypes, null);
                _mEmbark = t.GetMethod("PlayerEmbark", Private, null, Type.EmptyTypes, null);
                if (_fCurrentBoat == null || _mToWalkCol == null || _mEmbark == null)
                    Plugin.Logger.LogWarning("[BoatTeleport] PlayerEmbarkerNew members not found, boarding is left to the boat trigger");
            }
            return _fCurrentBoat != null && _mToWalkCol != null && _mEmbark != null;
        }

        /// <summary>The deck the player last stood on, else the host's boat, else the last owned one.
        /// A boat the game has switched off for distance has no deck to stand on.</summary>
        private Transform PickBoat(out bool tooFar)
        {
            tooFar = false;
            if (Usable(_deckBoat, ref tooFar)) return _deckBoat;

            if (_net.Role == Role.Client && _net.State == LinkState.Connected)
            {
                foreach (SessionMemberInfo member in _net.RosterSnapshot)
                {
                    if (!member.IsHost || member.BoatIndex < 0 || member.BoatIndex >= BoatLocator.NoBoat) continue;
                    Transform boat = BoatLocator.FindByIndex((ushort)member.BoatIndex);
                    if (Usable(boat, ref tooFar)) return boat;
                }
            }

            Transform owned = GameState.lastOwnedBoat;
            if (owned != null)
                foreach (Transform boat in BoatLocator.FindBoats())
                    if (boat != null && boat.IsChildOf(owned) && Usable(boat, ref tooFar)) return boat;

            foreach (Transform boat in BoatLocator.FindBoats())
                if (Usable(boat, ref tooFar)) return boat;
            return null;
        }

        private static bool Usable(Transform boat, ref bool tooFar)
        {
            if (boat == null) return false;
            if (boat.gameObject.activeInHierarchy) return true;
            tooFar = true;
            return false;
        }

        private static Transform WalkCopy(Transform hull)
        {
            foreach (var col in hull.GetComponentsInChildren<BoatEmbarkCollider>(true))
                if (col != null && col.transform.parent == hull && col.walkCollider != null) return col.walkCollider;
            return null;
        }

        private bool PickSpot(Transform hull, Transform walk, CharacterController cc, out Vector3 pos, out string spot)
        {
            if (_deckBoat == hull) { pos = _deckPos; spot = "where you last stood"; return true; }
            if (_players.TryGetCrewDeckPos(hull, out pos)) { spot = "next to the crew"; return true; }

            // Nobody has stood here in this session: drop a ray through the middle of the walk copy
            // and take the lowest floor, so a boom or a cabin roof above it is not chosen.
            spot = "amidships";
            Vector3 up = walk.up;
            bool found = false;
            float lowest = float.MaxValue;
            Vector3 floor = Vector3.zero;
            foreach (RaycastHit hit in Physics.RaycastAll(walk.position + up * 40f, -up, 80f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == null || !hit.collider.transform.IsChildOf(walk)) continue;
                if (Vector3.Dot(hit.normal, up) < 0.7f) continue;
                float height = Vector3.Dot(hit.point - walk.position, up);
                if (height >= lowest) continue;
                lowest = height;
                floor = hit.point;
                found = true;
            }
            if (!found) return false;
            pos = walk.InverseTransformPoint(floor + up * (cc.height * 0.5f - cc.center.y + 0.3f));
            return true;
        }

        /// <summary>A rope end, an anchor or a winch in the hand is tied to where the player was.
        /// Ordinary items stay in the hand and come along.</summary>
        private static void ReleaseHands()
        {
            // The player asked for this, and it runs outside the game's input handlers: without the
            // input origin the release and the drop are not relayed, and the host keeps the winch held.
            using (InteractionContext.Begin(InteractionSource.LocalInput))
            foreach (var pointer in UnityEngine.Object.FindObjectsOfType<GoPointer>())
            {
                try
                {
                    PickupableItem held = pointer.GetHeldItem();
                    if (held != null && !(held is ShipItem)) pointer.DropItem();

                    foreach (string name in new[] { "clickedButton", "stickyClickedButton" })
                    {
                        var button = ItemComponents.Read<GoPointerButton>(pointer, name);
                        if (button == null) continue;
                        button.OnUnactivate(); button.OnUnactivate(pointer); button.Unclick(); button.UnStickyClick();
                        ItemComponents.Set(pointer, name, null);
                    }
                }
                catch (Exception e) { Plugin.Logger.LogWarning("[BoatTeleport] release hands: " + e.Message); }
            }
        }
    }
}
