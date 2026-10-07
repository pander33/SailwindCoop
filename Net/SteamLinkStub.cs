namespace SailwindCoop.Net
{
    /// <summary>A Steam friend who is in the game right now.</summary>
    public struct SteamFriendInfo
    {
        public ulong Id;
        public string Name;
        public bool Hosting;
        public bool SameVersion;
    }

    /// <summary>
    /// Thunderstore edition: the Steam transport is not part of this build. Compiled instead of
    /// <c>SteamLink.cs</c>, with the same members, so the session layer needs no changes; the build
    /// references no Steam library and ships none.
    /// </summary>
    public static class SteamLink
    {
        /// <summary>This build contains the Steam transport.</summary>
        public static readonly bool Available = false;

        public static bool Ready => false;
        public static string Error => "";
        public static bool EnsureReady() => false;
        public static void Tick() { }
        public static ulong MyId => 0UL;
        public static string MyName => "";
        public static SteamFriendInfo[] FriendsInGame() => new SteamFriendInfo[0];
        public static string NameOf(ulong steamId) => "";
        public static IDatagramRelay OpenHost(bool friendsOnly) => null;
        public static IDatagramRelay OpenClient(ulong hostId) => null;
        public static void CloseRelay() { }
        public static void SetFriendsOnly(bool friendsOnly) { }
        public static void Advertise(bool open) { }
        public static void Block(ulong steamId) { }
        public static bool FailureIsFinal => false;
        public static string TakeFailure() => null;
        public static void Shutdown() { }
    }
}
