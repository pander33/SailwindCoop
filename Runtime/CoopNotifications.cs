using System.Collections.Generic;
using SailwindCoop.Net;
using UnityEngine;

namespace SailwindCoop.Runtime
{
	/// <summary>Small, bounded queue of transient co-op events. Actionable failures remain in LastNotice.</summary>
	public sealed class CoopNotifications
	{
		private sealed class Entry
		{
			public string Text;
			public float ExpiresAt;
		}

		private const int MaxVisible = 3;
		private const float LifetimeSec = 5f;
		private readonly List<Entry> _entries = new List<Entry>();
		private readonly List<string> _drawTexts = new List<string>();
		private GUIStyle _style;

		public void Add(string text, float lifetimeSec = LifetimeSec)
		{
			if (string.IsNullOrWhiteSpace(text)) return;
			float now = Time.realtimeSinceStartup;
			for (int i = 0; i < _entries.Count; i++)
			{
				if (_entries[i].Text != text) continue;
				_entries[i].ExpiresAt = now + lifetimeSec;
				return;
			}
			while (_entries.Count >= MaxVisible) _entries.RemoveAt(0);
			_entries.Add(new Entry { Text = text.Trim(), ExpiresAt = now + lifetimeSec });
		}

		public void Add(GameplayNoticeMsg msg, CoopNet net)
		{
			if (msg == null) return;
			string actor = msg.ActorNetId == 0 ? "Player" : net.GetPlayerName(msg.ActorNetId);
			string text;
			switch (msg.Kind)
			{
				case GameplayNoticeKind.PlayerJoined: text = actor + " joined"; break;
				case GameplayNoticeKind.PlayerReady: text = actor + " is ready"; break;
				case GameplayNoticeKind.PlayerLeft: text = actor + " left"; break;
				case GameplayNoticeKind.PlayerKicked: text = actor + " was removed by the host"; break;
				case GameplayNoticeKind.SessionOpened: text = "Session opened to new players"; break;
				case GameplayNoticeKind.SessionLocked: text = "Session locked"; break;
				case GameplayNoticeKind.AnchorDropped: text = msg.ActorNetId == 0 ? "Anchor set" : actor + " dropped the anchor"; break;
				case GameplayNoticeKind.AnchorRaised: text = msg.ActorNetId == 0 ? "Anchor raised" : actor + " raised the anchor"; break;
				case GameplayNoticeKind.SleepStarted: text = msg.ActorNetId == 0 ? "The crew fell asleep" : actor + " started sleeping"; break;
				case GameplayNoticeKind.SleepEnded: text = msg.ActorNetId == 0 ? "The crew woke up" : actor + " woke up"; break;
				case GameplayNoticeKind.ItemBought: text = actor + " bought " + msg.Detail; break;
				case GameplayNoticeKind.ItemSold: text = actor + " sold " + msg.Detail; break;
				default: return;
			}
			Add(text);
		}

		public void Draw()
		{
			if (Event.current.type == EventType.Layout)
			{
				float now = Time.realtimeSinceStartup;
				for (int i = _entries.Count - 1; i >= 0; i--)
					if (_entries[i].ExpiresAt <= now) _entries.RemoveAt(i);
				_drawTexts.Clear();
				for (int i = 0; i < _entries.Count; i++)
					_drawTexts.Add(_entries[i].Text);
			}
			if (_drawTexts.Count == 0) return;

			if (_style == null)
			{
				_style = new GUIStyle(GUI.skin.box)
				{
					fontSize = 14,
					alignment = TextAnchor.MiddleLeft,
					wordWrap = true,
					normal = { textColor = new Color(0.96f, 0.90f, 0.78f) }
				};
			}

			float width = Mathf.Min(380f, Screen.width - 24f);
			GUILayout.BeginArea(new Rect(Screen.width - width - 12f, 12f, width, 120f));
			for (int i = 0; i < _drawTexts.Count; i++)
				GUILayout.Label(_drawTexts[i], _style, GUILayout.MinHeight(30f));
			GUILayout.EndArea();
		}

		public void Clear() { _entries.Clear(); }
	}
}
