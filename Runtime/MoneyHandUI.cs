using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Money from hand to hand. The giver looks at a player standing close and holds the key: the
    /// avatar holds out a hand with a coin, the mouse wheel sets the amount. The other player looks
    /// at the giver and presses the same key to take it. Releasing the key lowers the hand.
    ///
    /// Input is read in <c>Update</c>; the prompt is drawn in <c>OnGUI</c> with absolute rectangles,
    /// so the set of controls does not depend on the IMGUI pass.
    /// </summary>
    internal sealed class MoneyHandUI
    {
        private const float Reach = 3f;        // how close the other player must be to start or take
        private const float KeepReach = 4.5f;  // the offer ends when the other player walks further away
        private const float AimAngle = 25f;


        private bool _offering;
        private bool _waitRelease;   // the key must come up before it means anything again
        private uint _to;
        private int _currency;
        private int _amount = 10;
        private string _title = "";
        private string _hint = "";

        // The same parchment plate, ink and game font as the gesture wheel.
        private Texture2D _paper;
        private GUIStyle _plateStyle, _titleStyle, _hintStyle;

        /// <param name="allowed">In a session, in a loaded world, outside every menu.</param>
        public void Update(KeyCode key, bool allowed, CoopNet net, WalletSync wallet, PlayerSync players)
        {
            _title = _hint = "";
            if (!allowed || !WalletSync.Ready)
            {
                Stop(wallet, players);
                _waitRelease = false;
                return;
            }
            if (wallet.ConsumeOfferTaken())
            {
                Stop(wallet, players);
                _waitRelease = true;
            }
            bool held = Input.GetKey(key);
            if (_waitRelease)
            {
                if (!held) _waitRelease = false;
                return;
            }

            if (!_offering)
            {
                uint aimed;
                if (!players.TryAimedRemote(Reach, AimAngle, out aimed)) return;
                int offeredCurrency, offeredAmount;
                if (wallet.TryGetOffer(aimed, out offeredCurrency, out offeredAmount))
                {
                    _title = net.GetPlayerName(aimed) + " holds out " + offeredAmount + " " + PlayerGold.GetCurrencyName(offeredCurrency);
                    _hint = "Press " + key + " to take it";
                    if (Input.GetKeyDown(key))
                    {
                        wallet.Take(aimed);
                        _waitRelease = true;
                    }
                    return;
                }
                if (!Input.GetKeyDown(key)) return;
                _offering = true;
                _to = aimed;
                _currency = StartCurrency();
            }

            if (!held)
            {
                Stop(wallet, players);
                return;
            }
            float distance = players.DistanceToRemote(_to);
            if (distance < 0f || distance > KeepReach)
            {
                Stop(wallet, players);
                _waitRelease = true;
                return;
            }

            if (Input.GetMouseButtonDown(2)) _currency = NextCurrency(_currency);
            int balance = WalletSync.Balance(_currency);
            if (balance <= 0)
            {
                wallet.DropOffer();
                players.StopEmote(EmoteId.Offer);
                _title = "You have no " + PlayerGold.GetCurrencyName(_currency);
                _hint = "Middle button: currency    Release " + key + ": cancel";
                return;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0f) _amount += Step(_amount);
            else if (wheel < 0f) _amount -= Step(_amount - 1);
            _amount = Mathf.Clamp(_amount, 1, balance);

            wallet.HoldOffer(_to, _currency, _amount);
            players.SustainEmote(EmoteId.Offer);
            _title = "Holding out " + _amount + " " + PlayerGold.GetCurrencyName(_currency) + " to " + net.GetPlayerName(_to);
            _hint = "Wheel: amount    Middle button: currency    Release " + key + ": cancel";
        }

        /// <summary>Lower the hand (session ended, world unloading).</summary>
        public void Cancel(WalletSync wallet, PlayerSync players)
        {
            Stop(wallet, players);
            _waitRelease = false;
            _title = _hint = "";
        }

        private void Stop(WalletSync wallet, PlayerSync players)
        {
            if (!_offering) return;
            _offering = false;
            wallet?.DropOffer();
            players?.StopEmote(EmoteId.Offer);
        }

        /// <summary>The currency of the region the player trades in, or the first one that is not empty.</summary>
        private static int StartCurrency()
        {
            int count = WalletSync.CurrencyCount;
            int current = 0;
            try { current = (int)GameState.currentCurrency; } catch { }
            if (current < 0 || current >= count) current = 0;
            return WalletSync.Balance(current) > 0 ? current : NextCurrency(current);
        }

        /// <summary>The next currency the player has any of; the same one when there is no other.</summary>
        private static int NextCurrency(int currency)
        {
            int count = WalletSync.CurrencyCount;
            for (int i = 1; i <= count; i++)
            {
                int next = (currency + i) % count;
                if (WalletSync.Balance(next) > 0) return next;
            }
            return currency;
        }

        /// <summary>One notch of the wheel: small steps for small sums, larger ones further up.</summary>
        private static int Step(int amount)
        {
            if (amount < 20) return 1;
            if (amount < 100) return 5;
            if (amount < 500) return 25;
            if (amount < 2000) return 100;
            return 500;
        }

        public void Draw(EmoteWheelUI fontSource)
        {
            if (string.IsNullOrEmpty(_title)) return;
            EnsureStyles(fontSource);
            bool hint = !string.IsNullOrEmpty(_hint);
            float titleWidth = _titleStyle.CalcSize(new GUIContent(_title)).x;
            float hintWidth = hint ? _hintStyle.CalcSize(new GUIContent(_hint)).x : 0f;
            float w = Mathf.Min(Mathf.Max(titleWidth, hintWidth) + 56f, Screen.width - 40f);
            float h = hint ? 72f : 46f;
            var rect = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, h);
            GUI.Label(rect, GUIContent.none, _plateStyle);
            GUI.Label(new Rect(rect.x + 14f, rect.y + 8f, rect.width - 28f, 30f), _title, _titleStyle);
            if (hint) GUI.Label(new Rect(rect.x + 14f, rect.y + 40f, rect.width - 28f, 22f), _hint, _hintStyle);
        }

        private void EnsureStyles(EmoteWheelUI fontSource)
        {
            if (_titleStyle != null && _paper != null) return;
            _paper = EmoteWheelUI.Plate(EmoteWheelUI.PaperTop, EmoteWheelUI.PaperBottom, EmoteWheelUI.PaperEdge, 2f);
            _plateStyle = new GUIStyle(GUI.skin.label) { border = new RectOffset(14, 14, 14, 14) };
            _plateStyle.normal.background = _paper;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Normal,
                wordWrap = false, clipping = TextClipping.Overflow,
            };
            // Asked for only now, with a prompt on screen: the search for the game's font is done once
            // and finds nothing before a world is loaded.
            Font font = fontSource != null ? fontSource.GameFont() : null;
            if (font != null) _titleStyle.font = font;
            _titleStyle.normal.textColor = EmoteWheelUI.InkHot;
            _hintStyle = new GUIStyle(_titleStyle) { fontSize = 14 };
            Color ink = EmoteWheelUI.Ink;
            _hintStyle.normal.textColor = new Color(ink.r, ink.g, ink.b, 0.8f);
        }
    }
}