using System;
using System.Collections.Generic;
using System.Threading;

namespace SailwindCoop.DevConsole
{
    /// <summary>
    /// Hands work from the HTTP thread to the Unity main thread and waits for it. Nothing here touches
    /// Unity: the owner calls <see cref="Pump"/> from its <c>Update</c>.
    /// </summary>
    internal sealed class MainThreadQueue
    {
        private const int Pending = 0, Running = 1, Abandoned = 2;

        private sealed class Job
        {
            public Action Work;
            public int State;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private readonly Queue<Job> _jobs = new Queue<Job>();

        /// <summary>Runs <paramref name="work"/> on the pumping thread. False when it did not start in time;
        /// work that has already started is always waited for.</summary>
        public bool Run(Action work, int timeoutMs)
        {
            var job = new Job { Work = work };
            lock (_jobs) _jobs.Enqueue(job);
            if (job.Done.WaitOne(timeoutMs)) return true;
            if (Interlocked.CompareExchange(ref job.State, Abandoned, Pending) == Pending) return false;
            job.Done.WaitOne();
            return true;
        }

        public void Pump()
        {
            while (true)
            {
                Job job;
                lock (_jobs)
                {
                    if (_jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }
                if (Interlocked.CompareExchange(ref job.State, Running, Pending) != Pending) continue;
                try { job.Work(); }
                finally { job.Done.Set(); }
            }
        }
    }
}
