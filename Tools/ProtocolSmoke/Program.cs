using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using SailwindCoop.Net;

namespace ProtocolSmoke
{
    internal static class Program
    {
        private static int Main()
        {
            var failures = new List<string>();
            var messageTypes = typeof(INetMessage).Assembly.GetTypes()
                .Where(t => typeof(INetMessage).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var type in messageTypes)
            {
                try
                {
                    var msg = (INetMessage)Activator.CreateInstance(type);
                    var writer = Protocol.Write(msg);
                    var reader = new NetDataReader(writer.Data, 0, writer.Length);
                    var wireType = (MsgType)reader.GetByte();
                    if (wireType != msg.Type)
                    {
                        failures.Add(type.Name + ": wrote " + wireType + ", expected " + msg.Type);
                        continue;
                    }

                    var clone = Protocol.ReadBody(wireType, reader);
                    if (clone == null)
                    {
                        failures.Add(type.Name + ": Protocol.ReadBody returned null for " + wireType);
                        continue;
                    }
                    if (clone.GetType() != type)
                        failures.Add(type.Name + ": decoded as " + clone.GetType().Name);
                }
                catch (Exception e)
                {
                    failures.Add(type.Name + ": " + e.GetType().Name + " " + e.Message);
                }
            }

            foreach (MsgType type in Enum.GetValues(typeof(MsgType)))
            {
                if (type == MsgType.InteractRequest)
                    continue; // reserved slot; intentionally no message body yet
                if (!messageTypes.Any(t => ((INetMessage)Activator.CreateInstance(t)).Type == type))
                    failures.Add("MsgType " + type + " has no INetMessage implementation");
            }

            int populated = 0, truncated = 0;
            foreach (var type in messageTypes.Where(t => t.GetField("LayoutHash") != null))
            {
                foreach (ushort boat in new ushort[] { 1, 511, 65534 })
                {
                    try
                    {
                        var msg = (INetMessage)Activator.CreateInstance(type);
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                            field.SetValue(msg, Sample(field.FieldType));
                        type.GetField("BoatIndex").SetValue(msg, boat);
                        type.GetField("LayoutHash").SetValue(msg, 0xFEDCBA98u);
                        if (msg is MooringStateMsg mooring) mooring.StateAvailable = boat != 511;
                        var writer = Protocol.Write(msg);
                        var reader = new NetDataReader(writer.Data, 1, writer.Length);
                        var clone = Protocol.ReadBody(msg.Type, reader);
                        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                            if (!Equal(field.GetValue(msg), field.GetValue(clone)))
                                throw new Exception("round-trip mismatch: " + field.Name);
                        if (reader.AvailableBytes != 0) throw new Exception("unconsumed payload");
                        populated++;
                        for (int length = 0; length < writer.Length - 1; length++)
                        {
                            bool rejected = false;
                            byte[] cut = new byte[length];
                            Buffer.BlockCopy(writer.Data, 1, cut, 0, length);
                            try { rejected = Protocol.ReadBody(msg.Type, new NetDataReader(cut)) == null; }
                            catch { rejected = true; }
                            if (!rejected) throw new Exception("accepted truncated body length " + length);
                            truncated++;
                            if (Protocol.ReadBody(msg.Type, new NetDataReader(writer.Data, 1, length + 1)) != null)
                                throw new Exception("accepted truncated pooled body length " + length);
                            truncated++;
                        }
                    }
                    catch (Exception e) { failures.Add(type.Name + " boat=" + boat + ": " + e.Message); }
                }
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("Protocol smoke OK: " + messageTypes.Count + " message types, " + populated +
                    " populated round-trips, " + truncated + " truncated packets rejected, protocol " + Protocol.Version);
                return 0;
            }

            Console.Error.WriteLine("Protocol smoke FAILED:");
            foreach (var failure in failures)
                Console.Error.WriteLine(" - " + failure);
            return 1;
        }

        private static object Sample(Type type)
        {
            if (type == typeof(ushort)) return (ushort)65534;
            if (type == typeof(uint)) return 0xFEDCBA98u;
            if (type == typeof(long)) return 9876543210L;
            if (type == typeof(float)) return 0.375f;
            if (type == typeof(bool)) return true;
            if (type == typeof(UnityEngine.Vector3)) return new UnityEngine.Vector3(12.5f, -8.25f, 100.75f);
            if (type == typeof(UnityEngine.Quaternion)) return new UnityEngine.Quaternion(0f, 0.6f, 0f, 0.8f);
            if (type.IsEnum) { var values = Enum.GetValues(type); return values.GetValue(values.Length - 1); }
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType(), 2);
                for (int i = 0; i < 2; i++) array.SetValue(Sample(type.GetElementType()), i);
                return array;
            }
            throw new Exception("no sample for " + type.Name);
        }

        private static bool Equal(object a, object b)
        {
            if (a is Array aa && b is Array bb)
            {
                if (aa.Length != bb.Length) return false;
                for (int i = 0; i < aa.Length; i++) if (!Equal(aa.GetValue(i), bb.GetValue(i))) return false;
                return true;
            }
            return object.Equals(a, b);
        }
    }
}
