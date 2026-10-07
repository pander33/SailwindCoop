using System;
using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Money between players. Wallets stay personal (<c>PlayerGold.currency</c> on each machine); a
    /// player can give part of theirs to another player. The giver takes the amount out of its own
    /// wallet and sends <see cref="MoneyTransferMsg"/>; the host credits itself or passes the message
    /// on; the receiver adds the amount. Money that finds no receiver goes back to the giver.
    ///
    /// Handing money over in person adds <see cref="MoneyOfferMsg"/> in front of that: the giver
    /// announces what it holds out, the other player takes it, and only then the giver sends the
    /// transfer. An offer moves no money, so a lost or stale one costs nothing.
    /// </summary>
    public sealed class WalletSync
    {
        public static WalletSync Instance { get; private set; }

        private readonly CoopNet _net;

        public string WalletText { get; private set; } = "—";

        public WalletSync(CoopNet net)
        {
            _net = net;
            Instance = this;
        }

        /// <summary>The local wallet exists: a world is loaded on this machine.</summary>
        public static bool Ready => PlayerGold.currency != null && GameState.playing;

        public static int CurrencyCount => PlayerGold.currency != null ? PlayerGold.currency.Length : 0;

        public static int Balance(int currency)
        {
            return currency >= 0 && currency < CurrencyCount ? PlayerGold.currency[currency] : 0;
        }

        /// <summary>The local player gives money to another player. Null on success, otherwise the reason.</summary>
        public string Give(uint toNetId, int currency, int amount)
        {
            try
            {
                if (_net.Role == Role.None || _net.State != LinkState.Connected) return "No active session";
                if (Shared) return "The crew shares one wallet";
                if (!Ready || currency < 0 || currency >= CurrencyCount) return "Your wallet is not loaded yet";
                if (toNetId == _net.MyNetId) return "Pick another player";
                if (amount <= 0) return "Enter an amount above zero";
                string unit = PlayerGold.GetCurrencyName(currency);
                if (PlayerGold.currency[currency] < amount)
                    return "You have only " + PlayerGold.currency[currency] + " " + unit;

                var msg = new MoneyTransferMsg
                {
                    FromNetId = _net.MyNetId, ToNetId = toNetId, Currency = (byte)currency, Amount = amount,
                };
                PlayerGold.currency[currency] -= amount;
                if (_net.Role == Role.Host)
                {
                    if (!_net.SendToPlayer(toNetId, msg, DeliveryMethod.ReliableOrdered))
                    {
                        PlayerGold.currency[currency] += amount;
                        return _net.GetPlayerName(toNetId) + " is not in the world yet";
                    }
                }
                else
                {
                    _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
                    MissionSync.Instance?.SaveGuestProfileNow("money given");
                }
                Remember("gave " + amount + " " + unit + " to " + _net.GetPlayerName(toNetId) + " (" + toNetId + ")");
                return null;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[Wallet] Give: " + e);
                return "Could not give money: " + e.Message;
            }
        }

        public void OnMoneyTransfer(MoneyTransferMsg msg, NetPeer fromPeer)
        {
            try
            {
                if (_net.Role == Role.Host) HostRoute(msg, fromPeer);
                else if (_net.Role == Role.Client) Receive(msg);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Wallet] OnMoneyTransfer: " + e); }
        }

        private void HostRoute(MoneyTransferMsg msg, NetPeer fromPeer)
        {
            uint from = _net.PlayerNetIdForPeer(fromPeer);
            if (from == 0) return;
            msg.FromNetId = from;   // the giver is whoever sent it
            msg.Returned = false;
            if (msg.ToNetId == _net.MyNetId)
            {
                if (Credit(msg, false)) return;
            }
            else if (msg.ToNetId != from && _net.SendToPlayer(msg.ToNetId, msg, DeliveryMethod.ReliableOrdered))
            {
                Plugin.Logger.LogInfo("[Wallet] role=Host passed " + msg.Amount + " cur=" + msg.Currency +
                                      " from=" + from + " to=" + msg.ToNetId);
                return;
            }
            msg.Returned = true;
            fromPeer.Send(msg, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogWarning("[Wallet] role=Host returned " + msg.Amount + " cur=" + msg.Currency + " to " + from +
                                     ": player " + msg.ToNetId + " is not in the world");
        }

        private void Receive(MoneyTransferMsg msg)
        {
            if (msg.Returned)
            {
                if (!Credit(msg, true))
                    Plugin.Logger.LogWarning("[Wallet] role=Client returned money could not be credited: " + msg.Amount +
                                             " cur=" + msg.Currency);
                return;
            }
            if (Credit(msg, false)) return;
            // No wallet here yet: hand it back through the host as a gift to the giver.
            _net.Broadcast(new MoneyTransferMsg
            {
                FromNetId = _net.MyNetId, ToNetId = msg.FromNetId, Currency = msg.Currency, Amount = msg.Amount,
            }, DeliveryMethod.ReliableOrdered);
            Plugin.Logger.LogWarning("[Wallet] role=Client no wallet, sent back " + msg.Amount + " cur=" + msg.Currency +
                                     " to " + msg.FromNetId);
        }

        private bool Credit(MoneyTransferMsg msg, bool returned)
        {
            int currency = msg.Currency;
            if (!Ready || currency >= CurrencyCount) return false;
            PlayerGold.currency[currency] += msg.Amount;
            string unit = PlayerGold.GetCurrencyName(currency);
            string text = returned
                ? _net.GetPlayerName(msg.ToNetId) + " could not take your " + msg.Amount + " " + unit + ". The money is back in your wallet."
                : _net.GetPlayerName(msg.FromNetId) + " gave you " + msg.Amount + " " + unit + ".";
            try { NotificationUi.instance?.ShowNotification(text); } catch { }
            try { MoneyNotification.instance?.PlayNotif(msg.Amount, currency); } catch { }
            if (!returned) { try { UISoundPlayer.instance?.PlayGoldSound(); } catch { } }
            Remember((returned ? "got back " : "received ") + msg.Amount + " " + unit +
                     (returned ? " (to " + msg.ToNetId + ")" : " from " + _net.GetPlayerName(msg.FromNetId) + " (" + msg.FromNetId + ")"));
            if (_net.Role == Role.Client) MissionSync.Instance?.SaveGuestProfileNow("money received");
            return true;
        }

        // -----------------------------------------------------------------
        // Hand to hand
        // -----------------------------------------------------------------

        private struct Incoming
        {
            public int Currency;
            public int Amount;
            public float At;
        }

        private const float OfferRepeatSecs = 1f;     // an offer is repeated while the hand is out
        private const float OfferChangeSecs = 0.15f;  // a changing amount is sent no more often than this
        private const float OfferLifeSecs = 3f;       // an offer that stopped arriving is forgotten

        private readonly Dictionary<uint, Incoming> _incoming = new Dictionary<uint, Incoming>();
        private bool _offerActive;
        private bool _offerDirty;
        private bool _offerTaken;
        private uint _offerTo;
        private int _offerCurrency;
        private int _offerAmount;
        private float _offerSentAt;

        /// <summary>The local player holds out money. Call every frame while the hand is out.</summary>
        public void HoldOffer(uint toNetId, int currency, int amount)
        {
            float now = Time.unscaledTime;
            if (_offerActive && _offerTo != toNetId) DropOffer();
            if (!_offerActive || _offerCurrency != currency || _offerAmount != amount) _offerDirty = true;
            bool first = !_offerActive;
            _offerActive = true;
            _offerTo = toNetId;
            _offerCurrency = currency;
            _offerAmount = amount;
            float since = now - _offerSentAt;
            if (!first && since < OfferRepeatSecs && !(_offerDirty && since >= OfferChangeSecs)) return;
            _offerSentAt = now;
            _offerDirty = false;
            SendOffer(MoneyOfferKind.Offer, toNetId, currency, amount);
        }

        /// <summary>The local player lowered the hand.</summary>
        public void DropOffer()
        {
            if (!_offerActive) return;
            _offerActive = false;
            SendOffer(MoneyOfferKind.Withdraw, _offerTo, _offerCurrency, 0);
        }

        /// <summary>True once after the other player took what the local player held out.</summary>
        public bool ConsumeOfferTaken()
        {
            bool taken = _offerTaken;
            _offerTaken = false;
            return taken;
        }

        /// <summary>What that player is holding out to the local player right now.</summary>
        public bool TryGetOffer(uint fromNetId, out int currency, out int amount)
        {
            currency = amount = 0;
            Incoming offer;
            if (!_incoming.TryGetValue(fromNetId, out offer)) return false;
            if (Time.unscaledTime - offer.At > OfferLifeSecs)
            {
                _incoming.Remove(fromNetId);
                return false;
            }
            currency = offer.Currency;
            amount = offer.Amount;
            return true;
        }

        /// <summary>The local player takes what that player holds out.</summary>
        public void Take(uint fromNetId)
        {
            _incoming.Remove(fromNetId);
            SendOffer(MoneyOfferKind.Take, fromNetId, 0, 0);
            Plugin.Logger.LogInfo("[Wallet] role=" + _net.Role + " takes the offer of " + fromNetId);
        }

        private void SendOffer(MoneyOfferKind kind, uint toNetId, int currency, int amount)
        {
            if (_net.Role == Role.None || _net.State != LinkState.Connected) return;
            var msg = new MoneyOfferMsg
            {
                FromNetId = _net.MyNetId, ToNetId = toNetId, Kind = kind, Currency = (byte)currency, Amount = amount,
            };
            if (_net.Role == Role.Host) _net.SendToPlayer(toNetId, msg, DeliveryMethod.ReliableOrdered);
            else _net.Broadcast(msg, DeliveryMethod.ReliableOrdered);
        }

        public void OnMoneyOffer(MoneyOfferMsg msg, NetPeer fromPeer)
        {
            try
            {
                if (_net.Role == Role.Host)
                {
                    uint from = _net.PlayerNetIdForPeer(fromPeer);
                    if (from == 0) return;
                    msg.FromNetId = from;   // the sender is whoever sent it
                    if (msg.ToNetId != _net.MyNetId)
                    {
                        _net.SendToPlayer(msg.ToNetId, msg, DeliveryMethod.ReliableOrdered);
                        return;
                    }
                }
                else if (_net.Role != Role.Client) return;

                switch (msg.Kind)
                {
                    case MoneyOfferKind.Offer:
                        _incoming[msg.FromNetId] = new Incoming { Currency = msg.Currency, Amount = msg.Amount, At = Time.unscaledTime };
                        break;
                    case MoneyOfferKind.Withdraw:
                        _incoming.Remove(msg.FromNetId);
                        break;
                    case MoneyOfferKind.Take:
                        if (!_offerActive || _offerTo != msg.FromNetId) break;   // the hand is already down
                        _offerActive = false;
                        _offerTaken = true;
                        // What is handed over is what this player holds out now, not what the message says.
                        string refused = Give(msg.FromNetId, _offerCurrency, _offerAmount);
                        string text = refused ?? "You gave " + _offerAmount + " " + PlayerGold.GetCurrencyName(_offerCurrency) +
                                                 " to " + _net.GetPlayerName(msg.FromNetId) + ".";
                        try { NotificationUi.instance?.ShowNotification(text); } catch { }
                        break;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Wallet] OnMoneyOffer: " + e); }
        }

        /// <summary>A player left: their offer and the offer made to them are gone.</summary>
        public void ClearRemoteActor(uint netId)
        {
            _told.Remove(netId);
            _ackByClient.Remove(netId);
            _incoming.Remove(netId);
            if (_offerActive && _offerTo == netId) _offerActive = false;
        }

        // -----------------------------------------------------------------
        // Shared wallet (host option Economy/SharedWallet)
        //
        // The host's PlayerGold.currency is the crew's wallet. A client sets its own money aside,
        // shows the host's balance in its place and lets the game spend and earn against it as
        // usual. Whatever the game changed is sent to the host as a difference; the host adds it
        // and sends the new balance back. Nothing in the shops, the shipyard or the port has to
        // know about it.
        // -----------------------------------------------------------------

        // Host
        private bool _hostShared;
        private int[] _hostSent;                                             // the balance the clients know
        private readonly HashSet<uint> _told = new HashSet<uint>();           // clients that have the current balance
        private readonly Dictionary<uint, uint> _ackByClient = new Dictionary<uint, uint>();
        private const float HostScanSecs = 0.5f;
        private float _hostScanAt;

        // Client
        private int[] _personal;          // own money, set aside while the host's wallet is used
        private int[] _synced;            // the wallet as of the last look at it
        private uint _seq;
        private WalletStateMsg _waiting;  // arrived before the wallet existed
        private readonly List<KeyValuePair<uint, int[]>> _pending = new List<KeyValuePair<uint, int[]>>();

        private Runtime.CoopLog.Repeat _tickFailures;

        /// <summary>The crew uses the host's wallet right now.</summary>
        public bool Shared => _net.Role == Role.Host ? _hostShared : _personal != null;

        /// <summary>
        /// What a guest's profile must store as its money: the wallet set aside, not the shared
        /// balance that stands in <c>PlayerGold.currency</c> while the crew shares the host's.
        /// </summary>
        public static int[] WalletForProfile
        {
            get { return Instance != null && Instance._personal != null ? Instance._personal : PlayerGold.currency; }
        }

        public void Tick()
        {
            try
            {
                if (_net.State != LinkState.Connected) return;
                if (_net.Role == Role.Host) TickHost();
                else if (_net.Role == Role.Client) TickClient();
            }
            catch (Exception e) { Plugin.Logger.ReportError("[Wallet] Tick failed", e, ref _tickFailures); }
        }

        private void TickHost()
        {
            bool want = Plugin.Cfg.SharedWallet.Value;
            if (want != _hostShared)
            {
                _hostShared = want;
                _hostScanAt = 0f;
                _told.Clear();
                _hostSent = null;
                if (!want)
                    foreach (uint id in _net.ReadyClientIds())
                        _net.SendToPlayer(id, new WalletStateMsg { Shared = false }, DeliveryMethod.ReliableOrdered);
                Remember(want ? "shared wallet on: the crew uses the host's money" : "shared wallet off: personal wallets");
            }
            if (!_hostShared || !Ready) return;

            bool changed = !Same(_hostSent, PlayerGold.currency);
            // The balance is compared every frame; who still has to be told is looked up only when it
            // changed, and otherwise a couple of times a second (a client that has just loaded in, or
            // an acknowledgement owed for a change that left the balance the same).
            float now = Time.unscaledTime;
            if (!changed && now < _hostScanAt) return;
            _hostScanAt = now + HostScanSecs;
            uint[] ready = _net.ReadyClientIds();
            _told.RemoveWhere(id => Array.IndexOf(ready, id) < 0);
            if (changed) _hostSent = (int[])PlayerGold.currency.Clone();
            foreach (uint id in ready)
            {
                if (!changed && _told.Contains(id)) continue;
                uint ack;
                _ackByClient.TryGetValue(id, out ack);
                _net.SendToPlayer(id, new WalletStateMsg { Shared = true, AckSeq = ack, Currency = _hostSent },
                                  DeliveryMethod.ReliableOrdered);
                _told.Add(id);
            }
        }

        public void OnWalletDelta(WalletDeltaMsg msg, NetPeer fromPeer)
        {
            try
            {
                if (_net.Role != Role.Host) return;
                uint from = _net.PlayerNetIdForPeer(fromPeer);
                if (from == 0) return;
                _ackByClient[from] = msg.Seq;
                _told.Remove(from);   // the answer carries the acknowledgement even when the balance ends up the same
                if (!_hostShared || !Ready) return;   // switched off meanwhile: the client is being told
                int count = Math.Min(msg.Delta.Length, PlayerGold.currency.Length);
                var text = new System.Text.StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (msg.Delta[i] == 0) continue;
                    PlayerGold.currency[i] += msg.Delta[i];
                    text.Append(' ').Append(PlayerGold.GetCurrencySymbol(i)).Append(msg.Delta[i] > 0 ? "+" : "").Append(msg.Delta[i]);
                }
                Remember("shared wallet:" + text + " by " + _net.GetPlayerName(from) + " (" + from + ")");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Wallet] OnWalletDelta: " + e); }
        }

        public void OnWalletState(WalletStateMsg msg)
        {
            try
            {
                if (_net.Role != Role.Client) return;
                if (!msg.Shared)
                {
                    _waiting = null;
                    if (RestorePersonal())
                    {
                        try { NotificationUi.instance?.ShowNotification("Wallets are personal again."); } catch { }
                    }
                    return;
                }
                if (Ready) ApplyShared(msg);
                else _waiting = msg;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Wallet] OnWalletState: " + e); }
        }

        private void TickClient()
        {
            if (!Ready) return;
            if (_waiting != null)
            {
                WalletStateMsg waiting = _waiting;
                _waiting = null;
                ApplyShared(waiting);
            }
            if (_personal != null) FlushLocal();
        }

        private void ApplyShared(WalletStateMsg msg)
        {
            int[] wallet = PlayerGold.currency;
            if (_personal == null)
            {
                _personal = (int[])wallet.Clone();
                _synced = (int[])wallet.Clone();
                _pending.Clear();
                try { NotificationUi.instance?.ShowNotification("The crew shares the host's wallet. Your own money is kept for you."); } catch { }
                Remember("shared wallet on; own money set aside");
            }
            else FlushLocal();   // a change made since the last look must not be overwritten

            _pending.RemoveAll(p => p.Key <= msg.AckSeq);
            int count = Math.Min(wallet.Length, msg.Currency.Length);
            for (int i = 0; i < count; i++)
            {
                int value = msg.Currency[i];
                // Changes the host has not counted yet are still ours to show.
                foreach (var p in _pending) value += p.Value[i];
                wallet[i] = value;
                _synced[i] = value;
            }
        }

        /// <summary>Send the host what the game changed in the wallet since the last look.</summary>
        private void FlushLocal()
        {
            int[] wallet = PlayerGold.currency;
            if (Same(wallet, _synced)) return;
            var delta = new int[wallet.Length];
            for (int i = 0; i < wallet.Length; i++) delta[i] = wallet[i] - (i < _synced.Length ? _synced[i] : 0);
            _synced = (int[])wallet.Clone();
            _seq++;
            _pending.Add(new KeyValuePair<uint, int[]>(_seq, delta));
            _net.Broadcast(new WalletDeltaMsg { Seq = _seq, Delta = delta }, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client: put the own money back. False when it was not set aside.</summary>
        private bool RestorePersonal()
        {
            if (_personal == null) return false;
            int[] wallet = PlayerGold.currency;
            if (wallet != null) Array.Copy(_personal, wallet, Math.Min(_personal.Length, wallet.Length));
            _personal = null;
            _synced = null;
            _pending.Clear();
            Remember("shared wallet off; own money is back");
            return true;
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public void Clear()
        {
            RestorePersonal();
            _waiting = null;
            _seq = 0;
            _hostShared = false;
            _hostSent = null;
            _told.Clear();
            _ackByClient.Clear();
            WalletText = "—";
            _incoming.Clear();
            _offerActive = _offerDirty = _offerTaken = false;
        }

        private void Remember(string text)
        {
            WalletText = text;
            Plugin.Logger.LogInfo("[Wallet] role=" + _net.Role + " " + text);
        }
    }
}
