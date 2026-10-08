using System;
using System.IO;

namespace SailwindCoop.Sync
{
	internal static class CoopIdentity
	{
		// Never throws: an unreadable identity costs the dice history its continuity, not the session.
		public static Guid Load(string directory)
		{
			string path = Path.Combine(directory, "player-identity.txt");
			try
			{
				Directory.CreateDirectory(directory);
				if (File.Exists(path) || File.Exists(path + ".bak"))
					return AtomicSaveFile.Read(path, stream =>
					{
						using (var reader = new StreamReader(stream)) return Guid.Parse(reader.ReadToEnd());
					}, out _);
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[CoopIdentity] player identity could not be read, a new one is used: " + e.Message); }
			Guid identity = Guid.NewGuid();
			try
			{
				AtomicSaveFile.Write(path, stream =>
				{
					using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, true)) writer.Write(identity.ToString("N"));
				});
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[CoopIdentity] player identity could not be saved; it lasts until the game closes: " + e.Message); }
			return identity;
		}
	}
}
