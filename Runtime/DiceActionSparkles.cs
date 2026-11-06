using System;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	// Small world-space glints: depth-tested, silent, and shared by all three action markers.
	internal sealed class DiceActionSparkles : IDisposable
	{
		private readonly Transform[] stars = new Transform[9];
		private readonly Mesh mesh;
		private readonly Material material;

		public DiceActionSparkles(Transform parent, int layer)
		{
			mesh = new Mesh { name = "Dice hint star" };
			var vertices = new Vector3[27];
			var triangles = new int[72];
			var colors = new Color[27];
			for (int plane = 0; plane < 3; plane++)
			{
				int start = plane * 9;
				colors[start] = Color.white;
				for (int point = 0; point < 8; point++)
				{
					float angle = point * Mathf.PI / 4;
					float radius = point % 2 == 0 ? .5f : .11f;
					float x = Mathf.Cos(angle) * radius, y = Mathf.Sin(angle) * radius;
					vertices[start + point + 1] = plane == 0 ? new Vector3(x, y, 0) :
						plane == 1 ? new Vector3(x, 0, y) : new Vector3(0, x, y);
					colors[start + point + 1] = new Color(1, 1, 1, 0);
					int triangle = (plane * 8 + point) * 3;
					triangles[triangle] = start;
					triangles[triangle + 1] = start + point + 1;
					triangles[triangle + 2] = start + (point + 1) % 8 + 1;
				}
			}
			mesh.vertices = vertices; mesh.triangles = triangles; mesh.colors = colors; mesh.RecalculateBounds();
			material = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, .85f, .48f, .8f) };
			for (int i = 0; i < stars.Length; i++)
			{
				var star = new GameObject("Dice action sparkle"); star.layer = layer;
				star.transform.SetParent(parent, false);
				star.AddComponent<MeshFilter>().sharedMesh = mesh;
				var renderer = star.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
				renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				renderer.receiveShadows = false;
				stars[i] = star.transform; star.SetActive(false);
			}
		}

		public void Show(int group, bool enabled, Vector3 center)
		{
			for (int i = 0; i < 3; i++)
			{
				Transform star = stars[group * 3 + i];
				float phase = Mathf.Repeat(Time.unscaledTime - i * .28f - group * .17f, 2.8f);
				bool visible = enabled && phase < .85f;
				if (star.gameObject.activeSelf != visible) star.gameObject.SetActive(visible);
				if (!visible) continue;
				float pulse = Mathf.Sin(phase / .85f * Mathf.PI);
				star.localPosition = center + new Vector3((i - 1) * .043f, .018f + i * .013f + phase * .012f, (i == 1 ? -1 : 1) * .028f);
				star.localRotation = Quaternion.Euler(0, phase * 25f, 12f);
				star.localScale = Vector3.one * (.022f * pulse * pulse);
			}
		}

		public void Dispose()
		{
			foreach (var star in stars) if (star != null) UnityEngine.Object.Destroy(star.gameObject);
			UnityEngine.Object.Destroy(mesh); UnityEngine.Object.Destroy(material);
		}
	}
}
