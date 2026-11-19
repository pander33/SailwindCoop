using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>A button of the co-op page: a copy of a game button whose script was swapped for this one.</summary>
    internal sealed class CoopMenuButton : GoPointerButton
    {
        internal Action Pressed;

        // MouseButtonPointer calls this for the cursor of the game's menus.
        public override void OnActivate()
        {
            Action pressed = Pressed;
            if (pressed != null) PatchGuard.Run(pressed, e => Plugin.Logger.LogWarning("[GameMenu] " + e));
        }
    }

    /// <summary>
    /// The co-op session in the game's own menu, built from copies of its buttons and texts: a
    /// <b>Co-op</b> button on the title screen opens a page to join a crew, and one in the Escape
    /// menu of a loaded world opens the same page to host that world and to see and end a session.
    /// The page reads and changes the session through <see cref="CoopMenuUI"/>, so it does what the
    /// F8 window does and shows what that window would be opened for; the window stays for the
    /// rarely changed settings. What is shown is worked out from the session every refresh.
    /// </summary>
    internal sealed class GameMenuUI
    {
        private enum Place { Closed, Title, World }

        private struct Entry
        {
            internal string Label;
            internal Action Action;
            internal bool Enabled;
            internal Entry(string label, Action action, bool enabled = true) { Label = label; Action = action; Enabled = enabled; }
        }

        private sealed class Row
        {
            internal GameObject Root;
            internal CoopMenuButton Button;
            internal TextMesh Text;
            internal Color Color;
            internal Action Action;
        }

        private const int Rows = 5;
        private const int BodyLines = 4, BodyWidth = 52, RowWidth = 40, FieldLength = 64;
        private const int MaxFailures = 3;
        // The page is a copy of the game's recovery page; these places on it were tried in game 0.39.
        private const float BodyY = 1.25f, FirstRowY = 0.82f, RowStep = 0.255f, PrimaryY = -0.48f, BackY = -0.82f, FrontZ = -0.10f;
        // The Escape menu's Co-op button: right column, beside the game's version.
        private const float WorldEntryX = 1.45f, WorldEntryY = -0.62f;
        private const float PauseEntryX = 1.55f, PauseEntryY = -0.96f;

        private static readonly FieldInfo AnimsPlaying = AccessTools.Field(typeof(StartMenu), "animsPlaying");
        private static readonly MethodInfo MoveToPlayer = AccessTools.Method(typeof(StartMenu), "MoveMenuToPlayer");

        private readonly CoopBehaviour _coop;
        private readonly CoopMenuUI _window;
        private readonly Action _togglePause;

        private StartMenu _menu;
        private GameObject _kit, _startUI, _settingsUI, _recoveryUI, _confirmQuitUI, _page, _titleEntry, _worldEntry;
        private TextMesh _body;
        private readonly Row[] _rows = new Row[Rows];
        private Row _primary;
        private Row _pauseEntry;

        private Place _place = Place.Closed;
        private bool _typing;
        private int _typingEndedFrame = -1;
        // A join confirmed in a world goes on from the title screen; the page is opened there again.
        private bool _reopenTitle;
        private float _bindAt, _boundAt, _refreshAt;
        private bool _broken, _layoutWarned;
        private bool _poseSyncedWhilePaused;
        private int _failures;

        public GameMenuUI(CoopBehaviour coop, CoopMenuUI window)
        {
            _coop = coop;
            _window = window;
            _togglePause = coop.ToggleWorldPause;
        }

        public bool Open => _place != Place.Closed;

        internal bool WorldMenuOpen => GameState.playing && (_place == Place.World ||
            (_settingsUI != null && _settingsUI.activeInHierarchy) ||
            (_recoveryUI != null && _recoveryUI.activeInHierarchy) ||
            (_confirmQuitUI != null && _confirmQuitUI.activeInHierarchy));

        // -----------------------------------------------------------------
        // Patch: Escape on the page
        // -----------------------------------------------------------------

        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(StartMenu), "LateUpdate", Type.EmptyTypes, method => harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GameMenuUI), nameof(MenuKeyPrefix)),
                postfix: new HarmonyMethod(typeof(GameMenuUI), nameof(KeepWorldMenuInView))));
            PatchHealth.Report("Game menu", hooks);

            // The game's key bindings (GameInput, in Oculus.VR) do not look
            // at its menus: F presses the button under the cursor, C switches the camera. While an
            // address is typed on the page they read nothing.
            Type keys = AccessTools.TypeByName("GameInput");
            foreach (string name in new[] { "GetKeyDown", "GetKey", "GetKeyUp" })
            {
                try
                {
                    MethodInfo method = keys != null ? AccessTools.Method(keys, name) : null;
                    if (method == null) throw new MissingMethodException("GameInput", name);
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(GameMenuUI), nameof(KeyPrefix)));
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning("[GameMenu] GameInput." + name + " not hooked, the game's keys stay live while an address is typed: " + e.Message);
                }
            }
        }

        private static bool KeyPrefix(InputName __0, ref bool __result)
        {
            // Called many times a frame: a plain try instead of a guard that allocates.
            try
            {
                if (CoopBehaviour.Instance?.GameMenu?.Typing != true &&
                    !(IsMovementKey(__0) && GameMenuPause.HoldsMovement)) return true;
            }
            catch { return true; }
            __result = false;
            return false;
        }

        private static bool IsMovementKey(InputName key)
        {
            switch (key)
            {
                case InputName.MoveUp:
                case InputName.MoveDown:
                case InputName.MoveLeft:
                case InputName.MoveRight:
                case InputName.Run:
                case InputName.Walk:
                case InputName.Jump:
                case InputName.Crouch:
                    return true;
                default:
                    return false;
            }
        }

        internal bool Typing => _typing && _place != Place.Closed;

        // The game reads Escape here and, with none of its own pages open, would open its menu again
        // over the page. Escape closes the page and the menu together, as on the game's other pages.
        private static bool MenuKeyPrefix() => PatchGuard.Prefix(
            () => CoopBehaviour.Instance?.GameMenu?.TakeMenuKey() != true, Report);

        private static void KeepWorldMenuInView()
        {
            try { CoopBehaviour.Instance?.GameMenu?.RefreshWorldMenu(); }
            catch (Exception error) { Report(error); }
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[GameMenu] " + error);

        private bool TakeMenuKey()
        {
            if (_place != Place.World || _page == null || !_page.activeSelf) return false;
            if (!Input.GetKeyDown("escape") && !Input.GetKeyDown("f10") && !Input.GetKeyDown(KeyCode.JoystickButton6)) return false;
            // This Escape only ended the typing (read earlier in the frame, in Update).
            if (_typingEndedFrame == Time.frameCount) return true;
            ClosePage();
            Invoke("SettingsToGame");
            return true;
        }

        // -----------------------------------------------------------------
        // Tick
        // -----------------------------------------------------------------

        public void Tick()
        {
            if (_broken) return;
            try
            {
                Bind();
                if (_menu == null) { Forget(); return; }
                // Joined, or the confirmed join never left the world (its save failed): nothing to reopen for.
                if (_coop.ClientJoined || (GameState.playing && !_coop.LeavingWorld && !_coop.SavingBeforeJoin &&
                                           !_coop.HasPendingWorldJoin && !_window.JoinConfirmationAccepted))
                    _reopenTitle = false;
                // Not in the frame the title scene came: StartMenu.Start has to measure its logo first.
                if (_reopenTitle && _place == Place.Closed && !GameState.playing && !GameState.currentlyLoading &&
                    Time.unscaledTime >= _boundAt + 1f)
                {
                    OpenTitle();
                    if (_place == Place.Title) _reopenTitle = false;
                }
                bool inWorld = GameState.playing && !GameState.currentlyLoading;
                if (_worldEntry != null && _worldEntry.activeSelf != inWorld) _worldEntry.SetActive(inWorld);
                // A world that starts loading takes the title page with it; a world that goes takes the Escape page.
                if (_place == Place.Title && (GameState.playing || GameState.currentlyLoading)) ClosePage();
                // The Escape menu closed some other way than through the page: the cursor went with it.
                if (_place == Place.World && (!inWorld || !GameState.inCursorMenu)) ClosePage();
                if (_place == Place.Closed) return;

                if (_place == Place.Title && !_typing && Input.GetKeyDown(KeyCode.Escape)) Back();
                else if (_typing) ReadTyping();
                if (_place != Place.Closed && Time.unscaledTime >= _refreshAt) Refresh();
            }
            catch (Exception e) { Fail(e); }
        }

        private void Forget()
        {
            _place = Place.Closed;
            _typing = false;
            _window.PageWantsLanSearch = false;
        }

        private void RefreshWorldMenu()
        {
            if (_pauseEntry != null && _settingsUI.activeInHierarchy)
                Fill(_pauseEntry, _coop.CanUseWorldPause
                    ? new Entry(_coop.WorldPauseLabel, _togglePause)
                    : default(Entry));

            if (!GameMenuPause.InSession || !WorldMenuOpen || !GameMenuPause.HasOpenWorldMenu(_menu))
            {
                _poseSyncedWhilePaused = false;
                return;
            }
            Vector3 before = _menu.transform.position;
            Quaternion facing = _menu.transform.rotation;
            MoveToPlayer?.Invoke(_menu, null);
            bool needsSync = !Physics.autoSyncTransforms;
            if (needsSync && (!_poseSyncedWhilePaused || _menu.transform.position != before ||
                              _menu.transform.rotation != facing))
                Physics.SyncTransforms();
            _poseSyncedWhilePaused = needsSync;
        }

        // One failure takes the page down to be built anew; a page that keeps failing is given up.
        private void Fail(Exception error)
        {
            Plugin.Logger.LogError("[GameMenu] " + error);
            _failures++;
            try { Destroy(); } catch { }
            if (_failures < MaxFailures) return;
            _broken = true;
            Plugin.Logger.LogWarning("[GameMenu] The co-op page of the game menu is switched off after " + _failures +
                                     " failures; the co-op window (" + Plugin.Cfg.MenuKey.Value + ") still hosts and joins");
        }

        private void Destroy()
        {
            // The game's page that the co-op page stood in for comes back, or the player is left with no menu.
            Place was = _place;
            try
            {
                if (_menu != null && was == Place.Title) Invoke("EnableStartMenu");
                if (_menu != null && was == Place.World && _settingsUI != null) _settingsUI.SetActive(true);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[GameMenu] The game's menu page was not given back: " + e.Message); }
            Forget();
            foreach (GameObject made in new[] { _titleEntry, _worldEntry, _pauseEntry?.Root, _page, _kit })
                if (made != null) UnityEngine.Object.Destroy(made);
            _titleEntry = _worldEntry = _page = _kit = null;
            _pauseEntry = null;
            _menu = null;
        }

        // -----------------------------------------------------------------
        // Building from the game's menu
        // -----------------------------------------------------------------

        // The menu is looked for, not waited for: twice a second until the scene's one is found. It
        // goes with its scene (a join from a loaded world starts the scenes over), and so does the page.
        private void Bind()
        {
            if (_menu != null || Time.unscaledTime < _bindAt) return;
            _bindAt = Time.unscaledTime + 0.5f;
            Forget();
            StartMenu menu = UnityEngine.Object.FindObjectOfType<StartMenu>();
            if (menu == null) return;
            if (!Build(menu))
            {
                Destroy();
                _broken = true;
                return;
            }
            _menu = menu;
            _boundAt = Time.unscaledTime;
            Plugin.Logger.LogInfo("[GameMenu] Co-op buttons added to the title screen and the Escape menu");
        }

        private bool Build(StartMenu menu)
        {
            _startUI = Field(menu, "startUI");
            _settingsUI = Field(menu, "settingsUI");
            _recoveryUI = Field(menu, "recoveryUI");
            _confirmQuitUI = Field(menu, "confirmQuitUI");
            GameObject recovery = _recoveryUI;
            Transform settingsButton = Child(_startUI, "button settings");
            Transform quitButton = Child(_settingsUI, "button quit");
            if (settingsButton == null || quitButton == null || recovery == null ||
                Child(recovery, "button recover") == null || Child(recovery, "button back") == null ||
                Child(recovery, "text") == null || Child(recovery, "bg (1)") == null)
            {
                if (!_layoutWarned) Plugin.Logger.LogWarning("[GameMenu] The game's menu is not built as expected; host and join from the co-op window (" + Plugin.Cfg.MenuKey.Value + ")");
                _layoutWarned = true;
                return false;
            }

            // Copies are made under an inactive parent, so none of the game's scripts in them wakes up.
            _kit = new GameObject("Coop menu kit");
            _kit.SetActive(false);
            _kit.transform.SetParent(menu.transform, false);

            _titleEntry = Copy(settingsButton.gameObject, _startUI.transform, OpenTitle).Root;
            _titleEntry.name = "button coop";
            SetLabel(_titleEntry, "Co-op");
            PlaceTitleEntry(_titleEntry.transform, settingsButton);

            _worldEntry = Copy(quitButton.gameObject, _settingsUI.transform, OpenWorld).Root;
            _worldEntry.name = "button coop";
            SetLabel(_worldEntry, "Co-op");
            _worldEntry.transform.localPosition = new Vector3(WorldEntryX, WorldEntryY, quitButton.localPosition.z);
            _pauseEntry = Copy(quitButton.gameObject, _settingsUI.transform, null);
            _pauseEntry.Root.name = "button world pause";
            _pauseEntry.Root.transform.localPosition = new Vector3(PauseEntryX, PauseEntryY, quitButton.localPosition.z);
            _pauseEntry.Button.Pressed = () => _pauseEntry.Action?.Invoke();
            Fill(_pauseEntry, default(Entry));

            BuildPage(menu, recovery, quitButton.gameObject);
            return true;
        }

        private void BuildPage(StartMenu menu, GameObject recovery, GameObject rowSource)
        {
            _page = UnityEngine.Object.Instantiate(recovery, _kit.transform);
            _page.name = "coop UI";
            Transform page = _page.transform;
            Swap(page.Find("button recover").gameObject, () => Press(_primary));
            Swap(page.Find("button back").gameObject, Back);
            page.Find("bg (1)").GetComponentInChildren<TextMesh>(true).text = "Co-op";

            _body = page.Find("text").GetComponent<TextMesh>();
            Transform body = _body.transform;
            body.localPosition = new Vector3(body.localPosition.x, BodyY, body.localPosition.z);
            body.localScale = Vector3.one * 0.012f;

            _primary = RowOf(page.Find("button recover").gameObject);
            Arrange(_primary.Root.transform, PrimaryY, 0.26f, 0.013f);
            page.Find("button back").localPosition = new Vector3(0f, BackY, FrontZ);

            for (int i = 0; i < Rows; i++)
            {
                int index = i;
                Row row = Copy(rowSource, page, () => Press(_rows[index]));
                row.Root.name = "row " + i;
                row.Root.transform.localScale = Vector3.one;
                Arrange(row.Root.transform, FirstRowY - RowStep * i, 0.24f, 0.011f);
                SetWidth(row.Root.transform, 1.7f);
                _rows[i] = row;
            }

            page.SetParent(menu.transform, false);
            _page.SetActive(false);
        }

        private static GameObject Field(StartMenu menu, string name)
            => AccessTools.Field(typeof(StartMenu), name)?.GetValue(menu) as GameObject;

        private static Transform Child(GameObject parent, string name) => parent != null ? parent.transform.Find(name) : null;

        private Row Copy(GameObject source, Transform parent, Action pressed)
        {
            GameObject copy = UnityEngine.Object.Instantiate(source, _kit.transform);
            copy.SetActive(true);
            Swap(copy, pressed);
            copy.transform.SetParent(parent, false);
            return RowOf(copy);
        }

        // The game's button script goes, with the outline it added; the page's takes its place.
        private static void Swap(GameObject button, Action pressed)
        {
            foreach (StartMenuButton old in button.GetComponentsInChildren<StartMenuButton>(true))
            {
                GameObject host = old.gameObject;
                UnityEngine.Object.DestroyImmediate(old);
                foreach (Component part in host.GetComponents<Component>())
                    if (part != null && part.GetType().Name == "Outline") UnityEngine.Object.DestroyImmediate(part);
                host.AddComponent<CoopMenuButton>().Pressed = pressed;
            }
        }

        private static Row RowOf(GameObject root)
        {
            var row = new Row { Root = root, Button = root.GetComponentInChildren<CoopMenuButton>(true), Text = root.GetComponentInChildren<TextMesh>(true) };
            if (row.Text != null) row.Color = row.Text.color;
            return row;
        }

        private static void SetLabel(GameObject button, string text)
        {
            TextMesh label = button.GetComponentInChildren<TextMesh>(true);
            if (label != null) label.text = text;
        }

        private static void Arrange(Transform button, float y, float height, float textScale)
        {
            button.localPosition = new Vector3(0f, y, FrontZ);
            Transform face = button.GetComponentInChildren<CoopMenuButton>(true)?.transform;
            if (face != null) face.localScale = new Vector3(face.localScale.x, height, face.localScale.z);
            TextMesh text = button.GetComponentInChildren<TextMesh>(true);
            if (text != null) text.transform.localScale = Vector3.one * textScale;
        }

        private static void SetWidth(Transform button, float width)
        {
            Transform face = button.GetComponentInChildren<CoopMenuButton>(true)?.transform;
            if (face != null) face.localScale = new Vector3(width, face.localScale.y, face.localScale.z);
        }

        // The title's buttons stand one step apart. Co-op takes the place under Settings, and moves
        // what stands there one step down.
        private static void PlaceTitleEntry(Transform entry, Transform settings)
        {
            float y = settings.localPosition.y;
            float step = 0f;
            var buttons = new List<Transform>();
            foreach (Transform sibling in settings.parent)
            {
                if (sibling == entry || sibling == settings || sibling.GetComponentInChildren<StartMenuButton>(true) == null) continue;
                buttons.Add(sibling);
                float gap = sibling.localPosition.y - y;
                if (gap > 0.01f && (step == 0f || gap < step)) step = gap;
            }
            if (step == 0f) step = 0.3f;
            float target = y - step;
            bool taken = false;
            foreach (Transform button in buttons)
                if (Mathf.Abs(button.localPosition.y - target) < step * 0.5f) taken = true;
            if (taken)
                foreach (Transform button in buttons)
                    if (button.localPosition.y < y - 0.01f) button.localPosition -= new Vector3(0f, step, 0f);
            entry.localPosition = new Vector3(settings.localPosition.x, target, settings.localPosition.z);
        }

        // -----------------------------------------------------------------
        // Opening and closing
        // -----------------------------------------------------------------

        private void OpenTitle()
        {
            // The game ignores its own buttons while its menus slide; so does this one.
            if (AnimsPlaying != null && (int)AnimsPlaying.GetValue(_menu) > 0) return;
            Invoke("DisableStartMenu");
            ShowPage(Place.Title);
        }

        private void OpenWorld()
        {
            _settingsUI.SetActive(false);
            ShowPage(Place.World);
        }

        private void ShowPage(Place place)
        {
            _place = place;
            _typing = false;
            _window.ClearStatus();
            _page.SetActive(true);
            Refresh();
        }

        private void Back()
        {
            if (_typing) { _typing = false; Refresh(); return; }
            Place was = _place;
            ClosePage();
            UISoundPlayer.instance?.PlayUISound(UISounds.buttonBack, 1f, 1.1f);
            if (was == Place.Title) Invoke("EnableStartMenu");
            else if (was == Place.World) _settingsUI.SetActive(true);
        }

        private void ClosePage()
        {
            Forget();
            if (_page != null) _page.SetActive(false);
        }

        private void Invoke(string method)
        {
            MethodInfo call = AccessTools.Method(typeof(StartMenu), method);
            if (call == null) throw new MissingMethodException("StartMenu", method);
            call.Invoke(_menu, null);
        }

        // -----------------------------------------------------------------
        // Pressing and typing
        // -----------------------------------------------------------------

        private void Press(Row row)
        {
            Action action = row?.Action;
            if (action == null) return;
            if (action != StartTyping && action != JoinTyped) _typing = false;
            action();
            _refreshAt = 0f;
        }

        private void StartTyping()
        {
            _typing = true;
            _refreshAt = 0f;
        }

        private void ConfirmWorldJoin()
        {
            _reopenTitle = true;
            _window.ConfirmJoin();
        }

        private void JoinTyped()
        {
            _typing = false;
            _window.JoinFromField();
        }

        private void ReadTyping()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { _typing = false; _typingEndedFrame = Time.frameCount; _refreshAt = 0f; return; }
            string field = _window.JoinField ?? "";
            string before = field;
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.V)) field += (GUIUtility.systemCopyBuffer ?? "").Trim();
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (field.Length > 0) field = field.Substring(0, field.Length - 1); }
                else if (c == '\n' || c == '\r')
                {
                    _window.JoinField = field;
                    JoinTyped();
                    _refreshAt = 0f;
                    return;
                }
                else if (!char.IsControl(c)) field += c;
            }
            if (field.Length > FieldLength) field = field.Substring(0, FieldLength);
            if (field == before) return;
            _window.JoinField = field;
            _refreshAt = 0f;
        }

        // -----------------------------------------------------------------
        // What is shown
        // -----------------------------------------------------------------

        private void Refresh()
        {
            _refreshAt = Time.unscaledTime + 0.2f;
            _window.RefreshView();
            // The list of games is wanted wherever it is offered: on the title page, and in a world
            // without a session (a guest whose session ended stays in the host's copy and may join again).
            _window.PageWantsLanSearch = true;

            var rows = new List<Entry>();
            Entry primary = default(Entry);
            string body;
            CoopNet net = _coop.Net;
            CoopMenuUI.Mode mode = _window.ViewMode;

            if (_window.ViewRestartPending)
            {
                body = "The host's mods are installed but load only when the game starts. Quit, start the game again and join.";
                primary = new Entry("Quit game", Application.Quit);
            }
            else if (_window.ViewConfirmWorldJoin)
            {
                body = (_window.ViewLeavesHostCopy ? "Leave this host's world and join " : "Save this world and join ") +
                       _coop.JoinDestination + "?" +
                       (_coop.PendingJoinReplacesOwnSave
                           ? "\nThis world is in the co-op save slot " + Plugin.Cfg.CoopSaveSlot.Value + ": the host's world will REPLACE it."
                           : _window.ViewLeavesHostCopy ? "\nYour character's progress is kept in your guest profile." : "\nThe host's world goes into its own save slot.");
                rows.Add(new Entry("Cancel", _coop.CancelWorldJoin));
                primary = new Entry(_window.ViewLeavesHostCopy ? "Leave and join" : "Save and join", ConfirmWorldJoin);
            }
            else if (_window.ViewModsPrompt && _window.ViewMods.Deciding)
            {
                body = _window.ViewMods.Text ?? "";
#if !THUNDERSTORE
                rows.Add(new Entry(_window.ViewMods.DownloadLabel, _coop.Mods.Download, _window.ViewMods.CanDownload));
#endif
                rows.Add(new Entry("Join anyway", _coop.Mods.JoinAnyway));
                primary = new Entry("Cancel", _coop.Mods.Cancel);
            }
            else if (mode == CoopMenuUI.Mode.Offline && _window.ViewJoinFailure.Length > 0)
            {
                body = "Could not join " + _coop.JoinDestination + ".\n" + _window.ViewJoinFailure;
                rows.Add(new Entry("Back to the list", _coop.DismissJoinFailure));
                primary = new Entry("Retry", _coop.RetryJoin);
            }
            else if (mode == CoopMenuUI.Mode.Connecting)
            {
                body = _coop.JoinProgress.Length > 0 ? _coop.JoinProgress + "..."
                     : "Connecting to " + (net.OverSteam ? net.HostLabel : _coop.JoinDestination) + "...";
                primary = new Entry("Cancel", () => _window.EndSession("Connection cancelled"),
                                    !_coop.LeavingWorld && !_coop.SavingBeforeJoin && !GameState.currentlyLoading);
            }
            else if (mode == CoopMenuUI.Mode.Hosting) body = Hosting(net, rows, out primary);
            else if (mode == CoopMenuUI.Mode.Joined)
            {
                body = "You sail in " + HostName(net) + "'s world." +
                       (_window.ViewSharedWallet ? "\nThe crew spends the host's money." : "\nWallets are personal; rewards are divided.");
                rows.Add(MoreOptions());
                primary = new Entry("Disconnect", () => _window.EndSession("Session stopped"));
            }
            else body = JoinList(rows, out primary, _place == Place.World);

            string message = _window.ViewMessage;
            if (message.Length > 0 && body.IndexOf(message, StringComparison.Ordinal) < 0) body += "\n" + message;
            Show(body, rows, primary);
        }

        // In a world the page also hosts it: the primary button is Host this world.
        private string JoinList(List<Entry> rows, out Entry primary, bool world)
        {
            bool steam = _window.SteamMode;
            bool canJoin = PatchHealth.Blocker == null && !_coop.LeavingWorld && !_window.ViewRestartPending;
            primary = _typing ? new Entry("Join", JoinTyped, canJoin) : world ? HostThisWorld() : MoreOptions();
            if (PatchHealth.Blocker != null)
            {
                rows.Add(Network());
                return "Co-op is unavailable: " + PatchHealth.Blocker;
            }

            var fixedRows = new List<Entry>();
            if (_window.ViewSavedTarget.Length > 0) fixedRows.Add(new Entry("Join again: " + _window.ViewSavedTarget, _window.JoinSaved, canJoin));
            string field = _window.JoinField ?? "";
            string caret = (int)(Time.unscaledTime * 2f) % 2 == 0 ? "_" : " ";
            if (!steam || _window.SteamReady)
                fixedRows.Add(_typing
                    ? new Entry((steam ? "Steam ID: " : "Address: ") + Tail(field, RowWidth - 14) + caret, JoinTyped, canJoin)
                    : new Entry(steam ? "Join by Steam ID" + (field.Length > 0 ? ": " + field : "")
                                      : "Join by address: " + (field.Length > 0 ? field : "this PC"), StartTyping, canJoin));
            fixedRows.Add(Network());

            string body;
            int room = Rows - fixedRows.Count;
            if (steam && !_window.SteamReady)
                body = string.IsNullOrEmpty(_window.SteamError) ? "Starting Steam..." : _window.SteamError;
            else if (steam)
            {
                int shown = 0;
                foreach (SteamFriendInfo friend in _window.SteamFriends)
                {
                    if (!friend.Hosting || shown >= room) continue;
                    SteamFriendInfo chosen = friend;
                    rows.Add(new Entry("Join " + friend.Name + (friend.SameVersion ? "" : " (other version)"),
                                       () => _window.JoinFriend(chosen), canJoin && friend.SameVersion));
                    shown++;
                }
                body = shown == 0 ? "No Steam friend hosts a game right now.\nA host who is not your friend is joined by Steam ID."
                                  : "Steam friends who host a game:";
            }
            else
            {
                LanHost[] hosts = _window.ViewLanHosts;
                for (int i = 0; i < hosts.Length && i < room; i++)
                {
                    LanHost host = hosts[i];
                    bool sameProtocol = host.ProtocolVersion == Protocol.Version;
                    bool full = host.Players >= host.MaxPlayers;
                    string name = string.IsNullOrEmpty(host.HostName) ? "Host" : host.HostName;
                    string state = !sameProtocol ? " (other version)" : !host.Accepting ? " (locked)" : full ? " (full)" : "";
                    rows.Add(new Entry("Join " + name + "  " + host.Players + "/" + host.MaxPlayers + state,
                                       () => _window.JoinLan(host), canJoin && sameProtocol && host.Accepting && !full));
                }
                body = hosts.Length == 0 ? "Looking for games on this network..." : "Games on this network:";
            }
            if (_typing) body = (steam ? "Type the host's Steam ID" : "Type the host's address, as the host's Co-op page shows it") +
                                ", then press Enter.";
            else if (world) body = "Host this world, or join a game. " + body;
            rows.AddRange(fixedRows);
            return body;
        }

        private Entry HostThisWorld()
        {
            bool available = PatchHealth.Blocker == null && !_coop.LeavingWorld;
            return new Entry("Host this world", _window.HostWorld, available && (!_window.SteamMode || _window.SteamReady));
        }

        private string Hosting(CoopNet net, List<Entry> rows, out Entry primary)
        {
            rows.Add(new Entry(net.OverSteam ? "Copy my Steam ID" : "Copy address", _window.CopyHostAddress,
                               net.OverSteam || _window.ViewHostAddress.Length > 0));
            rows.Add(new Entry(net.AcceptingClients ? "Lock session" : "Open session", _window.ToggleAccepting));
            rows.Add(new Entry(_window.ViewSharedWallet ? "Money: shared" : "Money: personal",
                               () => _window.SharedWallet(!_window.ViewSharedWallet)));
            if (net.OverSteam) rows.Add(WhoCanJoin());
            rows.Add(MoreOptions());
            primary = new Entry("End session", () => _window.EndSession("Session stopped"));
            string where = !net.OverSteam ? "Address: " + _window.ViewHostAddress
                         : net.SteamFriendsOnly ? "Over Steam, friends only." : "Over Steam, and on this network.";
            return "You are hosting: " + net.PeerCount + " of " + Plugin.Cfg.MaxClients.Value + " guests" +
                   (net.AcceptingClients ? "." : ", locked.") + "\n" + where;
        }

        // The host by the name it plays under; its address only while the crew list has not come.
        private string HostName(CoopNet net)
        {
            foreach (SessionMemberInfo member in net.RosterSnapshot)
                if (member.IsHost && !string.IsNullOrEmpty(member.Name)) return member.Name;
            return net.OverSteam ? net.HostLabel : _coop.JoinDestination;
        }

        private Entry Network()
            => new Entry("Network: " + (_window.SteamMode ? "Steam" : "LAN"), () => _window.UseSteam(!_window.SteamMode),
                         _coop.Net.Role == Role.None);

        private Entry WhoCanJoin()
            => new Entry("Who can join: " + (_window.SteamFriendsOnly ? "friends" : "anyone"),
                         () => _window.FriendsOnly(!_window.SteamFriendsOnly));

        private Entry MoreOptions() => new Entry("More options (" + Plugin.Cfg.MenuKey.Value + ")", _window.Show);

        private void Show(string body, List<Entry> rows, Entry primary)
        {
            _body.text = Wrap(body, BodyWidth, BodyLines);
            // The list is anchored at the bottom: what is always offered stays in one place.
            int first = Rows - Mathf.Min(rows.Count, Rows);
            for (int i = 0; i < Rows; i++)
            {
                int index = i - first;
                Fill(_rows[i], index >= 0 && index < rows.Count ? rows[index] : default(Entry));
            }
            Fill(_primary, primary);
        }

        private static void Fill(Row row, Entry entry)
        {
            bool shown = entry.Label != null;
            if (row.Root.activeSelf != shown) row.Root.SetActive(shown);
            row.Action = entry.Enabled ? entry.Action : null;
            if (!shown) return;
            row.Text.text = Clip(entry.Label, RowWidth);
            Color color = row.Color;
            if (!entry.Enabled) color.a *= 0.4f;
            row.Text.color = color;
            if (row.Button != null) row.Button.unclickable = !entry.Enabled;
        }

        private static string Clip(string text, int width)
            => text.Length <= width ? text : text.Substring(0, width - 3) + "...";

        private static string Tail(string text, int width)
            => text.Length <= width ? text : "..." + text.Substring(text.Length - width + 3);

        // TextMesh does not wrap; lines are broken at spaces and cut at the page's height.
        private static string Wrap(string text, int width, int maxLines)
        {
            var lines = new List<string>();
            foreach (string paragraph in (text ?? "").Split('\n'))
            {
                var line = new StringBuilder();
                foreach (string word in paragraph.Split(' '))
                {
                    if (line.Length > 0 && line.Length + 1 + word.Length > width)
                    {
                        lines.Add(line.ToString());
                        line.Length = 0;
                    }
                    if (line.Length > 0) line.Append(' ');
                    line.Append(word.Length > width ? word.Substring(0, width) : word);
                }
                lines.Add(line.ToString());
            }
            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                lines[maxLines - 1] = Clip(lines[maxLines - 1] + "...", width);
            }
            return string.Join("\n", lines.ToArray());
        }
    }
}
