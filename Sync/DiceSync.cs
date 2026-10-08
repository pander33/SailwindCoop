using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
	public sealed class DiceTable
	{
		public DiceStateMsg State;
		public DiceGame Game;
		public DiceTableView View;
		public Transform Boat;
		public Guid Owner;
	}

	public sealed class DiceSync
	{
		private readonly CoopNet net;
		private readonly Dictionary<uint, DiceTable> tables = new Dictionary<uint, DiceTable>();
		private readonly DiceRequestLedger ledger = new DiceRequestLedger();
		private readonly System.Random random = new System.Random();
		private readonly DiceClock clock = new DiceClock();
		private readonly Dictionary<uint, Guid> identities = new Dictionary<uint, Guid>();
		private uint revision, requestId;
		private string journalPath = "";
		private DiceJournal journal;
		private bool worldLoaded;
		private bool journalDirty, journalFailed;
		private float nextJournalRetry;
		public bool AllowStakes;
		private const string NoHistory = "No completed matches.";
		// Development builds of protocol 92 kept table places in the save under this key.
		private const string LegacyTablesKey = "SailwindCoop.DiceTables";
		public string JournalText = NoHistory;
		public IEnumerable<DiceTable> Tables => tables.Values;
		public DiceTableInput Input { get; }
		public DiceSync(CoopNet net)
		{
			this.net = net;
			Input = new DiceTableInput(this);
		}
		public void Clear()
		{
			if (journalDirty && journal != null) SaveJournal();
			foreach (var table in tables.Values) table.View?.Dispose();
			tables.Clear(); ledger.Clear(); identities.Clear(); Input.Clear(); worldLoaded = false;
			clock.Running = false; clock.ElapsedAtAnchorMs = 0; clock.AnchorServerTick = net.Clock.ServerTick; clock.Epoch = Guid.NewGuid();
			journal = null; journalPath = ""; JournalText = NoHistory;
			journalDirty = journalFailed = false; nextJournalRetry = 0;
		}
		public void Tick()
		{
			bool loaded = GameState.playing && !GameState.currentlyLoading;
			if (net.Role == Role.None || net.State != LinkState.Connected || !loaded)
			{
				if (worldLoaded) Clear();
				return;
			}
			worldLoaded = true;
			long tick = net.Clock.ServerTick;
			if (net.Role == Role.Host)
			{
				EnsureJournal();
				if (journalDirty && Time.realtimeSinceStartup >= nextJournalRetry) SaveJournal();
				bool changedClock = clock.SetRunning(tick, Time.timeScale > 0);
				foreach (var table in tables.Values.ToArray())
				{
					bool changed = table.Game.Advance(clock.Elapsed(tick));
					if (changed || changedClock) Publish(table);
					if (table.Game.Phase == DicePhase.Removed) Drop(table);
				}
			}
			foreach (var table in tables.Values)
			{
				var boat = BoatLocator.FindByIndex(table.State.BoatIndex);
				if (boat == null && table.State.BoatIndex != BoatLocator.NoBoat)
				{
					table.View?.Dispose(); table.View = null; continue;
				}
				if (table.Boat != null && table.Boat != boat && net.Role == Role.Host)
				{
					table.Game.Phase = DicePhase.Cancelled; table.Game.Fold(clock.Elapsed(tick)); Publish(table);
				}
				if (table.View == null || table.Boat != boat)
				{
					table.View?.Dispose(); table.Boat = boat;
					table.View = new DiceTableView(boat, table.State);
				}
				table.View.Apply(table.State, tick);
			}
			Input.ObservePresence();
		}
		private void Remove(DiceTable table)
		{
			table.Game.Phase = DicePhase.Removed; Publish(table); Drop(table);
		}
		private void Drop(DiceTable table)
		{
			table.View?.Dispose(); table.View = null;
			tables.Remove(table.State.TableId); Input.Forget(table);
		}
		// A table belongs to the session and to the player who set it up: it is not written to the save
		// and it goes when its owner leaves.
		private DiceTable Add(DiceTablePlace place, uint ownerNetId)
		{
			var table = new DiceTable
			{
				Owner = place.Owner,
				State = new DiceStateMsg { TableId = net.Registry.AllocateId(), Epoch = ++revision, OwnerNetId = ownerNetId, BoatIndex = place.Boat, X = place.X, Y = place.Y, Z = place.Z, Yaw = place.Yaw },
				Game = new DiceGame { PresentationAtMs = clock.Elapsed(net.Clock.ServerTick) }
			};
			tables.Add(table.State.TableId, table); Publish(table);
			return table;
		}
		private void EnsureJournal()
		{
			if (journal != null || journalFailed) return;
			try
			{
				if (GameState.modData == null) GameState.modData = new Dictionary<string, string>();
				GameState.modData.Remove(LegacyTablesKey);
				Guid identity = DiceJournal.Associate(GameState.modData);
				journalPath = Path.Combine(Application.persistentDataPath, DiceJournal.FileName(identity));
				try { journal = DiceJournal.Load(journalPath, identity); }
				catch (Exception e)
				{
					journal = new DiceJournal { Identity = identity };
					CoopBehaviour.Notice("Dice history could not be read; a new one was started.");
					Plugin.Logger.LogWarning("[Dice] " + e);
				}
				JournalText = journal.Matches.Count > 0 ? journal.Summary() : NoHistory;
			}
			catch (Exception e)
			{
				journalFailed = true;
				Plugin.Logger.LogWarning("[Dice] no match history in this session: " + e);
			}
		}
		private void SaveJournal()
		{
			journalDirty = true;
			nextJournalRetry = Time.realtimeSinceStartup + 10;
			try { journal.Save(journalPath); JournalText = journal.Summary(); journalDirty = false; }
			catch (Exception e) { CoopBehaviour.Notice("Dice journal could not be saved: " + e.Message); Plugin.Logger.LogWarning("[Dice] " + e); }
		}
		public void WorldChanging()
		{
			Clear();
			GameState.modData?.Remove(DiceJournal.SaveKey);
			GameState.modData?.Remove(LegacyTablesKey);
		}
		public void InvalidateHull(ushort boat)
		{
			if (net.Role != Role.Host) return;
			foreach (var table in tables.Values.Where(t => t.State.BoatIndex == boat))
			{
				table.Game.Phase = DicePhase.Cancelled; table.Game.Fold(clock.Elapsed(net.Clock.ServerTick)); Publish(table);
			}
		}
		public void PlayerLeft(uint id)
		{
			if (net.Role != Role.Host) return;
			identities.Remove(id);
			foreach (var table in tables.Values.ToArray())
			{
				// The table of a player who left goes with them, whatever is being played at it.
				if (table.State.OwnerNetId == id) { Remove(table); continue; }
				int seat = table.Game.Seats.FindIndex(p => p.NetId == id);
				if (seat < 0) continue;
				if (table.Game.Phase == DicePhase.Lobby) table.Game.Leave(seat);
				else { table.Game.Seats[seat].Connected = false; table.Game.Pause(); }
				Publish(table);
			}
		}
		// Two copies started from one install folder present the same identity. The later one plays
		// under an identity of its connection: it can sit down, but a reconnect does not return its seat.
		// The answer is decided once for a connection: who else is connected changes during a session.
		private Guid IdentityOf(uint id)
		{
			if (identities.TryGetValue(id, out Guid identity)) return identity;
			string text = net.GetPlayerGuid(id);
			if (!Guid.TryParse(text, out identity) || identity == Guid.Empty || net.SharesPlayerGuid(id))
				using (var hash = MD5.Create()) identity = new Guid(hash.ComputeHash(Encoding.UTF8.GetBytes(text + ":" + id)));
			identities[id] = identity;
			return identity;
		}
		public void SendBaseline(uint id)
		{
			if (net.Role != Role.Host) return;
			Guid identity = IdentityOf(id);
			foreach (var table in tables.Values)
			{
				var seat = table.Game.Seats.FirstOrDefault(p => p.Identity == identity);
				if (seat != null && !seat.Connected) { seat.NetId = id; seat.Connected = true; Publish(table); }
				net.SendToPlayer(id, table.State, DeliveryMethod.ReliableOrdered);
			}
			net.SendToPlayer(id, new DiceBaselineMsg { Revision = revision, Tables = tables.Keys.ToArray() }, DeliveryMethod.ReliableOrdered);
			net.SendToPlayer(id, new DiceJournalMsg { Text = JournalText }, DeliveryMethod.ReliableOrdered);
		}
		public void Receive(INetMessage message, NetPeer peer)
		{
			if (net.Role == Role.Host && message is DiceRequestMsg request) { ApplyRequest(request, net.PlayerNetIdForPeer(peer)); return; }
			if (net.Role != Role.Client) return;
			if (message is DiceStateMsg state)
			{
				bool known = tables.TryGetValue(state.TableId, out var existing);
				if (known && state.Revision <= existing.State.Revision) return;
				if (state.Phase == (byte) DicePhase.Removed) { if (known) Drop(existing); }
				else if (known)
				{
					bool completed = existing.State.Phase != (byte) DicePhase.Completed && state.Phase == (byte) DicePhase.Completed && existing.State.MatchId == state.MatchId;
					existing.State = state;
					if (completed) Settle(state);
					existing.View?.ObservePresentation(state);
				}
				else tables.Add(state.TableId, new DiceTable { State = state });
			}
			else if (message is DiceBaselineMsg baseline)
			{
				foreach (var table in tables.Values.Where(t => !baseline.Tables.Contains(t.State.TableId) && t.State.Revision <= baseline.Revision).ToArray())
					Drop(table);
			}
			else if (message is DiceJournalMsg history) JournalText = history.Text;
			else if (message is DiceResultMsg result && result.Detail != "") CoopBehaviour.Notice(result.Detail);
		}
		public void Request(DiceTable table, DiceAction action, byte mask = 0, bool flag = true, ushort boat = 0, Vector3 position = default, float yaw = 0, ushort stake = 0)
		{
			var state = table?.State;
			var request = new DiceRequestMsg
			{
				Action = action, TableId = state?.TableId ?? 0, Epoch = state?.Epoch ?? 0,
				RequestId = ++requestId, Revision = state?.Revision ?? 0, TurnId = state?.TurnId ?? 0,
				MatchId = state?.MatchId ?? "", Mask = mask, Flag = flag, BoatIndex = boat,
				X = position.x, Y = position.y, Z = position.z, Yaw = yaw, Stake = stake
			};
			if (net.Role == Role.Host) ApplyRequest(request, net.MyNetId);
			else net.Broadcast(request, DeliveryMethod.ReliableOrdered);
		}
		private void ApplyRequest(DiceRequestMsg request, uint actor)
		{
			// Detail is shown to the requester. A repeated or overtaken request stays silent: the current state follows it.
			var result = new DiceResultMsg { RequestId = request.RequestId, Result = DiceResultKind.ResyncNeeded };
			DiceTable table = null;
			if (!ledger.Accept(actor, request.RequestId)) result.Result = DiceResultKind.AlreadyApplied;
			else if (request.Action == DiceAction.RequestBaseline) { SendBaseline(actor); result.Result = DiceResultKind.Applied; }
			else if (request.Action == DiceAction.Place)
			{
				bool shore = request.BoatIndex == BoatLocator.NoBoat;
				Guid owner = IdentityOf(actor);
				// A player has one table: setting up another one moves it, wherever the old one stands.
				var old = tables.Values.FirstOrDefault(t => t.Owner == owner);
				if (old != null && old.Game.Phase >= DicePhase.AwaitRoll && old.Game.Phase <= DicePhase.RollingReroll) result.Detail = "Your dice table is in use. Finish or cancel the party first.";
				else if (tables.Count - (old != null ? 1 : 0) >= DiceTablePlace.MaxTables) result.Detail = "There are already " + DiceTablePlace.MaxTables + " dice tables in this world.";
				else if (!shore && BoatLocator.FindByIndex(request.BoatIndex) == null) result.Detail = "That boat is not available for a dice table.";
				else if (!shore && tables.Values.Any(t => t != old && t.State.BoatIndex == request.BoatIndex)) result.Detail = "This boat already has a dice table.";
				else
				{
					if (old != null) Remove(old);
					table = Add(new DiceTablePlace { Boat = request.BoatIndex, X = request.X, Y = request.Y, Z = request.Z, Yaw = request.Yaw, Owner = owner }, actor);
					result.Result = DiceResultKind.Applied;
				}
			}
			else if (tables.TryGetValue(request.TableId, out table) && request.Epoch == table.State.Epoch && request.MatchId == table.State.MatchId &&
				request.TurnId == table.State.TurnId && request.Revision == table.State.Revision)
			{
				var game = table.Game;
				Guid identity = IdentityOf(actor);
				int seat = game.Seats.FindIndex(p => p.Identity == identity && p.NetId == actor);
				bool changed = false;
				switch (request.Action)
				{
					case DiceAction.Join:
						changed = game.Join(identity, actor, net.GetPlayerName(actor));
						if (!changed) result.Detail = "Could not join: the table is full or the party has already started.";
						break;
					case DiceAction.Leave:
						if (seat >= 0) changed = game.Phase == DicePhase.Lobby ? game.Leave(seat) : game.Cancel();
						break;
					case DiceAction.Ready: changed = game.SetReady(seat, request.Flag); break;
					case DiceAction.Start: if (game.RollId == 0) game.FirstSeat = random.Next(Math.Max(1, game.Seats.Count)); changed = game.Start(); break;
					case DiceAction.Roll:
						if (game.Phase == DicePhase.AwaitRoll || game.Phase == DicePhase.Selecting && !game.Rerolled && game.HeldMask != 7)
							changed = game.Roll(new[] { (byte) random.Next(1, 7), (byte) random.Next(1, 7), (byte) random.Next(1, 7) }, clock.Elapsed(net.Clock.ServerTick));
						break;
					case DiceAction.SetHeldMask: changed = game.SetHeld(request.Mask); break;
					case DiceAction.Bank: changed = game.Bank(); break;
					case DiceAction.Pause: changed = game.Pause(); break;
					case DiceAction.ResumeReady: changed = game.SetResumeReady(seat, request.Flag); break;
					case DiceAction.CancelVote: changed = game.VoteCancel(seat); break;
					case DiceAction.Remove: changed = game.Fold(clock.Elapsed(net.Clock.ServerTick)); break;
					case DiceAction.Rematch: changed = game.Rematch(); break;
					case DiceAction.Presence: if (!request.Flag) changed = game.Pause(); break;
					case DiceAction.SetStake:
						if (!AllowStakes) result.Detail = "The host has not allowed dice stakes.";
						else if (WalletSync.Instance != null && WalletSync.Instance.Shared) result.Detail = "Dice stakes need personal wallets.";
						else changed = game.SetStake(request.Stake, request.Mask);
						break;
				}
				if (changed) { Publish(table); result.Result = DiceResultKind.Applied; }
			}
			result.Revision = table?.State.Revision ?? revision;
			if (actor == net.MyNetId) { if (result.Detail != "") CoopBehaviour.Notice(result.Detail); }
			else { net.SendToPlayer(actor, result, DeliveryMethod.ReliableOrdered); if (table != null) net.SendToPlayer(actor, table.State, DeliveryMethod.ReliableOrdered); }
		}
		private void Publish(DiceTable table)
		{
			var game = table.Game; var state = table.State;
			if (game.Phase == DicePhase.Completed && journal != null && journal.Add(game))
			{
				SaveJournal(); Broadcast(new DiceJournalMsg { Text = JournalText });
			}
			bool completed = state.Phase != (byte) DicePhase.Completed && game.Phase == DicePhase.Completed;
			state.Revision = ++revision; state.MatchId = game.MatchId.ToString("N"); state.TurnId = game.TurnId; state.RollId = game.RollId;
			state.Phase = (byte) game.Phase; state.ResumePhase = (byte) game.ResumePhase;
			state.Faces = (byte[]) game.Faces.Clone();
			state.HeldMask = game.HeldMask; state.RollHeldMask = game.RollHeldMask; state.Rerolled = game.Rerolled;
			state.Round = (byte) game.Round; state.Seat = (byte) game.Seat; state.FirstSeat = (byte) game.FirstSeat;
			state.PresentationAtMs = game.PresentationAtMs;
			state.Stake = game.Stake; state.StakeCurrency = game.StakeCurrency;
			state.StakesAllowed = AllowStakes && (WalletSync.Instance == null || !WalletSync.Instance.Shared);
			state.ClockEpoch = clock.Epoch.ToString("N"); state.ClockRevision = clock.Revision;
			state.AnchorTick = clock.AnchorServerTick; state.ElapsedMs = clock.ElapsedAtAnchorMs; state.Running = clock.Running;
			state.Players = game.Seats.Select(p => new DicePlayerData
			{
				Identity = p.Identity.ToString("N"), Name = p.Name, NetId = p.NetId, Connected = p.Connected,
				Ready = p.Ready, ResumeReady = p.ResumeReady, CancelVote = p.CancelVote,
				Scores = (int[]) p.Scores.Clone(), Scored = (bool[]) p.Scored.Clone()
			}).ToArray();
			table.View?.ObservePresentation(state);
			Broadcast(state);
			if (completed) Settle(state);
		}
		// A loser pays the stake itself, from its own wallet, when it sees the party end. A state that
		// already arrives completed (a late join) is not a reason to pay.
		private void Settle(DiceStateMsg state)
		{
			if (state.Stake == 0 || WalletSync.Instance == null || state.Players.Length == 0) return;
			int best = state.Players.Max(p => p.Scores.Sum());
			var winners = state.Players.Where(p => p.Scores.Sum() == best).ToArray();
			if (winners.Length == state.Players.Length || winners.Any(p => p.NetId == net.MyNetId) || !state.Players.Any(p => p.NetId == net.MyNetId)) return;
			int share = state.Stake / winners.Length;
			foreach (var winner in winners)
			{
				string error = share > 0 && winner.Connected ? WalletSync.Instance.Give(winner.NetId, state.StakeCurrency, share) : null;
				if (error != null) CoopBehaviour.Notice("Dice stake: " + error);
			}
		}
		private void Broadcast(INetMessage message)
		{
			foreach (uint id in net.ReadyClientIds()) net.SendToPlayer(id, message, DeliveryMethod.ReliableOrdered);
		}
	}
}
