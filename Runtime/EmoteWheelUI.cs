using System.Collections.Generic;
using SailwindCoop.Sync;
using UnityEngine;

namespace SailwindCoop.Runtime
{
    /// <summary>
    /// Колесо жестов: клавиша зажата — колесо на экране, мышь выбирает сектор, отпускание запускает
    /// жест. Отпускание в центре — отмена.
    ///
    /// Ввод читается в <c>Update</c>, рисуется в <c>OnGUI</c> абсолютными прямоугольниками: набор
    /// контролов не зависит от прохода IMGUI.
    ///
    /// Вид — под интерфейс игры: таблички цвета пергамента с чернильным текстом шрифтом самой
    /// игры, выбранная подсвечена тёплым светом фонаря. Все текстуры строятся кодом, ассетов нет.
    /// </summary>
    internal sealed class EmoteWheelUI
    {
        private const float Radius = 215f;
        private const float DeadZone = 60f;
        private const float BoxW = 156f;
        private const float BoxH = 44f;
        private const float HotScale = 1.10f;
        private const float OpenSecs = 0.14f;
        private const float BackdropSize = 2f * Radius + 2f * BoxW;

        // Палитра: бумага и чернила судового журнала, латунь и свет фонаря.
        private static readonly Color PaperTop = new Color(0.93f, 0.86f, 0.70f, 0.96f);
        private static readonly Color PaperBottom = new Color(0.84f, 0.74f, 0.55f, 0.96f);
        private static readonly Color PaperEdge = new Color(0.36f, 0.24f, 0.13f, 1f);
        private static readonly Color LampTop = new Color(1.00f, 0.92f, 0.66f, 1f);
        private static readonly Color LampBottom = new Color(0.96f, 0.76f, 0.38f, 1f);
        private static readonly Color LampEdge = new Color(0.47f, 0.22f, 0.08f, 1f);
        private static readonly Color Ink = new Color(0.22f, 0.14f, 0.08f, 1f);
        private static readonly Color InkHot = new Color(0.30f, 0.10f, 0.04f, 1f);
        private static readonly Color Dusk = new Color(0.07f, 0.05f, 0.03f, 1f);
        private static readonly Color Brass = new Color(0.86f, 0.70f, 0.42f, 1f);

        private bool _open;
        private int _hover = -1;
        private float _openedAt;

        private bool _prevCursorVisible;
        private CursorLockMode _prevLockState;
        private bool _prevMouseLook;
        private bool _prevInCursorMenu;

        private GUIStyle _plate, _plateHot, _hint, _hintHot;
        private Texture2D _paper, _lamp, _backdrop, _ring, _seal;
        private bool _fontSearched;
        private Font _gameFont;

        public bool IsOpen => _open;

        /// <param name="allowed">Можно ли сейчас открыть колесо и держать его открытым.</param>
        /// <returns>Выбранный в этом кадре жест или <see cref="EmoteId.None"/>.</returns>
        public EmoteId Update(KeyCode key, bool allowed)
        {
            if (!_open)
            {
                if (allowed && Input.GetKeyDown(key)) Open();
                return EmoteId.None;
            }

            if (!allowed || Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return EmoteId.None;
            }

            _hover = HoverIndex();
            if (Input.GetKey(key)) return EmoteId.None;

            int picked = _hover;
            Close();
            return picked >= 0 ? EmoteCatalog.Wheel[picked] : EmoteId.None;
        }

        /// <summary>Закрыть без выбора (сессия кончилась, мир выгружается).</summary>
        public void Cancel()
        {
            if (_open) Close();
        }

        private void Open()
        {
            _open = true;
            _hover = -1;
            _openedAt = Time.unscaledTime;
            _prevCursorVisible = Cursor.visible;
            _prevLockState = Cursor.lockState;
            _prevMouseLook = MouseLook.MouseLookIsEnabled();
            _prevInCursorMenu = GameState.inCursorMenu;
            // Тот же захват курсора, что у меню F8: без inCursorMenu игра вернула бы блокировку
            // курсора, а щелчок мышью ушёл бы в мир.
            MouseLook.ToggleMouseLook(newState: false);
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            GameState.inCursorMenu = true;
        }

        private void Close()
        {
            _open = false;
            _hover = -1;
            // Колесо открывается только из игры, поэтому возвращаем ровно то, что было.
            Cursor.visible = _prevCursorVisible;
            Cursor.lockState = _prevLockState;
            GameState.inCursorMenu = _prevInCursorMenu;
            MouseLook.ToggleMouseLook(_prevMouseLook);
        }

        private static int HoverIndex()
        {
            Vector2 d = (Vector2)Input.mousePosition - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (d.magnitude < DeadZone) return -1;
            int n = EmoteCatalog.Wheel.Length;
            // 0° — вверх, дальше по часовой стрелке; у Input.mousePosition ось Y направлена вверх.
            float angle = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
            return (int)Mathf.Repeat(Mathf.Round(angle / (360f / n)), n);
        }

        public void Draw()
        {
            if (!_open) return;
            EnsureStyles();

            // Колесо раскрывается из центра и проявляется за долю секунды.
            float k = Mathf.Clamp01((Time.unscaledTime - _openedAt) / OpenSecs);
            float ease = 1f - (1f - k) * (1f - k);
            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, ease);

            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float radius = Radius * Mathf.Lerp(0.75f, 1f, ease);

            // Затемнение под колесом отделяет таблички от яркого моря и неба.
            GUI.DrawTexture(Centered(center, BackdropSize, BackdropSize), _backdrop);
            GUI.DrawTexture(Centered(center, 2f * radius, 2f * radius), _ring);

            int n = EmoteCatalog.Wheel.Length;
            for (int i = 0; i < n; i++)
            {
                if (i == _hover) continue;
                GUI.Label(PlateRect(center, radius, i, n, 1f), EmoteCatalog.Label(EmoteCatalog.Wheel[i]), _plate);
            }
            // Выбранная рисуется последней и крупнее — поверх соседей.
            if (_hover >= 0)
                GUI.Label(PlateRect(center, radius, _hover, n, HotScale), EmoteCatalog.Label(EmoteCatalog.Wheel[_hover]), _plateHot);

            GUI.DrawTexture(Centered(center, 2f * DeadZone, 2f * DeadZone), _seal);
            bool picked = _hover >= 0;
            GUI.Label(Centered(center, 2f * DeadZone - 16f, 2f * DeadZone - 16f),
                picked ? EmoteCatalog.Label(EmoteCatalog.Wheel[_hover]) : "Release\nto cancel",
                picked ? _hintHot : _hint);

            GUI.color = oldColor;
        }

        private static Rect Centered(Vector2 c, float w, float h)
        {
            return new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
        }

        private static Rect PlateRect(Vector2 center, float radius, int i, int n, float scale)
        {
            float a = i * (360f / n) * Mathf.Deg2Rad;
            // У IMGUI ось Y направлена вниз, поэтому «вверх» — это минус косинус.
            Vector2 p = center + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * radius;
            return Centered(p, BoxW * scale, BoxH * scale);
        }

        private void EnsureStyles()
        {
            if (_plate != null && _paper != null) return;

            _paper = Plate(PaperTop, PaperBottom, PaperEdge, 2f);
            _lamp = Plate(LampTop, LampBottom, LampEdge, 3f);
            _backdrop = Disc(256, Dusk, 0.62f, 0f, 1f, 0f);
            _ring = Disc(256, Brass, 0.55f, 0.965f, 1f, 0.012f);
            _seal = Disc(128, PaperBottom, 0.94f, 0f, 0.93f, 0.04f, PaperEdge);

            Font font = GameFont();
            _plate = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Normal,
                wordWrap = false,
                clipping = TextClipping.Overflow,
                border = new RectOffset(14, 14, 14, 14),
                padding = new RectOffset(8, 8, 2, 4),
            };
            if (font != null) _plate.font = font;
            _plate.normal.background = _paper;
            _plate.normal.textColor = Ink;

            _plateHot = new GUIStyle(_plate) { fontSize = 20 };
            _plateHot.normal.background = _lamp;
            _plateHot.normal.textColor = InkHot;

            _hint = new GUIStyle(_plate) { fontSize = 14, wordWrap = true, border = new RectOffset() };
            _hint.normal.background = null;
            _hint.normal.textColor = new Color(Ink.r, Ink.g, Ink.b, 0.75f);

            _hintHot = new GUIStyle(_hint) { fontSize = 17 };
            _hintHot.normal.textColor = InkHot;
        }

        /// <summary>
        /// Шрифт игры: тот, которым набрано больше всего её надписей (<c>TextMesh</c>). IMGUI умеет
        /// менять размер только у динамических шрифтов, остальные не подходят. Не нашёлся —
        /// остаётся шрифт IMGUI по умолчанию.
        /// </summary>
        private Font GameFont()
        {
            if (_fontSearched) return _gameFont;
            _fontSearched = true;
            try
            {
                var counts = new Dictionary<Font, int>();
                foreach (TextMesh tm in Resources.FindObjectsOfTypeAll<TextMesh>())
                {
                    Font f = tm != null ? tm.font : null;
                    if (f == null || !f.dynamic) continue;
                    int c;
                    counts.TryGetValue(f, out c);
                    counts[f] = c + 1;
                }
                int best = 0;
                foreach (var kv in counts)
                    if (kv.Value > best) { best = kv.Value; _gameFont = kv.Key; }
                Plugin.Logger.LogInfo("[EmoteWheel] шрифт колеса: " +
                    (_gameFont != null ? _gameFont.name + " (надписей: " + best + ")" : "по умолчанию — шрифт игры не найден"));
            }
            catch (System.Exception e)
            {
                _gameFont = null;
                Plugin.Logger.LogWarning("[EmoteWheel] не удалось найти шрифт игры: " + e.Message);
            }
            return _gameFont;
        }

        /// <summary>
        /// Табличка со скруглёнными углами для девятичастной растяжки: заливка светлеет кверху,
        /// по краю — тёмная кайма, по бумаге — лёгкое зерно.
        /// </summary>
        private static Texture2D Plate(Color top, Color bottom, Color edge, float edgeWidth)
        {
            const int size = 48;
            const float corner = 11f;
            var tex = NewTexture(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Расстояние до края скруглённого прямоугольника: меньше нуля — внутри.
                    float qx = Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - corner);
                    float qy = Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - corner);
                    float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    float dist = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - corner;

                    Color c = Color.Lerp(bottom, top, y / (float)(size - 1));
                    float grain = (Hash(x, y) - 0.5f) * 0.05f;
                    c.r += grain; c.g += grain; c.b += grain * 0.8f;
                    c = Color.Lerp(c, edge, Mathf.Clamp01(dist + edgeWidth + 0.5f));
                    c.a *= Mathf.Clamp01(0.5f - dist);
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Круг или кольцо с мягким краем. <paramref name="inner"/> и <paramref name="outer"/> — в
        /// долях радиуса; при <paramref name="soft"/> = 0 прозрачность плавно спадает от центра к
        /// краю (затемнение), иначе это ширина сглаживания края.
        /// </summary>
        private static Texture2D Disc(int size, Color color, float alpha, float inner, float outer, float soft, Color? rim = null)
        {
            var tex = NewTexture(size, size);
            var px = new Color[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float r = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / half;
                    float a;
                    if (soft <= 0f)
                    {
                        float fall = Mathf.Clamp01(1f - r / outer);
                        a = fall * fall * (3f - 2f * fall);
                    }
                    else
                    {
                        a = Mathf.Clamp01((outer - r) / soft) * Mathf.Clamp01((r - inner) / soft + (inner <= 0f ? 1f : 0f));
                    }
                    Color c = color;
                    if (rim.HasValue) c = Color.Lerp(c, rim.Value, Mathf.Clamp01((r - (outer - 0.07f)) / 0.03f));
                    c.a = alpha * a;
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static Texture2D NewTexture(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        private static float Hash(int x, int y)
        {
            float v = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            return v - Mathf.Floor(v);
        }
    }
}
