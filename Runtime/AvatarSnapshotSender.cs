using UnityEngine;

namespace SailwindCoop.Runtime
{
    // PlayerControllerMirror copies the controller pose to the observer in LateUpdate.
    // Put only the sender after it so boat, item and remote avatar updates keep their order.
    [DefaultExecutionOrder(10000)]
    internal sealed class AvatarSnapshotSender : MonoBehaviour
    {
        internal CoopBehaviour Owner;
        private CoopLog.Repeat _failures;

        private void LateUpdate()
        {
            if (Owner == null || !Owner.isActiveAndEnabled) return;
            try { Owner.Players.SendLocalPose(Time.unscaledDeltaTime); }
            catch (System.Exception e)
            {
                Plugin.Logger.ReportError("[Coop] Sending avatar snapshot failed", e, ref _failures);
            }
        }
    }
}
