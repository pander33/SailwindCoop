using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Жест аватара. Значения идут в <c>PlayerStateMsg.Emote</c> — только дописывать.</summary>
    public enum EmoteId : byte
    {
        None = 0,
        Wave = 1,
        LandHo = 2,
        Salute = 3,
        ComeHere = 4,
        Clap = 5,
        Shrug = 6,
        Hooray = 7,
        Point = 8,
        /// <summary>Рука протянута с деньгами. Не из колеса: держится, пока игрок предлагает деньги.</summary>
        Offer = 9,
    }

    /// <summary>Цели рук жеста в один момент времени.</summary>
    internal struct EmotePose
    {
        // Векторы — от плеча, в долях полной длины руки: x — наружу от тела, y — вверх,
        // z — вперёд. Так жест не зависит от роста и пропорций модели.
        public bool Right, Left;
        public Vector3 RightArm, LeftArm;
    }

    /// <summary>
    /// Процедурные жесты: у NPC-скинов нет Animator, поэтому жест задаётся не клипом, а положением
    /// кистей во времени. Проигрывает его тот же IK, что тянет руки к предметам.
    /// </summary>
    internal static class EmoteCatalog
    {
        /// <summary>Порядок секторов колеса, начиная сверху по часовой стрелке.</summary>
        public static readonly EmoteId[] Wheel =
        {
            EmoteId.Wave, EmoteId.LandHo, EmoteId.Point, EmoteId.ComeHere,
            EmoteId.Clap, EmoteId.Shrug, EmoteId.Salute, EmoteId.Hooray,
        };

        public static string Label(EmoteId id)
        {
            switch (id)
            {
                case EmoteId.Wave: return "Wave";
                case EmoteId.LandHo: return "Land ho!";
                case EmoteId.Salute: return "Salute";
                case EmoteId.ComeHere: return "Come here";
                case EmoteId.Clap: return "Applause";
                case EmoteId.Shrug: return "Shrug";
                case EmoteId.Hooray: return "Hooray";
                case EmoteId.Point: return "Point";
                default: return "";
            }
        }

        /// <summary>Длительность жеста в секундах; 0 для неизвестного значения.</summary>
        public static float Duration(EmoteId id)
        {
            switch (id)
            {
                case EmoteId.Wave: return 2.4f;
                case EmoteId.LandHo: return 3.0f;
                case EmoteId.Salute: return 2.0f;
                case EmoteId.ComeHere: return 2.6f;
                case EmoteId.Clap: return 2.6f;
                case EmoteId.Shrug: return 1.6f;
                case EmoteId.Hooray: return 2.4f;
                case EmoteId.Point: return 2.5f;
                // Держится, пока его не снимет отправитель; предел — на случай потерянного снятия.
                case EmoteId.Offer: return 120f;
                default: return 0f;
            }
        }

        /// <param name="t">Секунды от начала жеста.</param>
        /// <param name="lookPitchDeg">Наклон взгляда игрока: указывающие жесты идут по нему.</param>
        /// <returns>false, если жест не идёт (неизвестен, ещё не начался или уже кончился).</returns>
        public static bool Evaluate(EmoteId id, float t, float lookPitchDeg, out EmotePose pose)
        {
            pose = default(EmotePose);
            float duration = Duration(id);
            if (duration <= 0f || t < 0f || t >= duration) return false;

            switch (id)
            {
                case EmoteId.Wave:
                    pose.Right = true;
                    pose.RightArm = new Vector3(0.38f + 0.20f * Mathf.Sin(t * 9f), 0.78f, 0.22f);
                    return true;

                case EmoteId.LandHo:
                    pose.Right = true;
                    pose.RightArm = Pointing(lookPitchDeg);
                    // Левая ладонь козырьком ко лбу.
                    pose.Left = true;
                    pose.LeftArm = new Vector3(-0.28f, 0.50f, 0.30f);
                    return true;

                case EmoteId.Point:
                    pose.Right = true;
                    pose.RightArm = Pointing(lookPitchDeg);
                    return true;

                case EmoteId.Offer:
                    // Ладонь вперёд на уровне груди, чуть покачивается.
                    pose.Right = true;
                    pose.RightArm = new Vector3(0.04f, -0.06f + 0.02f * Mathf.Sin(t * 2.5f), 0.82f);
                    return true;

                case EmoteId.Salute:
                    pose.Right = true;
                    pose.RightArm = new Vector3(-0.14f, 0.50f, 0.26f);
                    return true;

                case EmoteId.ComeHere:
                {
                    float pull = 0.5f + 0.5f * Mathf.Sin(t * 7f);
                    pose.Right = true;
                    pose.RightArm = new Vector3(0.10f, 0.20f + 0.18f * pull, 0.80f - 0.40f * pull);
                    return true;
                }

                case EmoteId.Clap:
                {
                    float apart = 0.14f * Mathf.Abs(Mathf.Sin(t * 10f));
                    var arm = new Vector3(-0.30f + apart, 0.05f, 0.55f);
                    pose.Right = pose.Left = true;
                    pose.RightArm = pose.LeftArm = arm;
                    return true;
                }

                case EmoteId.Shrug:
                {
                    var arm = new Vector3(0.55f, -0.22f, 0.28f);
                    pose.Right = pose.Left = true;
                    pose.RightArm = pose.LeftArm = arm;
                    return true;
                }

                case EmoteId.Hooray:
                {
                    var arm = new Vector3(0.28f, 0.90f + 0.05f * Mathf.Sin(t * 10f), 0.08f);
                    pose.Right = pose.Left = true;
                    pose.RightArm = pose.LeftArm = arm;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Рука вытянута вперёд по наклону взгляда.</summary>
        private static Vector3 Pointing(float lookPitchDeg)
        {
            float pitch = Mathf.Clamp(lookPitchDeg, -40f, 60f) * Mathf.Deg2Rad;
            return new Vector3(0.08f, 0.10f + 0.95f * Mathf.Sin(pitch), 0.95f * Mathf.Cos(pitch));
        }
    }
}
