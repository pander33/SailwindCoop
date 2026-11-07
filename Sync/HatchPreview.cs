using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Sync
{
    /// <summary>Shows a guest's hatch click while the real hatch and its walk collider await the host.</summary>
    internal sealed class HatchPreview
    {
        private static readonly FieldInfo InMotion = typeof(GPButtonTrapdoor).GetField("inMotion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private const float TravelDegrees = 85f, DegreesPerSecond = 125f;
        private const float ReplySeconds = 1.5f, ReturnSeconds = 0.15f;

        private sealed class View
        {
            public Transform Transform, Parent;
            public Vector3 StartPosition, EndPosition, SavedPosition, ReturnPosition;
            public Quaternion StartRotation, EndRotation, SavedRotation, ReturnRotation;
            public float Started, ReplyDeadline, ReturnStarted = -1f;
            public bool Target, Confirmed, Drawn;
        }

        private readonly Dictionary<GPButtonTrapdoor, View> _views = new Dictionary<GPButtonTrapdoor, View>();
        private readonly List<GPButtonTrapdoor> _done = new List<GPButtonTrapdoor>();
        private Camera _drawingCamera;
        private bool _hooked;

        internal bool Begin(GPButtonTrapdoor hatch)
        {
            if (_views.TryGetValue(hatch, out var pending)) return pending.Confirmed;
            if (InMotion == null || (bool)InMotion.GetValue(hatch)) return true;
            var tr = hatch.transform;
            bool target = !hatch.IsOpen();
            float direction = target ? 1f : -1f;
            var view = new View
            {
                Transform = tr, Parent = tr.parent, Target = target,
                StartPosition = tr.localPosition, EndPosition = tr.localPosition,
                StartRotation = tr.localRotation, EndRotation = tr.localRotation,
                Started = Time.time, ReplyDeadline = Time.unscaledTime + ReplySeconds
            };
            if (hatch.slideDistance == 0f)
                view.EndRotation *= Quaternion.AngleAxis(-direction * TravelDegrees, Vector3.up);
            else
            {
                Vector3 travel = tr.up * (hatch.slideDistance * direction);
                view.EndPosition += tr.parent == null ? travel : tr.parent.InverseTransformVector(travel);
            }
            _views.Add(hatch, view);
            if (!_hooked)
            {
                Camera.onPreCull += Draw;
                Camera.onPostRender += Restore;
                _hooked = true;
            }
            return true;
        }

        internal void Receive(GPButtonTrapdoor hatch, bool target)
        {
            if (!_views.TryGetValue(hatch, out var view)) return;
            if (view.Target == target && view.ReturnStarted < 0f) view.Confirmed = true;
            else BeginReturn(view);
        }

        internal void Tick()
        {
            if (_views.Count == 0) return;
            Restore(_drawingCamera);
            _done.Clear();
            foreach (var entry in _views)
            {
                var hatch = entry.Key;
                var view = entry.Value;
                if (hatch == null || view.Transform.parent != view.Parent ||
                    (view.ReturnStarted >= 0f && Time.unscaledTime - view.ReturnStarted >= ReturnSeconds) ||
                    (view.Confirmed && hatch.IsOpen() == view.Target && !(bool)InMotion.GetValue(hatch)))
                    _done.Add(hatch);
                else if (!view.Confirmed && Time.unscaledTime >= view.ReplyDeadline)
                    BeginReturn(view);
            }
            foreach (var hatch in _done) _views.Remove(hatch);
            UnhookIfEmpty();
        }

        private static void PredictedPose(View view, out Vector3 position, out Quaternion rotation)
        {
            float progress = Mathf.Clamp01((Time.time - view.Started) * DegreesPerSecond / TravelDegrees);
            position = Vector3.Lerp(view.StartPosition, view.EndPosition, progress);
            rotation = Quaternion.Slerp(view.StartRotation, view.EndRotation, progress);
        }

        private static void BeginReturn(View view)
        {
            if (view.ReturnStarted >= 0f) return;
            PredictedPose(view, out view.ReturnPosition, out view.ReturnRotation);
            view.ReturnStarted = Time.unscaledTime;
        }

        private void Draw(Camera camera)
        {
            if (_drawingCamera != null) return;
            _drawingCamera = camera;
            try
            {
                foreach (var view in _views.Values)
                {
                    var tr = view.Transform;
                    if (tr == null || tr.parent != view.Parent) continue;
                    view.SavedPosition = tr.localPosition;
                    view.SavedRotation = tr.localRotation;
                    view.Drawn = true;
                    if (view.ReturnStarted < 0f)
                    {
                        PredictedPose(view, out var pos, out var rot);
                        tr.localPosition = pos;
                        tr.localRotation = rot;
                    }
                    else
                    {
                        float blend = Mathf.SmoothStep(0f, 1f, (Time.unscaledTime - view.ReturnStarted) / ReturnSeconds);
                        tr.localPosition = Vector3.Lerp(view.ReturnPosition, view.SavedPosition, blend);
                        tr.localRotation = Quaternion.Slerp(view.ReturnRotation, view.SavedRotation, blend);
                    }
                }
            }
            catch (System.Exception e)
            {
                Restore(camera);
                Plugin.Logger?.LogWarning("[HatchPreview] " + e.Message);
            }
        }

        private void Restore(Camera camera)
        {
            if (ReferenceEquals(_drawingCamera, null) || camera != _drawingCamera) return;
            _drawingCamera = null;
            foreach (var view in _views.Values)
            {
                if (!view.Drawn) continue;
                view.Drawn = false;
                if (view.Transform == null || view.Transform.parent != view.Parent) continue;
                view.Transform.localPosition = view.SavedPosition;
                view.Transform.localRotation = view.SavedRotation;
            }
        }

        internal void Remove(GPButtonTrapdoor hatch)
        {
            Restore(_drawingCamera);
            _views.Remove(hatch);
            UnhookIfEmpty();
        }

        internal void Clear()
        {
            Restore(_drawingCamera);
            _views.Clear();
            _done.Clear();
            UnhookIfEmpty();
        }

        private void UnhookIfEmpty()
        {
            if (!_hooked || _views.Count != 0) return;
            Camera.onPreCull -= Draw;
            Camera.onPostRender -= Restore;
            _hooked = false;
        }
    }
}
