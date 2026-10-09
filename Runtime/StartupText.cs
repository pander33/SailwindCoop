using System;

namespace SailwindCoop.Runtime
{
    /// <summary>What a player is told when the mod did not start (<see cref="StartupNotice"/>).</summary>
    public static class StartupText
    {
        /// <summary>The game version this build was made for, as <c>Application.version</c> starts.</summary>
        public const string SupportedGame = "0.39";

        public static string Describe(string gameVersion, string fault)
        {
            string game = string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion;
            if (!game.StartsWith(SupportedGame, StringComparison.Ordinal))
                return "Sailwind Co-op did not start: it needs Sailwind " + SupportedGame + ", this game is " + game +
                       ".\nUpdate the game to play co-op.";

            return "Sailwind Co-op did not start on this game (" + game + ").\n" + FirstLine(fault) +
                   "\nDetails are in BepInEx/LogOutput.log.";
        }

        private static string FirstLine(string fault)
        {
            if (string.IsNullOrEmpty(fault)) return "The co-op component was not created.";
            int end = fault.IndexOf('\n');
            string line = (end < 0 ? fault : fault.Substring(0, end)).Trim();
            return line.Length > 160 ? line.Substring(0, 160) + "..." : line;
        }
    }
}
