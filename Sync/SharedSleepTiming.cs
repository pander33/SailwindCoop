namespace SailwindCoop.Sync
{
	internal sealed class SharedSleepTiming
	{
		internal const float Warp = 4f;
		internal const float FadeInSeconds = 3f;
		internal const float HostSnapshotTimeout = 180f;
		private const float StallLimit = 15f;
		private const int MaxFixedSteps = 8;

		internal float Age { get; private set; }
		internal float Hours { get; private set; }
		internal bool WakeDelayElapsed => _realAge >= FadeInSeconds;
		private float _realAge;
		private float _stalledFor;
		private float _lastHour;

		internal static float FrameBudget(float fixedStep, float normalBudget)
			=> System.Math.Min(normalBudget, fixedStep * MaxFixedSteps);

		internal static float EffectiveScale(float nominal, float simulated, float real)
			=> simulated > 0f && real > 0f ? System.Math.Min(nominal, simulated / real) : nominal;

		internal bool LostHostClock(float secondsSinceSnapshot)
			=> _realAge >= HostSnapshotTimeout && secondsSinceSnapshot >= HostSnapshotTimeout;

		internal void Reset()
		{
			Age = Hours = _realAge = _stalledFor = 0f;
			_lastHour = float.NaN;
		}

		internal void Advance(float elapsed, bool paused)
		{
			_realAge += elapsed;
			if (!paused) Age += elapsed;
		}

		internal bool ObserveHostClock(float elapsed, float hour)
		{
			float advanced = hour - _lastHour;
			_lastHour = hour;
			if (advanced < -12f) advanced += 24f;
			if (advanced > 0f && advanced < 12f)
			{
				Hours += advanced;
				_stalledFor = 0f;
			}
			else _stalledFor += elapsed;
			return _stalledFor >= StallLimit;
		}
	}
}
