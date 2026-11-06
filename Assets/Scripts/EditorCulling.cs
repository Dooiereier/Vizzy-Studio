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

        private static readonly List<EditorCulling> Instances = new List<EditorCulling>();

        /// <summary>Why culling can't run at all, such as a patch it needs that didn't apply; null when it can.</summary>
        public static string Unavailable { get; set; }

        private readonly List<BlockElementScript> _blocks = new List<BlockElementScript>();
        private readonly Dictionary<Transform, Rect> _stacks = new Dictionary<Transform, Rect>();
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
                            Off.Remove(stack);
                            stack.gameObject.SetActive(true);
                        }
                    }
                    else if (stack.gameObject.activeSelf)
                    {
                        stack.gameObject.SetActive(false);
                        _off.Add(stack);
                        Off.Add(stack);
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
            Off.RemoveWhere(stack => stack == null);
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
}
