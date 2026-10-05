using System.Collections.Generic;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
	/// <summary>Pure session ordering: the host's receive order wins between actors. A later begin
	/// replaces the previous cycle; a delayed wake/cancel cannot finish that replacement.</summary>
	internal sealed class SleepTransitionState
	{
		private readonly Dictionary<uint, uint> _requests = new Dictionary<uint, uint>();
		private uint _receivedRevision, _pending, _pendingEntrance;
		private bool _haveRevision;
		private uint _expiredActor, _expiredCycle;
		internal uint Revision { get; private set; }
		internal uint CycleActor { get; private set; }
		internal uint CycleId { get; private set; }
		internal SleepPhase Phase { get; private set; }
		internal bool Active => Phase == SleepPhase.Begin || Phase == SleepPhase.Sleeping;
		internal uint Pending => _pending;
		internal uint PendingEntrance => _pendingEntrance;

		internal void BeginPending(uint request)
		{
			_pending = request;
		}

		internal void BeginEntrance(uint request)
		{
			_pendingEntrance = request;
		}

		internal bool ReceiveEntrance(uint requester, uint request, uint localActor)
		{
			if (_pendingEntrance == 0 || requester != localActor || request != _pendingEntrance) return false;
			_pendingEntrance = 0;
			return true;
		}

		internal bool ExpireEntrance(uint request)
		{
			if (_pendingEntrance == 0 || request != _pendingEntrance) return false;
			_pendingEntrance = 0;
			return true;
		}

		internal bool AcceptRequest(uint actor, uint request)
		{
			if (actor == 0 || request == 0) return false;
			if (_requests.TryGetValue(actor, out var previous) && unchecked((int) (request - previous)) <= 0)
				return false;
			_requests[actor] = request;
			return true;
		}

		internal bool Transition(uint actor, uint request, SleepPhase phase, uint cycleActor, uint cycleId)
		{
			if (phase == SleepPhase.Awake || phase > SleepPhase.Cancel) return false;
			if (phase == SleepPhase.Begin)
			{
				CycleActor = actor;
				CycleId = request;
				Phase = phase;
				Advance();
				return true;
			}

			if (!Active || cycleActor != CycleActor || cycleId != CycleId) return false;
			if (phase == Phase || (phase == SleepPhase.Sleeping && Phase != SleepPhase.Begin)) return false;
			Phase = phase;
			Advance();
			return true;
		}

		internal bool Matches(uint actor, uint cycle) => CycleActor == actor && CycleId == cycle;
		internal bool IsForeignSleeping(uint actor) => Phase == SleepPhase.Sleeping && CycleActor != actor;
		internal bool ShouldPreservePresentationForRejectedAck(bool matchingAck, SleepReply reply, uint localCycle,
			uint localActor)
			=> matchingAck && reply != SleepReply.Applied && localCycle != 0 && IsForeignSleeping(localActor);
		internal bool Expired => Matches(_expiredActor, _expiredCycle) && CycleId != 0;

		internal void Expire()
		{
			_expiredActor = CycleActor;
			_expiredCycle = CycleId;
			_pending = 0;
		}

		internal void Expire(uint actor, uint cycle)
		{
			_expiredActor = actor;
			_expiredCycle = cycle;
			_pending = 0;
		}

		internal void ForgetActor(uint actor)
		{
			_requests.Remove(actor);
		}

		internal bool Receive(uint revision, uint requester, uint request, uint localActor, out bool ack)
		{
			ack = _pending != 0 && requester == localActor && request == _pending;
			if (ack) _pending = 0;
			if (_haveRevision && unchecked((int) (revision - _receivedRevision)) <= 0) return false;
			_haveRevision = true;
			_receivedRevision = revision;
			return true;
		}

		internal void Load(uint revision, uint actor, uint cycle, SleepPhase phase)
		{
			Revision = revision;
			CycleActor = actor;
			CycleId = cycle;
			Phase = phase;
		}

		internal void Touch()
		{
			Advance();
		}

		private void Advance()
		{
			if (++Revision == 0) ++Revision;
		}

		internal void Clear()
		{
			_requests.Clear();
			Revision = CycleActor = CycleId = _receivedRevision = _pending = _pendingEntrance = 0;
			Phase = SleepPhase.Awake;
			_haveRevision = false;
			_expiredActor = _expiredCycle = 0;
		}
	}
}
