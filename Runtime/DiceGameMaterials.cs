using System;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// Borrow only simple surface materials, not UV atlases painted for a particular game prop.
	// The originals belong to the game; every table gets disposable copies.
	internal static class DiceGameMaterials
	{
		private static Material wood, cloth, leather;
		private static float retryAt;

		private static void Find()
		{
			if (Time.unscaledTime < retryAt) return;
			retryAt = Time.unscaledTime + 30;
			int woodScore = 0, clothScore = 0, leatherScore = 0;
			try
			{
				foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
				{
					if (material == null || material.name.StartsWith("Dice ", StringComparison.Ordinal) ||
						material.renderQueue > 2450 || !material.HasProperty("_Color") ||
						!material.HasProperty("_MainTex") || material.mainTexture == null ||
						material.IsKeywordEnabled("_EMISSION")) continue;
					string name = material.name.ToLowerInvariant();
					string texture = material.mainTexture.name.ToLowerInvariant();
					string description = name + " " + texture;
					if (description.Contains("atlas") || description.Contains("paint") || description.Contains("flag") ||
						description.Contains("npc") || description.Contains("character") || description.Contains("crate") ||
						description.Contains("barrel") || description.Contains("banner")) continue;
					int w = SurfaceScore(name, texture, "wood"), c = SurfaceScore(name, texture, "cloth"),
						l = SurfaceScore(name, texture, "leather");
					if (w > woodScore) { woodScore = w; wood = material; }
					if (c > clothScore) { clothScore = c; cloth = material; }
					if (l > leatherScore) { leatherScore = l; leather = material; }
				}
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[Dice] surface materials: " + e.Message); }
		}

		private static int SurfaceScore(string name, string texture, string surface)
		{
			if (name == surface) return 100;
			if (name.StartsWith(surface, StringComparison.Ordinal) && name.Length == surface.Length + 1 &&
				char.IsDigit(name[name.Length - 1])) return 90;
			if (name.StartsWith(surface + "_", StringComparison.Ordinal) || name.StartsWith(surface + " ", StringComparison.Ordinal)) return 80;
			if (texture == surface) return 70;
			if (texture.StartsWith(surface + "_", StringComparison.Ordinal)) return 60;
			// Plain sail cloth is usable as felt; coloured/painted sails were filtered above.
			if (surface == "cloth" && (name == "sail cloth" || texture == "sail_cloth")) return 50;
			return 0;
		}

		public static Material Copy(string surface, Color tint)
		{
			if (!Plugin.Cfg.DiceLitMaterials.Value) return null;
			Find();
			Material source = surface == "wood" ? wood : surface == "cloth" ? cloth : leather;
			if (source == null) return null;
			var copy = new Material(source) { name = "Dice " + surface + " (" + source.name + ")" };
			copy.color = source.color * tint;
			Plugin.Logger.LogInfo("[Dice] " + surface + ": game material '" + source.name + "', texture '" + source.mainTexture.name + "'");
			return copy;
		}
	}
}
