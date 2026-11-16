using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LiteNetLib.Utils;
using SailwindCoop.Net;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Economy Overhaul 1.2.0's trade offices (<c>World.State.office</c>, an <c>OfficeState</c>), mirrored
    /// from the host (<see cref="OfficeStateSync"/>). The offices are the host's: a client refuses the
    /// actions and skips the office's midnight and its fleet's trades, so its copy only changes through
    /// this mirror. EO redraws offices, clerks and cart prices from the state once a second
    /// (<c>OfficeSites.Poll</c>); a received state also asks it to redraw at once.
    ///
    /// <para>The fields travel by name: arrays of int, float and string. Left out: the trade fleet's
    /// per-ship arrays (<c>fleet*</c>, indexed like the save's <c>playerFleet</c>, which is not
    /// mirrored) and <c>playerLook</c> (the local player's own look while Player Model edits a clerk).</para>
    /// </summary>
    internal static partial class EconomyOverhaulCompat
    {
        private const byte OfficeWireVersion = 1;
        private const byte KindInts = 1, KindFloats = 2, KindStrings = 3;

        private static bool _officeResolved;
        private static FieldInfo _officeField;
        private static Type _officeType;
        private static FieldInfo[] _officeFields;
        private static Dictionary<string, FieldInfo> _officeByName;
        private static MethodInfo _officeRefresh;
        private static int[] _officeHouses;

        internal static bool OfficesOn => ResolveOffice();

        private static bool ResolveOffice()
        {
            if (_officeResolved) return _officeFields != null;
            if (!Present || _state == null) return false;   // _state is found in Apply
            _officeResolved = true;
            try
            {
                _officeType = T("OfficeState");
                var stateType = _state?.FieldType;
                _officeField = stateType != null ? AccessTools.Field(stateType, "office") : null;
                if (_officeType == null || _officeField == null) return false;
                var list = new List<FieldInfo>();
                foreach (var f in _officeType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.Name.StartsWith("fleet", StringComparison.Ordinal) || f.Name == "playerLook") continue;
                    if (f.FieldType == typeof(int[]) || f.FieldType == typeof(float[]) || f.FieldType == typeof(string[])) list.Add(f);
                }
                _officeFields = list.ToArray();
                _officeByName = new Dictionary<string, FieldInfo>();
                foreach (var f in _officeFields) _officeByName[f.Name] = f;
                var office = T("Office");
                _officeRefresh = AccessTools.Method(T("OfficeSites"), "Refresh", new[] { typeof(int) });
                _officeHouses = office != null ? AccessTools.Field(office, "Houses")?.GetValue(null) as int[] : null;
                Plugin.Logger.LogInfo("[EconomyOverhaul] trade offices mirrored: " + _officeFields.Length + " fields");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[EconomyOverhaul] trade offices: " + e.Message);
                _officeFields = null;
                return false;
            }
        }

        /// <summary>Host: empty bytes clear the office mirror. Null means no state could be read.</summary>
        internal static byte[] ReadOffice()
        {
            if (!ResolveOffice()) return null;
            try
            {
                var state = _state.GetValue(null);
                if (state == null) return null;
                var office = _officeField.GetValue(state);
                if (office == null) return Array.Empty<byte>();
                var w = new NetDataWriter();
                w.Put(OfficeWireVersion);
                w.Put((byte)_officeFields.Length);
                foreach (var f in _officeFields)
                {
                    w.Put(f.Name);
                    var value = f.GetValue(office);
                    if (f.FieldType == typeof(int[]))
                    {
                        w.Put(KindInts);
                        var a = (int[])value; w.Put(a != null ? a.Length : -1);
                        if (a != null) foreach (int v in a) w.Put(v);
                    }
                    else if (f.FieldType == typeof(float[]))
                    {
                        w.Put(KindFloats);
                        var a = (float[])value; w.Put(a != null ? a.Length : -1);
                        if (a != null) foreach (float v in a) w.Put(v);
                    }
                    else
                    {
                        w.Put(KindStrings);
                        var a = (string[])value; w.Put(a != null ? a.Length : -1);
                        if (a != null) foreach (string v in a) { w.Put(v != null); if (v != null) w.Put(v); }
                    }
                }
                return w.CopyData();
            }
            catch (Exception e) { Fault("read offices", e); return null; }
        }

        /// <summary>Client: the host's office state over the local copy, then the offices redrawn.</summary>
        internal static void WriteOffice(byte[] data)
        {
            if (data == null || !ResolveOffice()) return;
            try
            {
                var state = _state.GetValue(null);
                if (state == null) return;
                if (data.Length == 0)
                {
                    if (_officeField.GetValue(state) == null) return;
                    _officeField.SetValue(state, null);
                    AccessTools.Method(T("OfficeSites"), "Clear")?.Invoke(null, null);
                    AccessTools.Method(T("OfficeCarts"), "Restore")?.Invoke(null, null);
                    return;
                }
                var r = new NetDataReader(data);
                if (r.GetByte() != OfficeWireVersion) return;
                int count = r.GetByte();
                // Read whole before touching the state: a malformed packet leaves the copy as it was.
                var values = new List<KeyValuePair<FieldInfo, object>>(count);
                for (int i = 0; i < count; i++)
                {
                    string name = r.GetString();
                    byte kind = r.GetByte();
                    int length = r.GetInt();
                    if (length < -1 || length > r.AvailableBytes) throw new FormatException("office field " + name + " length " + length);
                    object value = null;
                    if (kind == KindInts)
                    {
                        if (length >= 0) { var a = new int[length]; for (int k = 0; k < length; k++) a[k] = r.GetInt(); value = a; }
                    }
                    else if (kind == KindFloats)
                    {
                        if (length >= 0) { var a = new float[length]; for (int k = 0; k < length; k++) a[k] = r.GetFloat(); value = a; }
                    }
                    else if (kind == KindStrings)
                    {
                        if (length >= 0) { var a = new string[length]; for (int k = 0; k < length; k++) a[k] = r.GetBool() ? r.GetString() : null; value = a; }
                    }
                    else throw new FormatException("office field " + name + " kind " + kind);
                    // A field this EO build does not have, or has with another type, is skipped.
                    if (_officeByName.TryGetValue(name, out var field) && (value == null || field.FieldType == value.GetType()))
                        values.Add(new KeyValuePair<FieldInfo, object>(field, value));
                }
                var office = _officeField.GetValue(state);
                if (office == null) { office = Activator.CreateInstance(_officeType); _officeField.SetValue(state, office); }
                foreach (var pair in values) pair.Key.SetValue(office, pair.Value);
                if (_officeRefresh != null && _officeHouses != null)
                    foreach (int house in _officeHouses) _officeRefresh.Invoke(null, new object[] { house });
            }
            catch (Exception e) { Fault("write offices", e); }
        }
    }
}
