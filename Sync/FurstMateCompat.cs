using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using LiteNetLib;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// FurstMate (Dixiecapt, com.roy.furstmate) adds ship's pets: a shepherd dog bought on Bone Isle, a ship's cat won in
    /// the Gold Rock City tavern, and a wild booby who visits at sea and can be tamed. Each machine ran its own pets from
    /// its own save, with AI that follows only that machine's player, so every crewmate saw different animals or none.
    ///
    /// The host's pets are the crew's pets. The host runs FurstMate's AI as usual, with "you" widened to the nearest
    /// crewmate aboard when the host is not (DeckWalker.PlayerAboard / PlayerFeet, GameBird.PlayerAboard / PlayerFeet).
    /// Guests keep FurstMate's own pet objects, built from the host's state, as puppets: their AI ticks are skipped and
    /// their pose, animator state and sounds come from the host. Everything a guest does to a pet (feed, pet, commands,
    /// Here, buying the dog, taming the cat or the booby, a booby begging) runs locally for its own notes and item use,
    /// and is sent to the host, which applies it once to the real pet. The booby's dropped fish is made on the host and
    /// synced as an ordinary item.
    ///
    /// Built against FurstMate 0.9.6 (closed source, read from its release DLL). Every member is reflected; a miss turns
    /// the whole sync off with a warning, and FurstMate then runs per machine as it did before.
    /// </summary>
    public static class FurstMateCompat
    {
        public const string Guid = "com.roy.furstmate";

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        private static bool Guest => SyncEnabled && Net?.Role == Role.Client && Net.State == LinkState.Connected &&
            (Net.HostModFeatures & ModFeatures.FurstMate) != 0;
        private static bool Host => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true && Net.Role == Role.Host;

        // ---- message kinds inside PacketType.FurstMate ----
        private const byte MsgWorld = 1, MsgDogPose = 2, MsgCatPose = 3, MsgBirdPose = 4, MsgEvent = 5;
        private const byte MsgDogPurchase = 6, MsgDogPurchaseWorld = 7;
        private const byte ReqDogCmd = 10, ReqSleepSpot = 11, ReqPet = 12, ReqFeed = 13, ReqHere = 14, ReqBuyDog = 15,
                           ReqCatTame = 16, ReqBirdBeg = 17, ReqDogPurchaseStatus = 18;
        private const byte PetDog = 0, PetCat = 1, PetBird = 2;
        private const byte EvSay = 1, EvBark = 2, EvHowl = 3, EvPounce = 4, EvSplash = 5, EvCarry = 6;

        private const float PoseInterval = 0.1f;
        private const float WorldInterval = 1f;
        private static Guid _dogPurchaseWorld;
        private static float _nextPurchaseStatus;
        private static Dictionary<Guid, DogPurchaseJournal.Charge> PendingPurchases => CoopProfile.DogPurchases.Pending;

        // ---- reflection ----
        private static Type _plugin, _walker, _dog, _cat, _catKeeper, _birdKeeper, _flight, _gameBird, _perches, _home, _sounds,
                            _command, _stage, _mode, _body;
        private static FieldInfo _pInstance, _pDog, _pFood, _pBoughtDay, _pHadDog, _pBuyAskedAt, _pPrice, _pSpots;
        private static MethodInfo _pSpawn, _pBoatOf, _pNote, _pSaveObjectOf, _pOwned, _pPay, _pSetSleepSpot;
        private static PropertyInfo _pPuppyVoice;
        private static FieldInfo _wBoat, _wWorldBoat, _wWalkCol, _wAnim, _wModel, _wState, _wRough, _wSevere, _wStormy, _wRoute;
        private static MethodInfo _wSetState, _wPlayerAboard, _wSettle;
        private static FieldInfo _dCmd, _dPirate, _dPlaced, _dBox, _dPetUntil, _dShiver, _dFar, _dCallUntil;
        private static MethodInfo _dSay, _dBark, _dRunLoop, _dHush, _dSizeBox, _dSave, _dGive;
        private static PropertyInfo _dVolume;
        private static FieldInfo _cPlaced, _cBox, _cPetUntil, _cHunt, _cShiver;
        private static MethodInfo _cSay, _cUpdateVoice, _cHush, _cSizeBox, _cSave, _cThanks;
        private static FieldInfo _kInstance, _kCat, _kFood, _kFed, _kSunk, _kHadCat;
        private static MethodInfo _kSpawn;
        private static FieldInfo _bInstance, _bBird, _bWorld, _bShipOf, _bPerches, _bStage, _bFood, _bZeroHours, _bPerchPrefab,
                                 _bFlyPrefab, _bSize, _bOnYards;
        private static MethodInfo _bForget, _bAddLook, _bTame;
        private static FieldInfo _fRoot, _fP, _fF, _fCur, _fMode, _fState, _fW;
        private static MethodInfo _fShow, _fHang, _fSetState, _fBeats, _fCarry, _fBeg, _fFed;
        private static FieldInfo _bodyGo, _bodyAnim;
        private static FieldInfo _gBird, _gFish, _gBoat, _gModel;
        private static MethodInfo _gVoice, _gPlay, _gSplash;
        private static ConstructorInfo _ctorPerches, _ctorGameBird, _ctorFlight;
        private static MethodInfo _hFor, _hSaveString, _hLoad;
        private static PropertyInfo _hRadius;
        private static MethodInfo _sClip;

        private static readonly List<string> _missing = new List<string>();

        // ---- runtime state ----
        private static bool _replaying;          // host applying a guest's request: FurstMate's notes stay off the host's screen
        private static float _nextPose, _nextWorld;
        private static string _lastWorldHomes;
        private static string _dogLoop = "";     // host: the loop clip the dog last asked for
        private static readonly Dictionary<object, Vector3> _subFeet = new Dictionary<object, Vector3>();
        private static int _subFrame = -1;

        // Guest puppets: the host instance they stand for, and the latest pose. NoGen is a value no host instance has,
        // so the host's first world message always applies, including "no pet", which removes a pet this guest's own
        // save built before the session (otherwise left frozen on deck, since 0 == 0 never looked like a change).
        private const int NoGen = int.MinValue;
        private static int _dogGen = NoGen, _catGen = NoGen, _birdGen = NoGen;
        private static string _dogSave, _catSave;
        private static PetPose _dogPose, _catPose;
        private static readonly PetPlayback _dogMotion = new PetPlayback();
        private static readonly PetPlayback _catMotion = new PetPlayback();
        private static readonly PetPlayback _birdMotion = new PetPlayback();

        private sealed class PetPlayback
        {
            private readonly NetTransform _motion = new NetTransform();
            private Transform _parent;
            private int _generation;
            private long _latest = long.MinValue;

            public PetPlayback()
            {
                _motion.ToWorldPos = position => _parent != null ? _parent.TransformPoint(position) : CoordSpace.RealToLocal(position);
                _motion.ToWorldRot = rotation => _parent != null ? _parent.rotation * rotation : rotation;
            }

            public void Clear() { _generation = 0; _latest = long.MinValue; _parent = null; _motion.Clear(); }

            public bool Push(long tick, int generation, Vector3 position, Quaternion rotation)
            {
                if (tick < _latest && generation != _generation) return false;
                if (generation != _generation)
                {
                    _motion.Clear();
                    _generation = generation;
                }
                _motion.Push(tick, position, rotation);
                if (tick < _latest) return false;
                _latest = tick;
                return true;
            }

            public void Apply(Transform target, int generation)
            {
                if (target.parent == null || generation != _generation) return;
                _parent = target.parent;
                _motion.InterpDelayMs = Plugin.Cfg.InterpDelayMs.Value;
                _motion.Apply(target, Net.Clock.ServerTimeMs);
            }
        }

        private static void ReceivePetPose(BinaryReader reader, long tick, PetPlayback playback, ref PetPose current)
        {
            var pose = ReadPetPose(reader);
            if (playback.Push(tick, pose.Gen, pose.Pos, pose.Rot)) current = pose;
        }
        private static BirdPose _birdPose;
        private static float _nextDogBox, _nextCatBox;
        private static GameObject _guestFish;     // guest: the fish the host's booby carries
        private static float _hashMismatchSince = -1f;

        private class PetPose
        {
            public int Gen;
            public bool Placed, Petting, Shiver, Rough, Severe, Stormy, Hunting;
            public Vector3 Pos;
            public Quaternion Rot;
            public int State, Cmd, Pirate;
            public float WalkSpeed;
            public string Loop = "";
        }

        private class BirdPose
        {
            public int Gen, Mode, State, Hash;
            public bool Fly, FlyActive;
            public Vector3 Pos;
            public Quaternion Rot;
            public float Norm;
        }

        // ================================================================== detection

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _missing.Clear();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Logger.LogInfo("[FurstMateCompat] FurstMate not installed.");
                    return;
                }
                IsInstalled = true;
                string version = info.Metadata.Version.ToString();
                var asm = info.Instance != null ? info.Instance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "FurstMate");

                _plugin = T(asm, "FurstMate.FurstMatePlugin");
                _walker = T(asm, "FurstMate.DeckWalker");
                _dog = T(asm, "FurstMate.ShepherdDog");
                _cat = T(asm, "FurstMate.ShipCat");
                _catKeeper = T(asm, "FurstMate.CatKeeper");
                _birdKeeper = T(asm, "FurstMate.BirdKeeper");
                _flight = T(asm, "FurstMate.BirdFlight");
                _gameBird = T(asm, "FurstMate.GameBird");
                _perches = T(asm, "FurstMate.BirdPerches");
                _home = T(asm, "FurstMate.HomeRefs");
                _sounds = T(asm, "FurstMate.DogSounds");
                _command = T(asm, "FurstMate.Command");
                _stage = T(asm, "FurstMate.BirdKeeper+Stage");
                _mode = T(asm, "FurstMate.BirdFlight+Mode");
                _body = T(asm, "FurstMate.BirdFlight+Body");

                if (_missing.Count == 0)
                {
                    _pInstance = F(_plugin, "Instance"); _pDog = F(_plugin, "Dog"); _pFood = F(_plugin, "Food");
                    _pBoughtDay = F(_plugin, "BoughtDay"); _pHadDog = F(_plugin, "_hadDog"); _pBuyAskedAt = F(_plugin, "_buyAskedAt");
                    _pPrice = F(_plugin, "Price"); _pSpots = F(_plugin, "_spots");
                    _pSpawn = M(_plugin, "Spawn", typeof(BoatRefs), typeof(Vector3?), _command, typeof(Vector3?));
                    _pBoatOf = M(_plugin, "BoatOf", typeof(Transform)); _pNote = M(_plugin, "Note", typeof(string));
                    _pSaveObjectOf = M(_plugin, "SaveObjectOf", typeof(BoatRefs)); _pOwned = M(_plugin, "Owned", typeof(BoatRefs));
                    _pPay = M(_plugin, "Pay", typeof(int), typeof(int)); _pSetSleepSpot = M(_plugin, "SetSleepSpot", typeof(BoatRefs), typeof(Vector3));
                    _pPuppyVoice = P(_plugin, "PuppyVoice");

                    _wBoat = F(_walker, "Boat"); _wWorldBoat = F(_walker, "WorldBoat"); _wWalkCol = F(_walker, "WalkCol");
                    _wAnim = F(_walker, "_anim"); _wModel = F(_walker, "_model"); _wState = F(_walker, "_state");
                    _wRough = F(_walker, "_rough"); _wSevere = F(_walker, "_severe"); _wStormy = F(_walker, "_stormy"); _wRoute = F(_walker, "_route");
                    _wSetState = M(_walker, "SetState", typeof(int));
                    _wPlayerAboard = M(_walker, "PlayerAboard", typeof(Vector3).MakeByRefType());
                    _wSettle = M(_walker, "Settle", typeof(Vector3), typeof(float), typeof(float));

                    _dCmd = F(_dog, "Cmd"); _dPirate = F(_dog, "PirateStage"); _dPlaced = F(_dog, "_placed"); _dBox = F(_dog, "_box");
                    _dPetUntil = F(_dog, "_petUntil"); _dShiver = F(_dog, "_shiver"); _dFar = F(_dog, "_far"); _dCallUntil = F(_dog, "_callUntil");
                    _dSay = M(_dog, "Say", typeof(string)); _dBark = M(_dog, "Bark"); _dRunLoop = M(_dog, "RunLoop", typeof(AudioClip), typeof(float));
                    _dHush = M(_dog, "HushVoice", typeof(bool)); _dSizeBox = M(_dog, "SizeBox"); _dSave = M(_dog, "SaveString");
                    _dGive = M(_dog, "Give", _command); _dVolume = P(_dog, "Volume");

                    _cPlaced = F(_cat, "_placed"); _cBox = F(_cat, "_box"); _cPetUntil = F(_cat, "_petUntil"); _cHunt = F(_cat, "_hunt");
                    _cShiver = F(_cat, "_shiver");
                    _cSay = M(_cat, "Say", typeof(string)); _cUpdateVoice = M(_cat, "UpdateVoice", typeof(bool), typeof(float));
                    _cHush = M(_cat, "Hush", typeof(bool)); _cSizeBox = M(_cat, "SizeBox"); _cSave = M(_cat, "SaveString"); _cThanks = M(_cat, "Thanks");

                    _kInstance = F(_catKeeper, "Instance"); _kCat = F(_catKeeper, "Cat"); _kFood = F(_catKeeper, "Food");
                    _kFed = F(_catKeeper, "_fed"); _kSunk = F(_catKeeper, "_sunk"); _kHadCat = F(_catKeeper, "_hadCat");
                    _kSpawn = M(_catKeeper, "Spawn", typeof(BoatRefs), typeof(Vector3?), typeof(Vector3?));

                    _bInstance = F(_birdKeeper, "Instance"); _bBird = F(_birdKeeper, "Bird"); _bWorld = F(_birdKeeper, "_world");
                    _bShipOf = F(_birdKeeper, "_shipOf"); _bPerches = F(_birdKeeper, "_perches"); _bStage = F(_birdKeeper, "_stage");
                    _bFood = F(_birdKeeper, "Food"); _bZeroHours = F(_birdKeeper, "_zeroHours"); _bPerchPrefab = F(_birdKeeper, "_perchPrefab");
                    _bFlyPrefab = F(_birdKeeper, "_flyPrefab"); _bSize = F(_birdKeeper, "BirdSize"); _bOnYards = F(_birdKeeper, "OnYards");
                    _bForget = M(_birdKeeper, "ForgetBird"); _bAddLook = M(_birdKeeper, "AddLook"); _bTame = M(_birdKeeper, "Tame");

                    _fRoot = F(_flight, "Root"); _fP = F(_flight, "_p"); _fF = F(_flight, "_f"); _fCur = F(_flight, "_cur");
                    _fMode = F(_flight, "_mode"); _fState = F(_flight, "_state"); _fW = F(_flight, "W");
                    _fShow = M(_flight, "Show", _body); _fHang = M(_flight, "Hang", typeof(Transform)); _fSetState = M(_flight, "SetState", typeof(int));
                    _fBeats = M(_flight, "Beats"); _fCarry = M(_flight, "Carry", typeof(bool)); _fBeg = M(_flight, "Beg"); _fFed = M(_flight, "Fed");
                    _bodyGo = F(_body, "go"); _bodyAnim = F(_body, "anim");

                    _gBird = F(_gameBird, "Bird"); _gFish = F(_gameBird, "_fish"); _gBoat = F(_gameBird, "_boat"); _gModel = F(_gameBird, "_model");
                    _gVoice = M(_gameBird, "Voice", typeof(GameObject)); _gPlay = M(_gameBird, "Play", typeof(string));
                    _gSplash = M(_gameBird, "Splash", typeof(Vector3));

                    _ctorPerches = C(_perches, typeof(BoatRefs), typeof(bool));
                    _ctorGameBird = C(_gameBird, _birdKeeper, typeof(BoatRefs), _perches);
                    _ctorFlight = C(_flight, typeof(GameObject), typeof(GameObject), T(asm, "FurstMate.BirdWorld"), typeof(float));

                    _hFor = M(_home, "For", typeof(SaveableObject)); _hSaveString = M(_home, "SaveString"); _hLoad = M(_home, "Load", typeof(string));
                    _hRadius = P(_home, "Radius");
                    _sClip = M(_sounds, "Clip", typeof(string));
                }

                SyncEnabled = _missing.Count == 0;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo($"[FurstMateCompat] FurstMate v{version} detected; the host's pets are shared with the crew.");
                else
                    Plugin.Logger.LogWarning($"[FurstMateCompat] FurstMate v{version} detected, but {_missing.Count} member(s) did not resolve " +
                        $"({string.Join(", ", _missing.Take(8).ToArray())}). Pet sync disabled, so each crewmate keeps their own pets. " +
                        "Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[FurstMateCompat] Detection threw. Pet sync disabled. " + e);
            }
        }

        private static Type T(Assembly asm, string name)
        {
            var t = asm?.GetType(name);
            if (t == null) _missing.Add(name);
            return t;
        }

        private static FieldInfo F(Type t, string name)
        {
            var f = t != null ? AccessTools.Field(t, name) : null;
            if (f == null) _missing.Add((t?.Name ?? "?") + "." + name);
            return f;
        }

        private static PropertyInfo P(Type t, string name)
        {
            var p = t != null ? AccessTools.Property(t, name) : null;
            if (p == null) _missing.Add((t?.Name ?? "?") + "." + name);
            return p;
        }

        private static MethodInfo M(Type t, string name, params Type[] args)
        {
            var m = t != null && args.All(a => a != null) ? AccessTools.Method(t, name, args) : null;
            if (m == null) _missing.Add((t?.Name ?? "?") + "." + name + "()");
            return m;
        }

        private static ConstructorInfo C(Type t, params Type[] args)
        {
            var c = t != null && args.All(a => a != null) ? AccessTools.Constructor(t, args) : null;
            if (c == null) _missing.Add((t?.Name ?? "?") + ".ctor");
            return c;
        }

        // ================================================================== patches

        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!SyncEnabled) return;
            try
            {
                var me = typeof(FurstMateCompat);
                HarmonyMethod H(string n) => new HarmonyMethod(me, n);
                MethodInfo Own(Type t, string n, params Type[] a) => AccessTools.Method(t, n, a);

                // Guests: FurstMate's own decisions and save loading stay off; the host's state builds the pets.
                foreach (var m in new[]
                {
                    Own(_plugin, "DogTick"), Own(_plugin, "SpawnFromSave", typeof(string)),
                    Own(_catKeeper, "Tick"), Own(_catKeeper, "FromSave", typeof(string)),
                    Own(_birdKeeper, "Tick"), Own(_flight, "Tick", typeof(float)), Own(_gameBird, "DropFish", typeof(Vector3)),
                })
                    if (m != null) harmony.Patch(m, prefix: H(nameof(GuestSkipPrefix)));

                harmony.Patch(Own(_dog, "Update"), prefix: H(nameof(DogUpdatePrefix)), postfix: H(nameof(DogUpdatePostfix)));
                harmony.Patch(Own(_cat, "Update"), prefix: H(nameof(CatUpdatePrefix)));
                harmony.Patch(Own(_birdKeeper, "LateUpdate"), postfix: H(nameof(BirdLateUpdatePostfix)));

                // Host: what the pets say and do, for the crew.
                harmony.Patch(_dSay, prefix: H(nameof(DogSayPrefix)));
                harmony.Patch(_dBark, postfix: H(nameof(DogBarkPostfix)));
                harmony.Patch(_dRunLoop, prefix: H(nameof(DogRunLoopPrefix)));
                harmony.Patch(_cSay, prefix: H(nameof(CatSayPrefix)));
                harmony.Patch(_wSetState, postfix: H(nameof(WalkerSetStatePostfix)));
                harmony.Patch(_gPlay, postfix: H(nameof(BirdPlayPostfix)));
                harmony.Patch(_gSplash, postfix: H(nameof(BirdSplashPostfix)));
                harmony.Patch(_fCarry, postfix: H(nameof(BirdCarryPostfix)));
                harmony.Patch(Own(_gameBird, "DropFish", typeof(Vector3)), prefix: H(nameof(HostDropFishPrefix)));
                harmony.Patch(Own(_gameBird, "FishLook"), prefix: H(nameof(GuestFishLookPrefix)));
                harmony.Patch(_pNote, prefix: H(nameof(NotePrefix)));

                // Host: "you" is the nearest crewmate aboard when the host is not.
                harmony.Patch(_wPlayerAboard, postfix: H(nameof(WalkerPlayerAboardPostfix)));
                harmony.Patch(Own(_walker, "PlayerFeet"), postfix: H(nameof(WalkerPlayerFeetPostfix)));
                harmony.Patch(AccessTools.PropertyGetter(_gameBird, "PlayerAboard"), postfix: H(nameof(BirdPlayerAboardPostfix)));
                harmony.Patch(AccessTools.PropertyGetter(_gameBird, "PlayerFeet"), postfix: H(nameof(BirdPlayerFeetPostfix)));

                // Guests: what they do to a pet goes to the host.
                harmony.Patch(_dGive, prefix: H(nameof(GuestGivePrefix)));
                harmony.Patch(Own(_dog, "MarkSleepSpot"), prefix: H(nameof(GuestSleepSpotPrefix)));
                harmony.Patch(Own(_dog, "GetPetted"), prefix: H(nameof(PetUntilPrefix)), postfix: H(nameof(DogPettedPostfix)));
                harmony.Patch(Own(_cat, "GetPetted"), prefix: H(nameof(CatPetUntilPrefix)), postfix: H(nameof(CatPettedPostfix)));
                harmony.Patch(Own(_plugin, "Feed", typeof(PickupableItem)), prefix: H(nameof(DogFoodPrefix)), postfix: H(nameof(DogFoodPostfix)));
                harmony.Patch(Own(_catKeeper, "FeedAboard", typeof(PickupableItem)), prefix: H(nameof(CatFoodPrefix)), postfix: H(nameof(CatFoodPostfix)));
                harmony.Patch(Own(_catKeeper, "FeedAtTavern", typeof(PickupableItem)), prefix: H(nameof(CatFedPrefix)), postfix: H(nameof(CatTavernPostfix)));
                harmony.Patch(Own(_birdKeeper, "Offer", typeof(PickupableItem)), prefix: H(nameof(BirdOfferPrefix)), postfix: H(nameof(BirdOfferPostfix)));
                harmony.Patch(_fBeg, prefix: H(nameof(GuestBegPrefix)));
                harmony.Patch(Own(_plugin, "CallHere"), prefix: H(nameof(GuestCallHerePrefix)));
                harmony.Patch(Own(_plugin, "BuyClick"), prefix: H(nameof(GuestBuyClickPrefix)));
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[FurstMateCompat] Patching threw. Pet sync disabled. " + e);
            }
        }

        public static void Reset()
        {
            _dogPurchaseWorld = System.Guid.Empty;
            _nextPurchaseStatus = 0f;
            _replaying = false;
            _dogGen = _catGen = _birdGen = NoGen;
            _dogSave = _catSave = null;
            _dogPose = _catPose = null;
            _dogMotion.Clear(); _catMotion.Clear(); _birdMotion.Clear();
            _birdPose = null;
            _guestFish = null;
            _lastWorldHomes = null;
            _nextWorld = _nextPose = _nextDogBox = _nextCatBox = 0f;
            _hashMismatchSince = -1f;
            _dogLoop = null;
            _subFeet.Clear();
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[FurstMateCompat] " + error.Message);

        // ================================================================== helpers

        private static object PluginInst => _pInstance?.GetValue(null);
        private static object CatKeeperInst => _kInstance?.GetValue(null);
        private static object BirdKeeperInst => _bInstance?.GetValue(null);
        private static Component Dog => PluginInst != null ? _pDog.GetValue(PluginInst) as Component : null;
        private static Component Cat => CatKeeperInst != null ? _kCat.GetValue(CatKeeperInst) as Component : null;
        private static object Bird => BirdKeeperInst != null ? _bBird.GetValue(BirdKeeperInst) : null;

        private static T Get<T>(FieldInfo f, object o) => o != null && f.GetValue(o) is T v ? v : default;
        private static object Call(MethodInfo m, object o, params object[] a) => m.Invoke(o, a);

        private static BoatRefs BoatOf(Transform t) => t != null ? Call(_pBoatOf, null, t) as BoatRefs : null;

        private static int SceneIndexOf(BoatRefs b)
        {
            var so = b != null ? Call(_pSaveObjectOf, null, b) as SaveableObject : null;
            return so != null ? so.sceneIndex : -1;
        }

        /// <summary>A pet's ship or home by save index, as FurstMate itself resolves its save.</summary>
        private static BoatRefs ResolveBoat(int sceneIndex)
        {
            if (sceneIndex < 0 || SaveLoadManager.instance == null) return null;
            var so = SaveLoadManager.instance.GetObject(sceneIndex);
            if (so == null) return null;
            BoatRefs home = null;
            try { home = Call(_hFor, null, so) as BoatRefs; } catch { }
            return home ?? BoatOf(so.transform);
        }

        private static string Gen(Component c) => c != null ? c.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "0";

        private static void Note(string text)
        {
            try { Call(_pNote, null, text); } catch { }
        }

        private static void Send(byte kind, Action<BinaryWriter> body, bool reliable = true)
        {
            if (!Host && !Guest) return;
            Net.SendMod(CreateMessage(kind, body), ModFeatures.FurstMate,
                reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        private static PetMessage CreateMessage(byte kind, Action<BinaryWriter> body)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data))
            {
                writer.Write(kind);
                writer.Write(Net.Clock.ServerTick);
                body(writer);
                writer.Flush();
                PetMessage message = Host ? (PetMessage)new PetStateMsg() : new PetRequestMsg();
                message.Data = data.ToArray();
                return message;
            }
        }

        private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        private static void WriteQ(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        private static Quaternion ReadQ(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Vector3 LocalFeet(BoatRefs boat)
        {
            var cc = Refs.charController;
            var walk = boat != null ? boat.walkCol : null;
            if (cc == null || walk == null) return Vector3.zero;
            return walk.InverseTransformPoint(cc.transform.position);
        }

        // ================================================================== tick (from the item tick)

        public static void Tick()
        {
            if (Guest && _dogPurchaseWorld != System.Guid.Empty && Time.unscaledTime >= _nextPurchaseStatus &&
                PendingPurchases.TryGetValue(_dogPurchaseWorld, out var purchase))
            {
                _nextPurchaseStatus = Time.unscaledTime + 3f;
                Request(ReqDogPurchaseStatus, w => WritePurchaseId(w, _dogPurchaseWorld, purchase.Purchase));
            }
            if (!Host) return;
            float now = Time.unscaledTime;
            if (now >= _nextWorld)
            {
                _nextWorld = now + WorldInterval;
                try { SendWorld(); } catch (Exception e) { Plugin.Logger.LogWarning("[FurstMateCompat] World send failed: " + e.Message); }
            }
            if (now >= _nextPose)
            {
                _nextPose = now + PoseInterval;
                try { SendPoses(); } catch (Exception e) { Plugin.Logger.LogWarning("[FurstMateCompat] Pose send failed: " + e.Message); }
            }
        }

        public static void ResyncTo(NetPeer peer)
        {
            if (!Host || (Net.FeaturesOf(peer) & ModFeatures.FurstMate) == 0) return;
            var world = HostPurchaseWorld();
            peer.Send(CreateMessage(MsgDogPurchaseWorld, w => w.Write(world.ToByteArray())), DeliveryMethod.ReliableOrdered);
            _nextWorld = 0f;
            _lastWorldHomes = null;
        }

        internal static void PrepareHostSave(NetPeer peer)
        {
            if (SyncEnabled && (Net.FeaturesOf(peer) & ModFeatures.FurstMate) != 0) HostPurchaseWorld();
        }

        // ---- world ----

        private static void SendWorld()
        {
            var plugin = PluginInst;
            var keeper = CatKeeperInst;
            var bk = BirdKeeperInst;
            var dog = Dog;
            var cat = Cat;
            var bird = Bird;

            string homes = "";
            try { homes = Call(_hSaveString, null) as string ?? ""; } catch { }
            string dogSave = dog != null ? Call(_dSave, dog) as string ?? "" : "";
            string catSave = cat != null ? Call(_cSave, cat) as string ?? "" : "";
            int birdIdx = bird != null ? SceneIndexOf(Get<BoatRefs>(_bShipOf, bk)) : -1;

            Send(MsgWorld, w =>
            {
                w.Write(homes);
                w.Write(dogSave); w.Write(Gen(dog));
                w.Write(Get<float>(_pFood, plugin)); w.Write(Get<int>(_pBoughtDay, plugin));
                w.Write(catSave); w.Write(Gen(cat));
                w.Write(Get<float>(_kFood, keeper)); w.Write(Get<bool>(_kFed, keeper)); w.Write(Get<bool>(_kSunk, keeper));
                w.Write(bird != null ? (RuntimeHelpersHash(bird)) : 0); w.Write(birdIdx);
                w.Write(bk != null ? Convert.ToInt32(_bStage.GetValue(bk)) : 0);
                w.Write(Get<float>(_bFood, bk));
                var size = bk != null ? _bSize.GetValue(bk) as BepInEx.Configuration.ConfigEntry<float> : null;
                w.Write(size != null ? Mathf.Clamp(size.Value, 0.7f, 1.5f) : 1f);
            });
        }

        private static int RuntimeHelpersHash(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o) | 1;

        private static void OnWorld(BinaryReader r)
        {
            string homes = r.ReadString();
            string dogSave = r.ReadString(); string dogGen = r.ReadString();
            float dogFood = r.ReadSingle(); int bought = r.ReadInt32();
            string catSave = r.ReadString(); string catGen = r.ReadString();
            float catFood = r.ReadSingle(); bool fed = r.ReadBoolean(); bool sunk = r.ReadBoolean();
            int birdGen = r.ReadInt32(); int birdIdx = r.ReadInt32(); int stage = r.ReadInt32(); float birdFood = r.ReadSingle();
            float birdSize = r.ReadSingle();

            var plugin = PluginInst;
            var keeper = CatKeeperInst;
            var bk = BirdKeeperInst;

            if (homes != _lastWorldHomes)
            {
                _lastWorldHomes = homes;
                try { Call(_hLoad, null, homes); } catch { }
            }

            // ---- dog ----
            if (plugin != null)
            {
                _pFood.SetValue(plugin, dogFood);
                _pBoughtDay.SetValue(plugin, bought);
                int gen = int.Parse(dogGen, CultureInfo.InvariantCulture);
                if (gen != _dogGen || (gen != 0 && Dog == null))
                {
                    if (dogSave.Length == 0)
                    {
                        _dogGen = 0;
                        var old = Dog;
                        _pHadDog.SetValue(plugin, false);
                        _pDog.SetValue(plugin, null);
                        if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                    }
                    else if (TryParseSave(dogSave, out int idx, out var pos, out var fwd, out string cmd, 8))
                    {
                        var boat = ResolveBoat(idx);
                        if (boat != null)
                        {
                            object command = ParseCommand(cmd);
                            Call(_pSpawn, plugin, boat, (Vector3?)pos, command, (Vector3?)fwd);
                            _dogGen = gen;
                            _dogSave = dogSave;

                        }
                    }
                }
            }

            // ---- cat ----
            if (keeper != null)
            {
                _kFood.SetValue(keeper, catFood);
                _kFed.SetValue(keeper, fed);
                int gen = int.Parse(catGen, CultureInfo.InvariantCulture);
                if (gen != _catGen || (gen != 0 && Cat == null))
                {
                    if (catSave.Length == 0)
                    {
                        _catGen = 0;
                        var old = Cat;
                        _kHadCat.SetValue(keeper, false);
                        _kCat.SetValue(keeper, null);
                        if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                    }
                    else if (TryParseSave(catSave, out int idx, out var pos, out var fwd, out _, -1))
                    {
                        var boat = ResolveBoat(idx);
                        if (boat != null)
                        {
                            Call(_kSpawn, keeper, boat, (Vector3?)pos, (Vector3?)fwd);
                            _catGen = gen;
                            _catSave = catSave;

                        }
                    }
                }
                _kSunk.SetValue(keeper, sunk);
                var cat = Cat;
                if (cat != null && cat.gameObject.activeSelf == sunk) cat.gameObject.SetActive(!sunk);
            }

            // ---- bird ----
            if (bk != null)
            {
                _bStage.SetValue(bk, Enum.ToObject(_stage, stage));
                _bFood.SetValue(bk, birdFood);
                if (birdGen != _birdGen || (birdGen != 0 && Bird == null))
                {
                    if (Bird != null)
                    {
                        try { Call(_bForget, bk); } catch { }
                    }
                    _birdGen = 0;
                    _birdPose = null;
                    if (birdGen != 0 && BuildBirdPuppet(bk, ResolveBoat(birdIdx), birdSize)) _birdGen = birdGen;
                }
            }
        }

        /// <summary>FurstMate's own save line: "2|sceneIndex|px|py|pz|fx|fy|fz|..." (the dog's command is field 8).</summary>
        private static bool TryParseSave(string s, out int idx, out Vector3 pos, out Vector3 fwd, out string cmd, int cmdField)
        {
            idx = -1; pos = fwd = Vector3.zero; cmd = null;
            var f = s.Split('|');
            if (f.Length < 8 || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) return false;
            float P(int i) => float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            pos = new Vector3(P(2), P(3), P(4));
            fwd = new Vector3(P(5), P(6), P(7));
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            if (cmdField >= 0 && f.Length > cmdField) cmd = f[cmdField];
            return true;
        }

        private static object ParseCommand(string name)
        {
            try { return Enum.Parse(_command, name ?? "Heel", true); }
            catch { return Enum.ToObject(_command, 0); }
        }

        // ---- poses ----

        private static void SendPoses()
        {
            var dog = Dog;
            if (dog != null)
            {
                var anim = Get<Animator>(_wAnim, dog);
                Send(MsgDogPose, w =>
                {
                    w.Write(int.Parse(Gen(dog), CultureInfo.InvariantCulture));
                    w.Write(Get<bool>(_dPlaced, dog));
                    WriteV3(w, dog.transform.localPosition); WriteQ(w, dog.transform.localRotation);
                    w.Write(Get<int>(_wState, dog));
                    w.Write(anim != null ? anim.GetFloat("WalkSpeed") : 0f);
                    w.Write(Convert.ToInt32(_dCmd.GetValue(dog)));
                    w.Write(Get<int>(_dPirate, dog));
                    w.Write(Flags(Time.time < Get<float>(_dPetUntil, dog), Get<bool>(_dShiver, dog), Get<bool>(_wRough, dog),
                                  Get<bool>(_wSevere, dog), Get<bool>(_wStormy, dog), false));
                    w.Write(_dogLoop ?? "");
                }, reliable: false);
            }

            var cat = Cat;
            if (cat != null && cat.gameObject.activeSelf)
            {
                var anim = Get<Animator>(_wAnim, cat);
                Send(MsgCatPose, w =>
                {
                    w.Write(int.Parse(Gen(cat), CultureInfo.InvariantCulture));
                    w.Write(Get<bool>(_cPlaced, cat));
                    WriteV3(w, cat.transform.localPosition); WriteQ(w, cat.transform.localRotation);
                    w.Write(Get<int>(_wState, cat));
                    w.Write(anim != null ? anim.GetFloat("WalkSpeed") : 0f);
                    w.Write(0); w.Write(0);
                    w.Write(Flags(Time.time < Get<float>(_cPetUntil, cat), Get<bool>(_cShiver, cat), Get<bool>(_wRough, cat),
                                  Get<bool>(_wSevere, cat), Get<bool>(_wStormy, cat), Get<int>(_cHunt, cat) > 0));
                    w.Write("");
                }, reliable: false);
            }

            var bird = Bird;
            var bk = BirdKeeperInst;
            var world = bk != null ? _bWorld.GetValue(bk) : null;
            var model = world != null ? _gModel.GetValue(world) as Transform : null;
            if (bird != null && model != null)
            {
                var root = _fRoot.GetValue(bird) as Transform;
                var cur = _fCur.GetValue(bird);
                var f = _fF.GetValue(bird);
                var anim = cur != null ? _bodyAnim.GetValue(cur) as Animator : null;
                var fGo = f != null ? _bodyGo.GetValue(f) as GameObject : null;
                if (root == null) return;
                AnimatorStateInfo info = anim != null && anim.isActiveAndEnabled ? anim.GetCurrentAnimatorStateInfo(0) : default;
                Send(MsgBirdPose, w =>
                {
                    w.Write(RuntimeHelpersHash(bird));
                    w.Write(Convert.ToInt32(_fMode.GetValue(bird)));
                    w.Write(cur != null && ReferenceEquals(cur, f));
                    w.Write(fGo != null && fGo.activeSelf);
                    WriteV3(w, model.InverseTransformPoint(root.position));
                    WriteQ(w, Quaternion.Inverse(model.rotation) * root.rotation);
                    w.Write(Get<int>(_fState, bird));
                    w.Write(info.shortNameHash);
                    w.Write(info.normalizedTime);
                }, reliable: false);
            }
        }

        private static byte Flags(bool a, bool b, bool c, bool d, bool e, bool f)
        {
            return (byte)((a ? 1 : 0) | (b ? 2 : 0) | (c ? 4 : 0) | (d ? 8 : 0) | (e ? 16 : 0) | (f ? 32 : 0));
        }

        private static PetPose ReadPetPose(BinaryReader r)
        {
            var p = new PetPose
            {
                Gen = r.ReadInt32(), Placed = r.ReadBoolean(), Pos = ReadV3(r), Rot = ReadQ(r),
                State = r.ReadInt32(), WalkSpeed = r.ReadSingle(), Cmd = r.ReadInt32(), Pirate = r.ReadInt32(),
            };
            byte fl = r.ReadByte();
            p.Petting = (fl & 1) != 0; p.Shiver = (fl & 2) != 0; p.Rough = (fl & 4) != 0;
            p.Severe = (fl & 8) != 0; p.Stormy = (fl & 16) != 0; p.Hunting = (fl & 32) != 0;
            p.Loop = r.ReadString();
            return p;
        }

        // ================================================================== guest: dog and cat puppets

        private static bool DogUpdatePrefix(Component __instance, out float __state)
        {
            __state = Get<float>(_dCallUntil, __instance);
            if (!Guest) return true;
            DrivePet(__instance, _dogPose, _dogGen, isDog: true);
            return false;
        }

        // Host: a howl for being left alone starts a timer inside UpdateVoice; that timer moving is the howl.
        private static void DogUpdatePostfix(Component __instance, float __state)
        {
            try
            {
                if (!Host) return;
                if (!Mathf.Approximately(Get<float>(_dCallUntil, __instance), __state))
                {
                    bool pup = PluginInst != null && (bool)_pPuppyVoice.GetValue(PluginInst, null);
                    SendEvent(PetDog, EvHowl, pup ? "puppy_whine" : "howl", Vector3.zero, 0);
                }
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static bool CatUpdatePrefix(Component __instance)
        {
            if (!Guest) return true;
            DrivePet(__instance, _catPose, _catGen, isDog: false);
            return false;
        }

        private static void DrivePet(Component pet, PetPose pose, int gen, bool isDog)
        {
            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f)
                {
                    Call(isDog ? _dHush : _cHush, pet, true);
                    return;
                }
                Call(isDog ? _dHush : _cHush, pet, false);
                if (pose == null || pose.Gen != gen) return;

                // Shown or hidden: FurstMate keeps a pet "below" until her person boards.
                var placedField = isDog ? _dPlaced : _cPlaced;
                var model = Get<GameObject>(_wModel, pet);
                var box = Get<BoxCollider>(isDog ? _dBox : _cBox, pet);
                if (Get<bool>(placedField, pet) != pose.Placed || (model != null && model.activeSelf != pose.Placed))
                {
                    placedField.SetValue(pet, pose.Placed);
                    if (model != null) model.SetActive(pose.Placed);
                    if (box != null) box.enabled = pose.Placed;
                    _wState.SetValue(pet, -1);
                }

                (isDog ? _dogMotion : _catMotion).Apply(pet.transform, gen);

                if (Get<int>(_wState, pet) != pose.State) Call(_wSetState, pet, pose.State);
                var anim = Get<Animator>(_wAnim, pet);
                if (anim != null && anim.isActiveAndEnabled) anim.SetFloat("WalkSpeed", pose.WalkSpeed);

                _wRough.SetValue(pet, pose.Rough);
                _wSevere.SetValue(pet, pose.Severe);
                _wStormy.SetValue(pet, pose.Stormy);
                if (isDog)
                {
                    _dCmd.SetValue(pet, Enum.ToObject(_command, pose.Cmd));
                    _dPirate.SetValue(pet, pose.Pirate);
                    _dShiver.SetValue(pet, pose.Shiver);
                    _dPetUntil.SetValue(pet, pose.Petting ? Time.time + 1f : -1f);
                    AudioClip loop = pose.Loop.Length > 0 ? Call(_sClip, null, pose.Loop) as AudioClip : null;
                    Call(_dRunLoop, pet, loop, dt);
                }
                else
                {
                    _cShiver.SetValue(pet, pose.Shiver);
                    _cHunt.SetValue(pet, pose.Hunting ? 1 : 0);
                    _cPetUntil.SetValue(pet, pose.Petting ? Time.time + 1f : -1f);
                    Call(_cUpdateVoice, pet, pose.Petting, dt);
                }

                float due = isDog ? _nextDogBox : _nextCatBox;
                if (Time.unscaledTime >= due)
                {
                    if (isDog) _nextDogBox = Time.unscaledTime + 0.5f; else _nextCatBox = Time.unscaledTime + 0.5f;
                    Call(isDog ? _dSizeBox : _cSizeBox, pet);
                }
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        // ================================================================== guest: booby puppet

        private static bool BuildBirdPuppet(object keeper, BoatRefs boat, float size)
        {
            if (boat == null || boat.walkCol == null) return false;
            try
            {
                var perchPrefab = _bPerchPrefab.GetValue(keeper) as GameObject;
                var flyPrefab = _bFlyPrefab.GetValue(keeper) as GameObject;
                if (perchPrefab == null || flyPrefab == null) return false;
                var onYards = _bOnYards.GetValue(keeper) as BepInEx.Configuration.ConfigEntry<bool>;
                var scan = _ctorPerches.Invoke(new object[] { boat, onYards != null && onYards.Value });
                var world = _ctorGameBird.Invoke(new object[] { keeper, boat, scan });
                var flight = _ctorFlight.Invoke(new object[] { perchPrefab, flyPrefab, world, size });
                _gBird.SetValue(world, flight);
                var root = _fRoot.GetValue(flight) as Transform;
                Call(_gVoice, world, root.gameObject);
                _bBird.SetValue(keeper, flight);
                _bWorld.SetValue(keeper, world);
                _bShipOf.SetValue(keeper, boat);
                _bPerches.SetValue(keeper, scan);
                Call(_bAddLook, keeper);
                var model = _gModel.GetValue(world) as Transform;
                if (model != null) Call(_fHang, flight, model);

                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[FurstMateCompat] Could not build the booby for a crewmate's ship: " + e.Message);
                return false;
            }
        }

        private static void BirdLateUpdatePostfix(object __instance)
        {
            try
            {
                if (!Guest) return;
                var bird = _bBird.GetValue(__instance);
                var pose = _birdPose;
                if (bird == null || pose == null || pose.Gen != _birdGen) return;
                try
                {
                    var world = _bWorld.GetValue(__instance);
                    var model = world != null ? _gModel.GetValue(world) as Transform : null;
                    var root = _fRoot.GetValue(bird) as Transform;
                    if (model == null || root == null) return;

                    if (root.parent != model) Call(_fHang, bird, model);
                    _fMode.SetValue(bird, Enum.ToObject(_mode, pose.Mode));

                    var p = _fP.GetValue(bird);
                    var f = _fF.GetValue(bird);
                    var want = pose.Fly ? f : p;
                    var wantAnim = _bodyAnim.GetValue(want) as Animator;
                    if (!ReferenceEquals(_fCur.GetValue(bird), want))
                    {
                        Call(_fShow, bird, want);
                        if (wantAnim != null) { wantAnim.Rebind(); if (pose.Hash != 0) wantAnim.Play(pose.Hash, 0, pose.Norm); }
                    }
                    var fGo = _bodyGo.GetValue(f) as GameObject;
                    if (pose.Fly && fGo != null && fGo.activeSelf != pose.FlyActive) fGo.SetActive(pose.FlyActive);

                    float dt = Time.deltaTime;
                    _birdMotion.Apply(root, _birdGen);

                    if (Get<int>(_fState, bird) != pose.State) Call(_fSetState, bird, pose.State);
                    // One-shot animations pulse State for a few frames; when the clip itself differs for a while, play it.
                    if (wantAnim != null && wantAnim.isActiveAndEnabled && pose.Hash != 0 && !wantAnim.IsInTransition(0))
                    {
                        if (wantAnim.GetCurrentAnimatorStateInfo(0).shortNameHash != pose.Hash)
                        {
                            if (_hashMismatchSince < 0f) _hashMismatchSince = Time.unscaledTime;
                            else if (Time.unscaledTime - _hashMismatchSince > 0.25f)
                            {
                                wantAnim.Play(pose.Hash, 0, pose.Norm);
                                _hashMismatchSince = -1f;
                            }
                        }
                        else _hashMismatchSince = -1f;
                    }
                    if (pose.Fly && pose.FlyActive) Call(_fBeats, bird);
                }
                catch (Exception error) { PatchGuard.Fail(Report, error); }
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        // ================================================================== guest suppression

        private static bool GuestSkipPrefix() => !Guest;

        // ================================================================== host: events

        private static void SendEvent(byte pet, byte ev, string name, Vector3 v, int i)
        {
            Send(MsgEvent, w => { w.Write(pet); w.Write(ev); w.Write(name ?? ""); WriteV3(w, v); w.Write(i); });
        }

        private static void DogSayPrefix(string sound) { if (Host) SendEvent(PetDog, EvSay, sound, Vector3.zero, 0); }
        private static void DogBarkPostfix()
        {
            try
            {
                if (Host) SendEvent(PetDog, EvBark, "", Vector3.zero, 0);
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }
        private static void CatSayPrefix(string sound) { if (Host) SendEvent(PetCat, EvSay, sound, Vector3.zero, 0); }

        private static void DogRunLoopPrefix(AudioClip want)
        {
            if (!Host) return;
            string n = want != null ? want.name : "";
            _dogLoop = n.StartsWith("furstmate ", StringComparison.Ordinal) ? n.Substring(10) : n;
        }

        // The cat's pounce lasts only until her animator enters it, which a 10 Hz pose can miss.
        private static void WalkerSetStatePostfix(Component __instance, int s)
        {
            try
            {
                if (Host && s == 10 && _cat.IsInstanceOfType(__instance)) SendEvent(PetCat, EvPounce, "", Vector3.zero, 0);
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdPlayPostfix(string sound)
        {
            try
            {
                // Wing beats follow the animator on every machine (Beats), so only calls go over the wire.
                if (Host && sound != "beat") SendEvent(PetBird, EvSay, sound, Vector3.zero, 0);
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdSplashPostfix(object __instance, Vector3 at)
        {
            try
            {
                if (!Host) return;
                var model = _gModel.GetValue(__instance) as Transform;
                if (model != null) SendEvent(PetBird, EvSplash, "", model.InverseTransformPoint(at), 0);
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdCarryPostfix(object __instance, bool on)
        {
            try
            {
                if (!Host) return;
                var world = _fW.GetValue(__instance);
                var fish = world != null ? _gFish.GetValue(world) as GameObject : null;
                var sp = fish != null ? fish.GetComponent<SaveablePrefab>() : null;
                SendEvent(PetBird, EvCarry, "", Vector3.zero, on && sp != null ? sp.prefabIndex : -1);
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        // Host: the dropped fish is FurstMate's own code plus the item sync broadcast it could not know about.
        private static bool HostDropFishPrefix(object __instance, Vector3 at)
        {
            if (!Host) return true;
            try
            {
                var fish = _gFish.GetValue(__instance) as GameObject;
                var boat = _gBoat.GetValue(__instance) as BoatRefs;
                if (fish == null && OceanFishes.instance != null && boat != null) fish = OceanFishes.instance.GetFish(boat.transform);
                if (fish == null) return false;
                var go = UnityEngine.Object.Instantiate(fish, at, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                var item = go.GetComponent<ShipItem>();
                if (item == null) { UnityEngine.Object.Destroy(go); return false; }
                item.sold = true;
                var sp = go.GetComponent<SaveablePrefab>();
                if (sp != null) { sp.Start(); sp.RegisterToSave(); }
                _gFish.SetValue(__instance, null);
                ItemSync.Instance?.HostRefreshNextTick();

            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[FurstMateCompat] The booby's fish could not be dropped: " + e.Message);
            }
            return false;
        }

        // Guest: the fish in her bill is the host's fish, not one this machine rolls.
        private static bool GuestFishLookPrefix(object __instance, ref GameObject __result)
        {
            if (!Guest) return true;
            __result = null;
            var fish = _guestFish;
            _gFish.SetValue(__instance, fish);
            var mf = fish != null ? fish.GetComponent<MeshFilter>() : null;
            var mr = fish != null ? fish.GetComponent<MeshRenderer>() : null;
            if (mf == null || mr == null) return false;
            var go = new GameObject("the fish in her bill");
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
            go.transform.localScale = fish.transform.lossyScale;
            __result = go;
            return false;
        }

        private static bool NotePrefix() => !_replaying;

        private static void OnEvent(BinaryReader r)
        {
            byte pet = r.ReadByte(); byte ev = r.ReadByte(); string name = r.ReadString(); var v = ReadV3(r); int i = r.ReadInt32();
            try
            {
                if (pet == PetDog)
                {
                    var dog = Dog;
                    if (dog == null) return;
                    if (ev == EvSay) Call(_dSay, dog, name);
                    else if (ev == EvBark) Call(_dBark, dog);
                    else if (ev == EvHowl)
                    {
                        var far = _dFar.GetValue(dog) as AudioSource;
                        var clip = Call(_sClip, null, name) as AudioClip;
                        if (far != null && clip != null) far.PlayOneShot(clip, (float)_dVolume.GetValue(dog, null));
                    }
                }
                else if (pet == PetCat)
                {
                    var cat = Cat;
                    if (cat == null) return;
                    if (ev == EvSay) Call(_cSay, cat, name);
                    else if (ev == EvPounce)
                    {
                        var anim = Get<Animator>(_wAnim, cat);
                        if (anim != null && anim.isActiveAndEnabled) { anim.Play("Pounce", 0, 0f); Call(_wSetState, cat, 10); }
                    }
                }
                else if (pet == PetBird)
                {
                    var bk = BirdKeeperInst;
                    var bird = Bird;
                    var world = bk != null ? _bWorld.GetValue(bk) : null;
                    if (bird == null || world == null) return;
                    if (ev == EvSay) Call(_gPlay, world, name);
                    else if (ev == EvSplash)
                    {
                        var model = _gModel.GetValue(world) as Transform;
                        if (model != null) Call(_gSplash, world, model.TransformPoint(v));
                    }
                    else if (ev == EvCarry)
                    {
                        var dir = PrefabsDirectory.instance;
                        _guestFish = i >= 0 && dir != null && i < dir.directory.Length ? dir.directory[i] : null;
                        Call(_fCarry, bird, i >= 0 && _guestFish != null);
                    }
                }
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        // ================================================================== host: "you" includes the crew

        private static bool CrewmateAboard(BoatRefs boat, Transform frame, Vector3 near, bool home, out Vector3 feetWorld)
        {
            feetWorld = Vector3.zero;
            var players = CoopBehaviour.Instance?.Players;
            if (players == null || boat == null || frame == null) return false;
            float radius = 20f;
            if (home) { try { radius = (float)_hRadius.GetValue(null, null); } catch { } }
            return players.TryGetNearestCrewFeet(frame, near, home ? radius : 0f, home ? null : boat.transform, out feetWorld);
        }

        private static void WalkerPlayerAboardPostfix(Component __instance, ref bool __result, ref Vector3 pl)
        {
            try
            {
                if (!Host || __result) return;
                var boat = Get<BoatRefs>(_wBoat, __instance);
                var frame = Get<Transform>(_wWorldBoat, __instance);
                if (boat == null || frame == null) return;
                if (!CrewmateAboard(boat, frame, __instance.transform.position, _home.IsInstanceOfType(boat), out var feet)) return;
                pl = frame.InverseTransformPoint(feet);
                __result = true;
                if (_subFrame != Time.frameCount) { _subFrame = Time.frameCount; _subFeet.Clear(); }
                _subFeet[__instance] = pl;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void WalkerPlayerFeetPostfix(Component __instance, ref Vector3 __result)
        {
            try
            {
                if (!Host || _subFrame != Time.frameCount) return;
                if (_subFeet.TryGetValue(__instance, out var feet)) __result = feet;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdPlayerAboardPostfix(object __instance, ref bool __result)
        {
            try
            {
                if (!Host || __result) return;
                var boat = _gBoat.GetValue(__instance) as BoatRefs;
                var model = _gModel.GetValue(__instance) as Transform;
                if (model != null && CrewmateAboard(boat, model, model.position, false, out _)) __result = true;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdPlayerFeetPostfix(object __instance, ref Vector3 __result)
        {
            try
            {
                if (!Host) return;
                var boat = _gBoat.GetValue(__instance) as BoatRefs;
                if (boat == null || BoatOf(GameState.currentBoat) == boat) return;
                var model = _gModel.GetValue(__instance) as Transform;
                if (model != null && CrewmateAboard(boat, model, model.position, false, out var feet)) __result = feet;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        // ================================================================== guest: interactions go to the host

        private static void Request(byte kind, Action<BinaryWriter> body)
        {

            Send(kind, body);
        }

        private static bool GuestGivePrefix(object[] __args)
        {
            if (!Guest) return true;
            int c = Convert.ToInt32(__args[0]);
            Request(ReqDogCmd, w => w.Write(c));
            return false;
        }

        private static void GuestSleepSpotPrefix(Component __instance)
        {
            if (!Guest) return;
            var args = new object[] { Vector3.zero };
            if (!(bool)_wPlayerAboard.Invoke(__instance, args)) return;
            var spot = (Vector3)Call(_wSettle, __instance, (Vector3)args[0], 0.2f, 2.5f);
            int idx = SceneIndexOf(Get<BoatRefs>(_wBoat, __instance));
            Request(ReqSleepSpot, w => { w.Write(idx); WriteV3(w, spot); });
        }

        private static void PetUntilPrefix(Component __instance, out float __state) => __state = Get<float>(_dPetUntil, __instance);
        private static void CatPetUntilPrefix(Component __instance, out float __state) => __state = Get<float>(_cPetUntil, __instance);

        private static void DogPettedPostfix(Component __instance, float __state)
        {
            try
            {
                if (Guest && Get<float>(_dPetUntil, __instance) > __state) Request(ReqPet, w => w.Write(PetDog));
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void CatPettedPostfix(Component __instance, float __state)
        {
            try
            {
                if (Guest && Get<float>(_cPetUntil, __instance) > __state) Request(ReqPet, w => w.Write(PetCat));
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void DogFoodPrefix(object __instance, out float __state) => __state = Get<float>(_pFood, __instance);

        private static void DogFoodPostfix(object __instance, float __state)
        {
            try
            {
                float delta = Get<float>(_pFood, __instance) - __state;
                if (Guest && delta > 0f) Request(ReqFeed, w => { w.Write(PetDog); w.Write(delta); w.Write(false); });
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void CatFoodPrefix(object __instance, out float __state) => __state = Get<float>(_kFood, __instance);

        private static void CatFoodPostfix(object __instance, float __state)
        {
            try
            {
                float delta = Get<float>(_kFood, __instance) - __state;
                if (Guest && delta > 0f) Request(ReqFeed, w => { w.Write(PetCat); w.Write(delta); w.Write(false); });
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void CatFedPrefix(object __instance, out bool __state) => __state = Get<bool>(_kFed, __instance);

        private static void CatTavernPostfix(object __instance, bool __state)
        {
            try
            {
                if (!Guest || __state || !Get<bool>(_kFed, __instance)) return;
                var boat = BoatOf(GameState.currentBoat);
                int idx = SceneIndexOf(boat);
                var feet = LocalFeet(boat);
                Request(ReqCatTame, w => { w.Write(idx); WriteV3(w, feet); });
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void BirdOfferPrefix(object __instance, out float __state)
        {
            __state = Get<float>(_bFood, __instance) + 1000f * Convert.ToInt32(_bStage.GetValue(__instance));
        }

        private static void BirdOfferPostfix(object __instance, float __state, bool __result)
        {
            try
            {
                if (!Guest || !__result) return;
                int stageBefore = Mathf.FloorToInt((__state + 1f) / 1000f);
                float foodBefore = __state - stageBefore * 1000f;
                int stageNow = Convert.ToInt32(_bStage.GetValue(__instance));
                float delta = Mathf.Max(0f, Get<float>(_bFood, __instance) - foodBefore);
                bool tamed = stageNow != stageBefore;
                Request(ReqFeed, w => { w.Write(PetBird); w.Write(delta); w.Write(tamed); });
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static bool GuestBegPrefix()
        {
            if (!Guest) return true;
            Request(ReqBirdBeg, w => { });
            return false;
        }

        private static bool GuestCallHerePrefix()
        {
            if (!Guest) return true;
            BoatRefs target = BoatOf(GameState.currentBoat);
            if (target == null)
            {
                try { target = AccessTools.Method(_home, "Here")?.Invoke(null, null) as BoatRefs; } catch { }
            }
            if (target == null)
            {
                Note("Stand on a ship of yours, or in a home of yours, to call her.");
                return false;
            }
            int idx = SceneIndexOf(target);
            var feet = LocalFeet(target);
            Request(ReqHere, w => { w.Write(idx); WriteV3(w, feet); });
            return false;
        }

        private static bool GuestBuyClickPrefix(object __instance)
        {
            if (!Guest) return true;
            try { return RequestDogPurchase(__instance); }
            catch (Exception error)
            {
                PatchGuard.Fail(Report, error);
                return false;
            }
        }

        private static bool RequestDogPurchase(object plugin)
        {
            Guid world = _dogPurchaseWorld;
            if (PendingPurchases.ContainsKey(world)) return false;
            float asked = Get<float>(_pBuyAskedAt, plugin);
            if (Dog != null || Time.time - asked > 8f) return true; // FurstMate's own first click: the price, or why not
            if (world == System.Guid.Empty)
            {
                Note("The host's dog shop is still loading. Try again shortly.");
                return false;
            }
            _pBuyAskedAt.SetValue(plugin, -100f);
            var boat = BoatOf(GameState.currentBoat) ?? BoatOf(GameState.lastOwnedBoat);
            var price = _pPrice.GetValue(plugin) as BepInEx.Configuration.ConfigEntry<int>;
            if (price == null || boat == null || boat.walkCol == null) return false;
            int idx = SceneIndexOf(boat);
            var feet = LocalFeet(boat);
            bool shared = WalletSync.Instance?.Shared == true;
            int[] charge = Array.Empty<int>();
            if (!shared && !TryPay(price.Value, out charge))
            {
                Note("You cannot afford the dog.");
                return false;
            }
            var purchase = new DogPurchaseJournal.Charge { Purchase = System.Guid.NewGuid(), Currency = charge };
            PendingPurchases.Add(world, purchase);
            if (!CoopProfile.SaveFromGame())
            {
                PendingPurchases.Remove(world);
                Refund(charge, WalletSync.WalletForProfile);
                Note("Could not save your character profile. The dog purchase was cancelled.");
                return false;
            }
            _nextPurchaseStatus = Time.unscaledTime + 3f;
            Request(ReqBuyDog, w =>
            {
                WritePurchaseId(w, world, purchase.Purchase);
                w.Write(idx); WriteV3(w, feet); w.Write(price.Value); w.Write(shared);
            });
            return false;
        }

        private static bool TryPay(int price, out int[] charge)
        {
            charge = null;
            var wallet = PlayerGold.currency;
            if (wallet == null) return false;
            var before = (int[])wallet.Clone();
            try
            {
                if (!(bool)Call(_pPay, null, price, 2))
                {
                    Array.Copy(before, wallet, wallet.Length);
                    return false;
                }
                charge = before;
                for (int i = 0; i < charge.Length; i++) charge[i] -= wallet[i];
                return true;
            }
            catch
            {
                Array.Copy(before, wallet, wallet.Length);
                throw;
            }
        }

        private static void Refund(int[] charge, int[] wallet, int sign = 1)
        {
            if (charge == null || wallet == null) return;
            for (int i = 0; i < Math.Min(charge.Length, wallet.Length); i++) wallet[i] += sign * charge[i];
        }

        private static void SettleDogPurchase(Guid world, Guid id, bool success, string message)
        {
            if (world != _dogPurchaseWorld || !PendingPurchases.TryGetValue(world, out var charge) ||
                charge.Purchase != id) return;
            var wallet = WalletSync.WalletForProfile;
            if (!success && charge.Currency.Length > 0 && wallet == null) return;
            if (!success) Refund(charge.Currency, wallet);
            PendingPurchases.Remove(world);
            if (!CoopProfile.SaveFromGame())
            {
                PendingPurchases.Add(world, charge);
                if (!success) Refund(charge.Currency, wallet, -1);
                return;
            }
            Note(message);
        }

        private static Guid HostPurchaseWorld()
        {
            if (GameState.modData == null) GameState.modData = new Dictionary<string, string>();
            return DiceJournal.Associate(GameState.modData);
        }

        private static void WritePurchaseId(BinaryWriter writer, Guid world, Guid purchase)
        {
            writer.Write(world.ToByteArray());
            writer.Write(purchase.ToByteArray());
        }

        private static void ReplyDogPurchase(NetPeer sender, Guid world, Guid purchase, string result)
            => sender.Send(CreateMessage(MsgDogPurchase, w =>
            {
                WritePurchaseId(w, world, purchase); w.Write(result[0] == '1'); w.Write(result.Substring(1));
            }), DeliveryMethod.ReliableOrdered);

        private static void QueryDogPurchase(BinaryReader reader, NetPeer sender)
        {
            var world = new Guid(reader.ReadBytes(16));
            var purchase = new Guid(reader.ReadBytes(16));
            if (world != HostPurchaseWorld()) return;
            var result = DogPurchaseJournal.GetResult(GameState.modData, world, purchase);
            if (result == null)
            {
                // Close the purchase before replying. An old request must never spawn a dog after a refund.
                result = DogPurchaseJournal.RecordResult(GameState.modData, world, purchase, false,
                    "The dog purchase did not reach the host.");
            }
            ReplyDogPurchase(sender, world, purchase, result);
        }

        private static void BuyDog(BinaryReader reader, NetPeer sender)
        {
            var world = new Guid(reader.ReadBytes(16));
            var purchase = new Guid(reader.ReadBytes(16));
            int index = reader.ReadInt32();
            var feet = ReadV3(reader);
            int offeredPrice = reader.ReadInt32();
            bool shared = reader.ReadBoolean();
            if (world != HostPurchaseWorld()) return;
            var previous = DogPurchaseJournal.GetResult(GameState.modData, world, purchase);
            if (previous != null)
            {
                ReplyDogPurchase(sender, world, purchase, previous);
                return;
            }
            string result = "The dog purchase did not complete.";
            int[] charge = null;
            object changedPlugin = null;
            float previousFood = 0f;
            int previousBoughtDay = 0;
            try
            {
                var plugin = PluginInst;
                var boat = ResolveBoat(index);
                var price = plugin != null ? _pPrice.GetValue(plugin) as BepInEx.Configuration.ConfigEntry<int> : null;
                if (price == null || offeredPrice != price.Value || shared != (WalletSync.Instance?.Shared == true))
                    result = "The dog price or wallet changed. Ask for the price again.";
                else if (Dog != null || boat == null || boat.walkCol == null)
                    result = "The dog is no longer available for this boat.";
                else if (shared && !TryPay(price.Value, out charge))
                    result = "The crew cannot afford the dog.";
                else
                {
                    previousFood = Get<float>(_pFood, plugin);
                    previousBoughtDay = Get<int>(_pBoughtDay, plugin);
                    changedPlugin = plugin;
                    _pFood.SetValue(plugin, 100f);
                    _pBoughtDay.SetValue(plugin, GameState.day);
                    Call(_pSpawn, plugin, boat, (Vector3?)feet, Enum.ToObject(_command, 0), null);
                }
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
            bool success = changedPlugin != null && Dog != null;
            if (success)
                result = "You bought the dog.";
            else
            {
                Refund(charge, PlayerGold.currency);
                if (changedPlugin != null)
                {
                    try
                    {
                        _pFood.SetValue(changedPlugin, previousFood);
                        _pBoughtDay.SetValue(changedPlugin, previousBoughtDay);
                    }
                    catch (Exception error) { PatchGuard.Fail(Report, error); }
                }
            }
            string receipt = DogPurchaseJournal.RecordResult(GameState.modData, world, purchase, success, result);
            ReplyDogPurchase(sender, world, purchase, receipt);
        }

        // ================================================================== host: requests

        private static void OnRequest(byte kind, BinaryReader r, NetPeer sender)
        {
            var plugin = PluginInst;
            var keeper = CatKeeperInst;
            var bk = BirdKeeperInst;
            _replaying = true;
            try
            {
                switch (kind)
                {
                    case ReqDogCmd:
                    {
                        int c = r.ReadInt32();
                        var dog = Dog;
                        if (dog != null) Call(_dGive, dog, Enum.ToObject(_command, c));
                        break;
                    }
                    case ReqSleepSpot:
                    {
                        int idx = r.ReadInt32(); var spot = ReadV3(r);
                        var boat = ResolveBoat(idx);
                        if (plugin != null && boat != null) Call(_pSetSleepSpot, plugin, boat, spot);
                        break;
                    }
                    case ReqPet:
                    {
                        byte pet = r.ReadByte();
                        var who = pet == PetDog ? Dog : Cat;
                        if (who == null) break;
                        (pet == PetDog ? _dPetUntil : _cPetUntil).SetValue(who, Time.time + (pet == PetDog ? 12f : 15f));
                        _wRoute.SetValue(who, null);
                        break;
                    }
                    case ReqFeed:
                    {
                        byte pet = r.ReadByte(); float delta = r.ReadSingle(); bool tamed = r.ReadBoolean();
                        if (pet == PetDog && plugin != null)
                            _pFood.SetValue(plugin, Mathf.Clamp(Get<float>(_pFood, plugin) + delta, 0f, 100f));
                        else if (pet == PetCat && keeper != null)
                        {
                            _kFood.SetValue(keeper, Mathf.Clamp(Get<float>(_kFood, keeper) + delta, 0f, 100f));
                            var cat = Cat;
                            if (cat != null) Call(_cThanks, cat);
                        }
                        else if (pet == PetBird && bk != null)
                        {
                            bool visiting = Convert.ToInt32(_bStage.GetValue(bk)) == 1;
                            if (tamed && visiting) Call(_bTame, bk);
                            else
                            {
                                _bFood.SetValue(bk, Mathf.Clamp(Get<float>(_bFood, bk) + delta, 0f, 100f));
                                _bZeroHours.SetValue(bk, 0f);
                            }
                            var bird = Bird;
                            if (bird != null) Call(_fFed, bird);
                        }
                        break;
                    }
                    case ReqHere:
                    {
                        int idx = r.ReadInt32(); var feet = ReadV3(r);
                        var target = ResolveBoat(idx);
                        if (target == null || !(bool)Call(_pOwned, null, target)) break;
                        var dog = Dog;
                        if (dog != null && plugin != null && !Get<bool>(_wSevere, dog))
                        {
                            if (Get<BoatRefs>(_wBoat, dog) == target) Call(_dGive, dog, Enum.ToObject(_command, 0));
                            else if (target.walkCol != null) Call(_pSpawn, plugin, target, (Vector3?)feet, Enum.ToObject(_command, 0), null);
                        }
                        var cat = Cat;
                        if (cat != null && keeper != null && target.walkCol != null)
                        {
                            var catBoat = Get<BoatRefs>(_wBoat, cat);
                            bool toHome = _home.IsInstanceOfType(target), fromHome = _home.IsInstanceOfType(catBoat);
                            if (catBoat != target && (toHome || fromHome)) Call(_kSpawn, keeper, target, (Vector3?)feet, null);
                        }
                        break;
                    }
                    case ReqBuyDog:
                        BuyDog(r, sender); break;
                    case ReqDogPurchaseStatus:
                        QueryDogPurchase(r, sender); break;
                    case ReqCatTame:
                    {
                        int idx = r.ReadInt32(); var feet = ReadV3(r);
                        if (keeper == null || Get<bool>(_kFed, keeper)) break;
                        var old = Cat;
                        if (old != null) { _kCat.SetValue(keeper, null); UnityEngine.Object.Destroy(old.gameObject); }
                        _kFood.SetValue(keeper, 100f);
                        _kSunk.SetValue(keeper, false);
                        _kHadCat.SetValue(keeper, false);
                        _kFed.SetValue(keeper, true);
                        var boat = ResolveBoat(idx);
                        if (boat != null && boat.walkCol != null && (bool)Call(_pOwned, null, boat))
                        {
                            Call(_kSpawn, keeper, boat, (Vector3?)feet, null);
                            _kFed.SetValue(keeper, false);
                        }
                        break;
                    }
                    case ReqBirdBeg:
                    {
                        var bird = Bird;
                        if (bird != null) Call(_fBeg, bird);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[FurstMateCompat] A crewmate's pet request {kind} failed: {e.Message}");
            }
            finally
            {
                _replaying = false;
            }
            _nextWorld = 0f; // the crew sees the result on the next tick
        }

        // ================================================================== receive

        public static void Receive(PetMessage message, NetPeer sender)
        {
            if (message is PetRequestMsg)
            {
                if (!Host || (Net.FeaturesOf(sender) & ModFeatures.FurstMate) == 0) return;
            }
            else if (!Guest) return;
            if (!GameState.playing || GameState.currentlyLoading) return;
            using (var data = new MemoryStream(message.Data, writable: false))
            using (var reader = new BinaryReader(data))
            {
                byte kind = reader.ReadByte();
                long tick = reader.ReadInt64();
                if (message is PetRequestMsg)
                {
                    if (kind >= ReqDogCmd) OnRequest(kind, reader, sender);
                    return;
                }
                ReceiveState(kind, tick, reader);
            }
        }

        private static void ReceiveState(byte kind, long tick, BinaryReader reader)
        {
            switch (kind)
            {
                case MsgWorld: OnWorld(reader); break;
                case MsgDogPose: ReceivePetPose(reader, tick, _dogMotion, ref _dogPose); break;
                case MsgCatPose: ReceivePetPose(reader, tick, _catMotion, ref _catPose); break;
                case MsgBirdPose:
                    var birdPose = new BirdPose
                    {
                        Gen = reader.ReadInt32(), Mode = reader.ReadInt32(), Fly = reader.ReadBoolean(), FlyActive = reader.ReadBoolean(),
                        Pos = ReadV3(reader), Rot = ReadQ(reader), State = reader.ReadInt32(), Hash = reader.ReadInt32(),
                        Norm = reader.ReadSingle(),
                    };
                    if (_birdMotion.Push(tick, birdPose.Gen, birdPose.Pos, birdPose.Rot))
                        _birdPose = birdPose;
                    break;
                case MsgEvent: OnEvent(reader); break;
                case MsgDogPurchase:
                    SettleDogPurchase(new Guid(reader.ReadBytes(16)), new Guid(reader.ReadBytes(16)),
                        reader.ReadBoolean(), reader.ReadString());
                    break;
                case MsgDogPurchaseWorld:
                    _dogPurchaseWorld = new Guid(reader.ReadBytes(16));
                    _nextPurchaseStatus = 0f;
                    break;
            }
        }
    }
}
