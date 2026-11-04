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

        /// <summary>For a patch that runs every frame and so has its own try/catch instead of
        /// <see cref="Run"/> (whose closure and delegates are garbage on every call): call this from
        /// its catch. The report delegate is made only when something failed.</summary>
        public static void Fail(Action<Exception> report, Exception error) => Report(report, error);

        private static void Report(Action<Exception> report, Exception error)
        {
            try { report(error); } catch { } // Diagnostics must never interrupt vanilla.
        }
    }
}
