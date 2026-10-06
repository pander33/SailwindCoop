using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
	/// <summary>
	/// Sleep has two forms. A personal sleep belongs to one player: his screen goes dark, his sleep
	/// need recovers at the game's sleep speed and hunger and thirst stand still, while the world
	/// keeps its normal pace. A shared sleep is the game's own sleep for everyone: the host warps
	/// time and every screen is dark. The host starts it when every loaded player is in a bed (or
	/// already asleep) and at least one of them wants to sleep; nobody else can start or end it.
	/// </summary>
	public sealed class SleepSync
	{
		public static SleepSync Instance { get; private set; }
		public const float MaxBlackoutSeconds = 45f;
		public const float FadeSeconds = 2.5f;
		/// <summary>The game's own sleep time warp; a personal sleep recovers as fast without it.</summary>
		private const float SleepWarp = 16f;
		private const float PresencePeriod = 0.25f, PresenceResend = 2f;
		private const float RestedSleep = 99.99f;

		/// <summary>The local player's needs before the game's own PlayerNeeds.LateUpdate.</summary>
		internal struct NeedsSnapshot
		{
			internal bool Captured;
			internal float Sleep, Debt, Food, FoodDebt, Water, Protein, Vitamins;
		}

		private readonly CoopNet _net;
		private readonly SleepTransitionState _order = new SleepTransitionState();
		private readonly Dictionary<string, SleepAddress> _entrances = new Dictionary<string, SleepAddress>();
		private readonly Dictionary<string, SleepAddress> _deferredEntrances = new Dictionary<string, SleepAddress>();
		private readonly Dictionary<uint, byte> _presence = new Dictionary<uint, byte>();
		private SleepAddress _address = new SleepAddress();
		private SleepRequestMsg _pendingEntrance;
		private uint _nextRequest;
		private float _entranceRetryAt, _entrancePendingAge, _phaseAge, _duration;
		private float _presenceAt, _presenceResendAt, _personalAge, _personalDuration;
		private int _sentPresence = -1;
		// _blackout: the shared sleep is shown here, and only then are the game's own sleep flags
		// set. _personal: the local player sleeps alone. GameState.sleeping stays false for him: on
		// the host that flag freezes the other players' boats and items and calms the waves.
		private bool _blackout, _personal, _applying, _worldWarp;
		private float _normalTimeScale, _normalFixedStep;
		private bool _repairClock;
		private bool _presentationPending;
		private Coroutine _fade;
		public bool ClientAsleep => _blackout || _personal;
		/// <summary>A shared sleep ends for everyone at once: nobody gets out of bed during it.</summary>
		internal bool HoldsBed => _blackout;
		internal bool Connected => _net.State == LinkState.Connected;
		internal bool Applying => _applying;
		public string SleepText => _order.Active ? "shared " + _order.Phase : _personal ? "personal" : "awake";

		public SleepSync(CoopNet net)
		{
			_net = net;
			Instance = this;
		}

		private uint NextRequest()
		{
			if (++_nextRequest == 0) ++_nextRequest;
			return _nextRequest;
		}

		/// <summary>
		/// Replaces the game's FallAsleep (a bed, a tavern night, exhaustion): the player falls
		/// asleep alone. The world is not warped; the host turns it into a shared sleep if the
		/// whole crew is in bed.
		/// </summary>
		internal void FallAsleep(SleepAddress input)
		{
			if (!Connected || _applying) return;
			if (_blackout || _personal) return;
			bool tavern = input != null && input.Source == SleepSource.Tavern;
			// A house bed puts even a rested player to sleep. Alone that would only blink the
			// screen; in bed he still counts as wanting to sleep (see LocalPresence).
			if (!tavern && PlayerNeeds.sleep >= 99f) return;

			_personal = true;
			_personalAge = _personalDuration = 0f;
			GameState.sleepingInTavern = tavern;
			global::Sleep.timeskipSleep = false;
			PlayerNeedsUI.instance.CloseNeedsUI();
			MouseLook.ToggleMouseLook(false);
			SetNativeField("currentSleepDuration", 0f);
			Refs.SetPlayerControl(false);
			Fade(true);
			_presenceAt = PresencePeriod;
		}

		/// <summary>
		/// The game's own wake-up during a shared sleep. What the game's sleep loop decides (slept
		/// enough, slept long) is replaced by the host's rules in <see cref="Tick"/>. Anything else
		/// is an emergency on the host's simulation — a collision, running aground, water coming
		/// in — and wakes the crew.
		/// </summary>
		internal void Wake(bool fromSleepLoop)
		{
			if (!Connected || _applying || fromSleepLoop) return;
			if (_net.Role == Role.Host && _blackout && _order.Phase == SleepPhase.Sleeping)
				HostEnd(SleepPhase.Wake);
		}

		/// <summary>A key pressed in bed gets the player up; that also ends his personal sleep.</summary>
		internal void LeftBed()
		{
			if (!Connected || _applying) return;
			if (_personal) EndEffects(false);
		}

		internal void EntranceCommitted(GPButtonOnsenEntrance entrance)
		{
			if (!Connected || _applying || entrance == null) return;
			var request = new SleepRequestMsg
			{
				RequestId = NextRequest(), Phase = SleepPhase.Awake,
				Address = AddressFor(entrance.transform, SleepSource.Onsen)
			};
			if (_net.Role == Role.Host)
			{
				ProcessEntrance(request, _net.MyNetId, null);
				return;
			}

			_net.Broadcast(request, DeliveryMethod.ReliableOrdered);
			_pendingEntrance = request;
			_order.BeginEntrance(request.RequestId);
			_entrancePendingAge = _entranceRetryAt = 0f;
		}

		public void OnSleepRequest(SleepRequestMsg request, NetPeer peer)
		{
			if (_net.Role != Role.Host || !Connected) return;
			uint actor = _net.PlayerNetIdForPeer(peer);
			if (actor == 0) return;
			if (request.Baseline)
			{
				SendBaseline(peer);
				return;
			}

			// Only the host moves the shared sleep; a client's request carries an onsen entrance.
			if (request.Phase == SleepPhase.Awake && request.Address.Source == SleepSource.Onsen)
				ProcessEntrance(request, actor, peer);
		}

		private void ProcessEntrance(SleepRequestMsg request, uint actor, NetPeer peer)
		{
			if (!_order.AcceptRequest(actor, request.RequestId)) return;
			if (ApplyEntrance(request.Address))
			{
				_entrances[request.Address.Path] = request.Address;
				_order.Touch();
				_net.Broadcast(State(actor, request.RequestId, SleepReply.Applied, request.Address),
					DeliveryMethod.ReliableOrdered);
			}
			else if (peer != null)
				peer.Send(State(actor, request.RequestId, SleepReply.MissingTarget), DeliveryMethod.ReliableOrdered);
		}

		public void OnSleepPresence(SleepPresenceMsg msg, NetPeer peer)
		{
			if (_net.Role != Role.Host || !Connected) return;
			uint actor = _net.PlayerNetIdForPeer(peer);
			if (actor != 0) _presence[actor] = msg.Flags;
		}

		private SleepStateMsg State(uint actor = 0, uint request = 0, SleepReply reply = SleepReply.Applied,
			SleepAddress entrance = null)
			=> new SleepStateMsg
			{
				Revision = _order.Revision, CycleActor = _order.CycleActor, CycleId = _order.CycleId,
				Phase = _order.Phase, Address = _address, Requester = actor, RequestId = request, Reply = reply,
				EntranceCommitted = entrance != null, Entrance = entrance ?? new SleepAddress()
			};

		public void SendBaseline(NetPeer peer)
		{
			if (_net.Role != Role.Host || _net.PlayerNetIdForPeer(peer) == 0) return;
			peer.Send(State(), DeliveryMethod.ReliableOrdered);
			foreach (var entrance in _entrances.Values)
				peer.Send(State(entrance: entrance), DeliveryMethod.ReliableOrdered);
		}

		public void OnSleepState(SleepStateMsg state, NetPeer peer)
		{
			if (_net.Role != Role.Client || !_net.IsHostPeer(peer)) return;
			if (state.EntranceCommitted && !ApplyEntrance(state.Entrance))
				_deferredEntrances[state.Entrance.Path] = state.Entrance;
			bool entranceAck = _order.PendingEntrance != 0 && state.Requester == _net.MyNetId &&
			                   state.RequestId == _order.PendingEntrance;
			bool fresh = _order.Receive(state.Revision, state.Requester, state.RequestId, _net.MyNetId, out _);
			if (entranceAck)
			{
				_order.ReceiveEntrance(state.Requester, state.RequestId, _net.MyNetId);
				_pendingEntrance = null;
			}

			if (!fresh) return;
			bool changed = !_order.Matches(state.CycleActor, state.CycleId) || _order.Phase != state.Phase;
			_order.Load(state.Revision, state.CycleActor, state.CycleId, state.Phase);
			_address = state.Address;
			if (changed) _phaseAge = 0f;
			_presentationPending = true;
			if (!_order.Expired && PresentationReady())
			{
				ApplyPhase();
				_presentationPending = false;
			}
		}

		/// <summary>Host: the whole crew is in bed and someone wants to sleep.</summary>
		private void HostBegin(SleepAddress address)
		{
			uint cycle = NextRequest();
			_order.Transition(_net.MyNetId, cycle, SleepPhase.Begin, 0, 0);
			_order.Transition(_net.MyNetId, cycle, SleepPhase.Sleeping, _net.MyNetId, cycle);
			_address = address;
			_phaseAge = 0f;
			ApplyPhase();
			_net.Broadcast(State(), DeliveryMethod.ReliableOrdered);
			_net.BroadcastNotice(GameplayNoticeKind.SleepStarted, 0);
		}

		private void HostEnd(SleepPhase phase)
		{
			if (!_order.Active) return;
			_order.Transition(_net.MyNetId, NextRequest(), phase, _order.CycleActor, _order.CycleId);
			_phaseAge = 0f;
			// Everyone is put out of bed; until the clients report that, their old "in bed" must
			// not start the next sleep.
			_presence.Clear();
			ApplyPhase();
			_net.Broadcast(State(), DeliveryMethod.ReliableOrdered);
			_net.BroadcastNotice(GameplayNoticeKind.SleepEnded, 0);
		}

		private void ApplyPhase()
		{
			if (_net.Role == Role.Host)
				global::Sleep.timeskipSleep = _order.Phase == SleepPhase.Sleeping && _address.Timeskip;
			if (!PresentationReady() && !_blackout)
			{
				_presentationPending = true;
				return;
			}

			if (_order.Phase != SleepPhase.Sleeping)
			{
				// Only the shared sleep ends here: a personal one is not the host's to end.
				if (!_order.Active && _blackout) EndEffects(true);
				return;
			}

			bool startingPresentation = !_blackout, wasPersonal = _personal;
			_blackout = true;
			_personal = false;
			if (startingPresentation)
			{
				_duration = 0f;
				SetNativeField("currentSleepDuration", 0f);
				if (!wasPersonal)
				{
					PlayerNeedsUI.instance.CloseNeedsUI();
					MouseLook.ToggleMouseLook(false);
					Fade(true);
				}
			}

			Refs.SetPlayerControl(false);
		}

		public void Tick(float dt)
		{
			RepairClock();
			if (!Connected)
			{
				if (_order.Active || _blackout || _personal) Clear();
				return;
			}

			if (_presentationPending && PresentationReady())
			{
				if (!_order.Expired) ApplyPhase();
				_presentationPending = false;
			}

			if (_deferredEntrances.Count != 0 && PresentationReady())
			{
				var applied = new List<string>();
				foreach (var entrance in _deferredEntrances)
					if (ApplyEntrance(entrance.Value))
						applied.Add(entrance.Key);
				foreach (var path in applied) _deferredEntrances.Remove(path);
			}

			if (Paused() || !PresentationReady()) return;

			if (_pendingEntrance != null)
			{
				_entrancePendingAge += dt;
				_entranceRetryAt += dt;
				if (_entrancePendingAge >= MaxBlackoutSeconds)
				{
					_order.ExpireEntrance(_pendingEntrance.RequestId);
					_pendingEntrance = null;
				}
				else if (_entranceRetryAt >= 1f)
				{
					_entranceRetryAt = 0f;
					_net.Broadcast(_pendingEntrance, DeliveryMethod.ReliableOrdered);
				}
			}

			TickPersonal(dt);
			TickPresence(dt);

			if (!_order.Active || _order.Expired || _presentationPending) return;
			_phaseAge += dt;
			if (_phaseAge >= MaxBlackoutSeconds)
			{
				Plugin.Logger.LogWarning("[SleepSync] role=" + _net.Role + " cycle=" + _order.CycleActor + "/" +
				                         _order.CycleId + " timeout");
				if (_net.Role == Role.Host) HostEnd(SleepPhase.Cancel);
				else
				{
					_order.Expire();
					EndEffects(true);
				}

				return;
			}

			if (_order.Phase != SleepPhase.Sleeping) return;
			// The game's own sleep for everyone: each player is asleep for his copy of the game.
			if (!GameState.sleeping) GameState.sleeping = true;

			if (_phaseAge < 3f) return;
			GameState.eyesFullyClosed = true;
			if (_net.Role != Role.Host) return;
			if (!_worldWarp)
			{
				_normalTimeScale = Time.timeScale;
				_normalFixedStep = Time.fixedDeltaTime;
				Time.timeScale = SleepWarp;
				Time.fixedDeltaTime = _normalFixedStep * 10f;
				_worldWarp = true;
			}

			if (Sun.sun == null) return;
			_duration += dt * Time.timeScale * Sun.sun.timescale;
			bool tavern = _address.Source == SleepSource.Tavern;
			bool done = tavern
				? Sun.sun.localTime > 7f && Sun.sun.localTime < 10f && _duration > 3.3f
				: _duration > 4.5f;
			// Like the game: at sea nobody sleeps longer than needed.
			if (!done && !_address.Timeskip && CrewRested()) done = true;
			if (done) HostEnd(SleepPhase.Wake);
		}

		private void TickPersonal(float dt)
		{
			if (!_personal) return;
			_personalAge += dt;
			if (_personalAge < 3f) return;
			if (Sun.sun != null) _personalDuration += dt * SleepWarp * Sun.sun.timescale;
			if (_personalDuration > (GameState.sleepingInTavern ? 3.3f : 4.5f) || PlayerNeeds.sleep >= RestedSleep)
				EndEffects(false);
		}

		/// <summary>A client reports its presence to the host; the host decides on the shared sleep.</summary>
		private void TickPresence(float dt)
		{
			_presenceAt += dt;
			_presenceResendAt += dt;
			if (_presenceAt < PresencePeriod) return;
			_presenceAt = 0f;
			byte mine = LocalPresence();
			if (_net.Role == Role.Client)
			{
				if (mine == _sentPresence && _presenceResendAt < PresenceResend) return;
				_sentPresence = mine;
				_presenceResendAt = 0f;
				_net.Broadcast(new SleepPresenceMsg { Flags = mine }, DeliveryMethod.ReliableOrdered);
				return;
			}

			if (_net.Role != Role.Host || _order.Active) return;
			bool allInBed = Ready(mine), allTimeskip = Has(mine, SleepPresenceMsg.Timeskip);
			bool wanted = Has(mine, SleepPresenceMsg.Wants), tavern = Has(mine, SleepPresenceMsg.Tavern);
			foreach (var member in _net.RosterSnapshot)
			{
				if (member.IsHost || member.State != MemberJoinState.Ready) continue;
				_presence.TryGetValue(member.NetId, out byte flags);
				allInBed &= Ready(flags);
				allTimeskip &= Has(flags, SleepPresenceMsg.Timeskip);
				wanted |= Has(flags, SleepPresenceMsg.Wants);
				tavern |= Has(flags, SleepPresenceMsg.Tavern);
			}

			if (!allInBed || !wanted) return;
			// A tavern night lasts until morning, as in the game — but only if no boat is left
			// sailing by itself meanwhile.
			HostBegin(new SleepAddress
			{
				Source = tavern && allTimeskip ? SleepSource.Tavern : SleepSource.Exhaustion,
				Timeskip = allTimeskip
			});
		}

		private byte LocalPresence()
		{
			byte flags = 0;
			bool inBed = GameState.inBed != null;
			if (inBed) flags |= SleepPresenceMsg.InBed;
			if (_personal || _blackout || (inBed && GameState.currentHouse != null)) flags |= SleepPresenceMsg.Wants;
			if (PlayerNeeds.sleep >= RestedSleep) flags |= SleepPresenceMsg.Rested;
			if (GameState.sleepingInTavern) flags |= SleepPresenceMsg.Tavern;
			if (GameState.sleepingInTavern || GameState.currentBoat == null || CurrentBoatMoored())
				flags |= SleepPresenceMsg.Timeskip;
			return flags;
		}

		private static bool Has(byte flags, byte flag) => (flags & flag) != 0;
		private static bool Ready(byte flags) => (flags & (SleepPresenceMsg.InBed | SleepPresenceMsg.Wants)) != 0;

		private bool CrewRested()
		{
			if (!Has(LocalPresence(), SleepPresenceMsg.Rested)) return false;
			foreach (var member in _net.RosterSnapshot)
			{
				if (member.IsHost || member.State != MemberJoinState.Ready) continue;
				if (!_presence.TryGetValue(member.NetId, out byte flags) || !Has(flags, SleepPresenceMsg.Rested))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Runs after the game's PlayerNeeds.LateUpdate and replaces what it did to the sleeping
		/// player. Sleep need: the game's formula at the speed of the sleep in progress. Hunger and
		/// thirst: frozen in a personal sleep; in a shared one a client drains them as fast as the
		/// host, whose clock is the only one warped.
		/// </summary>
		internal void AdjustNeeds(NeedsSnapshot before)
		{
			if (!_blackout && !_personal) return;
			if ((_blackout && _order.Expired) || Sun.sun == null) return;
			bool paused = HostPauseHolds() || PauseHolds();
			float hostScale = _net.Role == Role.Host ? Time.timeScale : CoopBehaviour.Instance.Env.HostTimeScale;
			float scale = _blackout ? hostScale : Time.timeScale <= 0f ? 0f : SleepWarp;
			if (paused) scale = 0f;
			float step = Time.unscaledDeltaTime * Sun.sun.timescale;
			bool tavern = GameState.sleepingInTavern;

			float amount = step * 8f * scale;
			if (tavern) amount *= 4f;
			float debt = before.Debt;
			if (debt < 100f)
			{
				debt = Mathf.Min(100f, debt + amount);
				amount *= 0.2f;
			}

			PlayerNeeds.sleepDebt = debt;
			PlayerNeeds.sleep = Mathf.Min(100f, before.Sleep + amount);

			// A tavern night feeds the sleeper: the game has just refilled everything.
			if (tavern) return;
			if (!_blackout)
			{
				PlayerNeeds.food = before.Food;
				PlayerNeeds.foodDebt = before.FoodDebt;
				PlayerNeeds.water = before.Water;
				PlayerNeeds.protein = before.Protein;
				PlayerNeeds.vitamins = before.Vitamins;
				return;
			}

			// The rates are those of PlayerNeeds.LateUpdate (game 0.39).
			float missed = step * (scale - Time.timeScale);
			if (_net.Role != Role.Client || missed <= 0f) return;
			if (PlayerNeeds.food > 0f) PlayerNeeds.food = Mathf.Max(0f, PlayerNeeds.food - missed * 3f);
			else PlayerNeeds.foodDebt = Mathf.Max(0f, PlayerNeeds.foodDebt - missed * 5f);
			PlayerNeeds.water = Mathf.Max(0f, PlayerNeeds.water - missed * 4f);
			PlayerNeeds.vitamins = Mathf.Max(0f, PlayerNeeds.vitamins - missed * 0.2f);
			PlayerNeeds.protein = Mathf.Max(0f, PlayerNeeds.protein - missed * 0.2f);
			PlayerNeeds.alcohol = Mathf.Max(0f, PlayerNeeds.alcohol - missed * 12f);
		}

		/// <summary>Ends whatever sleep is shown here. A shared sleep also puts the player out of
		/// bed, so the crew does not fall asleep again at once; a personal one leaves him lying.</summary>
		private void EndEffects(bool leaveBed)
		{
			bool hadEffects = _blackout || _personal;
			_applying = true;
			try
			{
				if (_worldWarp)
				{
					if (PauseHolds()) CoopBehaviour.Instance.Pause.SetResumeTimeScale(_normalTimeScale);
					else if (Time.timeScale > 0f) Time.timeScale = _normalTimeScale;
					_repairClock = true;
					Time.fixedDeltaTime = _normalFixedStep;
					_worldWarp = false;
				}

				if (_blackout)
				{
					GameState.sleeping = false;
					GameState.eyesFullyClosed = false;
				}

				if (hadEffects)
				{
					GameState.sleepingInTavern = false;
					if (GameState.inBed == null) MouseLook.ToggleMouseLook(true);
					SetNativeField("sleepCooldown", 6f);
				}

				if (leaveBed && GameState.inBed != null && global::Sleep.instance != null)
					global::Sleep.instance.LeaveBed();

				global::Sleep.timeskipSleep = false;
				_blackout = _personal = false;
				_presenceAt = PresencePeriod;

				if (hadEffects) Fade(false);
				if (hadEffects && !HostPauseHolds() && !PauseHolds() && GameState.inBed == null &&
				    !(GameState.inCursorMenu && CoopBehaviour.Instance?.CoopMenuOpen != true))
					Refs.SetPlayerControl(true);
				if (HostPauseHolds() || PauseHolds()) Refs.SetPlayerControl(false);
			}
			finally
			{
				_applying = false;
			}
		}

		public void ClearRemoteActor(uint actor)
		{
			_presence.Remove(actor);
			_order.ForgetActor(actor);
		}

		public void Clear()
		{
			EndEffects(false);
			_order.Clear();
			_entrances.Clear();
			_deferredEntrances.Clear();
			_presence.Clear();
			_pendingEntrance = null;
			_nextRequest = 0;
			_sentPresence = -1;
			_address = new SleepAddress();
			_presentationPending = false;
		}

		private static bool PresentationReady()
			=> GameState.playing && !GameState.currentlyLoading && !GameState.justStarted;

		private bool Paused()
			=> PauseHolds() || HostPauseHolds() || (_net.Role == Role.Host && Time.timeScale <= 0f) ||
			   (GameState.inCursorMenu && CoopBehaviour.Instance?.CoopMenuOpen != true);

		private void Fade(bool sleeping)
		{
			var behaviour = CoopBehaviour.Instance;
			if (behaviour == null) return;
			if (_fade != null) behaviour.StopCoroutine(_fade);
			_fade = behaviour.StartCoroutine(Blackout.FadeTo(sleeping ? 1f : 0f, FadeSeconds));
		}

		private void RepairClock()
		{
			if (!_repairClock || _worldWarp || PauseHolds()) return;
			if (GameState.inCursorMenu && CoopBehaviour.Instance?.CoopMenuOpen != true) return;
			if (Time.timeScale == SleepWarp) Time.timeScale = _normalTimeScale;
			_repairClock = false;
		}

		private static bool PauseHolds() => CoopBehaviour.Instance?.Pause?.Active == true;
		private static bool HostPauseHolds() => CoopBehaviour.Instance?.HostPause?.Frozen == true;

		private static bool CurrentBoatMoored() => GameState.currentBoat != null &&
		                                           GameState.currentBoat.parent.GetComponent<BoatMooringRopes>()
			                                           ?.AnyRopeMoored() == true;

		private static void SetNativeField(string name, float value)
		{
			if (global::Sleep.instance == null) return;
			var field = typeof(global::Sleep).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
			if (field == null) throw new MissingFieldException(typeof(global::Sleep).Name, name);
			field.SetValue(global::Sleep.instance, value);
		}

		internal static SleepAddress AddressFor(Transform target, SleepSource source)
			=> new SleepAddress { Source = source, Path = PathFor(target) };

		private static string PathFor(Transform target)
		{
			string path = "";
			while (target != null)
			{
				if (target.parent == null) return target.gameObject.scene.name + "/" + target.name + path;
				path = "/" + target.name + "[" + target.GetSiblingIndex() + "]" + path;
				target = target.parent;
			}

			return path;
		}

		private static T Resolve<T>(string path) where T : Component
		{
			foreach (var component in Resources.FindObjectsOfTypeAll<T>())
				if (component != null && component.gameObject.scene.IsValid() && PathFor(component.transform) == path)
					return component;
			return null;
		}

		private static bool ApplyEntrance(SleepAddress address)
		{
			if (address.Source != SleepSource.Onsen) return false;
			var entrance = Resolve<GPButtonOnsenEntrance>(address.Path);
			if (entrance == null || entrance.cols == null) return false;
			entrance.cols.SetActive(false);
			return true;
		}
	}
}
