using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
	public sealed class SleepSync
	{
		public static SleepSync Instance { get; private set; }
		public const float MaxBlackoutSeconds = 45f;
		public const float FadeSeconds = 2.5f;
		private readonly CoopNet _net;
		private readonly SleepTransitionState _order = new SleepTransitionState();
		private readonly Dictionary<uint, SleepReply> _replies = new Dictionary<uint, SleepReply>();
		private readonly Dictionary<string, SleepAddress> _entrances = new Dictionary<string, SleepAddress>();
		private readonly Dictionary<string, SleepAddress> _deferredEntrances = new Dictionary<string, SleepAddress>();
		private SleepAddress _address = new SleepAddress();
		private SleepAddress _localAddress = new SleepAddress();
		private SleepAddress _localCommittedAddress = new SleepAddress();
		private SleepRequestMsg _pending;
		private SleepRequestMsg _pendingEntrance;
		private uint _nextRequest, _localCycle, _localCommittedCycle;
		private float _retryAt, _pendingAge, _entranceRetryAt, _entrancePendingAge, _phaseAge, _duration;
		private bool _blackout, _nativeActor, _applying, _worldWarp;
		private bool _localBedOwned;
		private float _normalTimeScale, _normalFixedStep;
		private bool _repairClock;
		private bool _presentationPending;
		private Coroutine _fade;
		public bool ClientAsleep => _blackout || _nativeActor;
		internal bool Connected => _net.State == LinkState.Connected;
		internal bool Applying => _applying;
		public string SleepText => _order.Active ? _order.Phase + " actor=" + _order.CycleActor : "awake";

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

		internal void EnteredBed(Transform bed)
		{
			if (!Connected || _applying || bed == null) return;
			var address = AddressFor(bed,
				bed.GetComponent<ShipItemBed>() != null ? SleepSource.ItemBed : SleepSource.Bed);
			_localAddress = address;
			_localCycle = NextRequest();
			_localBedOwned = true;
			_localCommittedCycle = _localCycle;
			_localCommittedAddress = address;
			Submit(new SleepRequestMsg { RequestId = _localCycle, Phase = SleepPhase.Begin, Address = address });
		}

		internal void FallAsleep(SleepAddress input)
		{
			if (!Connected || _applying) return;
			if (_blackout && !_nativeActor) return;
			var address = input ?? (_localCycle != 0 ? _localAddress : new SleepAddress());
			if (_localCycle == 0 || input != null)
			{
				if (_blackout || _nativeActor) EndEffects();
				_localAddress = address;
				_localCycle = NextRequest();
				_localCommittedCycle = _localCycle;
				_localCommittedAddress = address;
				Submit(new SleepRequestMsg { RequestId = _localCycle, Phase = SleepPhase.Begin, Address = address });
			}

			address.Timeskip = GameState.sleepingInTavern || CurrentBoatMoored();
			_nativeActor = true;
			GameState.sleeping = true;
			GameState.sleepingInTavern = address.Source == SleepSource.Tavern;
			global::Sleep.timeskipSleep = address.Timeskip;
			PlayerNeedsUI.instance.CloseNeedsUI();
			MouseLook.ToggleMouseLook(false);
			SetNativeField("currentSleepDuration", 0f);
			Submit(new SleepRequestMsg
			{
				RequestId = NextRequest(), CycleActor = _net.MyNetId,
				CycleId = _localCycle, Phase = SleepPhase.Sleeping, Address = address
			});
		}

		internal void Wake(bool cancel)
		{
			if (!Connected || _applying) return;
			uint actor = _nativeActor || _localCycle != 0 ? _net.MyNetId : _order.CycleActor;
			uint cycle = _nativeActor || _localCycle != 0 ? _localCycle : _order.CycleId;
			Submit(new SleepRequestMsg
			{
				RequestId = NextRequest(), CycleActor = actor, CycleId = cycle,
				Phase = cancel ? SleepPhase.Cancel : SleepPhase.Wake, Address = _address
			});
		}

		internal void EntranceCommitted(GPButtonOnsenEntrance entrance)
		{
			if (!Connected || _applying || entrance == null) return;
			Submit(new SleepRequestMsg
			{
				RequestId = NextRequest(), Phase = SleepPhase.Awake,
				Address = AddressFor(entrance.transform, SleepSource.Onsen)
			});
		}

		private void Submit(SleepRequestMsg request)
		{
			if (_net.Role == Role.Host)
			{
				Process(request, _net.MyNetId, null);
				return;
			}

			_net.Broadcast(request, DeliveryMethod.ReliableOrdered);
			if (request.Phase == SleepPhase.Awake && request.Address.Source == SleepSource.Onsen)
			{
				_pendingEntrance = request;
				_order.BeginEntrance(request.RequestId);
				_entrancePendingAge = _entranceRetryAt = 0f;
			}
			else
			{
				_pending = request;
				_order.BeginPending(request.RequestId);
				_pendingAge = _retryAt = 0f;
			}
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

			Process(request, actor, peer);
		}

		private void Process(SleepRequestMsg request, uint actor, NetPeer peer)
		{
			if (!_order.AcceptRequest(actor, request.RequestId))
			{
				SendState(peer, actor, request.RequestId,
					_replies.TryGetValue(actor, out var reply) ? reply : SleepReply.ObsoleteCycle);
				return;
			}

			SleepReply result = SleepReply.ObsoleteCycle;
			if (request.Phase == SleepPhase.Awake && request.Address.Source == SleepSource.Onsen)
			{
				if (ApplyEntrance(request.Address))
				{
					_entrances[request.Address.Path] = request.Address;
					_order.Touch();
					_net.Broadcast(State(actor, request.RequestId, SleepReply.Applied, request.Address),
						DeliveryMethod.ReliableOrdered);
					result = SleepReply.Applied;
				}
				else result = SleepReply.MissingTarget;
			}
			else if (request.Phase == SleepPhase.Begin && !TargetExists(request.Address))
				result = SleepReply.MissingTarget;
			else
			{
				uint oldActor = _order.CycleActor, oldCycle = _order.CycleId;
				bool wasSleeping = _order.Phase == SleepPhase.Sleeping;
				if (_order.Transition(actor, request.RequestId, request.Phase, request.CycleActor, request.CycleId))
				{
					result = SleepReply.Applied;
					if (request.Phase == SleepPhase.Begin && (oldActor != actor || oldCycle != request.RequestId) &&
					    (wasSleeping || _worldWarp || (_localCycle != 0 && actor != _net.MyNetId)))
					{
						uint localCycle = _localCycle;
						if (actor == _net.MyNetId && localCycle == request.RequestId) _localCycle = 0;
						EndEffects();
						if (actor == _net.MyNetId && localCycle == request.RequestId) _localCycle = localCycle;
					}

					_address = request.Address;
					_phaseAge = 0f;
					ApplyPhase();
					_net.Broadcast(State(actor, request.RequestId, result), DeliveryMethod.ReliableOrdered);
					if (!wasSleeping && _order.Phase == SleepPhase.Sleeping)
						_net.BroadcastNotice(GameplayNoticeKind.SleepStarted, _order.CycleActor);
					else if (wasSleeping && _order.Phase != SleepPhase.Sleeping)
						_net.BroadcastNotice(GameplayNoticeKind.SleepEnded, oldActor);
				}
			}

			_replies[actor] = result;
			if (result != SleepReply.Applied) SendState(peer, actor, request.RequestId, result);
			if (result != SleepReply.Applied && actor == _net.MyNetId && _localCycle != 0) EndEffects();
		}

		private SleepStateMsg State(uint actor = 0, uint request = 0, SleepReply reply = SleepReply.Applied,
			SleepAddress entrance = null)
			=> new SleepStateMsg
			{
				Revision = _order.Revision, CycleActor = _order.CycleActor, CycleId = _order.CycleId,
				Phase = _order.Phase, Address = _address, Requester = actor, RequestId = request, Reply = reply,
				EntranceCommitted = entrance != null, Entrance = entrance ?? new SleepAddress()
			};

		private void SendState(NetPeer peer, uint actor, uint request, SleepReply reply)
		{
			if (peer != null) peer.Send(State(actor, request, reply), DeliveryMethod.ReliableOrdered);
		}

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
			bool fresh = _order.Receive(state.Revision, state.Requester, state.RequestId, _net.MyNetId, out var ack);
			bool matchingSleepAck = ack && _pending != null && state.Requester == _net.MyNetId &&
			                        state.RequestId == _pending.RequestId;
			if (entranceAck) _order.ReceiveEntrance(state.Requester, state.RequestId, _net.MyNetId);
			ack |= entranceAck;
			if (matchingSleepAck || entranceAck)
			{
				if (entranceAck) _pendingEntrance = null;
				if (matchingSleepAck) _pending = null;
				if (matchingSleepAck && state.Reply != SleepReply.Applied && _localCommittedCycle != 0)
				{
					if (!_order.ShouldPreservePresentationForRejectedAck(matchingSleepAck, state.Reply,
						_localCommittedCycle, _net.MyNetId)) EndEffects();
					else ClearRejectedLocalSleep();
					_localCommittedCycle = 0;
					_localCommittedAddress = new SleepAddress();
				}
			}

			if (!fresh) return;
			bool replaced = !_order.Matches(state.CycleActor, state.CycleId);
			bool phaseChanged = _order.Phase != state.Phase;
			bool ownAcceptedCycle = state.CycleActor == _net.MyNetId && state.CycleId == _localCommittedCycle;
			if (replaced && !ownAcceptedCycle)
			{
				bool preserveIntent = _pending != null;
				EndEffects(preserveLocalIntent: preserveIntent);
				if (!preserveIntent)
				{
					_localCycle = 0;
					_localCommittedCycle = 0;
					_localCommittedAddress = new SleepAddress();
				}
			}

			_order.Load(state.Revision, state.CycleActor, state.CycleId, state.Phase);
			_address = state.Address;
			if (replaced || phaseChanged) _phaseAge = 0f;
			_presentationPending = true;
			if (!_order.Expired && PresentationReady())
			{
				ApplyPhase();
				_presentationPending = false;
			}

			if (matchingSleepAck && state.CycleActor == _net.MyNetId &&
			    state.CycleId == _localCommittedCycle && state.Phase == SleepPhase.Sleeping)
				RestoreLocalSleepFlags();
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
				if (!_order.Active) EndEffects();
				return;
			}

			bool startingPresentation = !_blackout;
			_blackout = true;
			if (startingPresentation)
			{
				_duration = 0f;
				Fade(true);
			}

			Refs.SetPlayerControl(false);
		}

		public void Tick(float dt)
		{
			RepairClock();
			if (!Connected)
			{
				if (_order.Active || _blackout || _nativeActor) Clear();
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

			bool paused = PauseHolds() || HostPauseHolds() || (_net.Role == Role.Host && Time.timeScale <= 0f) ||
			              (GameState.inCursorMenu && CoopBehaviour.Instance?.CoopMenuOpen != true);
			if (paused || !PresentationReady()) return;
			if (_pending != null)
			{
				_pendingAge += dt;
				_retryAt += dt;
				if (_pendingAge >= MaxBlackoutSeconds)
				{
					if (_localCycle != 0) _order.Expire(_net.MyNetId, _localCycle);
					else _order.Expire();
					_pending = null;
					EndEffects();
					_net.Broadcast(new SleepRequestMsg { Baseline = true }, DeliveryMethod.ReliableOrdered);
				}
				else if (_retryAt >= 1f)
				{
					_retryAt = 0f;
					_net.Broadcast(_pending, DeliveryMethod.ReliableOrdered);
				}
			}

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

			if (!_order.Active || _order.Expired || _presentationPending) return;
			_phaseAge += dt;
			if (_phaseAge >= MaxBlackoutSeconds)
			{
				Plugin.Logger.LogWarning("[SleepSync] role=" + _net.Role + " cycle=" + _order.CycleActor + "/" +
				                         _order.CycleId + " timeout");
				Wake(true);
				if (_net.Role == Role.Client)
				{
					_order.Expire();
					EndEffects();
				}

				return;
			}

			if (_order.Phase != SleepPhase.Sleeping) return;
			if (_nativeActor && !GameState.sleeping)
			{
				GameState.sleeping = true;
				GameState.sleepingInTavern = _address.Source == SleepSource.Tavern;
			}

			if (_phaseAge >= 3f)
			{
				if (_nativeActor) GameState.eyesFullyClosed = true;
				if (_net.Role == Role.Host && !_worldWarp)
				{
					_normalTimeScale = Time.timeScale;
					_normalFixedStep = Time.fixedDeltaTime;
					Time.timeScale = 16f;
					Time.fixedDeltaTime = _normalFixedStep * 10f;
					_worldWarp = true;
				}
			}

			float scale = _net.Role == Role.Host ? Time.timeScale : CoopBehaviour.Instance.Env.HostTimeScale;
			if (_net.Role != Role.Host || Sun.sun == null || _phaseAge < 3f) return;
			_duration += dt * scale * Sun.sun.timescale;
			bool tavern = _address.Source == SleepSource.Tavern;
			if ((!tavern && _duration > 4.5f) ||
			    (tavern && Sun.sun.localTime > 7f && Sun.sun.localTime < 10f && _duration > 3.3f))
			{
				Process(new SleepRequestMsg
				{
					RequestId = NextRequest(), CycleActor = _order.CycleActor,
					CycleId = _order.CycleId, Phase = SleepPhase.Wake, Address = _address
				}, _net.MyNetId, null);
			}
		}

		internal void RestoreNeeds(float sleepBefore, float debtBefore)
		{
			if (!_blackout || _order.Expired || Sun.sun == null) return;
			float hostScale = _net.Role == Role.Host ? Time.timeScale : CoopBehaviour.Instance.Env.HostTimeScale;
			if (HostPauseHolds() || PauseHolds()) hostScale = 0f;
			float amount = Time.unscaledDeltaTime * 8f * Sun.sun.timescale * hostScale;
			bool tavern = _nativeActor && _localCommittedAddress.Source == SleepSource.Tavern;
			if (tavern) amount *= 4f;
			if (debtBefore < 100f)
			{
				debtBefore = Mathf.Min(100f, debtBefore + amount);
				amount *= 0.2f;
			}

			PlayerNeeds.sleepDebt = debtBefore;
			PlayerNeeds.sleep = Mathf.Min(100f, sleepBefore + amount);
		}

		private void EndEffects(bool preserveLocalIntent = false)
		{
			bool hadEffects = _blackout || _nativeActor;
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

				if (_nativeActor)
				{
					GameState.sleeping = false;
					GameState.eyesFullyClosed = false;
					GameState.sleepingInTavern = false;
					MouseLook.ToggleMouseLook(true);
					SetNativeField("sleepCooldown", 6f);
				}

				if (_localBedOwned && GameState.inBed != null && global::Sleep.instance != null)
				{
					_localBedOwned = false;
					global::Sleep.instance.LeaveBed();
				}

				_localBedOwned = false;

				global::Sleep.timeskipSleep = false;
				_localBedOwned = false;
				_blackout = _nativeActor = false;
				if (!preserveLocalIntent)
				{
					_localCycle = 0;
					_localCommittedCycle = 0;
					_localCommittedAddress = new SleepAddress();
				}

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
			if (_net.Role == Role.Host && _order.Active && _order.CycleActor == actor)
				Process(new SleepRequestMsg
				{
					RequestId = NextRequest(), CycleActor = actor, CycleId = _order.CycleId,
					Phase = SleepPhase.Cancel, Address = _address
				}, _net.MyNetId, null);
			_order.ForgetActor(actor);
			_replies.Remove(actor);
		}

		public void Clear()
		{
			EndEffects();
			_order.Clear();
			_replies.Clear();
			_entrances.Clear();
			_deferredEntrances.Clear();
			_pending = _pendingEntrance = null;
			_nextRequest = 0;
			_address = new SleepAddress();
			_localCommittedCycle = 0;
			_localCommittedAddress = new SleepAddress();
			_presentationPending = false;
		}

		private static bool PresentationReady()
			=> GameState.playing && !GameState.currentlyLoading && !GameState.justStarted;

		private void RestoreLocalSleepFlags()
		{
			_nativeActor = true;
			GameState.sleeping = true;
			GameState.sleepingInTavern = _localCommittedAddress.Source == SleepSource.Tavern;
		}

		private void ClearRejectedLocalSleep()
		{
			if (_nativeActor)
			{
				GameState.sleeping = false;
				GameState.eyesFullyClosed = false;
				GameState.sleepingInTavern = false;
				MouseLook.ToggleMouseLook(true);
				SetNativeField("sleepCooldown", 6f);
			}

			if (_localBedOwned && GameState.inBed != null && global::Sleep.instance != null)
				global::Sleep.instance.LeaveBed();
			_localBedOwned = false;
			_nativeActor = false;
		}

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
			if (Time.timeScale == 16f) Time.timeScale = _normalTimeScale;
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
		{
			var address = new SleepAddress { Source = source };
			if (source == SleepSource.ItemBed)
			{
				var item = target.GetComponent<ShipItemBed>();
				ItemSync.Instance?.TrySharedIdentity(item, out address.InstanceId, out address.PrefabIndex);
			}
			else address.Path = PathFor(target);

			return address;
		}

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

		private static bool TargetExists(SleepAddress address)
		{
			switch (address.Source)
			{
				case SleepSource.Exhaustion: return global::Sleep.instance != null;
				case SleepSource.Bed: return Resolve<GPButtonBed>(address.Path) != null;
				case SleepSource.ItemBed:
					return ItemSync.Instance?.HostFindItem(address.InstanceId, address.PrefabIndex) is ShipItemBed;
				case SleepSource.Tavern: return Resolve<Tavern>(address.Path) != null;
				default: return false;
			}
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
