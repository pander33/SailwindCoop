using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PsychoticLab;
using UnityEngine;

namespace SailwindCoop.Avatar
{
	/// <summary>
	/// «Скины» из игровых NPC. Все NPC Sailwind — сборки PsychoticLab Modular Fantasy
	/// Character: один общий скелет + полный набор частей тела (детьми, выключены),
	/// у конкретного NPC включено своё подмножество и назначен свой материал.
	/// Поэтому скин полностью описывается строкой-ключом
	/// <c>npc:&lt;материал&gt;|&lt;часть,часть,...&gt;</c> — имена частей и материалов
	/// одинаковы на всех машинах, и ключ спокойно ездит по сети в тех же строковых
	/// полях, что имя avatar-бандла (протокол не меняется).
	///
	/// Скан ловит живых NPC на загруженных островах; первый пойманный клонируется в
	/// скрытый шаблон (DontDestroyOnLoad), чтобы модель можно было построить и после
	/// выгрузки острова. Ограничение: если с момента запуска ни один остров с NPC не
	/// подгружался, шаблона нет — строитель вернёт null, и PlayerSync откатится на
	/// avatar.bundle.
	/// </summary>
	public static class NpcSkinLibrary
	{
		public const string KeyPrefix = "npc:";

		public sealed class Skin
		{
			public string Key;
			public string DisplayName;
		}

		private static readonly Dictionary<string, Skin> _skins =
			new Dictionary<string, Skin>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, Material> _materials =
			new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
		private static GameObject _template;

		public static IReadOnlyList<Skin> Skins =>
			_skins.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

		public static bool CanBuild => _template != null;

		public static bool IsNpcKey(string selection) =>
			!string.IsNullOrEmpty(selection) &&
			selection.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// Найти NPC на загруженных островах, пополнить каталог скинов и кэш материалов,
		/// при первой возможности захватить шаблон. Список только растёт за сессию —
		/// уплывание от острова не «теряет» уже увиденные скины.
		/// </summary>
		public static void Scan()
		{
			int before = _skins.Count;
			try
			{
				var found = UnityEngine.Object.FindObjectsOfType<CharacterCustomizer>();
				foreach (var cc in found)
				{
					if (cc == null || cc.gameObject == null) continue;
					CacheMaterial(cc.mat);
					EnsureTemplate(cc);

					string key = BuildKey(cc);
					if (key == null || _skins.ContainsKey(key)) continue;
					_skins[key] = new Skin { Key = key, DisplayName = MakeDisplayName(cc) };
				}
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[NpcSkins] NPC scan: " + e.Message);
			}

			if (_skins.Count != before || _skins.Count > 0)
				Plugin.Logger?.LogInfo("[NpcSkins] NPC skins in catalog: " + _skins.Count +
					", template " + (_template != null ? "present" : "NONE") +
					", materials " + _materials.Count);
		}

		/// <summary>
		/// Построить модель по ключу скина: клон шаблона, включаем только запрошенные
		/// части, назначаем материал. Возвращает активный GameObject (root модели) или
		/// null, если шаблона ещё нет / ключ не разобрался — вызывающий откатывается
		/// на avatar.bundle.
		/// </summary>
		public static GameObject BuildModel(string key)
		{
			try
			{
				if (_template == null || !IsNpcKey(key)) return null;
				string body = key.Substring(KeyPrefix.Length);
				int sep = body.IndexOf('|');
				if (sep < 0) return null;
				string matName = body.Substring(0, sep);
				var wanted = new HashSet<string>(
					body.Substring(sep + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries),
					StringComparer.OrdinalIgnoreCase);
				if (wanted.Count == 0) return null;

				var model = UnityEngine.Object.Instantiate(_template);
				model.name = "NpcSkin";

				Material mat = null;
				_materials.TryGetValue(matName, out mat);

				int matched = 0;
				foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
				{
					bool on = wanted.Contains(smr.gameObject.name);
					smr.gameObject.SetActive(on);
					if (!on) continue;
					matched++;
					if (mat != null) smr.sharedMaterial = mat;
				}

				if (matched == 0)
				{
					Plugin.Logger?.LogWarning("[NpcSkins] No parts from key were found in template: " + key);
					UnityEngine.Object.Destroy(model);
					return null;
				}

				model.SetActive(true);
				Plugin.Logger?.LogInfo("[NpcSkins] Model built: parts " + matched + "/" + wanted.Count +
					", material " + (mat != null ? "'" + matName + "'" : "default"));
				return model;
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[NpcSkins] BuildModel: " + e.Message);
				return null;
			}
		}

		/// <summary>Ключ скина живого NPC: материал + имена включённых частей тела.</summary>
		private static string BuildKey(CharacterCustomizer cc)
		{
			var parts = new List<string>();
			foreach (var smr in cc.GetComponentsInChildren<SkinnedMeshRenderer>(false))
			{
				if (smr.gameObject.activeSelf) parts.Add(smr.gameObject.name);
			}
			if (parts.Count == 0) return null;
			parts.Sort(StringComparer.OrdinalIgnoreCase);

			var sb = new StringBuilder(KeyPrefix);
			sb.Append(CleanMatName(cc.mat != null ? cc.mat.name : ""));
			sb.Append('|');
			sb.Append(string.Join(",", parts));
			return sb.ToString();
		}

		private static void EnsureTemplate(CharacterCustomizer cc)
		{
			if (_template != null) return;
			try
			{
				var clone = UnityEngine.Object.Instantiate(cc.gameObject);
				clone.name = "CoopNpcSkinTemplate";
				clone.SetActive(false); // до первого Start — скрипты клона не успевают ожить
				UnityEngine.Object.DontDestroyOnLoad(clone);
				clone.hideFlags = HideFlags.HideInHierarchy;

				// Вычищаем игровые скрипты (CharacterCustomizer.Start сбросил бы части на
				// дефолт), коллайдеры и физику; остаются только кости и рендереры.
				foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
					if (mb != null) UnityEngine.Object.DestroyImmediate(mb);
				foreach (var col in clone.GetComponentsInChildren<Collider>(true))
					if (col != null) UnityEngine.Object.DestroyImmediate(col);
				foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
					if (rb != null) UnityEngine.Object.DestroyImmediate(rb);

				clone.transform.localScale = Vector3.one;
				_template = clone;
				Plugin.Logger?.LogInfo("[NpcSkins] NPC template captured from '" + cc.gameObject.name + "'");
			}
			catch (Exception e)
			{
				Plugin.Logger?.LogWarning("[NpcSkins] Failed to capture template: " + e.Message);
			}
		}

		private static void CacheMaterial(Material mat)
		{
			if (mat == null) return;
			string name = CleanMatName(mat.name);
			if (string.IsNullOrEmpty(name) || _materials.ContainsKey(name)) return;
			_materials[name] = mat;
		}

		private static string CleanMatName(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return "";
			string n = raw;
			while (n.EndsWith(" (Instance)", StringComparison.OrdinalIgnoreCase))
				n = n.Substring(0, n.Length - " (Instance)".Length);
			// Разделители ключа в имени недопустимы.
			return n.Replace('|', '_').Replace(',', '_').Trim();
		}

		private static string MakeDisplayName(CharacterCustomizer cc)
		{
			string raw = cc.gameObject.name;
			// Имя родителя обычно осмысленнее ("Shopkeeper"), чем имя модели ("character").
			var parent = cc.transform.parent;
			if (parent != null && parent.GetComponent<NPCPlayerCol>() != null) raw = parent.name;
			raw = raw.Replace("(Clone)", "").Trim();
			string name = "NPC: " + raw;

			// Дубли имён при разных ключах — нумеруем.
			int n = 2;
			string candidate = name;
			while (_skins.Values.Any(s => string.Equals(s.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
				candidate = name + " #" + n++;
			return candidate;
		}
	}
}
