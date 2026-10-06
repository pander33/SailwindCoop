using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// Снимает у ЛОКАЛЬНОГО игрока точки хвата: что у него в руках и за какой орган управления
    /// он держится.
    ///
    /// Меряется у источника по той же причине, что и походка (см. <c>PlayerStateMsg</c>): только
    /// владелец указателя знает, что именно зажато. Собирать это у получателя пришлось бы из
    /// держателей каждого домена (лебёдки, якорь, швартовы, предметы) по отдельности.
    /// </summary>
    internal sealed class LocalHandProbe
    {
        public bool HasRight, HasLeft;
        public Vector3 RightWorld, LeftWorld;

        private GoPointer _gp;
        private float _gpRetryAt;

        private static readonly FieldInfo FSticky =
            typeof(GoPointer).GetField("stickyClickedButton", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo FClicked =
            typeof(GoPointer).GetField("clickedButton", BindingFlags.NonPublic | BindingFlags.Instance);

        private const float WheelHalfGrip = 0.20f;
        private const float WinchHalfGrip = 0.07f;
        private const float BigItemHalfGrip = 0.22f;

        public void Clear()
        {
            _gp = null;
            _gpRetryAt = 0f;
            HasRight = HasLeft = false;
        }

        /// <param name="player">Трансформ локального игрока (render-кадр).</param>
        public void Sample(Transform player)
        {
            HasRight = HasLeft = false;
            if (player == null) return;
            try { SampleHands(player); }
            catch (System.Exception e)
            {
                HasRight = HasLeft = false;
                Plugin.Logger.LogWarning("[PlayerHands] не удалось прочитать руки игрока: " + e.Message);
            }
        }

        private void SampleHands(Transform player)
        {
            if (_gp == null)
            {
                if (Time.unscaledTime < _gpRetryAt) return;
                _gpRetryAt = Time.unscaledTime + 1f;
                _gp = Object.FindObjectOfType<GoPointer>();
                if (_gp == null) return;
            }

            PickupableItem item = _gp.GetHeldItem();
            GoPointerButton button = HeldButton();
            if (button != null && item != null && button == item) button = null;

            Vector3 side = player.right;
            if (item != null)
            {
                Vector3 p = item.transform.position;
                if (item.big)
                {
                    RightWorld = p + side * BigItemHalfGrip;
                    LeftWorld = p - side * BigItemHalfGrip;
                    HasRight = HasLeft = true;
                }
                else
                {
                    RightWorld = p;
                    HasRight = true;
                }
            }

            if (button == null) return;
            Vector3 grip = GripPoint(button);
            if (item != null)
            {
                // Правая занята предметом — за орган управления берётся свободная левая.
                if (!HasLeft) { LeftWorld = grip; HasLeft = true; }
                return;
            }

            float half = button is GPButtonSteeringWheel ? WheelHalfGrip
                       : button is GPButtonRopeWinch ? WinchHalfGrip
                       : 0f;
            if (half > 0f)
            {
                RightWorld = grip + side * half;
                LeftWorld = grip - side * half;
                HasRight = HasLeft = true;
            }
            else
            {
                RightWorld = grip;
                HasRight = true;
            }
        }

        /// <summary>Залипшее или зажатое управление локального игрока (тот же отбор, что в ControlsSync).</summary>
        private GoPointerButton HeldButton()
        {
            var sticky = FSticky?.GetValue(_gp) as GoPointerButton;
            if (sticky != null) return sticky;
            var clicked = FClicked?.GetValue(_gp) as GoPointerButton;
            // GoPointer пишет clickedButton до Click; отклонённый Click — не хват.
            return clicked != null && clicked.IsCliked() ? clicked : null;
        }

        private static Vector3 GripPoint(GoPointerButton button)
        {
            var col = button.GetComponent<Collider>();
            return col != null && col.enabled ? col.bounds.center : button.transform.position;
        }
    }

    /// <summary>
    /// Двухкостный IK рук удалённого аватара. Вызывается из LateUpdate после того, как Animator
    /// или процедурная походка выставили кости в этом кадре: оба пути каждый кадр пишут плечо и
    /// локоть заново, поэтому поворот от IK не накапливается.
    /// </summary>
    internal sealed class AvatarArmIk
    {
        private sealed class Arm
        {
            public Transform Upper, Lower, Hand;
            public Quaternion UpperBind, LowerBind;
            public float Side;            // +1 правая, -1 левая
            public bool Active;
            public Vector3 Target;        // в кадре сетевого корня аватара
            public Vector3 Smoothed;
            public bool HasSmoothed;
            public float Weight;
            public bool Usable => Upper != null && Lower != null && Hand != null;
        }

        private readonly Transform _root;    // сетевой корень: в его кадре приходят цели
        private readonly Transform _model;   // визуальная модель: её оси задают «локоть вниз»
        private readonly Arm _right = new Arm { Side = 1f };
        private readonly Arm _left = new Arm { Side = -1f };

        /// <summary>
        /// Возвращать плечо и локоть в исходный поворот перед каждым решением. Нужно там, где кости
        /// рук никто не пишет заново каждый кадр (нет Animator, походка NPC не поднялась): иначе
        /// рука после отпускания осталась бы вытянутой.
        /// </summary>
        public bool RestoreBind;

        public bool RightUsable => _right.Usable;
        public bool LeftUsable => _left.Usable;
        public float RightWeight => _right.Weight;
        public float LeftWeight => _left.Weight;

        private AvatarArmIk(Transform root, Transform model)
        {
            _root = root;
            _model = model;
        }

        /// <summary>
        /// Находит кости рук; null, если у модели нет ни одной целой руки. Не бросает: сбой здесь
        /// не должен ломать создание аватара, которое повторяется на каждый входящий PlayerState.
        /// </summary>
        public static AvatarArmIk TryCreate(Transform root, Transform model, Animator animator)
        {
            try
            {
                AvatarArmIk ik = Create(root, model, animator);
                if (ik == null) return null;
                Capture(ik._right);
                Capture(ik._left);
                return ik;
            }
            catch (System.Exception e)
            {
                Plugin.Logger.LogWarning("[PlayerHands] кости рук не найдены из-за ошибки: " + e.Message);
                return null;
            }
        }

        private static void Capture(Arm arm)
        {
            if (!arm.Usable) return;
            arm.UpperBind = arm.Upper.localRotation;
            arm.LowerBind = arm.Lower.localRotation;
        }

        private static AvatarArmIk Create(Transform root, Transform model, Animator animator)
        {
            var ik = new AvatarArmIk(root, model);
            bool human = animator != null && animator.isHuman;
            if (human)
            {
                ik._right.Upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                ik._right.Lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                ik._right.Hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                ik._left.Upper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                ik._left.Lower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                ik._left.Hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            }
            if (!ik._right.Usable) FindByName(model, ik._right, "R", "Right");
            if (!ik._left.Usable) FindByName(model, ik._left, "L", "Left");
            return ik._right.Usable || ik._left.Usable ? ik : null;
        }

        private static void FindByName(Transform model, Arm arm, string s, string word)
        {
            // Synty (NPC игры), затем Mixamo и распространённые generic-риги.
            string[][] sets =
            {
                new[] { "Shoulder_" + s, "Elbow_" + s, "Hand_" + s },
                new[] { "mixamorig:" + word + "Arm", "mixamorig:" + word + "ForeArm", "mixamorig:" + word + "Hand" },
                new[] { word + "Arm", word + "ForeArm", word + "Hand" },
                new[] { "UpperArm_" + s, "LowerArm_" + s, "Hand_" + s },
            };
            foreach (var set in sets)
            {
                Transform upper = Find(model, set[0]);
                Transform lower = upper != null ? Find(upper, set[1]) : null;
                if (lower == null) continue;
                Transform hand = Find(lower, set[2]);
                // Без кости кисти концом предплечья служит первый потомок локтя.
                if (hand == null && lower.childCount > 0) hand = lower.GetChild(0);
                if (hand == null) continue;
                arm.Upper = upper;
                arm.Lower = lower;
                arm.Hand = hand;
                return;
            }
        }

        private static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = Find(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        public void SetTarget(bool right, bool active, Vector3 rootLocal)
        {
            Arm arm = right ? _right : _left;
            arm.Active = active;
            if (active) arm.Target = rootLocal;
        }

        public void Solve()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            SolveArm(_right, dt);
            SolveArm(_left, dt);
        }

        private void SolveArm(Arm arm, float dt)
        {
            if (!arm.Usable) return;
            if (RestoreBind)
            {
                arm.Upper.localRotation = arm.UpperBind;
                arm.Lower.localRotation = arm.LowerBind;
            }
            arm.Weight = Mathf.MoveTowards(arm.Weight, arm.Active ? 1f : 0f, dt * 6f);
            if (arm.Weight <= 0.001f) { arm.HasSmoothed = false; return; }

            if (arm.Active)
            {
                // Цели приходят с частотой снимков; сглаживаем их в кадре корня, чтобы рука не дёргалась.
                if (!arm.HasSmoothed) { arm.Smoothed = arm.Target; arm.HasSmoothed = true; }
                else arm.Smoothed = Vector3.Lerp(arm.Smoothed, arm.Target, 1f - Mathf.Exp(-14f * dt));
            }

            Vector3 target = _root.TransformPoint(arm.Smoothed);
            Vector3 a = arm.Upper.position;
            Vector3 b = arm.Lower.position;
            Vector3 c = arm.Hand.position;
            float upperLen = Vector3.Distance(a, b);
            float lowerLen = Vector3.Distance(b, c);
            if (upperLen < 1e-4f || lowerLen < 1e-4f) return;

            Vector3 toTarget = target - a;
            float reach = upperLen + lowerLen;
            // Дальняя цель — рука просто вытянута в её сторону; слишком близкая — не складываем в ноль.
            float dist = Mathf.Clamp(toTarget.magnitude, reach * 0.15f, reach * 0.999f);
            Vector3 dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : _model.forward;

            float cosA = Mathf.Clamp((upperLen * upperLen + dist * dist - lowerLen * lowerLen) / (2f * upperLen * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);

            // Локоть смотрит вниз, наружу и немного назад.
            Vector3 hint = -_model.up + _model.right * (arm.Side * 0.5f) - _model.forward * 0.35f;
            Vector3 pole = Vector3.ProjectOnPlane(hint, dir);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.ProjectOnPlane(_model.right * arm.Side, dir);
            if (pole.sqrMagnitude < 1e-6f) return;
            pole.Normalize();

            Vector3 elbow = a + dir * (upperLen * cosA) + pole * (upperLen * sinA);
            Vector3 hand = a + dir * dist;

            Quaternion upper0 = arm.Upper.rotation;
            Quaternion upper1 = Quaternion.FromToRotation(b - a, elbow - a) * upper0;
            arm.Upper.rotation = Quaternion.Slerp(upper0, upper1, arm.Weight);

            b = arm.Lower.position;
            c = arm.Hand.position;
            Quaternion lower0 = arm.Lower.rotation;
            Quaternion lower1 = Quaternion.FromToRotation(c - b, hand - b) * lower0;
            arm.Lower.rotation = Quaternion.Slerp(lower0, lower1, arm.Weight);
        }
    }
}
