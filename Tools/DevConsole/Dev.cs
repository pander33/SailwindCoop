using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.DevConsole
{
    /// <summary>
    /// Helpers available to every snippet as <c>Dev.*</c>. Reflection reaches private members, so a
    /// snippet can read what the decompile shows without a rebuild.
    /// </summary>
    public static class Dev
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        /// <summary>Values kept between snippets (each snippet is its own assembly). Cleared by POST /reset.</summary>
        public static readonly Dictionary<string, object> Vars = new Dictionary<string, object>();

        /// <summary>Adds a line to the "out" field of the response.</summary>
        public static void Print(object value) => SnippetOutput.Write(value == null ? "null" : value.ToString());

        public static T One<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectOfType<T>();
        public static T[] All<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectsOfType<T>();
        /// <summary>Like <see cref="All{T}"/> but also inactive objects (and prefabs/assets).</summary>
        public static T[] AllLoaded<T>() where T : UnityEngine.Object => Resources.FindObjectsOfTypeAll<T>();

        /// <summary>Finds a type by short or full name in every loaded assembly.</summary>
        public static Type Type(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var type in types)
                    if (type != null && (type.FullName == name || type.Name == name)) return type;
            }
            return null;
        }

        /// <summary>Reads a field or property by name, private or not. Pass a <see cref="System.Type"/>
        /// as <paramref name="target"/> for a static member.</summary>
        public static object Get(object target, string name)
        {
            for (Type type = TypeOf(target); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Any);
                if (field != null) return field.GetValue(field.IsStatic ? null : target);
                var property = type.GetProperty(name, Any);
                if (property != null) return property.GetValue(property.GetGetMethod(true).IsStatic ? null : target, null);
            }
            throw new MissingMemberException(TypeOf(target).Name, name);
        }

        public static void Set(object target, string name, object value)
        {
            for (Type type = TypeOf(target); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Any);
                if (field != null) { field.SetValue(field.IsStatic ? null : target, value); return; }
                var property = type.GetProperty(name, Any);
                if (property != null && property.GetSetMethod(true) != null)
                { property.SetValue(property.GetSetMethod(true).IsStatic ? null : target, value, null); return; }
            }
            throw new MissingMemberException(TypeOf(target).Name, name);
        }

        public static object Call(object target, string name, params object[] args)
        {
            for (Type type = TypeOf(target); type != null; type = type.BaseType)
                foreach (var method in type.GetMethods(Any | BindingFlags.DeclaredOnly))
                    if (method.Name == name && method.GetParameters().Length == (args?.Length ?? 0))
                        return method.Invoke(method.IsStatic ? null : target, args);
            throw new MissingMethodException(TypeOf(target).Name, name);
        }

        /// <summary>Every instance field of the object, inherited private ones included.</summary>
        public static Dictionary<string, object> Dump(object target)
        {
            var fields = new Dictionary<string, object>();
            for (Type type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (fields.ContainsKey(field.Name)) continue;
                    try { fields[field.Name] = field.GetValue(target); }
                    catch (Exception e) { fields[field.Name] = "<" + e.Message + ">"; }
                }
            return fields;
        }

        public static string Path(Component component) => component != null ? Path(component.transform) : null;
        public static string Path(GameObject gameObject) => gameObject != null ? Path(gameObject.transform) : null;
        public static string Path(Transform transform)
        {
            if (transform == null) return null;
            string path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        /// <summary>One row per ShipItem with a save identity: what the save and the item caches see.</summary>
        public static List<Dictionary<string, object>> Items()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var item in All<ShipItem>())
            {
                var saveable = item.GetComponent<SaveablePrefab>();
                if (saveable == null) continue;
                var collider = item.GetComponent<Collider>();
                rows.Add(new Dictionary<string, object>
                {
                    { "name", item.name },
                    { "instanceId", saveable.instanceId },
                    { "prefabIndex", saveable.prefabIndex },
                    { "parentObject", saveable.GetParentObject() },
                    { "parent", item.transform.parent != null ? item.transform.parent.name : null },
                    { "sold", item.sold },
                    { "held", item.held != null },
                    { "collider", collider != null && collider.enabled },
                    { "position", item.transform.position },
                });
            }
            return rows;
        }

        private static Type TypeOf(object target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            return target as Type ?? target.GetType();
        }

        internal static string Describe(object value)
        {
            if (value is UnityEngine.Object unityObject)
            {
                if (unityObject == null) return "<destroyed " + value.GetType().Name + ">";
                var transform = value is Component c ? c.transform : value is GameObject g ? g.transform : null;
                return value.GetType().Name + " '" + unityObject.name + "'" + (transform != null ? " " + Path(transform) : "");
            }
            if (value is Vector3 v3) return "(" + v3.x.ToString("F3") + ", " + v3.y.ToString("F3") + ", " + v3.z.ToString("F3") + ")";
            if (value is Vector2 v2) return "(" + v2.x.ToString("F3") + ", " + v2.y.ToString("F3") + ")";
            if (value is Quaternion q) return "euler" + Describe(q.eulerAngles);
            return null;
        }
    }
}
