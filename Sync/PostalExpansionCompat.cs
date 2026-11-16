using System;
using System.Collections;
using System.Collections.Generic;
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
    /// Postal Expansion (DogEggz, com.DogEggz.postalexpansion) adds a Mail tab with express, urgent and golden mail,
    /// registered and anonymous letters delivered to a place within delivery hours, its own reward rules and two new
    /// cargo items. It keeps what kind of mail a mission is in tables beside the Mission object, and builds its boards
    /// fresh every time one is shown. Co-op's boards came from the host's vanilla lists and rebuilt each mission from its
    /// plain fields, so guests never saw the Mail tab's offers, saw the host's special mail as plain express mail, could
    /// not deliver a letter at its place, and could hand a letter in at the office for its full reward.
    ///
    /// Now the host builds every tab with Postal Expansion's own lists, the kind of mail and the letter's id travel with
    /// each mission and are attached on the guest through Postal Expansion's own marking code, an accept names the offer
    /// by what it is rather than by its place on the page, and only the host completes a letter, at its place, in its
    /// hours. Built against 1.1.3; every member is reflected and a miss turns this off with a warning.
    /// </summary>
    public static class PostalExpansionCompat
    {
        public const string Guid = "com.DogEggz.postalexpansion";

        public const byte KindNone = 0, KindUrgent = 1, KindGolden = 2, KindRegistered = 3, KindAnonymous = 4;

        public static bool IsInstalled { get; private set; }
        public static bool SyncEnabled { get; private set; }
        private static string _version = "";

        private static Type _tabType, _kindType;
        private static MethodInfo _available, _currentTab, _boardPage, _updatePageCount, _updatePageText;
        private static MethodInfo _isLetter, _isUrgent, _markUrgent, _isGolden, _markGolden;
        private static MethodInfo _regTryGet, _anonTryGet, _letterTryGet, _letterDelivered, _isOpen;
        private static MethodInfo _getKind, _getDisplayName;
        private static Type _deliveryArea;
        private static MethodInfo _matchingLetter, _getDialogue, _showDialogue;
        private static FieldInfo _waitingLetter, _closedNotice, _dialogueArea;
        private static PropertyInfo _deliveryWindow;
        private static object _regSet, _anonSet;
        private static MethodInfo _regAttach, _anonAttach;
        private static PropertyInfo _regAll, _anonAll;
        private static readonly List<string> _missing = new List<string>();

        public static void Init()
        {
            IsInstalled = false;
            SyncEnabled = false;
            _missing.Clear();
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info == null)
                {
                    Plugin.Logger.LogInfo("[PostalCompat] Postal Expansion not installed.");
                    return;
                }
                IsInstalled = true;
                _version = info.Metadata.Version.ToString();
                var asm = info.Instance != null ? info.Instance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PostalExpansion");

                Type ui = T(asm, "PostalExpansion.PostalMissionUi"), mail = T(asm, "PostalExpansion.PostalMail");
                Type urgent = T(asm, "PostalExpansion.UrgentExpressMail"), golden = T(asm, "PostalExpansion.GoldenDeliveryMissions");
                Type reg = T(asm, "PostalExpansion.RegisteredLetterMissions"), anon = T(asm, "PostalExpansion.AnonymousLetterMissions");
                Type regDef = T(asm, "PostalExpansion.RegisteredLetterMissionDefinition");
                Type anonDef = T(asm, "PostalExpansion.AnonymousLetterMissionDefinition");
                Type letterDef = T(asm, "PostalExpansion.LetterMissionDefinition");
                _deliveryArea = T(asm, "PostalExpansion.LetterDeliveryArea");
                Type dialogueArea = T(asm, "PostalExpansion.LetterDialoguePresenceArea");
                Type letters = T(asm, "PostalExpansion.LetterMissions"), hours = T(asm, "PostalExpansion.LetterDeliveryHours");
                Type window = T(asm, "PostalExpansion.LetterDeliveryWindow");
                Type regRegistry = T(asm, "PostalExpansion.RegisteredLetterMissionRegistry");
                Type anonRegistry = T(asm, "PostalExpansion.AnonymousLetterMissionRegistry");
                Type display = T(asm, "PostalExpansion.SpecialMailDisplay");
                _tabType = T(asm, "PostalExpansion.PostalMissionTab");
                _kindType = T(asm, "PostalExpansion.SpecialMailKind");

                if (_missing.Count == 0)
                {
                    _available = M(ui, "GetAvailableMissions", typeof(Port), _tabType);
                    _currentTab = M(ui, "GetCurrentTab", typeof(MissionListUI));
                    _boardPage = M(ui, "GetMissions", typeof(MissionListUI), typeof(int));
                    _updatePageCount = M(ui, "UpdatePageCount", typeof(MissionListUI));
                    _updatePageText = M(ui, "UpdatePageCountText", typeof(MissionListUI));
                    _isLetter = M(mail, "IsRegisteredLetter", typeof(SaveablePrefab));
                    _isUrgent = M(urgent, "IsUrgent", typeof(Mission));
                    _markUrgent = M(urgent, "MarkRuntime", typeof(Mission));
                    _isGolden = M(golden, "IsGolden", typeof(Mission));
                    _markGolden = M(golden, "MarkRuntime", typeof(Mission));
                    _regTryGet = M(reg, "TryGetDefinition", typeof(Mission), regDef.MakeByRefType());
                    _anonTryGet = M(anon, "TryGetDefinition", typeof(Mission), anonDef.MakeByRefType());
                    _letterTryGet = M(letters, "TryGetDefinition", typeof(Mission), letterDef.MakeByRefType());
                    _letterDelivered = M(letters, "MissionDelivered", typeof(Mission));
                    _isOpen = M(hours, "IsOpen", window);
                    _matchingLetter = M(_deliveryArea, "TryGetMatchingMission", typeof(Good), typeof(Mission).MakeByRefType());
                    _getDialogue = M(letterDef, "GetDeliveryDialogue", typeof(Mission));
                    _showDialogue = M(dialogueArea, "ShowForDelivery", typeof(string));
                    _deliveryWindow = AccessTools.Property(letterDef, "DeliveryWindow");
                    _waitingLetter = AccessTools.Field(_deliveryArea, "waitingLetter");
                    _closedNotice = AccessTools.Field(_deliveryArea, "closedNoticeShown");
                    _dialogueArea = AccessTools.Field(_deliveryArea, "dialogueArea");
                    if (_deliveryWindow == null || _waitingLetter == null || _closedNotice == null || _dialogueArea == null)
                        _missing.Add("LetterDeliveryArea state");
                    _getKind = M(display, "GetKind", typeof(Mission));
                    _getDisplayName = M(display, "GetDisplayName", _kindType);
                    _regSet = AccessTools.Field(reg, "Missions")?.GetValue(null);
                    _anonSet = AccessTools.Field(anon, "Missions")?.GetValue(null);
                    _regAttach = _regSet != null ? AccessTools.Method(_regSet.GetType(), "Attach", new[] { typeof(Mission), regDef }) : null;
                    _anonAttach = _anonSet != null ? AccessTools.Method(_anonSet.GetType(), "Attach", new[] { typeof(Mission), anonDef }) : null;
                    if (_regAttach == null) _missing.Add("RegisteredLetterMissions.Missions.Attach");
                    if (_anonAttach == null) _missing.Add("AnonymousLetterMissions.Missions.Attach");
                    _regAll = AccessTools.Property(regRegistry, "All");
                    _anonAll = AccessTools.Property(anonRegistry, "All");
                    if (_regAll == null || _anonAll == null) _missing.Add("LetterMissionRegistry.All");
                }

                SyncEnabled = _missing.Count == 0;
                if (SyncEnabled)
                    Plugin.Logger.LogInfo($"[PostalCompat] Postal Expansion v{_version} detected; the host's mail boards and letters are shared.");
                else
                    Plugin.Logger.LogWarning($"[PostalCompat] Postal Expansion v{_version} detected, but {_missing.Count} member(s) did not resolve " +
                        $"({string.Join(", ", _missing.Take(6).ToArray())}). Postal sync disabled: guests see vanilla boards. " +
                        "Check for a Sailwind Co-op update.");
            }
            catch (Exception e)
            {
                SyncEnabled = false;
                Plugin.Logger.LogWarning("[PostalCompat] Detection threw. Postal sync disabled. " + e);
            }
        }

        internal static string Version => _version;

        internal static bool MatchesHost(ModEntry[] mods)
        {
            if (!SyncEnabled) return false;
            foreach (var mod in mods)
                foreach (var plugin in mod.Plugins)
                    if (string.Equals(plugin.Guid, Guid, StringComparison.OrdinalIgnoreCase))
                        return string.Equals(plugin.Version, Version, StringComparison.Ordinal);
            return false;
        }

        private static CoopNet Net => CoopBehaviour.Instance?.Net;
        internal static bool GuestBoard => SyncEnabled && CoopBehaviour.Instance?.PlayingCoop == true && Net.Role == Role.Client &&
            (Net.HostModFeatures & ModFeatures.PostalExpansion) != 0;
        private static PostalBoardRequestMsg _pendingBoard;
        private sealed class PendingLetter
        {
            public Component Area;
            public Good Letter;
            public string Dialogue;
        }
        private static readonly Dictionary<uint, PendingLetter> PendingLetters = new Dictionary<uint, PendingLetter>();

        internal static void ApplyPatches(Harmony harmony)
        {
            if (!SyncEnabled) return;
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(MissionListUI), nameof(MissionListUI.EnablePortMissionUI),
                new[] { typeof(Mission[]), typeof(Transform), typeof(PortDude) },
                target => harmony.Patch(target, postfix: Callback(nameof(BoardChanged))));
            hooks.Install(typeof(MissionListUI), nameof(MissionListUI.ResetPage), Type.EmptyTypes,
                target => harmony.Patch(target, postfix: Callback(nameof(BoardChanged))));
            hooks.Install(typeof(MissionListUI), nameof(MissionListUI.ChangePage), new[] { typeof(int) },
                target => harmony.Patch(target, postfix: Callback(nameof(BoardChanged))));
            hooks.Install(typeof(Good), nameof(Good.Deliver), Type.EmptyTypes,
                target => harmony.Patch(target, prefix: Callback(nameof(BeforeLetterDelivery))));
            hooks.Install(_deliveryArea, "TryDeliver", Type.EmptyTypes,
                target => harmony.Patch(target, prefix: Callback(nameof(BeforeDropOff))));
            if (hooks.Installed != hooks.Total) SyncEnabled = false;
            PatchHealth.Report("Postal Expansion", hooks);
        }

        private static HarmonyMethod Callback(string method)
            => new HarmonyMethod(typeof(PostalExpansionCompat), method) { after = new[] { Guid } };

        private static void BoardChanged(MissionListUI __instance)
        {
            if (!GuestBoard || !GameState.inPortMissionList) return;
            PatchGuard.Run(() =>
            {
                var board = Traverse.Create(__instance);
                var port = board.Field("currentPortDude").GetValue<PortDude>()?.GetPort();
                if (port == null) return;
                _pendingBoard = new PostalBoardRequestMsg
                {
                    Request = SessionCounters.NextRevision(), Port = port.portIndex,
                    Page = board.Field("currentPage").GetValue<int>(), Tab = CurrentTab(__instance.worldMissions),
                };
                Net.SendMod(_pendingBoard, ModFeatures.PostalExpansion, DeliveryMethod.ReliableOrdered);
            }, Report);
        }

        internal static void OnBoardRequest(PostalBoardRequestMsg request, NetPeer peer)
        {
            if (!SyncEnabled || (Net.FeaturesOf(peer) & ModFeatures.PostalExpansion) == 0) return;
            var port = PortAt(request.Port);
            if (!TryBuildBoard(port, request.Tab, request.Page, out var offered, out int total)) return;
            var entries = new List<MissionEntry>(offered.Length);
            for (int i = 0; i < offered.Length; i++)
            {
                if (offered[i] == null) continue;
                var d = offered[i].PrepareSaveData();
                var e = new MissionEntry
                {
                    MissionIndex = (byte)i, OriginPort = d.originPort, DestinationPort = d.destinationPort,
                    GoodPrefabIndex = d.goodPrefabIndex, GoodCount = d.goodCount, TotalPrice = d.totalPrice,
                    InsuranceLevel = d.insuranceLevel, Distance = d.distance, DueDay = d.dueDay, PostalTab = request.Tab,
                };
                Describe(offered[i], out e.PostalKind, out e.PostalLetterId);
                if ((Net.FeaturesOf(peer) & ModFeatures.EconomyOverhaul) != 0 &&
                    EconomyOverhaulCompat.ReadOffer(offered[i], out e.ModModifiers, out bool world, out e.ModOffered))
                    e.ModFlags = (byte)(1 | (world ? 2 : 0));
                entries.Add(e);
            }
            peer.Send(new PostalBoardMsg
            {
                Address = request, Total = total, Offers = new MissionJournalMsg { Missions = entries.ToArray() },
            }, DeliveryMethod.ReliableOrdered);
        }

        internal static void OnBoard(PostalBoardMsg message)
        {
            var ui = MissionListUI.instance;
            var request = message.Address;
            if (!GuestBoard || !GameState.inPortMissionList || ui == null || _pendingBoard?.Request != request.Request) return;
            var board = Traverse.Create(ui);
            if (board.Field("currentPortDude").GetValue<PortDude>()?.GetPort()?.portIndex != request.Port ||
                CurrentTab(ui.worldMissions) != request.Tab || board.Field("currentPage").GetValue<int>() != request.Page) return;
            _pendingBoard = null;
            var missions = new Mission[5];
            foreach (var e in message.Offers.Missions)
            {
                if (e.MissionIndex >= missions.Length) continue;
                try
                {
                    var mission = new Mission(new SaveMissionData(-1, e.OriginPort, e.DestinationPort,
                        e.GoodPrefabIndex, e.GoodCount, e.TotalPrice, e.InsuranceLevel, e.Distance, 0, e.DueDay));
                    Attach(mission, e.PostalKind, e.PostalLetterId);
                    if ((e.ModFlags & 1) != 0) EconomyOverhaulCompat.WriteOffer(mission, e.ModModifiers, (e.ModFlags & 2) != 0, e.ModOffered);
                    missions[e.MissionIndex] = mission;
                }
                catch (Exception error) { Report(error); }
            }
            int pages = Math.Max(1, (int)Math.Ceiling(message.Total / 5d));
            board.Field("currentPageCount").SetValue(pages);
            var label = board.Field("pageCountText").GetValue<TextMesh>();
            if (label != null) label.text = (request.Page + 1) + " / " + pages;
            ui.DisplayMissions(missions);
        }

        internal static void Reset()
        {
            _pendingBoard = null;
            PendingLetters.Clear();
        }

        internal static void RefreshGuestBoard()
        {
            if (_pendingBoard == null && MissionListUI.instance != null) BoardChanged(MissionListUI.instance);
        }

        internal static bool IsLetterMission(Mission mission)
        {
            if (!SyncEnabled || mission == null) return false;
            try { return (bool)_letterTryGet.Invoke(null, new object[] { mission, null }); }
            catch (Exception error) { Report(error); return false; }
        }

        internal static Port PortAt(int index)
            => Port.ports != null && index >= 0 && index < Port.ports.Length ? Port.ports[index] : null;

        private static bool BeforeLetterDelivery(Good __instance)
        {
            var sync = MissionSync.Instance;
            if (sync?.IsConnectedClient != true || __instance == null ||
                !IsLetter(__instance.GetComponent<SaveablePrefab>())) return true;
            try
            {
                var mission = __instance.GetAssignedMission();
                if (GuestBoard && mission?.destinationPort != null)
                    sync.RequestDeliver(__instance.GetComponent<ShipItem>(), mission.destinationPort.portIndex, postalDropOff: true);
            }
            catch (Exception error) { Report(error); }
            return false;
        }

        private static bool BeforeDropOff(Component __instance)
        {
            var sync = MissionSync.Instance;
            if (sync?.IsConnectedClient != true) return true;
            if (!GuestBoard) return false;

            try
            {
                var letter = _waitingLetter.GetValue(__instance) as Good;
                if (letter == null) return true;
                if (PendingLetters.Values.Any(attempt => attempt.Letter == letter)) return false;
                var match = new object[] { letter, null };
                if (!(bool)_matchingLetter.Invoke(__instance, match) || !(match[1] is Mission mission)) return true;

                var args = new object[] { mission, null };
                if (!(bool)_letterTryGet.Invoke(null, args)) return false;
                if (!IsOpen(args[1])) return true; // The local trigger handles its normal closed-hours message.
                string dialogue = _getDialogue.Invoke(args[1], new object[] { mission }) as string;
                uint id = SessionCounters.NextRevision();
                if (sync.RequestDeliver(letter.GetComponent<ShipItem>(), mission.destinationPort.portIndex, true, id))
                    PendingLetters.Add(id, new PendingLetter { Area = __instance, Letter = letter, Dialogue = dialogue });
            }
            catch (Exception error) { Report(error); }
            return false;
        }

        internal static bool CompleteLetterDelivery(uint id, bool delivered)
        {
            if (!PendingLetters.TryGetValue(id, out var attempt)) return false;
            PendingLetters.Remove(id);
            if (attempt.Area == null) return true;
            bool stillWaiting = ReferenceEquals(_waitingLetter.GetValue(attempt.Area), attempt.Letter);
            if (!delivered)
            {
                if (!stillWaiting) return true;
                // Leave the letter waiting. The trigger can try again after clock sync or when it reopens.
                bool show = !(bool)_closedNotice.GetValue(attempt.Area);
                _closedNotice.SetValue(attempt.Area, true);
                return show;
            }

            if (stillWaiting)
            {
                _waitingLetter.SetValue(attempt.Area, null);
                _closedNotice.SetValue(attempt.Area, false);
            }
            var presence = _dialogueArea.GetValue(attempt.Area) as Component;
            if (presence != null && !string.IsNullOrEmpty(attempt.Dialogue))
                _showDialogue.Invoke(presence, new object[] { attempt.Dialogue });
            return true;
        }

        private static bool IsOpen(object definition)
            => (bool)_isOpen.Invoke(null, new[] { _deliveryWindow.GetValue(definition, null) });

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[PostalCompat] " + error.Message);

        private static Type T(Assembly asm, string name)
        {
            var t = asm?.GetType(name);
            if (t == null) _missing.Add(name);
            return t;
        }

        private static MethodInfo M(Type t, string name, params Type[] args)
        {
            var m = t != null && args.All(a => a != null) ? AccessTools.Method(t, name, args) : null;
            if (m == null) _missing.Add((t?.Name ?? "?") + "." + name);
            return m;
        }

        // ================================================================== mission metadata

        /// <summary>What kind of Postal Expansion mail a mission is, and the letter's id.</summary>
        public static void Describe(Mission mission, out byte kind, out string letterId)
        {
            kind = KindNone;
            letterId = "";
            if (!SyncEnabled || mission == null) return;
            try
            {
                var args = new object[] { mission, null };
                if ((bool)_anonTryGet.Invoke(null, args)) { kind = KindAnonymous; letterId = DefinitionId(args[1]); return; }
                args = new object[] { mission, null };
                if ((bool)_regTryGet.Invoke(null, args)) { kind = KindRegistered; letterId = DefinitionId(args[1]); return; }
                if ((bool)_isGolden.Invoke(null, new object[] { mission })) { kind = KindGolden; return; }
                if ((bool)_isUrgent.Invoke(null, new object[] { mission })) kind = KindUrgent;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PostalCompat] Could not read a mission's mail kind: " + e.Message);
            }
        }

        /// <summary>Marks a mission rebuilt from the wire the way Postal Expansion marks its own, without changing its numbers.</summary>
        public static void Attach(Mission mission, byte kind, string letterId)
        {
            if (!SyncEnabled || mission == null || kind == KindNone) return;
            try
            {
                switch (kind)
                {
                    case KindUrgent: _markUrgent.Invoke(null, new object[] { mission }); break;
                    case KindGolden: _markGolden.Invoke(null, new object[] { mission }); break;
                    case KindRegistered:
                    case KindAnonymous:
                    {
                        bool anon = kind == KindAnonymous;
                        var def = FindDefinition(anon ? _anonAll : _regAll, letterId);
                        if (def == null)
                        {
                            Plugin.Logger.LogWarning($"[PostalCompat] No letter '{letterId}' here; the host may run another Postal Expansion version.");
                            return;
                        }
                        // Attach also sets the letter's name, fixed gold and route distance, which match the host's.
                        int price = mission.totalPrice, due = mission.dueDay;
                        float distance = mission.distance;
                        (anon ? _anonAttach : _regAttach).Invoke(anon ? _anonSet : _regSet, new[] { mission, def });
                        mission.totalPrice = price;
                        mission.dueDay = due;
                        mission.distance = distance;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"[PostalCompat] Could not mark a mission as mail kind {kind}: {e.Message}");
            }
        }

        private static object FindDefinition(PropertyInfo all, string id)
        {
            if (all == null || string.IsNullOrEmpty(id)) return null;
            if (!(all.GetValue(null, null) is IEnumerable list)) return null;
            foreach (var def in list)
                if (def != null && DefinitionId(def) == id) return def;
            return null;
        }

        private static string DefinitionId(object def)
        {
            if (def == null) return "";
            return AccessTools.Property(def.GetType(), "Id")?.GetValue(def, null) as string ?? "";
        }

        /// <summary>The name the host's delivery toast should use for special mail, or null for anything else.</summary>
        public static string DisplayName(Mission mission)
        {
            if (!SyncEnabled || mission == null) return null;
            try
            {
                var kind = _getKind.Invoke(null, new object[] { mission });
                if (Convert.ToInt32(kind) == 0) return null;
                return _getDisplayName.Invoke(null, new[] { kind }) as string;
            }
            catch { return null; }
        }

        public static bool IsLetter(SaveablePrefab prefab)
        {
            if (!IsInstalled || _isLetter == null || prefab == null) return false;
            try { return (bool)_isLetter.Invoke(null, new object[] { prefab }); }
            catch { return false; }
        }

        // ================================================================== boards

        /// <summary>Guest: which of Postal Expansion's tabs the open board shows (0 local, 1 world, 2 mail).</summary>
        public static byte CurrentTab(bool isWorld)
        {
            if (SyncEnabled && MissionListUI.instance != null)
            {
                try { return (byte)Convert.ToInt32(_currentTab.Invoke(null, new object[] { MissionListUI.instance })); }
                catch { }
            }
            return isWorld ? (byte)1 : (byte)0;
        }

        private static List<Mission> Offers(Port port, byte tab)
        {
            var list = _available.Invoke(null, new[] { port, Enum.ToObject(_tabType, (int)tab) }) as List<Mission>;
            return list ?? new List<Mission>();
        }

        /// <summary>Host: one page of a port's board for a tab, built by Postal Expansion itself.</summary>
        public static bool TryBuildBoard(Port port, byte tab, int page, out Mission[] missions, out int total)
        {
            missions = null;
            total = 0;
            if (!SyncEnabled || port == null) return false;
            try
            {
                var list = Offers(port, tab);
                total = list.Count;
                missions = new Mission[5];
                for (int i = 0; i < 5; i++)
                {
                    long at = (long)page * 5 + i;
                    missions[i] = at >= 0 && at < list.Count ? list[(int)at] : null;
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PostalCompat] Could not build a board for a crewmate: " + e.Message);
                missions = null;
                return false;
            }
        }

        /// <summary>Host: the offer a guest accepted, found in a freshly built list by what it is.</summary>
        public static Mission FindOffer(Port port, byte tab, MissionEntry want)
        {
            try
            {
                Mission best = null;
                int bestScore = int.MaxValue;
                foreach (var m in Offers(port, tab))
                {
                    if (m == null || m.originPort == null || m.destinationPort == null || m.goodPrefab == null) continue;
                    if (m.originPort.portIndex != want.OriginPort || m.destinationPort.portIndex != want.DestinationPort) continue;
                    var sp = m.goodPrefab.GetComponent<SaveablePrefab>();
                    if (sp == null || sp.prefabIndex != want.GoodPrefabIndex) continue;
                    Describe(m, out byte kind, out string id);
                    if (kind != want.PostalKind || (id ?? "") != (want.PostalLetterId ?? "")) continue;
                    int score = Math.Abs(m.totalPrice - want.TotalPrice) + Math.Abs(m.dueDay - want.DueDay) * 1000
                                + Math.Abs(m.goodCount - want.GoodCount) * 100000;
                    if (score < bestScore) { bestScore = score; best = m; }
                }
                return best;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PostalCompat] Could not find a crewmate's offer: " + e.Message);
                return null;
            }
        }

        /// <summary>Host: redraw the host's own open board after a guest's accept, through Postal Expansion's paging.</summary>
        public static bool RefreshHostBoard(MissionListUI ui)
        {
            if (!SyncEnabled || ui == null) return false;
            try
            {
                int page = Traverse.Create(ui).Field("currentPage").GetValue<int>();
                var missions = _boardPage.Invoke(null, new object[] { ui, page }) as Mission[];
                if (missions == null) return false;
                _updatePageCount.Invoke(null, new object[] { ui });
                _updatePageText.Invoke(null, new object[] { ui });
                ui.DisplayMissions(missions);
                if (MissionDetailsUI.instance != null)
                    Traverse.Create(MissionDetailsUI.instance).Field("UI").GetValue<GameObject>()?.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PostalCompat] Could not redraw the board: " + e.Message);
                return false;
            }
        }

        // ================================================================== delivery

        /// <summary>
        /// Host: check delivery hours and claim the letter as Postal Expansion does at a drop-off.
        /// </summary>
        public static bool HostMayDeliver(Mission mission, out string refusal)
        {
            refusal = "This letter cannot be delivered here.";
            if (!SyncEnabled || mission == null) return false;
            try
            {
                var args = new object[] { mission, null };
                if (!(bool)_letterTryGet.Invoke(null, args)) return false;
                if (!IsOpen(args[1]))
                {
                    refusal = "The drop-off is closed on the host. The letter is still waiting.";
                    return false;
                }
                _letterDelivered.Invoke(null, new object[] { mission });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[PostalCompat] Could not check a letter delivery: " + e.Message);
                return false;
            }
        }
    }
}
