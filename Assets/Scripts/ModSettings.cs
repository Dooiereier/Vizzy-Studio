namespace Assets.Scripts
{
    using ModApi.Settings.Core;
    using UnityEngine;

    /// <summary>The letter keys the editor shortcut can use, with the chosen modifier.</summary>
    public enum EditorKey
    {
        A,

        B,

        C,

        D,

        E,

        F,

        G,

        H,

        I,

        J,

        K,

        L,

        M,

        N,

        O,

        P,

        Q,

        R,

        S,

        T,

        U,

        V,

        W,

        X,

        Y,

        Z,
    }

    /// <summary>
    /// The key held with the editor letter. Shift and Ctrl are the throttle in the game's flight
    /// controls, so Alt (Option on a Mac) is the default.
    /// </summary>
    public enum EditorModifier
    {
        [EnumOption("{VizzyStudio.Settings.Modifier.Alt}", "{VizzyStudio.Settings.Modifier.Alt.Description}")]
        Alt,

        [EnumOption("{VizzyStudio.Settings.Modifier.Control}", "{VizzyStudio.Settings.Modifier.Control.Description}")]
        Control,

        [EnumOption("{VizzyStudio.Settings.Modifier.Shift}", "{VizzyStudio.Settings.Modifier.Shift.Description}")]
        Shift,
    }

    /// <summary>The mod's settings (Settings → Mods → Vizzy Studio).</summary>
    public class ModSettings : SettingsCategory<ModSettings>
    {
        private static ModSettings _instance;

        public ModSettings() : base("Vizzy Studio")
        {
        }

        public static ModSettings Instance => _instance ?? (_instance = Game.Instance.Settings.ModSettings.GetCategory<ModSettings>());

        public EnumSetting<EditorKey> Key { get; private set; }

        public EnumSetting<EditorModifier> Modifier { get; private set; }

        public BoolSetting DebugLogging { get; private set; }

        public BoolSetting ShowFlightButtons { get; private set; }

        public BoolSetting CheckForUpdates { get; private set; }

        public BoolSetting ArrangeMfd { get; private set; }

        public BoolSetting EditorCulling { get; private set; }

        public BoolSetting FunkAutoFill { get; private set; }

        public BoolSetting FunkSyntaxHighlight { get; private set; }

        public KeyCode KeyCode => KeyCode.A + (int)this.Key.Value;

        /// <summary>Gets the shortcut as text, for messages.</summary>
        public string ShortcutText => ModifierName(this.Modifier.Value) + "+" + (char)('A' + (int)this.Key.Value);

        /// <summary>Gets the shortcut that goes back to the quicksave's program, as text.</summary>
        public string RevertShortcutText => ModifierName(this.Modifier.Value) + "+" + ModifierName(RevertModifier(this.Modifier.Value)) + "+" + (char)('A' + (int)this.Key.Value);

        /// <summary>The extra key that makes the shortcut a revert: Shift, or Alt when Shift is the modifier.</summary>
        public static EditorModifier RevertModifier(EditorModifier modifier)
        {
            return modifier == EditorModifier.Shift ? EditorModifier.Alt : EditorModifier.Shift;
        }

        public static bool IsHeld(EditorModifier modifier)
        {
            switch (modifier)
            {
                case EditorModifier.Alt:
                    return UnityEngine.Input.GetKey(KeyCode.LeftAlt) || UnityEngine.Input.GetKey(KeyCode.RightAlt);
                case EditorModifier.Control:
                    return UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
                default:
                    return UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            }
        }

        public static string ModifierName(EditorModifier modifier)
        {
            switch (modifier)
            {
                case EditorModifier.Alt:
                    return Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor ? "Option" : "Alt";
                case EditorModifier.Control:
                    return "Ctrl";
                default:
                    return "Shift";
            }
        }

        // XML names are given explicitly: the game derives them from the display name otherwise.
        protected override void InitializeSettings()
        {
            this.CheckForUpdates = this.CreateBool("{VizzyStudio.Settings.CheckForUpdates}", "CheckForUpdates")
                .SetDescription("{VizzyStudio.Settings.CheckForUpdates.Description}")
                .SetDefault(true);

            this.DebugLogging = this.CreateBool("{VizzyStudio.Settings.DebugLogging}", "DebugLogging")
                .SetDescription("{VizzyStudio.Settings.DebugLogging.Description}")
                .SetDefault(false);

            this.Key = this.CreateEnum<EditorKey>("{VizzyStudio.Settings.Key}", "Key")
                .SetDescription("{VizzyStudio.Settings.Key.Description}")
                .SetDefault(EditorKey.A);

            this.Modifier = this.CreateEnum<EditorModifier>("{VizzyStudio.Settings.Modifier}", "Modifier")
                .SetDescription("{VizzyStudio.Settings.Modifier.Description}")
                .SetDefault(EditorModifier.Alt);

            this.ShowFlightButtons = this.CreateBool("{VizzyStudio.Settings.ShowFlightButtons}", "ShowFlightButtons")
                .SetDescription("{VizzyStudio.Settings.ShowFlightButtons.Description}")
                .SetDefault(true);

            this.ArrangeMfd = this.CreateBool("{VizzyStudio.Settings.ArrangeMfd}", "ArrangeMfd")
                .SetDescription("{VizzyStudio.Settings.ArrangeMfd.Description}")
                .SetDefault(false);

            this.EditorCulling = this.CreateBool("{VizzyStudio.Settings.EditorCulling}", "EditorCulling")
                .SetDescription("{VizzyStudio.Settings.EditorCulling.Description}")
                .SetDefault(false);

            this.FunkSyntaxHighlight = this.CreateBool("{VizzyStudio.Settings.FunkSyntaxHighlight}", "FunkSyntaxHighlight")
                .SetDescription("{VizzyStudio.Settings.FunkSyntaxHighlight.Description}")
                .SetDefault(true);

            this.FunkAutoFill = this.CreateBool("{VizzyStudio.Settings.FunkAutoFill}", "FunkAutoFill")
                .SetDescription("{VizzyStudio.Settings.FunkAutoFill.Description}")
                .SetDefault(true);
        }
    }
}
