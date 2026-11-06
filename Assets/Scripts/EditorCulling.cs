namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.Vizzy.UI.Elements;
    using HarmonyLib;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    /// <summary>
    /// The stock Vizzy editor keeps every block of a program as live UI: each block and text field runs an
    /// Update every frame and Unity lays out and draws them all, on screen or not, so large programs get slow
    /// to edit. This switches whole stacks off (their GameObjects inactive) while they're well outside the
    /// view, and on again as they come near, and masks the view so Unity leaves out what's outside it.
    /// The program is saved from the editor's node tree, so it isn't affected. A few stock methods look only
    /// at active blocks; <see cref="EditorCullingRestorePatch"/> switches everything on before them.
    /// </summary>
    public sealed class EditorCulling : MonoBehaviour
    {
        private const float CheckInterval = 0.25f;
        private const float RefreshInterval = 2f;
        private const float LogInterval = 5f;

        private static readonly List<EditorCulling> Instances = new List<EditorCulling>();

        private readonly List<BlockElementScript> _blocks = new List<BlockElementScript>();
        private readonly Dictionary<Transform, Rect> _stacks = new Dictionary<Transform, Rect>();
        private readonly HashSet<Transform> _off = new HashSet<Transform>();
        private readonly Vector3[] _corners = new Vector3[4];

        private VizzyUIController _controller;

        // The stock view has no mask, so Unity draws and batches every block. A RectMask2D on the view is
        // Unity's own way to leave out graphics outside it; it's only added while culling is on.
        private RectMask2D _mask;
        private float _nextCheck;
        private float _nextRefresh;
        private float _nextLog;
        private int _frames;
        private float _frameTime;

        public void Connect(VizzyUIController controller)
        {
            _controller = controller;
        }

        /// <summary>Everything back on in this editor, for stock code that only looks at active blocks.</summary>
        public static void RestoreFor(object vizzyUI)
        {
            foreach (var culling in Instances)
            {
                if (culling != null && culling._controller != null && ReferenceEquals(culling._controller.VizzyUI, vizzyUI))
                {
                    culling.RestoreAll();

                    // Long enough for the stock action to finish before anything switches off again.
                    culling._nextCheck = Time.unscaledTime + 1f;
                }
            }
        }

        private static bool MouseHeld()
        {
            return UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2);
        }

        private void OnEnable()
        {
            Instances.Add(this);
        }

        private void Update()
        {
            try
            {
                this.LogFrameTime();
                var culling = ModSettings.Instance.EditorCulling.Value;
                this.SetMask(culling);
                if (!culling)
                {
                    this.RestoreAll();
                    if (!ModSettings.Instance.DebugLogging.Value)
                    {
                        return;
                    }
                }

                // Nothing changes while a button is held: a drag may be carrying blocks across the view.
                var now = Time.unscaledTime;
                if (now < _nextCheck || MouseHeld())
                {
                    return;
                }

                _nextCheck = now + CheckInterval;
                var program = _controller == null ? null : (_controller.VizzyUI as VizzyUIScript)?.ProgramTransform;
                var view = _controller == null || _controller.ProgramContainer == null ? null : (RectTransform)_controller.ProgramContainer.transform;
                if (program == null || view == null)
                {
                    return;
                }

                // Stacks come and go with editing and undo; a new one starts switched on, which is safe.
                if (now >= _nextRefresh)
                {
                    _nextRefresh = now + RefreshInterval;
                    this.Measure(program);
                }

                if (!culling)
                {
                    return;
                }

                // The view and one view's width and height around it, in the program's own space, where a
                // stack's place only changes when it's edited, not when the view pans or zooms.
                view.GetWorldCorners(_corners);
                var a = program.InverseTransformPoint(_corners[0]);
                var b = program.InverseTransformPoint(_corners[2]);
                var width = Mathf.Abs(b.x - a.x);
                var height = Mathf.Abs(b.y - a.y);
                var near = Rect.MinMaxRect(Mathf.Min(a.x, b.x) - width, Mathf.Min(a.y, b.y) - height, Mathf.Max(a.x, b.x) + width, Mathf.Max(a.y, b.y) + height);
                var busy = Busy();

                foreach (var pair in _stacks)
                {
                    var stack = pair.Key;
                    if (stack == null)
                    {
                        continue;
                    }

                    if (pair.Value.Overlaps(near) || (busy != null && busy.IsChildOf(stack)))
                    {
                        if (_off.Remove(stack))
                        {
                            stack.gameObject.SetActive(true);
                        }
                    }
                    else if (stack.gameObject.activeSelf)
                    {
                        stack.gameObject.SetActive(false);
                        _off.Add(stack);
                    }
                }
            }
            catch (Exception e)
            {
                EditorLog.Error("Editor culling failed, so it's off for this editor: " + e);
                this.RestoreAll();
                this.SetMask(false);
                this.enabled = false;
            }
        }

        /// <summary>The selected object, such as a text field being typed in: its stack always stays on.</summary>
        private static Transform Busy()
        {
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            return selected == null ? null : selected.transform;
        }

        /// <summary>Each stack's box in the program's space: every block in it, switched off or not.</summary>
        private void Measure(Transform program)
        {
            _blocks.Clear();
            program.GetComponentsInChildren(true, _blocks);
            _stacks.Clear();
            var scale = program.lossyScale.x;
            foreach (var block in _blocks)
            {
                if (block == null)
                {
                    continue;
                }

                var stack = block.transform;
                while (stack.parent != null && stack.parent != program)
                {
                    stack = stack.parent;
                }

                if (stack.parent != program)
                {
                    continue;
                }

                var rect = block.RectTransform;
                var size = block.Size * (rect.lossyScale.x / scale);
                var corner = program.InverseTransformPoint(rect.position);
                var left = corner.x - (rect.pivot.x * size.x);
                var top = corner.y + ((1f - rect.pivot.y) * size.y);
                var box = Rect.MinMaxRect(left, top - size.y, left + size.x, top);
                _stacks[stack] = _stacks.TryGetValue(stack, out var known)
                    ? Rect.MinMaxRect(Mathf.Min(known.xMin, box.xMin), Mathf.Min(known.yMin, box.yMin), Mathf.Max(known.xMax, box.xMax), Mathf.Max(known.yMax, box.yMax))
                    : box;
            }

            _off.RemoveWhere(stack => stack == null);
        }

        /// <summary>With Debug Logging, the average frame time and how many stacks are switched off, every few seconds.</summary>
        private void LogFrameTime()
        {
            _frames++;
            _frameTime += Time.unscaledDeltaTime;
            var now = Time.unscaledTime;
            if (now < _nextLog)
            {
                return;
            }

            if (_frames > 0 && _nextLog > 0f && ModSettings.Instance.DebugLogging.Value)
            {
                var offBlocks = _blocks.Count(block => block != null && !block.gameObject.activeInHierarchy);
                EditorLog.Debug("Editor: " + (1000f * _frameTime / _frames).ToString("0.0") + " ms per frame, " + _off.Count + " of " + _stacks.Count + " stacks (" + offBlocks + " of " + _blocks.Count + " blocks) switched off, culling " + (ModSettings.Instance.EditorCulling.Value ? "on" : "off") + ".");
            }

            _nextLog = now + LogInterval;
            _frames = 0;
            _frameTime = 0f;
        }

        private void SetMask(bool on)
        {
            if (on && _mask == null && _controller != null && _controller.ProgramContainer != null)
            {
                var view = _controller.ProgramContainer.gameObject;
                if (view.GetComponent<RectMask2D>() == null)
                {
                    _mask = view.AddComponent<RectMask2D>();
                }
            }
            else if (!on && _mask != null)
            {
                Destroy(_mask);
                _mask = null;
            }
        }

        private void RestoreAll()
        {
            foreach (var stack in _off)
            {
                if (stack != null)
                {
                    stack.gameObject.SetActive(true);
                }
            }

            _off.Clear();
        }

        private void OnDisable()
        {
            Instances.Remove(this);
            this.RestoreAll();
            this.SetMask(false);
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
            this.RestoreAll();
            this.SetMask(false);
        }
    }

    /// <summary>
    /// Stock methods that collect the canvas's blocks with GetComponentsInChildren() see only active ones:
    /// renaming or deleting a variable would miss the switched-off stacks. Everything goes back on first.
    /// </summary>
    [HarmonyPatch]
    public static class EditorCullingRestorePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VizzyUIScript), "GetVariableElements");
            yield return AccessTools.Method(typeof(VizzyUIScript), "CanDeleteSelection");
        }

        private static void Prefix(VizzyUIScript __instance)
        {
            try
            {
                EditorCulling.RestoreFor(__instance);
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't switch the editor's blocks back on: " + e);
            }
        }
    }
}
