namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Assets.Scripts.Vizzy;
    using ModApi.Scenes.Events;
    using UnityEngine;

    public class Mod : ModApi.Mods.GameMod
    {
        /// <summary>The mod's Harmony id, which marks its own patches.</summary>
        public const string HarmonyId = "VizzyStudio";

        private static bool _checked;
        private static bool _started;
        private static string _startupProblem;

        private Mod() : base()
        {
        }

        public static Mod Instance { get; } = GetModInstance<Mod>();

        public override void OnModLoaded()
        {
            base.OnModLoaded();
            _ = ModSettings.Instance;
            Game.Instance.SceneManager.SceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// At the first scene: a mod's OnModLoaded runs as soon as that mod loads, and Juno Harmony and
        /// Vizzy Debugger may only load later.
        /// </summary>
        private static void CheckAndStart()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToList();
            if (!assemblies.Contains("0Harmony"))
            {
                _startupProblem = Text.Get("VizzyStudio.Startup.NeedsHarmony");
                Debug.LogError("[VizzyStudio] Juno Harmony isn't loaded, so the mod is off.");
                return;
            }

            var replaced = new[] { "VizzyDebugger", "Vizzy Exporter", "Vizzy McBlinky" }.Where(assemblies.Contains).ToList();
            if (replaced.Count > 0)
            {
                _startupProblem = Text.Get("VizzyStudio.Startup.ReplacedMods", string.Join(", ", replaced));
                Debug.LogWarning("[VizzyStudio] Replaced mods are also loaded: " + string.Join(", ", replaced));
            }

            try
            {
                StartFeatures();
                _started = true;
            }
            catch (Exception e)
            {
                _startupProblem = Text.Get("VizzyStudio.Startup.HarmonyFailed");
                Debug.LogError("[VizzyStudio] Couldn't start: " + e);
            }
        }

        /// <summary>Only compiled once Harmony is known to be there. Each patch alone, so one failing leaves the others.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartFeatures()
        {
            FlightEditorUi.Register();
            FlightButtons.Register();
            if (ModSettings.Instance.CheckForUpdates.Value)
            {
                Guarded("start the update check", UpdateCheck.Start);
            }

            Guarded("add the console command", () => Assets.Packages.DevConsole.DevConsoleApi.RegisterCommand("CleanUnusedVariables", VizzyVariableCleaner.CleanOpenEditor));

            var harmony = new HarmonyLib.Harmony(HarmonyId);
            var applied = 0;
            var failed = 0;
            var patches = new List<Type> { typeof(FunkInputPatch), typeof(FunkHighlightRegisterPatch), typeof(EditorCullingLookupPatch), typeof(EditorCullingVariablePatch), typeof(EditorCullingLoadPatch), typeof(EditorTimingPatch) };
            foreach (var patch in patches)
            {
                try
                {
                    RuntimeHelpers.RunClassConstructor(patch.TypeHandle);
                    harmony.CreateClassProcessor(patch).Patch();
                    applied++;
                    if (patch == typeof(FunkInputPatch))
                    {
                        FunkInputPatch.Installed = true;
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    Debug.LogWarning("[VizzyStudio] Patch " + patch.Name + " failed: " + e);
                    if (patch == typeof(EditorCullingLookupPatch) || patch == typeof(EditorCullingVariablePatch))
                    {
                        // Without them culling could hide blocks from a rename, delete or Undo.
                        EditorCulling.Unavailable = "its patch " + patch.Name + " didn't apply";
                    }
                }
            }

            try
            {
                Assets.Scripts.Exporter.ContextMenuPatcher.Apply(harmony);
                applied++;
            }
            catch (Exception e)
            {
                failed++;
                Debug.LogWarning("[VizzyStudio] Export Block is off: " + e);
            }

            Debug.Log("[VizzyStudio] Patches: " + applied + " applied, " + failed + " failed.");
        }

        private static void OnSceneLoaded(object sender, SceneEventArgs e)
        {
            if (!_checked)
            {
                _checked = true;
                CheckAndStart();
            }

            Guarded("show the startup message", ShowStartupProblem);
            if (!_started)
            {
                return;
            }

            // Each step alone: a failure in one must not cost the others, least of all the editor.
            if (Game.InFlightScene)
            {
                Guarded("set up the flight editor", () =>
                {
                    var host = new GameObject("VizzyStudio.Controller");
                    host.AddComponent<FlightEditorController>();
                    host.AddComponent<FunkAutoFill>();
                });
            }
            else
            {
                Guarded("clear the kept edits", LatestEdits.Clear);
                if (Game.InDesignerScene)
                {
                    Guarded("set up auto-fill", () => new GameObject("VizzyStudio.AutoFill").AddComponent<FunkAutoFill>());
                }
            }

            Guarded("clear the coloring's fields", FunkHighlight.ClearScene);
        }

        private static void ShowStartupProblem()
        {
            if (_startupProblem != null && Game.Instance.UserInterface != null)
            {
                var problem = _startupProblem;
                _startupProblem = null;
                var dialog = Game.Instance.UserInterface.CreateMessageDialog(ModApi.Ui.MessageDialogType.Okay, null, true);
                dialog.MessageText = problem;
            }
        }

        private static void Guarded(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError("[VizzyStudio] Couldn't " + what + ": " + e);
            }
        }
    }
}
