using System;

namespace SailwindCoop.Net
{
    [Flags]
    public enum WorldPauseReason : byte { None = 0, Manual = 1, Loading = 2, Settling = 4 }
}
