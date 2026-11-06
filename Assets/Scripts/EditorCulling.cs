namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.Vizzy.UI.Elements;
    using UnityEngine;

    /// <summary>
    /// Every block and text field on the Vizzy canvas runs its own Update, every frame, even far off screen,
    /// so a large program costs time each frame whatever is visible. This switches the block scripts off for
    /// blocks well outside the view and back on when they come near it. Their Update only handles a waiting
    /// layout change and a text field's editing switch, which can wait until the block is near the view again.
    /// The program itself is saved from the editor's node tree, so it isn't affected.
    /// </summary>
    public sealed class EditorCulling : MonoBehaviour
    {
        private const float CheckInterval = 0.25f;
        private const float RefreshInterval = 2f;
        private const float LogInterval = 5f;

        private readonly List<BlockElementScript> _blocks = new List<BlockElementScript>();
        private readonly HashSet<BlockElementScript> _off = new HashSet<BlockElementScript>();
        private readonly Vector3[] _corners = new Vector3[4];

        private VizzyUIController _controller;
        private float _nextCheck;
        private float _nextRefresh;
        private float _nextLog;
        private int _frames;
        private float _frameTime;

        public void Connect(VizzyUIController controller)
        {
            _controller = controller;
        }

        private static bool MouseHeld()
        {
            return UnityEngine.Input.GetMouseButton(0) || UnityEngine.Input.GetMouseButton(1) || UnityEngine.Input.GetMouseButton(2);
        }

        private void Update()
        {
            try
            {
                this.LogFrameTime();
                var culling = ModSettings.Instance.EditorCulling.Value;
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

                // Blocks come and go with editing and undo; a new one starts switched on, which is safe.
                // Counted with culling off too, for the debug line's comparison.
                if (now >= _nextRefresh)
                {
                    _nextRefresh = now + RefreshInterval;
                    _blocks.Clear();
                    program.GetComponentsInChildren(true, _blocks);
                    _off.RemoveWhere(block => block == null);
                }

                if (!culling)
                {
                    return;
                }

                // The view and one view's width and height around it, so nothing switches on in sight.
                view.GetWorldCorners(_corners);
                var width = _corners[2].x - _corners[0].x;
                var height = _corners[2].y - _corners[0].y;
                var left = _corners[0].x - width;
                var right = _corners[2].x + width;
                var bottom = _corners[0].y - height;
                var top = _corners[2].y + height;

                foreach (var block in _blocks)
                {
                    if (block == null)
                    {
                        continue;
                    }

                    var rect = block.RectTransform;
                    var scale = rect.lossyScale.x;
                    var size = block.Size * scale;
                    var x = rect.position.x - (rect.pivot.x * size.x);
                    var y = rect.position.y + ((1f - rect.pivot.y) * size.y);
                    var near = x <= right && x + size.x >= left && y >= bottom && y - size.y <= top;
                    if (near)
                    {
                        if (_off.Remove(block))
                        {
                            block.enabled = true;
                        }
                    }
                    else if (block.enabled && !block.IsDragging)
                    {
                        block.enabled = false;
                        _off.Add(block);
                    }
                }
            }
            catch (Exception e)
            {
                EditorLog.Error("Editor culling failed, so it's off for this editor: " + e);
                this.RestoreAll();
                this.enabled = false;
            }
        }

        /// <summary>With Debug Logging, the average frame time and how many blocks are switched off, every few seconds.</summary>
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
                EditorLog.Debug("Editor: " + (1000f * _frameTime / _frames).ToString("0.0") + " ms per frame, " + _off.Count + " of " + _blocks.Count + " blocks switched off, culling " + (ModSettings.Instance.EditorCulling.Value ? "on" : "off") + ".");
            }

            _nextLog = now + LogInterval;
            _frames = 0;
            _frameTime = 0f;
        }

        private void RestoreAll()
        {
            foreach (var block in _off)
            {
                if (block != null)
                {
                    block.enabled = true;
                }
            }

            _off.Clear();
        }

        private void OnDisable()
        {
            this.RestoreAll();
        }

        private void OnDestroy()
        {
            this.RestoreAll();
        }
    }
}
