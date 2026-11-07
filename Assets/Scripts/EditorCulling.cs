namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Emit;
    using System.Runtime.CompilerServices;
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

        // How far around the view stacks stay on, in view widths and heights: room for a quarter second of panning.
        private const float Margin = 0.5f;

        // A generous guess at a block's height and a stack's width, in program units, for blocks not laid out yet.
        private const float EstimatedBlockHeight = 60f;
        private const float EstimatedWidth = 2000f;

        private static readonly List<EditorCulling> Instances = new List<EditorCulling>();

        /// <summary>Why culling can't run at all, such as a patch it needs that didn't apply; null when it can.</summary>
        public static string Unavailable { get; set; }

        private readonly List<BlockElementScript> _blocks = new List<BlockElementScript>();
        private readonly Dictionary<Transform, Rect> _stacks = new Dictionary<Transform, Rect>();
        private readonly Dictionary<Transform, int> _stackBlocks = new Dictionary<Transform, int>();
        private readonly Dictionary<Transform, int> _unsized = new Dictionary<Transform, int>();

        // The mask clips every block it covers, which costs time when nearly all of them are in view (zoomed
        // out), so it's only on while more blocks that are switched on lie outside the view than inside it.
        private bool _maskWanted;
        private readonly HashSet<Transform> _off = new HashSet<Transform>();

        // Every editor's switched-off stacks, for IsCulled.
        private static readonly HashSet<Transform> Off = new HashSet<Transform>();
        private readonly Vector3[] _corners = new Vector3[4];

        private VizzyUIController _controller;

        // Why culling stays off in this editor (Unavailable, or another mod that patches the Vizzy editor).
        private string _blocked;
        private bool _blockedLogged;

        // The stock view has no mask, so Unity draws and batches every block. A RectMask2D on the view is
        // Unity's own way to leave out graphics outside it; it's only added while culling is on.
        private RectMask2D _mask;
        private float _nextCheck;
        private float _nextRefresh;
        private float _nextLog;
        private int _frames;
        private float _frameTime;

        // After a load, the next few frames' times are logged: the first one lays out what's switched on.
        private int _framesToLog;

        public void Connect(VizzyUIController controller)
        {
            _controller = controller;
            _blocked = Unavailable;
            if (_blocked == null)
            {
                try
                {
                    var others = OtherVizzyEditorPatches();
                    if (others.Count > 0)
                    {
                        _blocked = "other mods patch the Vizzy editor (" + string.Join(", ", others) + ")";
                    }
                }
                catch (Exception e)
                {
                    _blocked = "couldn't check other mods' patches: " + e.Message;
                }
            }

            this.FirstPass();
            this.LogLoad(-1);
        }

        /// <summary>
        /// The Harmony ids of other mods that patch the stock Vizzy editor. Their code may look only at active
        /// blocks, and it can't be checked, so culling stays off with them.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> OtherVizzyEditorPatches()
        {
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var type = method.DeclaringType;
                while (type != null && type.DeclaringType != null)
                {
                    type = type.DeclaringType;
                }

                if (type == null || type.Namespace == null || !type.Namespace.StartsWith("Assets.Scripts.Vizzy", StringComparison.Ordinal))
                {
                    continue;
                }

                var info = Harmony.GetPatchInfo(method);
                if (info == null)
                {
                    continue;
                }

                foreach (var owner in info.Owners)
                {
                    if (owner != Mod.HarmonyId)
                    {
                        owners.Add(owner);
                    }
                }
            }

            return owners.ToList();
        }

        /// <summary>Whether culling may switch blocks off in this editor of the given stock editor.</summary>
        public static bool ActiveFor(object vizzyUI)
        {
            foreach (var culling in Instances)
            {
                if (culling != null && culling._blocked == null && culling._controller != null && ReferenceEquals(culling._controller.VizzyUI, vizzyUI))
                {
                    return ModSettings.Instance.EditorCulling.Value;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the object is inside a stack culling switched off. Stock lookups patched by
        /// <see cref="EditorCullingLookupPatch"/> see these as if they were on, and still skip anything else inactive.
        /// </summary>
        public static bool IsCulled(Transform transform)
        {
            if (Off.Count == 0)
            {
                return false;
            }

            for (var t = transform; t != null; t = t.parent)
            {
                if (Off.Contains(t))
                {
                    return true;
                }
            }

            return false;
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
                if (culling && _blocked != null)
                {
                    if (!_blockedLogged)
                    {
                        _blockedLogged = true;
                        EditorLog.Warning("Editor Culling is off for this editor: " + _blocked + ".");
                    }

                    culling = false;
                }

                this.SetMask(culling && _maskWanted);
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
                this.Pass(culling, now >= _nextRefresh);
            }
            catch (Exception e)
            {
                EditorLog.Error("Editor culling failed, so it's off for this editor: " + e);
                this.RestoreAll();
                this.SetMask(false);
                this.enabled = false;
            }
        }

        /// <summary>Measures if asked, then switches stacks off and on for where the view is now.</summary>
        private void Pass(bool culling, bool measure)
        {
            var program = _controller == null ? null : (_controller.VizzyUI as VizzyUIScript)?.ProgramTransform;
            var view = _controller == null || _controller.ProgramContainer == null ? null : (RectTransform)_controller.ProgramContainer.transform;
            if (program == null || view == null)
            {
                return;
            }

            // Stacks come and go with editing and undo; a new one starts switched on, which is safe.
            if (measure)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                this.Measure(program);
            }

            if (!culling)
            {
                return;
            }

            // The view and half a view's width and height around it, in the program's own space, where a
            // stack's place only changes when it's edited, not when the view pans or zooms.
            view.GetWorldCorners(_corners);
            var a = program.InverseTransformPoint(_corners[0]);
            var b = program.InverseTransformPoint(_corners[2]);
            var width = Margin * Mathf.Abs(b.x - a.x);
            var height = Margin * Mathf.Abs(b.y - a.y);
            var near = Rect.MinMaxRect(Mathf.Min(a.x, b.x) - width, Mathf.Min(a.y, b.y) - height, Mathf.Max(a.x, b.x) + width, Mathf.Max(a.y, b.y) + height);
            var inView = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            var busy = Busy();
            var inside = 0;
            var outside = 0;

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
                        Off.Remove(stack);
                        stack.gameObject.SetActive(true);
                    }

                    _stackBlocks.TryGetValue(stack, out var count);
                    if (pair.Value.Overlaps(inView))
                    {
                        inside += count;
                    }
                    else
                    {
                        outside += count;
                    }
                }
                else if (stack.gameObject.activeSelf)
                {
                    stack.gameObject.SetActive(false);
                    _off.Add(stack);
                    Off.Add(stack);
                }
            }

            // With some room either way, so it doesn't flicker on and off at the boundary.
            _maskWanted = _maskWanted ? outside * 2 >= inside : outside > inside;
        }

        /// <summary>
        /// A pass right away, when the editor connects and right after a program loads: the blocks are created
        /// then but lay out on their first active frame, so stacks switched off now never lay out until they're
        /// near. Their sizes aren't known yet, so they're estimated (generously), and measured again soon.
        /// </summary>
        public void FirstPass()
        {
            try
            {
                if (!this.isActiveAndEnabled || _blocked != null || !ModSettings.Instance.EditorCulling.Value)
                {
                    return;
                }

                this.Pass(true, true);
                _nextRefresh = Time.unscaledTime + CheckInterval;
                _nextCheck = Time.unscaledTime + CheckInterval;
            }
            catch (Exception e)
            {
                EditorLog.Warning("Editor culling's first pass failed: " + e.Message);
            }
        }

        /// <summary>After a stock program load: the editor's first pass, and with Debug Logging the time.</summary>
        public static void AfterLoad(object vizzyUI, long milliseconds)
        {
            foreach (var culling in Instances.ToList())
            {
                if (culling != null && culling._controller != null && ReferenceEquals(culling._controller.VizzyUI, vizzyUI))
                {
                    culling.FirstPass();
                    culling.LogLoad(milliseconds);
                    return;
                }
            }

            if (ModSettings.Instance.DebugLogging.Value)
            {
                EditorLog.Debug("Editor: program loaded in " + milliseconds + " ms, before culling was connected.");
            }
        }

        private void LogLoad(long milliseconds)
        {
            if (ModSettings.Instance.DebugLogging.Value)
            {
                var blocks = _stackBlocks.Values.Sum();
                EditorLog.Debug("Editor: " + (milliseconds < 0 ? "culling connected" : "program loaded in " + milliseconds + " ms") + " (" + blocks + " blocks); first pass switched off " + _off.Count + " of " + _stacks.Count + " stacks.");
                _framesToLog = 3;
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
            _stackBlocks.Clear();
            _unsized.Clear();
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
                if (size.x <= 0f || size.y <= 0f)
                {
                    // Not laid out yet: only its corner is known; the stack gets an estimate below.
                    _unsized.TryGetValue(stack, out var unsized);
                    _unsized[stack] = unsized + 1;
                }

                var corner = program.InverseTransformPoint(rect.position);
                var left = corner.x - (rect.pivot.x * size.x);
                var top = corner.y + ((1f - rect.pivot.y) * size.y);
                var box = Rect.MinMaxRect(left, top - size.y, left + size.x, top);
                _stacks[stack] = _stacks.TryGetValue(stack, out var known)
                    ? Rect.MinMaxRect(Mathf.Min(known.xMin, box.xMin), Mathf.Min(known.yMin, box.yMin), Mathf.Max(known.xMax, box.xMax), Mathf.Max(known.yMax, box.yMax))
                    : box;
                _stackBlocks.TryGetValue(stack, out var blocks);
                _stackBlocks[stack] = blocks + 1;
            }

            // A stack with blocks not laid out may reach much further than its known part: each such block
            // counts as EstimatedBlockHeight more below its top, and the stack as at least EstimatedWidth wide.
            // Too big is safe: a stack switched on needlessly lays out and is measured properly next time.
            foreach (var pair in _unsized)
            {
                if (_stacks.TryGetValue(pair.Key, out var known))
                {
                    _stacks[pair.Key] = Rect.MinMaxRect(known.xMin, Mathf.Min(known.yMin, known.yMax - (pair.Value * EstimatedBlockHeight)), Mathf.Max(known.xMax, known.xMin + EstimatedWidth), known.yMax);
                }
            }

            _off.RemoveWhere(stack => stack == null);
            Off.RemoveWhere(stack => stack == null);
        }

        /// <summary>With Debug Logging, the average frame time and how many stacks are switched off, every few seconds.</summary>
        private void LogFrameTime()
        {
            if (_framesToLog > 0)
            {
                _framesToLog--;
                EditorLog.Debug("Editor: frame after loading took " + (1000f * Time.unscaledDeltaTime).ToString("0") + " ms.");
            }

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
                EditorLog.Debug("Editor: " + (1000f * _frameTime / _frames).ToString("0.0") + " ms per frame, " + _off.Count + " of " + _stacks.Count + " stacks (" + offBlocks + " of " + _blocks.Count + " blocks) switched off, culling " + (ModSettings.Instance.EditorCulling.Value ? "on" : "off") + ", mask " + (_mask != null ? "on" : "off") + ".");
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
                Off.Remove(stack);
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
    /// renaming or deleting a variable would miss the switched-off stacks, deleting a custom instruction would
    /// miss its calls there, and loading a program (also Undo, Redo, Import and New) would leave them behind.
    /// Each such call is replaced by one that also sees blocks in switched-off stacks, so nothing has to be
    /// switched on (which would build every block not yet built, a freeze of seconds). Other inactive objects
    /// are still skipped, as stock, so with culling off the result is the stock one. The full list of such
    /// stock methods, with why the others are harmless, is tools/editor_lookups.txt.
    /// </summary>
    [HarmonyPatch]
    public static class EditorCullingLookupPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VizzyUIScript), "GetVariableElements");
            yield return AccessTools.Method(typeof(VizzyUIScript), "CanDeleteSelection");
            yield return AccessTools.Method(typeof(VizzyUIScript), "LoadFlightProgram", new[] { typeof(System.Xml.Linq.XElement), typeof(bool), typeof(bool), typeof(Vector2) });
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var onComponent = AccessTools.Method(typeof(EditorCullingLookupPatch), nameof(InChildren));
            var onGameObject = AccessTools.Method(typeof(EditorCullingLookupPatch), nameof(InChildrenOfGameObject));
            var replaced = 0;
            var result = new List<CodeInstruction>();
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo method && method.Name == "GetComponentsInChildren"
                    && method.IsGenericMethod && method.GetParameters().Length == 0
                    && (method.DeclaringType == typeof(Component) || method.DeclaringType == typeof(GameObject)))
                {
                    var helper = method.DeclaringType == typeof(GameObject) ? onGameObject : onComponent;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = helper.MakeGenericMethod(method.GetGenericArguments());
                    replaced++;
                }

                result.Add(instruction);
            }

            // The game changed: without these culling could hide blocks from it, so the patch fails and culling stays off.
            if (replaced == 0)
            {
                throw new InvalidOperationException(original.Name + " no longer calls GetComponentsInChildren<T>()");
            }

            return result;
        }

        /// <summary>component.GetComponentsInChildren&lt;T&gt;(), but blocks in switched-off stacks count as active.</summary>
        public static T[] InChildren<T>(Component component)
        {
            return Visible(component.GetComponentsInChildren<T>(true));
        }

        public static T[] InChildrenOfGameObject<T>(GameObject gameObject)
        {
            return Visible(gameObject.GetComponentsInChildren<T>(true));
        }

        private static T[] Visible<T>(T[] found)
        {
            var kept = new List<T>(found.Length);
            foreach (var item in found)
            {
                var component = item as Component;
                if (component == null || component.gameObject.activeInHierarchy || EditorCulling.IsCulled(component.transform))
                {
                    kept.Add(item);
                }
            }

            return kept.Count == found.Length ? found : kept.ToArray();
        }
    }

    /// <summary>
    /// A safety net behind <see cref="EditorCullingLookupPatch"/>: after a stock rename or delete of a global
    /// variable, any block that still uses the old name, switched off or not, gets what the stock method does to
    /// the blocks it found (renamed, or removed). Normally there's none; one is logged. Only while culling is on.
    /// </summary>
    [HarmonyPatch]
    public static class EditorCullingVariablePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VizzyUIScript), "RenameVariable", new[] { typeof(string), typeof(string) });
            yield return AccessTools.Method(typeof(VizzyUIScript), "DeleteVariable", new[] { typeof(string) });
        }

        private static void Postfix(VizzyUIScript __instance, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                if (!EditorCulling.ActiveFor(__instance) || __instance.ProgramTransform == null)
                {
                    return;
                }

                var oldName = (string)__args[0];
                var rename = __originalMethod.Name == "RenameVariable";
                var missed = 0;
                foreach (var block in __instance.ProgramTransform.GetComponentsInChildren<BlockElementScript>(true))
                {
                    if (block == null || !(block.Node is ModApi.Craft.Program.VariableExpression variable) || variable.IsLocal || variable.VariableName != oldName)
                    {
                        continue;
                    }

                    missed++;
                    if (rename)
                    {
                        variable.VariableName = (string)__args[1];
                        block.OnChildSizeChanged();
                    }
                    else
                    {
                        block.Destroy();
                    }
                }

                if (missed > 0)
                {
                    EditorLog.Warning("Editor Culling: the stock " + __originalMethod.Name + " missed " + missed + " blocks using '" + oldName + "'; they were " + (rename ? "renamed" : "removed") + " too.");
                }
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't check the blocks after a variable change: " + e);
            }
        }
    }

    /// <summary>
    /// Times the stock program load and, right after it, runs the editor's first culling pass, before the new
    /// blocks get their first frame (and lay out). Losing it only loses that head start.
    /// </summary>
    [HarmonyPatch]
    public static class EditorCullingLoadPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(VizzyUIScript), "LoadFlightProgram", new[] { typeof(System.Xml.Linq.XElement), typeof(bool), typeof(bool), typeof(Vector2) });
        }

        private static void Prefix(out System.Diagnostics.Stopwatch __state)
        {
            __state = System.Diagnostics.Stopwatch.StartNew();
        }

        private static void Postfix(VizzyUIScript __instance, System.Diagnostics.Stopwatch __state)
        {
            try
            {
                EditorCulling.AfterLoad(__instance, __state == null ? -1 : __state.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                EditorLog.Warning("Editor culling after loading failed: " + e.Message);
            }
        }
    }
}
