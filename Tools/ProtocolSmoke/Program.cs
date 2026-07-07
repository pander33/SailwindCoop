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

            if (failures.Count == 0)
            {
                Console.WriteLine("Protocol smoke OK: " + messageTypes.Count + " message types, protocol " + Protocol.Version);
                return 0;
            }

            Console.Error.WriteLine("Protocol smoke FAILED:");
            foreach (var failure in failures)
                Console.Error.WriteLine(" - " + failure);
            return 1;
        }
    }
}
