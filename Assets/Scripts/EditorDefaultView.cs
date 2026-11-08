namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Assets.Scripts.Vizzy.UI;
    using HarmonyLib;
    using UnityEngine;

    /// <summary>
    /// Each editor's starting view (the stock zoom and position, before a remembered view replaces it), so a
    /// program chosen in the stock Load menu opens there instead of where the previous program was left.
    /// </summary>
    public static class EditorDefaultView
    {
        private static readonly Dictionary<VizzyUIScript, View> Views = new Dictionary<VizzyUIScript, View>();

        /// <summary>The editor's view now, as its default; only the first time for each editor.</summary>
        public static void Capture(VizzyUIScript editor, ProgramContainerScript container)
        {
            if (editor == null || container == null || editor.ProgramTransform == null)
            {
                return;
            }

            foreach (var gone in Views.Keys.Where(key => key == null).ToList())
            {
                Views.Remove(gone);
            }

            if (!Views.ContainsKey(editor))
            {
                Views[editor] = new View { Container = container, Position = editor.ProgramTransform.localPosition, Zoom = container.Zoom };
            }
        }

        public static void Reset(VizzyUIScript editor)
        {
            if (editor != null && Views.TryGetValue(editor, out var view) && view.Container != null && editor.ProgramTransform != null)
            {
                // Zoom first: it moves the canvas too.
                view.Container.Zoom = view.Zoom;
                editor.ProgramTransform.localPosition = view.Position;
            }
        }

        private struct View
        {
            public ProgramContainerScript Container;
            public Vector3 Position;
            public float Zoom;
        }
    }

    /// <summary>
    /// A program loaded from the stock Load menu goes to the editor's starting view. Import (which adds to the
    /// program) keeps the view, and so do Undo, Redo and New, which load programs too.
    /// </summary>
    [HarmonyPatch]
    public static class LoadMenuViewPatch
    {
        private static bool _fromMenu;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LoadProgramViewModel), "OnPrimaryButtonClicked");
            yield return AccessTools.Method(typeof(VizzyUIScript), "LoadFlightProgram", new[] { typeof(System.Xml.Linq.XElement), typeof(bool), typeof(bool), typeof(Vector2) });
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            if (__originalMethod.DeclaringType == typeof(LoadProgramViewModel))
            {
                _fromMenu = true;
            }
        }

        // Clears the mark even when the Load menu's handler throws, so a later Undo doesn't reset the view.
        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__originalMethod.DeclaringType == typeof(LoadProgramViewModel))
            {
                _fromMenu = false;
            }

            return __exception;
        }

        private static void Postfix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (__originalMethod.DeclaringType == typeof(LoadProgramViewModel))
            {
                _fromMenu = false;
                return;
            }

            try
            {
                var import = __args.Length > 2 && __args[2] is bool flag && flag;
                if (_fromMenu && !import)
                {
                    EditorDefaultView.Reset(__instance as VizzyUIScript);
                }
            }
            catch (Exception e)
            {
                EditorLog.Warning("Couldn't reset the view after loading: " + e.Message);
            }
        }
    }
}
