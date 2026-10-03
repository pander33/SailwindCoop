using System;

namespace SailwindCoop.Runtime
{
    internal static class PatchGuard
    {
        public static void Run(Action action, Action<Exception> report)
        {
            try { action(); }
            catch (Exception error) { Report(report, error); }
        }

        public static bool Prefix(Func<bool> action, Action<Exception> report)
        {
            try { return action(); }
            catch (Exception error) { Report(report, error); return true; }
        }

        private static void Report(Action<Exception> report, Exception error)
        {
            try { report(error); } catch { } // Diagnostics must never interrupt vanilla.
        }
    }
}
