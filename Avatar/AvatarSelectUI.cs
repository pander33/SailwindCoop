using UnityEngine;

namespace SailwindCoop.Avatar
{
	/// <summary>
	/// Minimal IMGUI panel for picking the local player's avatar bundle.
	/// Opened/closed from the co-op menu.
	/// The catalog scan itself lives in <see cref="AvatarCatalog"/>.
	/// </summary>
	public sealed class AvatarSelectUI
	{
		private const float PanelWidth = 360f;
		private const float PanelHeight = 280f;
		private const int FontSize = 14;

		public bool Visible;

		private GUIStyle _windowStyle;
		private GUIStyle _headerStyle;
		private GUIStyle _entryStyle;
		private GUIStyle _entrySelectedStyle;
		private GUIStyle _hintStyle;
		private Vector2 _scroll;
		private string _lastDrawnSelection = "";

		public AvatarSelectUI(string currentSelection)
		{
			_lastDrawnSelection = currentSelection;
		}

		public void Draw()
		{
			if (!Visible) return;
			EnsureStyles();

			float w = PanelWidth;
			float h = PanelHeight;
			float x = (Screen.width - w) * 0.5f;
			float y = (Screen.height - h) * 0.5f;
			var rect = new Rect(x, y, w, h);

			GUI.Box(rect, GUIContent.none, _windowStyle);
			GUILayout.BeginArea(new Rect(x + 10f, y + 8f, w - 20f, h - 16f));

			GUILayout.Label("Character Model Selection", _headerStyle);
			GUILayout.Label("Bundles come from the mod folder (avatar*.bundle); NPC skins come from loaded islands (approach an island and reopen the window).", _hintStyle);
			GUILayout.Space(4f);

			var entries = AvatarCatalog.Entries;
			if (entries == null || entries.Count == 0)
			{
				GUILayout.Label("No bundles found.", _hintStyle);
			}
			else
			{
				_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
				string current = AvatarCatalog.CurrentSelection;
				for (int i = 0; i < entries.Count; i++)
				{
					var e = entries[i];
					bool selected = string.Equals(e.FileName, current, System.StringComparison.OrdinalIgnoreCase);
					string title = e.IsNpc ? e.DisplayName : e.FileName;
					var label = (e.Exists ? "● " : "○ ") + title +
								(selected ? "   ✓" : (e.Exists ? "" : (e.IsNpc ? "   (template missing)" : "   (file missing)")));
					var style = selected ? _entrySelectedStyle : _entryStyle;
					if (GUILayout.Button(label, style, GUILayout.ExpandWidth(true)))
					{
						AvatarCatalog.SetSelection(e.FileName);
					}
				}
				GUILayout.EndScrollView();
			}

			GUILayout.FlexibleSpace();
			GUILayout.Label("Current: " + AvatarCatalog.DisplayNameFor(AvatarCatalog.CurrentSelection), _hintStyle);
			GUILayout.Label("Close this window from the co-op menu.", _hintStyle);

			GUILayout.EndArea();
		}

		private void EnsureStyles()
		{
			if (_windowStyle != null) return;
			_windowStyle = new GUIStyle(GUI.skin.box) { normal = { background = MakeBg(new Color(0f, 0f, 0f, 0.85f)) } };
			_headerStyle = new GUIStyle(GUI.skin.label) { fontSize = FontSize + 2, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
			_entryStyle = new GUIStyle(GUI.skin.button) { fontSize = FontSize, alignment = TextAnchor.MiddleLeft };
			_entrySelectedStyle = new GUIStyle(_entryStyle) { normal = { textColor = new Color(0.6f, 1f, 0.6f) } };
			_hintStyle = new GUIStyle(GUI.skin.label) { fontSize = FontSize - 2, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
		}

		private Texture2D MakeBg(Color c)
		{
			var t = new Texture2D(1, 1);
			t.SetPixel(0, 0, c);
			t.Apply();
			return t;
		}
	}
}
