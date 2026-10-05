using System;
using System.Collections.Generic;

namespace SailwindCoop.Sync
{
    internal enum ItemResultSection { Identity, CreatedState, Changed, Hull, Rainbow, Weather, Consumed, RejectedCreation }
    internal enum ItemApplyStatus { Applied, Pending, Fault }
    internal sealed class ItemMembership<T> where T : class
    {
        private readonly HashSet<T> unfinished = new HashSet<T>();
        internal ItemApplyStatus Apply(T item, Func<bool> complete, Func<bool> ready, Action apply, Action<Exception> fault)
        {
            try
            {
                if (!unfinished.Contains(item) && complete()) return ItemApplyStatus.Applied;
                if (!ready()) return ItemApplyStatus.Pending;
                unfinished.Add(item);
                apply();
                if (!complete()) return ItemApplyStatus.Pending;
                unfinished.Remove(item);
                return ItemApplyStatus.Applied;
            }
            catch (Exception error) { fault(error); return ItemApplyStatus.Fault; }
        }
        internal void Clear() => unfinished.Clear();
    }

    internal static class ItemTargetReadiness
    {
        internal static bool Binding(bool required, bool component, bool addressed, bool target, bool slot)
            => !required || (component && (!addressed || (target && slot)));
    }
    internal static class ItemOperationWaiting
    {
        internal static void RemoveActor<T>(Dictionary<ulong, T> waiting, uint actor)
        {
            foreach (var key in new List<ulong>(waiting.Keys))
                if ((uint)(key >> 32) == actor) waiting.Remove(key);
        }
    }
    internal sealed class ItemResultProgress
    {
        private readonly HashSet<ulong> done = new HashSet<ulong>();
        internal bool Applying { get; private set; }
        internal bool Completed { get; private set; }
        internal bool Begin()
        {
            if (Applying || Completed) return false;
            Applying = true;
            return true;
        }
        internal bool TryApply(ItemResultSection section, int index, Func<bool> apply, Action<Exception> fault)
        {
            ulong key = ((ulong)section << 32) | (uint)index;
            if (done.Contains(key)) return true;
            try
            {
                if (!apply()) return false;
                done.Add(key);
                return true;
            }
            catch (Exception error) { fault(error); return false; }
        }
        internal void End(bool complete) { Applying = false; Completed = complete; }
    }
}
