using System;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Sync
{
    internal static class FoldableState
    {
        private static readonly MethodInfo Fold = typeof(ShipItemFoldable).GetMethod("Fold", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo Unfold = typeof(ShipItemFoldable).GetMethod("Unfold", BindingFlags.Instance | BindingFlags.NonPublic);

        public static float Capture(ShipItem item)
        {
            // OnLoad can show foldedMesh while resetting amount to zero. The mesh tells the truth.
            if (item is ShipItemFoldable folded)
            {
                var filter = folded.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh == folded.foldedMesh) return 1f;
                if (filter != null && filter.sharedMesh == folded.unfoldedMesh) return 0f;
            }
            return item.amount;
        }

        public static void Apply(ShipItemFoldable item, float amount)
        {
            try
            {
                var filter = item.GetComponent<MeshFilter>();
                Mesh target = amount <= 0f ? item.unfoldedMesh : item.foldedMesh;
                if (filter == null || filter.sharedMesh != target)
                {
                    // Absolute setters update the mesh, detail visibility and cached collider size.
                    // OnAltActivate would toggle, play UI sounds and consult the receiver's chart camera.
                    MethodInfo method = amount <= 0f ? Unfold : Fold;
                    if (method == null) throw new MissingMethodException("ShipItemFoldable", amount <= 0f ? "Unfold" : "Fold");
                    method.Invoke(item, null);
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[FoldableState] Не удалось обновить карту/мебель '" + item.name + "': " + e.Message); }
            finally { item.amount = amount; } // Keep trusted scalar values; do not sanitize requests.
        }
    }
}
