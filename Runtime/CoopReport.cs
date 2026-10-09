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
				// Two copies of the game on one PC write into the same folder: the role tells them apart.
				string role = coop == null || coop.Net == null ? "none" : coop.Net.Role.ToString().ToLowerInvariant();
				string path = Path.Combine(dir, "coop-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + role + ".txt");
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
			Put(sb, "generated.local", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			Put(sb, "mod.version", Plugin.Version);
			Put(sb, "mod.edition", Plugin.Edition);
			Put(sb, "protocol.version", Protocol.Version);
			Put(sb, "unity.version", Application.unityVersion);
			Put(sb, "application.version", Application.version);
			Put(sb, "logging.enabled", Plugin.Logger.Enabled);
			Put(sb, "game.playing", GameState.playing);
			Section(sb, "plugins", () =>
			{
				foreach (var plugin in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
					Put(sb, "plugin", plugin.Metadata.GUID + " " + plugin.Metadata.Version);
			});
			Section(sb, "player", () =>
			{
				Put(sb, "time.scale", Time.timeScale);
				Put(sb, "time.fixedStep", Time.fixedDeltaTime.ToString("0.0000"));
				Put(sb, "sun.timescale", Sun.sun == null ? "unknown" : Sun.sun.timescale.ToString("G4"));
				Put(sb, "sun.localTime", Sun.sun == null ? "unknown" : Sun.sun.localTime.ToString("0.00"));
				Put(sb, "needs", "sleep=" + PlayerNeeds.sleep.ToString("0.0") + "; sleepDebt=" + PlayerNeeds.sleepDebt.ToString("0.0") +
					"; water=" + PlayerNeeds.water.ToString("0.0") + "; food=" + PlayerNeeds.food.ToString("0.0") +
					"; foodDebt=" + PlayerNeeds.foodDebt.ToString("0.0") + "; vitamins=" + PlayerNeeds.vitamins.ToString("0.0") +
					"; protein=" + PlayerNeeds.protein.ToString("0.0") + "; alcohol=" + PlayerNeeds.alcohol.ToString("0.0"));
				Put(sb, "state", "inBed=" + (GameState.inBed != null) + "; sleeping=" + GameState.sleeping +
					"; recovering=" + GameState.recovering + "; loading=" + GameState.currentlyLoading +
					"; onBoat=" + (GameState.currentBoat != null) + "; cursorMenu=" + GameState.inCursorMenu);
			});
			if (net != null)
			{
				Put(sb, "net.myNetId", net.MyNetId);
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
						"; state=" + m.State + "; ping=" + m.PingMs + "; boat=" + m.BoatIndex + "; mod=" + m.ModVersion);
				}
			}
			if (coop != null)
			{
				Put(sb, "save.receiving", coop.SaveTransfer != null && coop.SaveTransfer.Receiving);
				Put(sb, "save.progress", coop.SaveTransfer == null ? "unknown" : coop.SaveTransfer.Progress.ToString("0.000"));
				Put(sb, "joinPause.active", coop.Pause != null && coop.Pause.Active);
				Put(sb, "joinPause.pending", coop.Pause == null ? 0 : coop.Pause.PendingCount);
				Section(sb, "session", () =>
				{
					Put(sb, "sleep", coop.Sleep == null ? "unknown" : coop.Sleep.SleepText);
					Put(sb, "host.timeScale", coop.Env == null || !coop.Env.HasHostState ? "unknown" : coop.Env.HostTimeScale.ToString("G4"));
					Put(sb, "wallet.shared", coop.Wallet != null && coop.Wallet.Shared);
					Put(sb, "player.boat", coop.Players == null ? "unknown" : coop.Players.LocalBoatIndex.ToString());
				});
			}
			Put(sb, "patchHealth", PatchHealth.Summary);
			Put(sb, "notice", CoopBehaviour.LastNotice);
			sb.AppendLine();
			sb.AppendLine("[recent diagnostics]");
			string[] recent = Plugin.Logger.RecentSnapshot();
			for (int i = 0; i < recent.Length; i++) sb.AppendLine(recent[i]);
			sb.AppendLine();
			sb.AppendLine("[log, last lines, kept whether logging is on or not]");
			string[] trail = Plugin.Logger.TrailSnapshot();
			for (int i = 0; i < trail.Length; i++) sb.AppendLine(trail[i]);
			return sb.ToString();
		}

		/// <summary>A part of the report that reads live game state: if the game is not in a state
		/// to answer, the report says so and goes on.</summary>
		private static void Section(StringBuilder sb, string name, Action write)
		{
			try { write(); }
			catch (Exception e) { Put(sb, name + ".unavailable", e.GetType().Name + ": " + e.Message); }
		}

		private static void Put(StringBuilder sb, string key, object value)
		{
			sb.Append(key).Append(" = ").Append(value ?? "").AppendLine();
		}
	}
}
