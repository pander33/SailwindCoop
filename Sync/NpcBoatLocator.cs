using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Нумерация AI-кораблей, одинаковая на всех машинах.
    ///
    /// В отличие от игровых лодок, NPC-лодки НЕ спавнятся в рантайме: <c>SaveLoadManager</c> держит
    /// их editor-assigned массивом <c>npcBoats</c> и сохраняет состояние ПО ИНДЕКСУ этого массива
    /// (<c>npcBoatData[k] = npcBoats[k].GetSaveData()</c>). Ни одного <c>Instantiate</c> для
    /// <c>NPCBoatController</c> в игре нет. Значит набор объектов и их порядок у хоста и клиента
    /// совпадают по построению — и позиция в массиве это самый надёжный идентификатор, какой вообще
    /// есть: им пользуется сам файл сохранения.
    ///
    /// Поэтому здесь нет гейта авторитетности, как в <see cref="BoatLocator"/>: тому приходится ждать,
    /// пока набор перестанет меняться, потому что лодки игрока появляются по ходу загрузки и покупок.
    /// Здесь набор неизменен, и единственный риск — прочитать его до того, как сцена поднялась;
    /// это ловится проверкой на пустоту.
    ///
    /// Запасной путь (рефлексия не удалась) — <c>FindObjectsOfType</c> с сортировкой по пути в
    /// иерархии. Он даёт СВОЮ нумерацию, тоже одинаковую на обеих машинах, но не совпадающую с
    /// сейвовой. Это допустимо, потому что индекс живёт только внутри сессии, но обе стороны обязаны
    /// оказаться на одном пути — иначе лодки перепутаются. Отсюда <see cref="UsingFallback"/>:
    /// значение уезжает в снапшот-логи, чтобы расхождение было видно, а не проявлялось как
    /// «корабли не в тех местах».
    /// </summary>
    public static class NpcBoatLocator
    {
        /// <summary>Как долго живёт результат сканирования. Набор неизменен, поэтому долго.</summary>
        public const float CacheSeconds = 5f;

        private static readonly List<NPCBoatController> _cache = new List<NPCBoatController>();
        private static float _cacheStamp = float.NegativeInfinity;
        private static bool _usingFallback;
        private static Runtime.CoopLog.Repeat _reflectReport;

        private static FieldInfo _npcBoatsField;
        private static bool _fieldLookupDone;

        /// <summary>Пришлось ли перейти на нумерацию по иерархии вместо сейвовой.</summary>
        public static bool UsingFallback => _usingFallback;

        /// <summary>Сколько кораблей в текущем наборе (включая пустые позиции массива).</summary>
        public static int Count => _cache.Count;

        /// <summary>
        /// Текущий набор. Возвращается общий кеш — читать, но не менять и не хранить между кадрами.
        /// Позиции могут содержать <c>null</c>: пустая ячейка в сценическом массиве не должна сдвигать
        /// номера всех, кто идёт следом.
        /// </summary>
        public static List<NPCBoatController> FindBoats()
        {
            if (_cache.Count > 0 && Time.unscaledTime - _cacheStamp < CacheSeconds && !HasDestroyed())
                return _cache;
            Rescan();
            return _cache;
        }

        public static NPCBoatController FindByIndex(int index)
        {
            var boats = FindBoats();
            if (index < 0 || index >= boats.Count) return null;
            return boats[index];
        }

        /// <summary>Сбросить кеш — при смене мира или сессии.</summary>
        public static void Invalidate()
        {
            _cache.Clear();
            _cacheStamp = float.NegativeInfinity;
        }

        private static bool HasDestroyed()
        {
            for (int i = 0; i < _cache.Count; i++)
                if (_cache[i] == null) return true;   // Unity-null: объект уничтожен вместе со сценой
            return false;
        }

        private static void Rescan()
        {
            _cache.Clear();
            _cacheStamp = Time.unscaledTime;

            var fromSave = ReadSaveOrder();
            if (fromSave != null && fromSave.Length > 0)
            {
                _usingFallback = false;
                for (int i = 0; i < fromSave.Length; i++) _cache.Add(fromSave[i]);
                return;
            }

            _usingFallback = true;
            var found = UnityEngine.Object.FindObjectsOfType<NPCBoatController>();
            if (found == null || found.Length == 0) return;
            Array.Sort(found, (a, b) => string.CompareOrdinal(BoatLocator.PathOf(SafeTransform(a)),
                                                              BoatLocator.PathOf(SafeTransform(b))));
            for (int i = 0; i < found.Length; i++) _cache.Add(found[i]);
        }

        private static Transform SafeTransform(NPCBoatController c)
        {
            try { return c != null ? c.transform : null; }
            catch { return null; }
        }

        /// <summary>
        /// Достаёт <c>SaveLoadManager.npcBoats</c>. Поле приватное и <c>[SerializeField]</c>, то есть
        /// именно тот случай, для которого в проекте принята рефлексия в try/catch: обновление игры
        /// должно приводить к деградации на запасной путь, а не к падению.
        /// </summary>
        private static NPCBoatController[] ReadSaveOrder()
        {
            try
            {
                var mgr = SaveLoadManager.instance;
                if (mgr == null) return null;

                if (!_fieldLookupDone)
                {
                    _fieldLookupDone = true;
                    _npcBoatsField = typeof(SaveLoadManager).GetField(
                        "npcBoats", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_npcBoatsField == null)
                {
                    Plugin.Logger.ReportError(
                        "[NpcBoatLocator] SaveLoadManager.npcBoats not found - falling back to hierarchy order",
                        "AI boats stay synced, but their numbering is ours, not the save's",
                        ref _reflectReport);
                    return null;
                }
                return _npcBoatsField.GetValue(mgr) as NPCBoatController[];
            }
            catch (Exception e)
            {
                Plugin.Logger.ReportError("[NpcBoatLocator] Could not read the save's NPC boat order",
                                          e.Message, ref _reflectReport);
                return null;
            }
        }
    }
}
