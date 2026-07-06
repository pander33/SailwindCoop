using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace SailwindCoop.Avatar
{
	/// <summary>
	/// Discovers available avatar bundles in the mod directory and persists the
	/// current local player's selection. The selection is the bundle file name
	/// (e.g. "avatar1.bundle"), so it survives across machines only if the
	/// same file is present in the peer's mod directory; otherwise the loader
	/// falls back to <c>avatar.bundle</c> or a primitive avatar.
	/// </summary>
	public static class AvatarCatalog
	{
		public const string DefaultBundleFile = "avatar.bundle";
		private const string SelectionFile = "selected_avatar.txt";

		public sealed class Entry
		{
			public string FileName;        // e.g. "avatar1.bundle", or an "npc:..." skin key
			public string DisplayName;     // human-friendly label
			public string FullPath;        // absolute path on disk (empty for NPC skins)
			public bool Exists;            // file present locally / NPC template captured
			public bool IsNpc;             // true = NPC skin key, not a bundle file
		}

		/// <summary>All known bundle slots in scan order. Slots may or may not exist on disk.</summary>
		public static IReadOnlyList<Entry> Entries => _entries;
		public static string CurrentSelection { get; private set; } = DefaultBundleFile;
		public static event Action<string> OnSelectionChanged;

		private static List<Entry> _entries = new List<Entry>();
		private static string _modDir = "";

		public static void Initialize()
		{
			try
			{
				_modDir = Path.GetDirectoryName(Plugin.Instance.Info.Location) ?? "";
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[AvatarCatalog] Не удалось определить папку мода: " + e.Message);
				_modDir = "";
			}
			Scan();
			LoadSelection();
		}

		/// <summary>Re-scan the mod directory and rebuild the slot list. Adds the default slot if absent.</summary>
		public static void Scan()
		{
			var found = new List<Entry>();
			try
			{
				if (!string.IsNullOrEmpty(_modDir) && Directory.Exists(_modDir))
				{
					foreach (var path in Directory.GetFiles(_modDir, "avatar*.bundle", SearchOption.TopDirectoryOnly))
					{
						var fi = new FileInfo(path);
						found.Add(new Entry
						{
							FileName = fi.Name,
							DisplayName = MakeDisplayName(fi.Name),
							FullPath = fi.FullName,
							Exists = true,
						});
					}
				}
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[AvatarCatalog] Ошибка сканирования папки: " + e.Message);
			}

			found = found
				.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
				.ToList();

			// Ensure the default slot is always listed even if no file is on disk,
			// so the UI can show the "default" option.
			if (!found.Any(e => string.Equals(e.FileName, DefaultBundleFile, StringComparison.OrdinalIgnoreCase)))
			{
				found.Insert(0, new Entry
				{
					FileName = DefaultBundleFile,
					DisplayName = MakeDisplayName(DefaultBundleFile),
					FullPath = string.IsNullOrEmpty(_modDir) ? "" : Path.Combine(_modDir, DefaultBundleFile),
					Exists = !string.IsNullOrEmpty(_modDir) && File.Exists(Path.Combine(_modDir, DefaultBundleFile)),
				});
			}

			// NPC skins from the game world (list only grows during the session).
			try
			{
				NpcSkinLibrary.Scan();
				foreach (var s in NpcSkinLibrary.Skins)
				{
					found.Add(new Entry
					{
						FileName = s.Key,
						DisplayName = s.DisplayName,
						FullPath = "",
						Exists = NpcSkinLibrary.CanBuild,
						IsNpc = true,
					});
				}
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[AvatarCatalog] Скан NPC-скинов: " + e.Message);
			}

			_entries = found;
			Plugin.Logger?.LogInfo("[AvatarCatalog] Найдено бандлов: " + _entries.Count(e => !e.IsNpc) +
				", NPC-скинов: " + _entries.Count(e => e.IsNpc));
		}

		/// <summary>Human-friendly label for any selection string (bundle file or NPC key).</summary>
		public static string DisplayNameFor(string selection)
		{
			if (string.IsNullOrEmpty(selection)) return DefaultBundleFile;
			var e = _entries.FirstOrDefault(x => string.Equals(x.FileName, selection, StringComparison.OrdinalIgnoreCase));
			if (e != null) return e.IsNpc ? e.DisplayName : e.FileName;
			return NpcSkinLibrary.IsNpcKey(selection) ? "NPC (незнакомый скин)" : selection;
		}

		/// <summary>Returns the full path for a given file name, or empty string if not on disk.</summary>
		public static string ResolvePath(string fileName)
		{
			if (string.IsNullOrEmpty(_modDir) || string.IsNullOrEmpty(fileName)) return "";
			// NPC-ключ — не файл; двоеточие в Path.Combine кидает ArgumentException на Mono.
			if (NpcSkinLibrary.IsNpcKey(fileName)) return "";
			string path = Path.Combine(_modDir, fileName);
			return File.Exists(path) ? path : "";
		}

		/// <summary>True if the given selection can be built locally (bundle on disk / NPC template captured).</summary>
		public static bool IsAvailable(string fileName)
		{
			if (string.IsNullOrEmpty(fileName)) return false;
			if (NpcSkinLibrary.IsNpcKey(fileName)) return NpcSkinLibrary.CanBuild;
			return !string.IsNullOrEmpty(ResolvePath(fileName));
		}

		/// <summary>Change the local selection and persist it. Pass empty/null to reset to default.</summary>
		public static void SetSelection(string fileName)
		{
			string normalized = string.IsNullOrWhiteSpace(fileName) ? DefaultBundleFile : fileName.Trim();
			if (string.Equals(CurrentSelection, normalized, StringComparison.OrdinalIgnoreCase))
			{
				SaveSelection();
				return;
			}
			CurrentSelection = normalized;
			SaveSelection();
			Plugin.Logger?.LogInfo("[AvatarCatalog] Выбор модели: " + CurrentSelection);
			try { OnSelectionChanged?.Invoke(CurrentSelection); }
			catch (Exception e) { Plugin.Logger?.LogWarning("[AvatarCatalog] OnSelectionChanged: " + e.Message); }
		}

		private static void LoadSelection()
		{
			try
			{
				if (string.IsNullOrEmpty(_modDir)) return;
				string path = Path.Combine(_modDir, SelectionFile);
				if (!File.Exists(path)) { CurrentSelection = DefaultBundleFile; return; }
				string raw = File.ReadAllText(path).Trim();
				CurrentSelection = string.IsNullOrEmpty(raw) ? DefaultBundleFile : raw;
				Plugin.Logger?.LogInfo("[AvatarCatalog] Загружен выбор: " + CurrentSelection);
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[AvatarCatalog] Не удалось прочитать выбор: " + e.Message);
				CurrentSelection = DefaultBundleFile;
			}
		}

		private static void SaveSelection()
		{
			try
			{
				if (string.IsNullOrEmpty(_modDir)) return;
				string path = Path.Combine(_modDir, SelectionFile);
				File.WriteAllText(path, CurrentSelection ?? DefaultBundleFile);
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[AvatarCatalog] Не удалось сохранить выбор: " + e.Message);
			}
		}

		private static string MakeDisplayName(string fileName)
		{
			if (string.IsNullOrEmpty(fileName)) return "(unknown)";
			string core = Path.GetFileNameWithoutExtension(fileName);
			if (string.Equals(core, "avatar", StringComparison.OrdinalIgnoreCase)) return "Default (avatar.bundle)";
			if (core.StartsWith("avatar", StringComparison.OrdinalIgnoreCase) && core.Length > 6)
				return core.Substring(6).TrimStart('-', '_', ' ', '.');
			return core;
		}
	}
}
