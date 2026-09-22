using System;
using System.IO;
using System.Text;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	public static class CoopReport
	{
		public static string Write(CoopBehaviour coop)
		{
			try
			{
				string dir = Path.Combine(BepInEx.Paths.GameRootPath, "debug");
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "coop-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
				File.WriteAllText(path, Build(coop), Encoding.UTF8);
				return "Report written: " + path;
			}
			catch (Exception e)
			{
				Plugin.Logger.LogError("[CoopReport] Export failed: " + e);
				return "Report failed: " + e.Message;
			}
		}

		private static string Build(CoopBehaviour coop)
		{
			var sb = new StringBuilder(4096);
			CoopNet net = coop == null ? null : coop.Net;
			Put(sb, "generated.utc", DateTime.UtcNow.ToString("O"));
			Put(sb, "mod.version", Plugin.Version);
			Put(sb, "protocol.version", Protocol.Version);
			Put(sb, "unity.version", Application.unityVersion);
			Put(sb, "game.playing", GameState.playing);
			if (net != null)
			{
				Put(sb, "net.role", net.Role);
				Put(sb, "net.state", net.State);
				Put(sb, "net.peers", net.PeerCount);
				Put(sb, "net.acceptingClients", net.AcceptingClients);
				Put(sb, "net.rtt.ms", net.Clock.HasSample ? net.Clock.RttMs.ToString("0") : "unknown");
				Put(sb, "net.clockOffset.ms", net.Clock.HasSample ? net.Clock.OffsetMs.ToString("0") : "unknown");
				Put(sb, "net.lastError", net.LastError);
				Put(sb, "net.lastDisconnect", net.LastDisconnectReason);
				SessionMemberInfo[] roster = net.RosterSnapshot;
				Put(sb, "roster.count", roster.Length);
				for (int i = 0; i < roster.Length; i++)
				{
					SessionMemberInfo m = roster[i];
					Put(sb, "roster." + i, "id=" + m.NetId + "; name=" + m.Name + "; host=" + m.IsHost +
						"; state=" + m.State + "; ping=" + m.PingMs + "; boat=" + m.BoatIndex);
				}
			}
			if (coop != null)
			{
				Put(sb, "save.receiving", coop.SaveTransfer != null && coop.SaveTransfer.Receiving);
				Put(sb, "save.progress", coop.SaveTransfer == null ? "unknown" : coop.SaveTransfer.Progress.ToString("0.000"));
				Put(sb, "joinPause.active", coop.Pause != null && coop.Pause.Active);
				Put(sb, "joinPause.pending", coop.Pause == null ? 0 : coop.Pause.PendingCount);
			}
			Put(sb, "patchHealth", PatchHealth.Summary);
			Put(sb, "notice", CoopBehaviour.LastNotice);
			sb.AppendLine();
			sb.AppendLine("[recent diagnostics]");
			string[] recent = Plugin.Logger.RecentSnapshot();
			for (int i = 0; i < recent.Length; i++) sb.AppendLine(recent[i]);
			return sb.ToString();
		}

		private static void Put(StringBuilder sb, string key, object value)
		{
			sb.Append(key).Append(" = ").Append(value ?? "").AppendLine();
		}
	}
}
