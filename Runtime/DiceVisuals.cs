using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// The look of the dice table. Everything here is generated in code: the face atlas of the dice, the
	// meshes, the cloth, the slate and fallback wood/leather. DiceGameMaterials can replace surface
	// materials with the game's own. Generated assets are shared by every table, so five tables cost one
	// set of textures and one set of meshes. Nothing is ever destroyed here: a texture or a mesh that is
	// not a scene object lives as long as the process, like the borrowed font.
	internal static class DiceVisuals
	{
		// The atlas is one row of square tiles: six faces, then a plain tile for the bevels.
		private const int TilePixels = 128, Tiles = 8, Ivory = 6;
		private const int ClothPixels = 256, WoodPixels = 256, SlatePixels = 128, LeatherPixels = 256;
		public const float DieSize = .04f, DieBevel = .0022f;
		// A cup that stands beside the tray: its rim clears the tray's rail by a millimetre and a half.
		public const float CupHeight = .11f, CupBase = .030f, CupTop = .036f, CupWall = .005f, CupFloor = .008f;
		private const int CupSegments = 24;
		private static Texture2D atlas, cloth, slate, wood, leather;
		private static Mesh die, cup, cupBands;
		private static Font font;
		private static Material fontMaterial;
		private static float fontRetryAt;

		// ---- textures -------------------------------------------------------------------------

		/// <summary>Six pip faces and a plain tile for the bevels, drawn once.</summary>
		public static Texture2D Atlas()
		{
			if (atlas != null) return atlas;
			var pixels = new Color32[TilePixels * TilePixels * Tiles];
			var centres = new List<Vector2>(6);
			for (int tile = 0; tile < Tiles; tile++)
			{
				Pips(tile < 6 ? tile + 1 : 0, centres);
				for (int y = 0; y < TilePixels; y++)
				{
					for (int x = 0; x < TilePixels; x++)
					{
						float u = (x + .5f) / TilePixels, v = (y + .5f) / TilePixels;
						// Bone is darker towards the edge, where the bevel would take the light off it.
						float edge = Mathf.Max(Mathf.Abs(u - .5f), Mathf.Abs(v - .5f)) * 2f;
						float shade = 1f - .16f * edge * edge * edge;
						float grain = (Hash(x, y) - .5f) * .025f;
						Color color = new Color(.965f + grain, .930f + grain, .855f + grain * .6f);
						for (int i = 0; i < centres.Count; i++)
						{
							float dx = (u - centres[i].x) * TilePixels, dy = (v - centres[i].y) * TilePixels;
							float distance = Mathf.Sqrt(dx * dx + dy * dy);
							// The rim of the hole takes a little light out of the bone: a recessed pip.
							shade *= 1f - .22f * Mathf.Clamp01(1f - Mathf.Abs(distance - 11.4f) / 2.2f);
							float ink = Mathf.Clamp01((11.4f + .8f - distance) / 1.6f);
							color = Color.Lerp(color, new Color(.10f, .085f, .070f), ink);
						}
						pixels[y * (TilePixels * Tiles) + tile * TilePixels + x] = color * shade;
					}
				}
			}
			atlas = MakeTexture("Dice faces", TilePixels * Tiles, TilePixels, pixels);
			return atlas;
		}

		/// <summary>Dark green cloth with a weave and a shadow towards the edges of the tray.</summary>
		public static Texture2D Cloth()
		{
			if (cloth != null) return cloth;
			var pixels = new Color32[ClothPixels * ClothPixels];
			for (int y = 0; y < ClothPixels; y++)
			{
				for (int x = 0; x < ClothPixels; x++)
				{
					float dx = (x + .5f) / ClothPixels * 2f - 1f, dy = (y + .5f) / ClothPixels * 2f - 1f;
					float weave = ((x >> 1) + (y >> 1) & 1) == 0 ? 1f : .915f;
					float vignette = 1f - .38f * Mathf.Clamp01((dx * dx + dy * dy) * .92f - .10f);
					float noise = (Hash(x, y) - .5f) * .05f;
					pixels[y * ClothPixels + x] = new Color(.105f + noise, .215f + noise, .160f + noise) * (weave * vignette);
				}
			}
			cloth = MakeTexture("Dice cloth", ClothPixels, ClothPixels, pixels);
			return cloth;
		}

		/// <summary>The written board: dark slate with a little dust on it.</summary>
		public static Texture2D Slate()
		{
			if (slate != null) return slate;
			var pixels = new Color32[SlatePixels * SlatePixels];
			for (int y = 0; y < SlatePixels; y++)
			{
				for (int x = 0; x < SlatePixels; x++)
				{
					float grain = (Hash(x, y) - .5f) * .055f;
					float dust = Hash(x >> 1, y >> 1) > .985f ? .10f : 0f;
					float fade = .92f + .08f * (y / (float) SlatePixels);
					pixels[y * SlatePixels + x] = new Color(.150f + grain + dust, .158f + grain + dust, .170f + grain + dust) * fade;
				}
			}
			slate = MakeTexture("Dice board", SlatePixels, SlatePixels, pixels);
			return slate;
		}

		/// <summary>Wood for a table that stands where no material of the game could be borrowed.</summary>
		public static Texture2D Wood()
		{
			if (wood != null) return wood;
			var pixels = new Color32[WoodPixels * WoodPixels];
			var dark = new Color(.235f, .135f, .065f);
			var light = new Color(.455f, .285f, .145f);
			for (int y = 0; y < WoodPixels; y++)
			{
				for (int x = 0; x < WoodPixels; x++)
				{
					float u = x / (float) WoodPixels, v = y / (float) WoodPixels;
					float drift = Mathf.PerlinNoise(u * 3.1f + 7f, v * .6f) * 3.2f;
					float line = Mathf.Abs(Mathf.Sin((u * 17f + drift) * Mathf.PI));
					float grain = Mathf.PerlinNoise(u * 33f + drift * 3f, v * 2.4f + 11f);
					float knot = Mathf.PerlinNoise(u * 1.7f + 3f, v * 1.3f + 5f);
					float tone = Mathf.Clamp01(.28f + .48f * grain + .30f * line + .20f * (knot - .5f));
					float plank = (x % 64 < 2) ? .80f : 1f;
					pixels[y * WoodPixels + x] = Color.Lerp(dark, light, tone) * plank;
				}
			}
			wood = MakeTexture("Dice wood", WoodPixels, WoodPixels, pixels);
			return wood;
		}

		/// <summary>Saddle leather with polished brass bands for the dice cup.</summary>
		public static Texture2D Leather()
		{
			if (leather != null) return leather;
			var pixels = new Color32[LeatherPixels * LeatherPixels];
			var dark = new Color(.21f, .13f, .07f);
			var rich = new Color(.36f, .22f, .12f);
			var brass = new Color(.84f, .68f, .28f);
			var darkBrass = new Color(.52f, .40f, .16f);
			for (int y = 0; y < LeatherPixels; y++)
			{
				float v = (y + .5f) / LeatherPixels;
				bool rim = v > .88f && v < .98f, baseBand = v > .04f && v < .14f;
				bool bevel = Mathf.Abs(v - .88f) < .012f || Mathf.Abs(v - .98f) < .012f ||
					Mathf.Abs(v - .04f) < .012f || Mathf.Abs(v - .14f) < .012f;
				for (int x = 0; x < LeatherPixels; x++)
				{
					float u = (x + .5f) / LeatherPixels;
					float noise = (Hash(x, y) - .5f) * .05f;
					Color c;
					if (bevel) c = new Color(.12f, .08f, .04f);
					else if (rim || baseBand)
					{
						float sheen = Mathf.Sin((u * 4f + v * 2f) * Mathf.PI);
						c = Color.Lerp(darkBrass, brass, .5f + .5f * sheen) + new Color(noise * .4f, noise * .4f, noise * .2f);
					}
					else
					{
						float pore = Mathf.PerlinNoise(u * 16f, v * 24f);
						float seam = Mathf.Clamp01(Mathf.Abs(u - .5f) * 50f);
						float tone = Mathf.Clamp01(.38f + .62f * pore + noise);
						c = Color.Lerp(dark, rich, tone * seam);
					}
					pixels[y * LeatherPixels + x] = c;
				}
			}
			leather = MakeTexture("Dice cup leather", LeatherPixels, LeatherPixels, pixels);
			return leather;
		}

		private static Texture2D MakeTexture(string name, int width, int height, Color32[] pixels)
		{
			// Shading multiplies the alpha of a colour with everything else; the table has no
			// transparent part, and in the unlit fallback a semi-transparent die would show through.
			for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, true)
			{
				name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
			};
			texture.SetPixels32(pixels); texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
			return texture;
		}

		/// <summary>The centres of the pips of one face, in the unit square of its tile.</summary>
		private static void Pips(int count, List<Vector2> into)
		{
			into.Clear();
			switch (count)
			{
				case 1: into.Add(new Vector2(.5f, .5f)); break;
				case 2: into.Add(new Vector2(.30f, .30f)); into.Add(new Vector2(.70f, .70f)); break;
				case 3:
					into.Add(new Vector2(.27f, .27f)); into.Add(new Vector2(.5f, .5f)); into.Add(new Vector2(.73f, .73f));
					break;
				case 4:
					into.Add(new Vector2(.29f, .29f)); into.Add(new Vector2(.71f, .29f));
					into.Add(new Vector2(.29f, .71f)); into.Add(new Vector2(.71f, .71f));
					break;
				case 5:
					into.Add(new Vector2(.29f, .29f)); into.Add(new Vector2(.71f, .29f)); into.Add(new Vector2(.5f, .5f));
					into.Add(new Vector2(.29f, .71f)); into.Add(new Vector2(.71f, .71f));
					break;
				case 6:
					for (int i = 0; i < 3; i++) { into.Add(new Vector2(.29f, .26f + i * .24f)); into.Add(new Vector2(.71f, .26f + i * .24f)); }
					break;
			}
		}

		public static float Noise(int x, int y) => Hash(x, y);
		private static float Hash(int x, int y)
		{
			unchecked
			{
				uint h = (uint) (x * 374761393 + y * 668265263);
				h = (h ^ (h >> 13)) * 1274126177u;
				return (h ^ (h >> 16)) / 4294967296f;
			}
		}

		// ---- meshes ---------------------------------------------------------------------------

		/// <summary>A die: the six faces of the atlas on a box with beveled edges and corners.</summary>
		public static Mesh DieMesh()
		{
			if (die != null) return die;
			float half = DieSize * .5f, flat = half - DieBevel;
			var builder = new Builder();
			var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
			for (int axis = 0; axis < 3; axis++)
			{
				for (int sign = -1; sign <= 1; sign += 2)
				{
					Vector3 normal = axes[axis] * sign, side = axes[(axis + 1) % 3], other = axes[(axis + 2) % 3];
					Vector3 centre = normal * half;
					int tile = Tile(normal);
					builder.Quad(centre - side * flat - other * flat, centre + side * flat - other * flat,
						centre + side * flat + other * flat, centre - side * flat + other * flat, normal,
						Corner(tile, 0f, 0f), Corner(tile, 1f, 0f), Corner(tile, 1f, 1f), Corner(tile, 0f, 1f));
				}
			}
			// The twelve edges between two faces of different axes.
			for (int first = 0; first < 3; first++)
			{
				for (int second = first + 1; second < 3; second++)
				{
					Vector3 along = axes[3 - first - second];
					for (int a = -1; a <= 1; a += 2)
					{
						for (int b = -1; b <= 1; b += 2)
						{
							Vector3 one = axes[first] * a, two = axes[second] * b;
							builder.Quad(one * half + two * flat + along * flat, one * half + two * flat - along * flat,
								two * half + one * flat - along * flat, two * half + one * flat + along * flat,
								(one + two).normalized, Plain(), Plain(), Plain(), Plain());
						}
					}
				}
			}
			// The eight corners, one triangle each.
			for (int x = -1; x <= 1; x += 2)
			{
				for (int y = -1; y <= 1; y += 2)
				{
					for (int z = -1; z <= 1; z += 2)
					{
						builder.Triangle(new Vector3(x * half, y * flat, z * flat), new Vector3(x * flat, y * half, z * flat),
							new Vector3(x * flat, y * flat, z * half), new Vector3(x, y, z).normalized, Plain(), Plain(), Plain());
					}
				}
			}
			die = builder.Bake("Dice bone");
			return die;
		}

		/// <summary>The cup: a lathe of one profile, open at the top and closed at the bottom.</summary>
		public static Mesh CupMesh()
		{
			if (cup != null) return cup;
			// Walked from the middle of the outside bottom, up the outside, over the rim, down the
			// inside and back across the floor: the surface normal is the profile tangent turned once.
			var profile = new[]
			{
				new Vector2(0f, 0f), new Vector2(CupBase, 0f), new Vector2(CupTop, CupHeight),
				new Vector2(CupTop - CupWall, CupHeight), new Vector2(CupBase - CupWall, CupFloor), new Vector2(0f, CupFloor)
			};
			var builder = new Builder();
			for (int segment = 0; segment < profile.Length - 1; segment++)
			{
				Vector2 from = profile[segment], to = profile[segment + 1];
				Vector2 tangent = (to - from).normalized, side = new Vector2(tangent.y, -tangent.x);
				for (int step = 0; step < CupSegments; step++)
				{
					float a0 = step * Mathf.PI * 2f / CupSegments, a1 = (step + 1) * Mathf.PI * 2f / CupSegments;
					Vector3 r0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)), r1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
					Vector3 normal = (r0 + r1).normalized * side.x + Vector3.up * side.y;
					float u0 = step / (float) CupSegments, u1 = (step + 1) / (float) CupSegments;
					Vector3 up0 = Vector3.up * from.y, up1 = Vector3.up * to.y;
					Vector2 uv00 = new Vector2(u0, from.y / CupHeight), uv10 = new Vector2(u1, from.y / CupHeight);
					Vector2 uv01 = new Vector2(u0, to.y / CupHeight), uv11 = new Vector2(u1, to.y / CupHeight);
					if (from.x <= 1e-5f)
						builder.Triangle(up0, r1 * to.x + up1, r0 * to.x + up1, normal, uv00, uv11, uv01);
					else if (to.x <= 1e-5f)
						builder.Triangle(r0 * from.x + up0, r1 * from.x + up0, up1, normal, uv00, uv10, uv11);
					else
						builder.Quad(r0 * from.x + up0, r1 * from.x + up0, r1 * to.x + up1, r0 * to.x + up1, normal,
							uv00, uv10, uv11, uv01);
				}
			}
			cup = builder.Bake("Dice cup");
			return cup;
		}

		public static Mesh CupBands()
		{
			if (cupBands != null) return cupBands;
			var builder = new Builder();
			for (int band = 0; band < 2; band++)
			{
				float bottom = band == 0 ? .005f : CupHeight - .015f, upper = bottom + .01f;
				float rb = Mathf.Lerp(CupBase, CupTop, bottom / CupHeight), rt = Mathf.Lerp(CupBase, CupTop, upper / CupHeight);
				for (int step = 0; step < CupSegments; step++)
				{
					float a = step * Mathf.PI * 2 / CupSegments, b = (step + 1) * Mathf.PI * 2 / CupSegments;
					Vector3 r0 = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)), r1 = new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b));
					Vector3 p0 = r0 * (rb + .001f) + Vector3.up * bottom, p1 = r1 * (rb + .001f) + Vector3.up * bottom;
					Vector3 p2 = r1 * (rt + .001f) + Vector3.up * upper, p3 = r0 * (rt + .001f) + Vector3.up * upper;
					builder.Quad(p0, p1, p2, p3, (r0 + r1).normalized, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
					builder.Quad(p3, p2, p2 - r1 * .001f, p3 - r0 * .001f, Vector3.up, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
					builder.Quad(p0, p1, p1 - r1 * .001f, p0 - r0 * .001f, Vector3.down, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
				}
			}
			cupBands = builder.Bake("Dice cup brass hoops");
			return cupBands;
		}

		/// <summary>Which tile of the atlas a die face looking along this normal shows.</summary>
		private static int Tile(Vector3 normal)
		{
			if (Mathf.Abs(normal.y) > .5f) return normal.y > 0f ? 0 : 5;
			if (Mathf.Abs(normal.z) > .5f) return normal.z > 0f ? 1 : 4;
			return normal.x > 0f ? 2 : 3;
		}

		private static Vector2 Corner(int tile, float u, float v) => new Vector2((tile + u) / Tiles, v);

		/// <summary>A point in the middle of the plain tile: a bevel takes one colour and no pattern.</summary>
		private static Vector2 Plain() => new Vector2((Ivory + .5f) / Tiles, .5f);

		private sealed class Builder
		{
			private readonly List<Vector3> vertices = new List<Vector3>();
			private readonly List<Vector3> normals = new List<Vector3>();
			private readonly List<Vector2> uvs = new List<Vector2>();
			private readonly List<int> triangles = new List<int>();

			public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
			{
				int first = Push(a, normal, ua);
				Push(b, normal, ub); Push(c, normal, uc); Push(d, normal, ud);
				// The order of the corners is decided here, not by the caller: a face that would be
				// wound inside out is reversed.
				if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
				{
					triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
					triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
				}
				else
				{
					triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1);
					triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2);
				}
			}

			public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc)
			{
				int first = Push(a, normal, ua);
				Push(b, normal, ub); Push(c, normal, uc);
				if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
				{
					triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
				}
				else
				{
					triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1);
				}
			}

			private int Push(Vector3 vertex, Vector3 normal, Vector2 uv)
			{
				int index = vertices.Count;
				vertices.Add(vertex); normals.Add(normal); uvs.Add(uv);
				return index;
			}

			public Mesh Bake(string name)
			{
				var mesh = new Mesh { name = name };
				if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
				mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
				mesh.SetTriangles(triangles, 0);
				mesh.RecalculateBounds();
				return mesh;
			}
		}

		// ---- the board's font -----------------------------------------------------------------

		/// <summary>
		/// A TextMesh made in code has no font material. The game's own labels have one that is hidden by
		/// what stands in front of it, so the board borrows the most used pair of font and material.
		/// </summary>
		public static void BoardText(TextMesh mesh, TextAnchor anchor, Color color)
		{
			mesh.anchor = anchor; mesh.color = color; mesh.fontSize = 48; mesh.characterSize = .0045f;
			if (font == null || fontMaterial == null) FindFont();
			if (font != null) mesh.font = font;
			if (fontMaterial != null) mesh.GetComponent<MeshRenderer>().sharedMaterial = fontMaterial;
		}

		private static void FindFont()
		{
			if ((font != null && fontMaterial != null) || Time.unscaledTime < fontRetryAt) return;
			fontRetryAt = Time.unscaledTime + 30;
			try
			{
				var counts = new Dictionary<Material, int>(); int best = 0;
				foreach (TextMesh mesh in Resources.FindObjectsOfTypeAll<TextMesh>())
				{
					var renderer = mesh != null && mesh.font != null ? mesh.GetComponent<MeshRenderer>() : null;
					Material material = renderer != null ? renderer.sharedMaterial : null;
					if (material == null) continue;
					counts.TryGetValue(material, out int count); counts[material] = ++count;
					if (count > best) { best = count; font = mesh.font; fontMaterial = material; }
				}
			}
			catch (Exception e) { Plugin.Logger.LogWarning("[Dice] game font search failed: " + e.Message); }
			if (font != null && fontMaterial != null) return;
			font = Resources.GetBuiltinResource<Font>("Arial.ttf");
			fontMaterial = font != null ? font.material : null;
		}
	}
}
