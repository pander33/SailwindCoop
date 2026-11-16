using System;
using System.Collections.Generic;
using LiteNetLib;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Sync
{
    public sealed partial class ItemSync
    {
        private Guid _tradeWorld;
        private float _nextTradeRetry;
        private bool _tradeProfileDirty;

        private bool CanSendTradeSales => _net.Role == Role.Client && _net.State == LinkState.Connected &&
            _tradeWorld != Guid.Empty && EconomyOverhaulCompat.TradeSalesOn &&
            (_net.HostModFeatures & ModFeatures.EconomyTradeSales) != 0;

        private bool PeerTrades(NetPeer peer) => EconomyOverhaulCompat.TradeSalesOn &&
            (_net.FeaturesOf(peer) & ModFeatures.EconomyTradeSales) != 0;

        private static Guid HostTradeWorld()
        {
            if (GameState.modData == null) GameState.modData = new Dictionary<string, string>();
            return DiceJournal.Associate(GameState.modData);
        }

        internal void PrepareTradeSaleWorld(NetPeer peer)
        {
            if (PeerTrades(peer)) HostTradeWorld();
        }

        internal void SendTradeSaleBaseline(NetPeer peer)
        {
            if (PeerTrades(peer))
                peer.Send(new TradeSaleReceiptMsg { World = HostTradeWorld() }, DeliveryMethod.ReliableOrdered);
        }

        private void ClearTradeSaleSession()
        {
            _tradeWorld = Guid.Empty;
            _nextTradeRetry = 0f;
            _tradeProfileDirty = false;
        }

        private void QueueTradeSale(int instance, int prefab, Guid author, bool flush)
        {
            CoopProfile.TradeSales.Pending.Add(Guid.NewGuid(), new TradeSaleJournal.Sale
            {
                World = _tradeWorld, Author = author, Instance = instance, Prefab = prefab,
            });
            _tradeProfileDirty = true;
            if (flush) FlushTradeSales();
        }

        internal void FlushTradeSales()
        {
            if (_tradeProfileDirty && CanSendTradeSales) SendPendingTradeSales();
        }

        private void TickTradeSales()
        {
            if (CanSendTradeSales && Time.unscaledTime >= _nextTradeRetry) SendPendingTradeSales();
        }

        private void SendPendingTradeSales()
        {
            // Write local proceeds and the outgoing sale together in the profile.
            if (_tradeProfileDirty) _tradeProfileDirty = !CoopProfile.SaveFromGame();
            _nextTradeRetry = Time.unscaledTime + 3f;
            foreach (var entry in CoopProfile.TradeSales.Pending)
            {
                var sale = entry.Value;
                if (sale.World != _tradeWorld) continue;
                _net.SendMod(new TradeSaleMsg
                {
                    World = sale.World, Sale = entry.Key, Author = sale.Author,
                    Instance = sale.Instance, Prefab = sale.Prefab,
                }, ModFeatures.EconomyTradeSales, DeliveryMethod.ReliableOrdered);
            }
        }

        internal void OnTradeSale(TradeSaleMsg msg, NetPeer peer)
        {
            if (_net.Role != Role.Host || !PeerTrades(peer) || msg.Sale == Guid.Empty) return;
            Guid world = HostTradeWorld();
            if (msg.World != world) return;
            var data = GameState.modData;
            if (!TradeSaleJournal.IsComplete(data, world, msg.Sale))
            {
                int id = msg.Author != Guid.Empty ? TradeSaleJournal.FindAuthor(data, world, msg.Author) : msg.Instance;
                if (id > 0 && !ConsumeTradedItem(id, msg.Prefab)) return;
                if (msg.Author != Guid.Empty) TradeSaleJournal.RecordAuthor(data, world, msg.Author, -1);
                TradeSaleJournal.Complete(data, world, msg.Sale);
            }
            peer.Send(new TradeSaleReceiptMsg { World = world, Sale = msg.Sale }, DeliveryMethod.ReliableOrdered);
        }

        private bool ConsumeTradedItem(int id, int prefab)
        {
            var item = HostFindItem(id, prefab);
            if (item == null)
            {
                if (!TryRemoveCachedLocalItem(id, prefab, out bool removed)) return false;
                if (removed)
                    _net.Broadcast(new DespawnObjectMsg { Kind = (byte)NetObjKind.Item, InstanceId = id }, DeliveryMethod.ReliableOrdered);
                return true;
            }
            if (_destroyingItems.Contains(item)) return true;
            var previous = _consumingItem;
            _consumingItem = item;
            try { item.DestroyItem(); }
            catch (Exception e) { Plugin.Logger.LogWarning("[ItemSync] Trade sale consume: " + e.Message); }
            finally { _consumingItem = previous; }
            if (item != null && !_destroyingItems.Contains(item)) return false;
            _consumeRefresh = true;
            if (!_receivingBatch) FlushReceivedItems();
            return true;
        }

        internal void OnTradeSaleReceipt(TradeSaleReceiptMsg msg)
        {
            if (_net.Role != Role.Client || msg.World == Guid.Empty ||
                (_net.HostModFeatures & ModFeatures.EconomyTradeSales) == 0 || !EconomyOverhaulCompat.TradeSalesOn) return;
            if (msg.Sale == Guid.Empty)
            {
                _tradeWorld = msg.World;
                _nextTradeRetry = 0f;
                return;
            }
            var pending = CoopProfile.TradeSales.Pending;
            if (msg.World == _tradeWorld && pending.TryGetValue(msg.Sale, out var sale) && sale.World == _tradeWorld)
                _tradeProfileDirty |= pending.Remove(msg.Sale);
        }
    }
}
