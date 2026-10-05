using System;
using System.Collections.Generic;
using System.Reflection;

namespace SailwindCoop.Sync
{
    internal static partial class ItemComponents
    {
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> Fields = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        internal static FieldInfo Field(Type type, string name)
        {
            // Read/Set are used on per-frame paths; the lookup must not build a key string each time.
            if (!Fields.TryGetValue(type, out var byName)) Fields[type] = byName = new Dictionary<string, FieldInfo>();
            if (byName.TryGetValue(name, out var cached)) return cached;
            FieldInfo field = null;
            for (var t = type; t != null && field == null; t = t.BaseType)
                field = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field == null) throw new MissingFieldException(type.Name, name);
            return byName[name] = field;
        }
        internal static T Read<T>(object value, string name) => (T)Field(value.GetType(), name).GetValue(value);
        internal static void Set(object value, string name, object state) => Field(value.GetType(), name).SetValue(value, state);
    }
}
