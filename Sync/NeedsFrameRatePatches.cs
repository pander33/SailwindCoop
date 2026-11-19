using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using SailwindCoop.Runtime;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class NeedsFrameRatePatches
    {
        private const float ReferenceFps = 60f;
        internal const float ConsumptionScale = 0.7f;
        private static bool InCoop => CoopBehaviour.Instance?.PlayingCoop == true;
        private static bool NormalizeFrameRate => Plugin.Cfg.NeedsFrameRateFix.Value && Time.timeScale > 0f;

        internal static void Apply(Harmony harmony)
        {
            var hooks = new PatchHookCatalog();
            hooks.Install(typeof(PlayerNeeds), "LateUpdate", Type.EmptyTypes, target => harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(NeedsFrameRatePatches), nameof(NeedsTickAllowed)),
                transpiler: new HarmonyMethod(typeof(NeedsFrameRatePatches), nameof(PatchNeedsDrain))));
            hooks.Install(typeof(PlayerNeeds), "DrainEnergyFromMovement", Type.EmptyTypes, target => harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(NeedsFrameRatePatches), nameof(BeforeMovement)),
                postfix: new HarmonyMethod(typeof(NeedsFrameRatePatches), nameof(AfterMovement))));
            PatchHealth.Report("Needs FPS", hooks);
        }

        private static bool NeedsTickAllowed() => CoopBehaviour.Instance?.WorldGameplayPaused != true;

        private static float FoodDebtStep()
        {
            if (!InCoop) return 0.5f;
            if (SleepSync.Instance?.HoldsBed == true && SleepSync.NeedsDeltaTime() <= 0f) return 0f;
            float step = 0.5f * ConsumptionScale;
            return NormalizeFrameRate ? step * Mathf.Clamp(Time.unscaledDeltaTime * ReferenceFps, 0f, 10f) : step;
        }

        private static float ScaleDrain(float amount) => InCoop ? amount * ConsumptionScale : amount;

        private static IEnumerable<CodeInstruction> PatchNeedsDrain(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var timescale = AccessTools.Field(typeof(Sun), nameof(Sun.timescale));
            var deltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var needsDeltaTime = AccessTools.Method(typeof(SleepSync), nameof(SleepSync.NeedsDeltaTime));
            // Change the time used by needs before vanilla clamps them or decides to pass out.
            // Some expressions multiply by a constant before reading Sun.timescale. The eat
            // cooldown uses only Time.deltaTime and stays on the local clock.
            var clockSites = Enumerable.Range(0, code.Count - 4).Where(i => code[i].Calls(deltaTime) &&
                (code[i + 2].LoadsField(timescale) ||
                 (code[i + 2].opcode == OpCodes.Mul && code[i + 4].LoadsField(timescale)))).ToArray();
            if (clockSites.Length != 9)
                throw new InvalidOperationException("Expected nine timed needs calculations, found " + clockSites.Length);
            foreach (int index in clockSites) code[index].operand = needsDeltaTime;

            var matches = new List<int>();
            for (int i = 1; i < code.Count - 1; i++)
                if (code[i - 1].LoadsField(timescale) && code[i].opcode == OpCodes.Ldc_R4 &&
                    code[i].operand is float value && value == 0.5f && code[i + 1].opcode == OpCodes.Mul)
                    matches.Add(i);

            if (matches.Count != 2)
                throw new InvalidOperationException("Expected two food-debt multiplications, found " + matches.Count);

            // Scale the drain before subtraction, so the game's clamp and pass-out checks see the new values.
            var drainSites = new List<int>(3);
            foreach (string field in new[] { nameof(PlayerNeeds.food), nameof(PlayerNeeds.foodDebt), nameof(PlayerNeeds.water) })
            {
                var target = AccessTools.Field(typeof(PlayerNeeds), field);
                var sites = Enumerable.Range(2, code.Count - 3).Where(i =>
                    code[i].opcode == OpCodes.Sub && code[i - 1].opcode == OpCodes.Mul &&
                    !matches.Contains(i - 2) && code[i + 1].StoresField(target)).ToArray();
                if (sites.Length != 1)
                    throw new InvalidOperationException("Expected one passive drain for " + field + ", found " + sites.Length);
                drainSites.Add(sites[0]);
            }

            var step = AccessTools.Method(typeof(NeedsFrameRatePatches), nameof(FoodDebtStep));
            foreach (int index in matches)
            {
                code[index].opcode = OpCodes.Call;
                code[index].operand = step;
            }
            var scale = AccessTools.Method(typeof(NeedsFrameRatePatches), nameof(ScaleDrain));
            foreach (int index in drainSites.OrderByDescending(i => i))
            {
                code[index].opcode = OpCodes.Call;
                code[index].operand = scale;
                code.Insert(index + 1, new CodeInstruction(OpCodes.Sub));
            }
            return code;
        }

        private static bool BeforeMovement(out Vector3? __state)
        {
            bool allowed = NeedsTickAllowed();
            __state = allowed ? new Vector3(PlayerNeeds.food, PlayerNeeds.foodDebt, PlayerNeeds.water) : (Vector3?)null;
            return allowed;
        }

        private static void AfterMovement(Vector3? __state)
        {
            try
            {
                if (!InCoop || !__state.HasValue) return;
                Vector3 before = __state.Value;
                float scale = ConsumptionScale;
                if (NormalizeFrameRate && Time.deltaTime > 0f)
                    scale *= Mathf.Clamp(1f / (Time.deltaTime * ReferenceFps), 0.1f, 10f);
                PlayerNeeds.food = before.x + (PlayerNeeds.food - before.x) * scale;
                PlayerNeeds.foodDebt = before.y + (PlayerNeeds.foodDebt - before.y) * scale;
                PlayerNeeds.water = before.z + (PlayerNeeds.water - before.z) * scale;
            }
            catch (Exception error) { PatchGuard.Fail(Report, error); }
        }

        private static void Report(Exception error) => Plugin.Logger.LogWarning("[Needs FPS] " + error.Message);
    }
}
