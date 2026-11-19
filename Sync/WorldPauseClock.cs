using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    internal sealed class WorldPauseClock
    {
        private bool _ownsScale, _ownsAutoSync;
        internal float ResumeScale { get; private set; }
        internal WorldPauseReason Reasons { get; private set; }
        internal uint Revision { get; private set; }

        internal bool SetReasons(WorldPauseReason reasons, ref float scale, ref bool autoSync)
        {
            if (Reasons == reasons) return false;
            if (Reasons == WorldPauseReason.None)
            {
                ResumeScale = scale;
                _ownsScale = scale != 0f;
                _ownsAutoSync = autoSync;
                scale = 0f;
                autoSync = false;
            }
            else if (reasons == WorldPauseReason.None)
            {
                if (_ownsScale && scale == 0f) scale = ResumeScale;
                if (_ownsAutoSync && !autoSync) autoSync = true;
            }
            Reasons = reasons;
            Revision++;
            return true;
        }

        internal void SetResumeScale(float scale)
        {
            if (Reasons != WorldPauseReason.None && _ownsScale) ResumeScale = scale;
        }
    }
}
