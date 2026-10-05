using System;
using System.Collections.Generic;
using System.Reflection;

namespace SailwindCoop.Runtime
{
    /// <summary>Required signatures and their installation/coverage results; no game calls.</summary>
    internal sealed class PatchHookCatalog
    {
        private enum Result { Ready, Missing, Failed, Pending }
        private sealed class Entry
        {
            public Result Result;
            public string Reason;
        }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly HashSet<MethodInfo> _installed = new HashSet<MethodInfo>();

        internal int Total => _entries.Count;
        internal int Ready
        {
            get { int count = 0; foreach (var e in _entries.Values) if (e.Result == Result.Ready) count++; return count; }
        }
        internal int Installed => _installed.Count;

        internal void InspectType(Type type, string name)
        {
            if (!_entries.ContainsKey(name))
                _entries.Add(name, new Entry { Result = type == null ? Result.Missing : Result.Ready,
                    Reason = type == null ? "required type absent" : null });
        }

        internal void InspectNames(Type type, string typeName, string method, string[] parameterNames, string pending = null)
        {
            string signature = typeName + "." + method + "(" + string.Join(",", parameterNames) + ")";
            if (_entries.ContainsKey(signature)) return;
            var entry = new Entry();
            _entries.Add(signature, entry);
            try
            {
                MethodInfo target = null;
                if (type != null)
                    foreach (var candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (candidate.Name != method || candidate.IsAbstract) continue;
                        var parameters = candidate.GetParameters();
                        if (parameters.Length != parameterNames.Length) continue;
                        bool matches = true;
                        for (int i = 0; i < parameters.Length; i++)
                            if (parameters[i].ParameterType.Name != parameterNames[i]) matches = false;
                        if (matches) { target = candidate; break; }
                    }
                entry.Result = target == null ? Result.Missing : string.IsNullOrEmpty(pending) ? Result.Ready : Result.Pending;
                entry.Reason = target == null ? "required declared signature absent" : pending;
            }
            catch (Exception error)
            {
                entry.Result = Result.Failed;
                entry.Reason = error.GetType().Name + ": " + error.Message;
            }
        }

        internal static string Signature(string type, string method, Type[] args)
        {
            var names = new string[args.Length];
            for (int i = 0; i < args.Length; i++) names[i] = args[i].Name;
            return type + "." + method + "(" + string.Join(",", names) + ")";
        }

        internal bool Install(Type type, string method, Type[] args, Action<MethodInfo> install)
            => Resolve(type, type == null ? "<missing type>" : type.Name, method, args, install, null);

        internal bool Inspect(Type type, string typeName, string method, Type[] args, string pending)
            => Resolve(type, typeName, method, args, null, pending);

        private bool Resolve(Type type, string typeName, string method, Type[] args,
            Action<MethodInfo> install, string pending)
        {
            string signature = Signature(typeName, method, args);
            if (_entries.TryGetValue(signature, out var previous)) return previous.Result == Result.Ready;
            var entry = new Entry();
            _entries.Add(signature, entry);
            try
            {
                var target = type?.GetMethod(method, BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, args, null);
                if (target == null || target.IsAbstract)
                {
                    entry.Result = Result.Missing;
                    entry.Reason = "required declared signature absent";
                    return false;
                }
                if (install != null && !_installed.Contains(target))
                {
                    install(target);
                    _installed.Add(target);
                }
                entry.Result = string.IsNullOrEmpty(pending) ? Result.Ready : Result.Pending;
                entry.Reason = pending;
                return entry.Result == Result.Ready;
            }
            catch (Exception error)
            {
                entry.Result = Result.Failed;
                entry.Reason = error.GetType().Name + ": " + error.Message;
                return false;
            }
        }

        internal string Detail
        {
            get
            {
                var problems = new List<string>();
                foreach (var kv in _entries)
                    if (kv.Value.Result != Result.Ready)
                        problems.Add(kv.Value.Result + " " + kv.Key + ": " + kv.Value.Reason);
                return "ready=" + Ready + "/" + Total + ", installed=" + Installed +
                    (problems.Count == 0 ? "" : ", " + string.Join("; ", problems.ToArray()));
            }
        }
    }
}
