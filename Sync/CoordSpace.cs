using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>
    /// The crux of multiplayer in Sailwind: the world uses a *floating origin* — it
    /// periodically recenters around the local player, so the same physical spot has a
    /// different local <c>transform.position</c> on each machine (their origins have
    /// drifted by different amounts).
    ///
    /// <see cref="FloatingOriginManager"/> exposes the mapping between the local
    /// (shifting) space and a stable "real" space that is identical on every client.
    /// So everything on the wire travels in REAL space; we convert at the edges.
    ///
    ///   send:    real = LocalToReal(transform.position)
    ///   receive: local = RealToLocal(real); transform.position = local
    ///
    /// The origin only ever translates — it never rotates the world — so rotations are
    /// the same in both spaces and need no conversion.
    /// </summary>
    public static class CoordSpace
    {
        public static bool Ready => FloatingOriginManager.instance != null;

        /// <summary>Local (shifting) world position -> stable real position (wire space).</summary>
        public static Vector3 LocalToReal(Vector3 localPos)
        {
            var m = FloatingOriginManager.instance;
            return m != null ? m.ShiftingPosToRealPos(localPos) : localPos;
        }

        /// <summary>Stable real position (wire space) -> local (shifting) world position.</summary>
        public static Vector3 RealToLocal(Vector3 realPos)
        {
            var m = FloatingOriginManager.instance;
            return m != null ? m.RealPosToShiftingPos(realPos) : realPos;
        }

        /// <summary>True while the origin is mid-shift this frame; treat fresh samples with care.</summary>
        public static bool ShiftingThisFrame => FloatingOriginManager.ShiftingThisFrame;
    }
}
