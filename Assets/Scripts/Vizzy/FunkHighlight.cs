using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Assets.Scripts.Vizzy.UI;
using Assets.Scripts.Vizzy.UI.Elements;
using HarmonyLib;
using ModApi.Craft.Program;
using ModApi.Craft.Program.Expressions;
using ModApi.Craft.Program.Instructions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Assets.Scripts.Vizzy
{
    /// <summary>
    /// Funk expression syntax highlighting, by setting TMP vertex colors per character.
    ///
    /// Inserts no rich text markup: it changes the TMP_Text mesh vertex colors
    /// (textInfo.meshInfo[i].colors32) directly. textComponent.text stays plain text,
    /// so caret placement (from characterInfo) and selection highlighting (separate geometry)
    /// are unaffected, and input.text and ExpressionResult.TextValue are always plain text.
    ///
    /// Timing: subscribes to TMP_Text.OnPreRenderText. It fires inside GenerateTextMesh, before the mesh
    /// data is uploaded (m_mesh.colors32 = meshInfo[0].colors32), so changes to meshInfo.colors32 here
    /// are uploaded next and always take effect, for typing, display and drag reordering alike.
    ///
    /// Mapping: when a block starts (Harmony postfix on Start), textComponent.GetInstanceID() goes in a
    /// dictionary and the event is subscribed, without searching up the hierarchy with GetComponentInParent.
    /// </summary>
    public static class FunkHighlight
    {
        // ---- Lexical rules: the same as ModApi.Expressions.Parser.TokenDefs, keeping the \G anchors ----
        private static readonly Regex WhitespacePat = new Regex(@"\G\s*");
        private static readonly Regex StringPat = new Regex(@"\G""((?:[^\\""]|\\.)*)""");
        private static readonly Regex DataSlotPat = new Regex(@"\G\[[^\]]+\]");
        private static readonly Regex NumberPat = new Regex(@"\G[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
        private static readonly Regex NamePat = new Regex(@"\G(?:v:)?[A-z_\.\$][A-z_0-9\.]*");
        private static readonly Regex OperatorPat = new Regex(@"\G(?:(?:[<>!]=?)|[+\-/*&|=%?:])");
        private static readonly Regex SeparatorPat = new Regex(@"\G,");
        private static readonly Regex GroupPat = new Regex(@"\G[()]");

        
        private static readonly Color32 DefaultColor = new Color32(255, 255, 255, 255);
        private static readonly Color32 ColorNumber = FromHex("#B5CEA8");   // number: green
        private static readonly Color32 ColorString = FromHex("#CE9178");   // string: orange
        private static readonly Color32 ColorOperator = FromHex("#D4D4D4"); // operator: light gray
        private static readonly Color32 ColorFunction = FromHex("#DCDCAA"); // function: yellow
        private static readonly Color32 ColorKeyword = FromHex("#569CD6");  // keyword: blue
        private static readonly Color32 ColorVariable = FromHex("#9CDCFE"); // variable: light blue
        private static readonly Color32 ColorPart = FromHex("#808080");     // part selector: gray
        private static readonly Color32 ColorFlightData = FromHex("#F48FB1"); // FD. and OD. data: rose
        private static readonly Color32 ColorDataSlot = FromHex("#4EC9B0"); // data slot: teal
        private static readonly Color32 ColorGroup = FromHex("#D4D4D4");    // bracket: light gray
        private static readonly Color32 ColorVector = FromHex("#4EC9B0");   // v: vector prefix: teal
        private static readonly Color32 ColorError = FromHex("#FF4D4D");    // syntax error: red

        private static bool _namesFailed;

        private static readonly HashSet<string> Functions = NamesOf(() => FunkNames.Functions);

        private static readonly HashSet<string> Keywords = NamesOf(() => FunkNames.Constants);

        // Bare control names: the game's ResolveVariable looks these up with GetProperty(text).
        private static readonly HashSet<string> ControlsProperties = NamesOf(() => FunkNames.Controls);

        // Argument counts per function, from Context.GetDefaults, Mathd and CraftService. Keys are lowercase.
        // Only functions listed here get an argument count check; sum, rate, PID, smooth and custom functions don't.
        private static readonly Dictionary<string, int> FunctionArgCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Math and Mathd scalar functions
            { "abs", 1 }, { "acos", 1 }, { "asin", 1 }, { "atan", 1 }, { "atan2", 2 },
            { "cos", 1 }, { "sin", 1 }, { "tan", 1 }, { "log", 2 }, { "ln", 1 }, { "log10", 1 },
            { "max", 2 }, { "min", 2 }, { "pow", 2 }, { "sqrt", 1 }, { "exp", 1 }, { "ceil", 1 }, { "floor", 1 },
            { "clamp", 3 }, { "clamp01", 1 }, { "deltaangle", 2 }, { "inverselerp", 3 },
            { "lerp", 3 }, { "lerpangle", 3 }, { "lerpunclamped", 3 }, { "pingpong", 2 },
            { "repeat", 2 }, { "round", 1 }, { "sign", 1 }, { "smoothstep", 3 },
            // Vector construction and extraction
            { "vec", 3 }, { "x", 1 }, { "y", 1 }, { "z", 1 },
            // Vector3d static functions
            { "angle", 2 }, { "clampmagnitude", 2 }, { "cross", 2 }, { "distance", 2 }, { "dot", 2 },
            { "exclude", 2 }, { "isnan", 1 }, { "lerp3", 3 }, { "magnitude", 1 }, { "max3", 2 },
            { "min3", 2 }, { "movetowards", 3 }, { "normalize", 1 }, { "project", 2 }, { "projectonplane", 2 },
            { "reflect", 2 }, { "scale", 2 }, { "signedangle", 3 }, { "slerp", 3 }, { "sqrmagnitude", 1 },
            // List readers
            { "listnum", 2 }, { "listvec", 2 }, { "listbool", 2 }, { "liststr", 2 }, { "listlen", 1 }
        };

        // Names usable as FlightData.X or FD.X.
        private static readonly HashSet<string> FlightDataProperties = NamesOf(() => FunkNames.FlightData);

        // Names usable as OrbitData.X or OD.X.
        private static readonly HashSet<string> OrbitDataProperties = NamesOf(() => FunkNames.OrbitData);

        // textComponent.GetInstanceID() -> its TMP_InputField
        private static readonly Dictionary<int, TMP_InputField> _textToInput = new Dictionary<int, TMP_InputField>();

        // textComponent.GetInstanceID() -> its error underline overlay, a child of textComponent
        private static readonly Dictionary<int, TextMeshProUGUI> _errorOverlays = new Dictionary<int, TextMeshProUGUI>();

        // Variable name cache: the current program and a 0.5 s timestamp, to avoid walking the tree every frame
        private static FlightProgram _varCacheFp;
        private static HashSet<string> _varCacheNames;
        private static float _varCacheTime;

        /// <summary>
        /// Registers a Funk field's textComponent and subscribes to OnPreRenderText, when a block starts.
        /// Only real Funk expression blocks; comment blocks and other TextInputElementScripts are left alone.
        /// </summary>
        /// <summary>If the game's names can't be read, coloring turns off rather than mark everything red.</summary>
        private static HashSet<string> NamesOf(Func<IEnumerable<FunkName>> names)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var name in names())
                {
                    set.Add(name.Name);
                }
            }
            catch (Exception e)
            {
                _namesFailed = true;
                EditorLog.Error("Funk coloring is off: couldn't read the game's names: " + e);
            }

            return set;
        }

        /// <summary>The fields of the last scene are gone, so nothing of them is kept.</summary>
        public static void ClearScene()
        {
            _textToInput.Clear();
            _errorOverlays.Clear();
            _varCacheFp = null;
            _varCacheNames = null;
        }

        public static void Register(TMP_InputField input)
        {
            if (input == null)
            {
                return;
            }
            TextInputElementScript block = input.GetComponent<TextInputElementScript>();
            if (block == null)
            {
                return;
            }
            if (!IsFunkExpressionBlock(block))
            {
                return; // Not a Funk block (a comment, for example): no subscription, no effect
            }
            TMP_Text tc = input.textComponent;
            if (tc == null)
            {
                return;
            }
            _textToInput[tc.GetInstanceID()] = input;
            // Avoid subscribing twice: a block can start or rebuild more than once
            tc.OnPreRenderText -= OnBeforeMeshUpload;
            tc.OnPreRenderText += OnBeforeMeshUpload;
        }

        /// <summary>
        /// Whether this block is a Funk expression block: an Input token with no validation, on a
        /// ConstantExpression. Text inputs in other instructions, such as CommentInstruction, aren't
        /// ConstantExpressions, so highlighting and errors don't touch them.
        /// </summary>
        public static bool IsFunkExpressionBlock(TextInputElementScript block)
        {
            if (block == null || !IsHighlightEnabled())
            {
                return false;
            }
            NodeFormat.Token token = block.Token;
            if (token == null
                || token.TokenType != NodeFormat.TokenType.Input
                || !string.IsNullOrEmpty(token.Validation))
            {
                return false;
            }
            // A real Funk slot has EvaluationExpression as its parent (EvaluationExpression.Evaluate compiles
            // this slot's text with GetInputExpression).
            // String inputs of display, log, broadcast and part id instructions, and comment blocks,
            // have other parents, so they're excluded.
            return block.Parent != null && block.Parent.Node is EvaluationExpression;
        }

        /// <summary>
        /// Collects the variable names of the current program, as FunkNames.ProgramVariables finds them,
        /// with a 0.5 s cache. Returns null with no program available, which skips variable checks to avoid false errors.
        /// </summary>
        private static HashSet<string> GetVariableNames(TextInputElementScript block)
        {
            if (block == null)
            {
                return null;
            }
            FlightProgram fp = null;
            try
            {
                IVizzyUI vizzy = block.VizzyUI;
                if (vizzy != null)
                {
                    fp = vizzy.FlightProgram;
                }
            }
            catch
            {
                return null;
            }
            if (fp == null)
            {
                _varCacheFp = null;
                _varCacheNames = null;
                return null;
            }
            if (_varCacheFp == fp && Time.unscaledTime - _varCacheTime < 0.5f)
            {
                return _varCacheNames;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (block.VizzyUI is VizzyUIScript editor)
                {
                    names = FunkNames.ProgramVariables(editor);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[FunkHighlight] GetVariableNames: " + e.Message);
            }
            _varCacheFp = fp;
            _varCacheNames = names;
            _varCacheTime = Time.unscaledTime;
            return names;
        }

        private static bool IsHighlightEnabled()
        {
            return !_namesFailed && ModSettings.Instance.FunkSyntaxHighlight.Value;
        }

        // ---- Render entry: OnPreRenderText, which fires before TMP uploads the mesh data ----

        /// <summary>Called just before TMP uploads meshInfo to m_mesh: changes to colors32 here always take effect.</summary>
        public static void OnBeforeMeshUpload(TMP_TextInfo textInfo)
        {
            try
            {
                if (textInfo == null || textInfo.textComponent == null)
                {
                    return;
                }
                TMP_Text textComponent = textInfo.textComponent;
                TMP_InputField input = null;
                if (!_textToInput.TryGetValue(textComponent.GetInstanceID(), out input) || input == null)
                {
                    return; // Not registered: text outside a Funk field, skipped as normal
                }
                TextInputElementScript block = input.GetComponent<TextInputElementScript>();
                if (!IsFunkExpressionBlock(block))
                {
                    return;
                }
                HashSet<string> variableNames = GetVariableNames(block);
                ApplyMeshColors(input, textComponent, variableNames);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[FunkHighlight] OnPreRenderText: " + e.Message);
            }
        }

        /// <summary>Colors textComponent's meshInfo.colors32 per character by token, without dirtying or rebuilding.</summary>
        private static void ApplyMeshColors(TMP_InputField input, TMP_Text textComponent, HashSet<string> variableNames)
        {
            TMP_TextInfo textInfo = textComponent.textInfo;
            if (textInfo == null || textInfo.characterCount == 0)
            {
                return;
            }
            string text = input.text;
            int errStart, errEnd;
            string errMsg;
            bool hasError = FindError(text, out errStart, out errEnd, out errMsg, variableNames);
            Color32[] perChar = TokenizeToColors(text, errStart, errEnd);

            // Screen range of the visible characters in the error span, for the red underline overlay
            bool errFound = false;
            float errX0 = 0f, errX1 = 0f, errY = 0f;

            for (int i = 0; i < textInfo.characterCount; i++)
            {
                TMP_CharacterInfo ch = textInfo.characterInfo[i];
                // Skip invisible characters (spaces, \u200B, newlines): TMP gives them no vertices, so their
                // vertexIndex is shared with a neighbour, and writing them would overwrite its color.
                if (!ch.isVisible)
                {
                    continue;
                }
                int mat = ch.materialReferenceIndex;
                if (mat < 0 || mat >= textInfo.meshInfo.Length)
                {
                    continue;
                }
                Color32[] colors32 = textInfo.meshInfo[mat].colors32;
                int vi = ch.vertexIndex;
                if (vi < 0 || vi + 3 >= colors32.Length)
                {
                    continue;
                }
                Color32 c = ch.index >= 0 && ch.index < perChar.Length ? perChar[ch.index] : DefaultColor;
                colors32[vi] = c;
                colors32[vi + 1] = c;
                colors32[vi + 2] = c;
                colors32[vi + 3] = c;

                // Collect the error span's coordinates, from indexes in the source string
                if (hasError && ch.index >= errStart && ch.index < errEnd)
                {
                    if (!errFound)
                    {
                        errX0 = ch.origin;
                        errY = ch.baseLine;
                        errFound = true;
                    }
                    // Across lines: only measure the first error line, as the underline is only drawn there
                    if (Mathf.Abs(ch.baseLine - errY) < textComponent.fontSize * 0.5f)
                    {
                        errX1 = ch.xAdvance;
                    }
                }
            }

            // Error underline overlay: a red ~ line under the error span
            if (hasError && errFound)
            {
                TextMeshProUGUI overlay = GetErrorOverlay(textComponent);
                RectTransform rt = overlay.rectTransform;
                float width = Mathf.Max(4f, errX1 - errX0);
                // Keep the underline within textComponent's visible width, so ~ doesn't spill onto neighbouring content
                float maxW = textComponent.rectTransform.rect.width;
                if (width > maxW)
                {
                    width = maxW;
                }
                float offset = Mathf.Max(2f, textComponent.fontSize * 0.12f);
                rt.localPosition = new Vector3(errX0, errY - offset, 0f);
                rt.sizeDelta = new Vector2(width, Mathf.Max(6f, textComponent.fontSize * 0.3f));
                float approxCharW = Mathf.Max(2f, overlay.fontSize * 0.6f);
                int n = Mathf.Clamp(Mathf.RoundToInt(width / approxCharW), 2, 200);
                overlay.text = new string('~', n);
                overlay.enabled = true;
            }
            else
            {
                HideErrorOverlay(textComponent);
            }
        }

        // ---- Error underline overlay ----

        /// <summary>Gets or creates textComponent's red underline overlay, which takes no part in layout or clicks.</summary>
        private static TextMeshProUGUI GetErrorOverlay(TMP_Text textComponent)
        {
            int id = textComponent.GetInstanceID();
            TextMeshProUGUI overlay;
            if (_errorOverlays.TryGetValue(id, out overlay) && overlay != null)
            {
                return overlay;
            }
            var go = new GameObject("FunkErrorOverlay", typeof(RectTransform));
            go.transform.SetParent(textComponent.transform, false);
            overlay = go.AddComponent<TextMeshProUGUI>();
            overlay.font = textComponent.font;
            overlay.fontSize = Mathf.Max(8f, textComponent.fontSize * 0.7f);
            overlay.color = new Color32(255, 77, 77, 220);
            overlay.raycastTarget = false;
            overlay.richText = false;
            overlay.alignment = TextAlignmentOptions.Left;
            // No wrapping: ~ follows the global word wrap by default, and an overlong line would fold over the text
            overlay.enableWordWrapping = false;
            overlay.text = "";
            overlay.enabled = false;
            // Takes no part in LayoutGroup layout
            var le = overlay.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            RectTransform rt = overlay.rectTransform;
            rt.pivot = new Vector2(0f, 0f);
            // Anchor at textComponent's pivot: TMP character coordinates (origin, baseLine) are relative to that
            // pivot, and the overlay's localPosition is relative to its anchor, so this lines them up.
            Vector2 pv = textComponent.rectTransform.pivot;
            rt.anchorMin = rt.anchorMax = pv;
            rt.localPosition = Vector3.zero;
            rt.sizeDelta = new Vector2(0f, 0f);
            _errorOverlays[id] = overlay;
            return overlay;
        }

        private static void HideErrorOverlay(TMP_Text textComponent)
        {
            TextMeshProUGUI overlay;
            if (_errorOverlays.TryGetValue(textComponent.GetInstanceID(), out overlay) && overlay != null)
            {
                overlay.enabled = false;
            }
        }

        // ---- Tokenizing ----

        private static Color32[] TokenizeToColors(string text, int errStart, int errEnd)
        {
            var colors = new Color32[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                colors[i] = DefaultColor;
            }
            int pos = 0;
            while (pos < text.Length)
            {
                Match ws = WhitespacePat.Match(text, pos);
                if (ws.Success && ws.Index == pos && ws.Length > 0)
                {
                    pos += ws.Length;
                    continue;
                }
                if (TryToken(text, ref pos, colors, StringPat, ColorString)) continue;
                if (TryToken(text, ref pos, colors, DataSlotPat, ColorDataSlot)) continue;
                if (TryToken(text, ref pos, colors, NumberPat, ColorNumber)) continue;

                Match name = NamePat.Match(text, pos);
                if (name.Success && name.Index == pos)
                {
                    Fill(colors, pos, name.Length, KindOfName(name.Value));
                    pos += name.Length;
                    continue;
                }

                if (TryToken(text, ref pos, colors, OperatorPat, ColorOperator)) continue;
                if (TryToken(text, ref pos, colors, SeparatorPat, ColorOperator)) continue;
                if (TryToken(text, ref pos, colors, GroupPat, ColorGroup)) continue;

                pos++;
            }
            // Error span in red, over the token colors, so errors stand out
            if (errStart >= 0 && errStart < text.Length)
            {
                int end = errEnd < text.Length ? errEnd : text.Length;
                Fill(colors, errStart, end - errStart, ColorError);
            }
            return colors;
        }

        /// <summary>
        /// Syntax error detection, with the same lexical rules and order as ModApi.Expressions.Parser.
        /// Two layers: lexical (unrecognised characters), then token stream checks (missing operators, repeated
        /// operators, misplaced commas or brackets, unclosed brackets, incomplete expressions), with exact positions.
        /// </summary>
        private static bool IsActionGroupName(string name)
        {
            // The game's ResolveVariable: text.StartsWith("AG") and the rest parses as an int (AG1, AG2, ...).
            if (name.Length < 3 || name[0] != 'A' || name[1] != 'G')
            {
                return false;
            }
            for (int k = 2; k < name.Length; k++)
            {
                if (name[k] < '0' || name[k] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Whether tokens[i] can start an expression: a value, an opening bracket, or unary + or -.</summary>
        private static bool IsExpressionStart(List<FunkToken> tokens, int i)
        {
            if (i >= tokens.Count)
            {
                return false;
            }
            switch (tokens[i].kind)
            {
                case TokKind.Value:
                case TokKind.LParen:
                    return true;
                case TokKind.Op:
                    return tokens[i].value == "-" || tokens[i].value == "+" || tokens[i].value == "!";
                default:
                    return false;
            }
        }

        private static bool FindError(string text, out int errStart, out int errEnd, out string message, HashSet<string> variableNames = null)
        {
            errStart = -1;
            errEnd = -1;
            message = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // ---- Layer 1: lexical scan, collecting tokens ----
            var tokens = new List<FunkToken>();
            int pos = 0;
            int depth = 0;
            bool endsWithBadToken = false;
            int badTokenPos = 0;
            while (pos < text.Length)
            {
                Match ws = WhitespacePat.Match(text, pos);
                if (ws.Success && ws.Index == pos && ws.Length > 0)
                {
                    pos += ws.Length;
                    continue;
                }
                Match m = StringPat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Value, m.Value, false, false, depth)); endsWithBadToken = false; pos += m.Length; continue; }
                m = DataSlotPat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Value, m.Value, false, true, depth)); endsWithBadToken = false; pos += m.Length; continue; }
                m = NumberPat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Value, m.Value, false, false, depth)); endsWithBadToken = false; pos += m.Length; continue; }
                m = NamePat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Value, m.Value, true, false, depth)); endsWithBadToken = false; pos += m.Length; continue; }
                m = OperatorPat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Op, m.Value, false, false, depth)); endsWithBadToken = true; badTokenPos = pos; pos += m.Length; continue; }
                m = SeparatorPat.Match(text, pos);
                if (m.Success && m.Index == pos) { tokens.Add(new FunkToken(pos, m.Length, TokKind.Comma, ",", false, false, depth)); endsWithBadToken = true; badTokenPos = pos; pos += m.Length; continue; }
                m = GroupPat.Match(text, pos);
                if (m.Success && m.Index == pos)
                {
                    if (m.Value == "(")
                    {
                        tokens.Add(new FunkToken(pos, m.Length, TokKind.LParen, "(", false, false, depth));
                        depth++;
                    }
                    else
                    {
                        if (depth == 0)
                        {
                            errStart = pos;
                            errEnd = pos + 1;
                            message = "Unexpected ')'";
                            return true;
                        }
                        tokens.Add(new FunkToken(pos, m.Length, TokKind.RParen, ")", false, false, depth - 1));
                        depth--;
                    }
                    endsWithBadToken = false;
                    pos += m.Length;
                    continue;
                }
                // Lexical error: unrecognised character
                errStart = pos;
                errEnd = pos + 1;
                message = "Invalid character '" + text[pos] + "'";
                return true;
            }
            // ---- Layer 2: token stream checks ----
            TokKind prevKind = TokKind.None;
            bool prevIsName = false;
            int ternaryPending = 0;   // unmatched '?' count, each missing a ':' false branch
            int lastTernaryPos = 0;   // position of the latest unmatched '?', for the message
            // callParen[depth]: whether the opening bracket at this depth is a function call (the previous token is a name).
            // Commas are only allowed in call brackets, not in grouping brackets like (a, b).
            bool[] callParen = new bool[text.Length + 2];
            for (int i = 0; i < tokens.Count; i++)
            {
                FunkToken tk = tokens[i];
                switch (tk.kind)
                {
                    case TokKind.Value:
                        // Missing operator between values (tolerates a name followed by a data slot, as in x[0])
                        if (prevKind == TokKind.Value && !(prevIsName && tk.isDataSlot))
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Missing operator";
                            return true;
                        }
                        if (prevKind == TokKind.RParen)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Missing operator";
                            return true;
                        }
                        break;
                    case TokKind.Op:
                        // Ternary ?: needs an expression before (condition), an expression after (true branch), and a matching ':'
                        if (tk.value == "?")
                        {
                            if (prevKind == TokKind.Op || prevKind == TokKind.Comma
                                || prevKind == TokKind.None || prevKind == TokKind.LParen)
                            {
                                errStart = tk.start; errEnd = tk.start + tk.length;
                                message = "Missing expression before '?'";
                                return true;
                            }
                            if (!IsExpressionStart(tokens, i + 1))
                            {
                                errStart = tk.start; errEnd = tk.start + tk.length;
                                message = "Missing expression after '?'";
                                return true;
                            }
                            ternaryPending++;
                            lastTernaryPos = tk.start;
                            break;
                        }
                        // Ternary ':': needs a matching '?', and an expression after (false branch)
                        if (tk.value == ":")
                        {
                            if (ternaryPending == 0)
                            {
                                errStart = tk.start; errEnd = tk.start + tk.length;
                                message = "Unexpected ':'";
                                return true;
                            }
                            ternaryPending--;
                            if (!IsExpressionStart(tokens, i + 1))
                            {
                                errStart = tk.start; errEnd = tk.start + tk.length;
                                message = "Missing expression after ':'";
                                return true;
                            }
                            break;
                        }
                        // Unary - + ! allowed at the start, and after an operator, opening bracket or comma
                        bool unary = tk.value == "-" || tk.value == "+" || tk.value == "!";
                        if ((prevKind == TokKind.Op || prevKind == TokKind.Comma || prevKind == TokKind.None) && !unary)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Unexpected operator '" + tk.value + "'";
                            return true;
                        }
                        if (prevKind == TokKind.LParen && !unary)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Unexpected operator '" + tk.value + "'";
                            return true;
                        }
                        break;
                    case TokKind.LParen:
                        // Before '(' only a name (function call), an operator, an opening bracket or the start; after other values it's an error
                        if (prevKind == TokKind.Value && !prevIsName)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Unexpected '('";
                            return true;
                        }
                        if (prevKind == TokKind.RParen)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Unexpected '('";
                            return true;
                        }
                        // Whether this bracket is a call bracket: only if the previous token is a name are argument commas allowed
                        callParen[tk.depth] = prevKind == TokKind.Value && prevIsName;
                        break;
                    case TokKind.RParen:
                        if (prevKind == TokKind.Op || prevKind == TokKind.Comma || prevKind == TokKind.LParen)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Missing expression";
                            return true;
                        }
                        break;
                    case TokKind.Comma:
                        // Commas only inside function call brackets, not grouping brackets like (a, b)
                        if (tk.depth == 0 || !callParen[tk.depth - 1])
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Unexpected ','";
                            return true;
                        }
                        if (prevKind == TokKind.Op || prevKind == TokKind.LParen || prevKind == TokKind.Comma)
                        {
                            errStart = tk.start; errEnd = tk.start + tk.length;
                            message = "Missing expression";
                            return true;
                        }
                        break;
                }
                prevKind = tk.kind;
                prevIsName = tk.isName;
            }

            // Unclosed ternary: '?' with no ':' (the game's SquashTernary reports "Expecting : after ?")
            if (ternaryPending > 0)
            {
                errStart = lastTernaryPos;
                errEnd = text.Length;
                message = "Expecting ':'";
                return true;
            }

            // ---- Variable and bare name checks ----
            // The game's VariableToken.Create resolves in order: constants -> Context properties -> VariableResolve delegates
            // (bare controls, AGxx, FlightData.X, FD.X, OrbitData.X, OD.X, Part.Modifier.Property,
            //  v: vector variables, number variables). A bare name that's none of function, constant, control, AG or defined
            //  variable falls through CustomResolve (CraftService) to a number variable token, and GetVariableValueAsDoubleAndThrow
            //  throws "Could not find Vizzy variable..." at run time. This marks that error in the editor first.
            // With no program available (variableNames == null) this is skipped, to avoid false errors.
            if (variableNames != null)
            {
                for (int i = 0; i < tokens.Count; i++)
                {
                    FunkToken tk = tokens[i];
                    if (tk.kind != TokKind.Value || !tk.isName)
                    {
                        continue;
                    }
                    string name = tk.value;

                    // v:name (a vector variable) must be defined.
                    if (name.StartsWith("v:", StringComparison.Ordinal))
                    {
                        string varName = name.Substring(2);
                        if (varName.Length == 0)
                        {
                            continue;
                        }
                        int dot = varName.IndexOf('.');
                        string baseName = dot >= 0 ? varName.Substring(0, dot) : varName;
                        if (!variableNames.Contains(baseName))
                        {
                            errStart = tk.start;
                            errEnd = tk.start + tk.length;
                            message = "Unknown variable '" + baseName + "'";
                            return true;
                        }
                        continue;
                    }

                    // Data property prefixes (FlightData., FD., OrbitData., OD.) go to the property checks below.
                    if (name.StartsWith("FlightData.", StringComparison.Ordinal)
                        || name.StartsWith("FD.", StringComparison.Ordinal)
                        || name.StartsWith("OrbitData.", StringComparison.Ordinal)
                        || name.StartsWith("OD.", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    // Dotted bare names (Part.Modifier.Property) aren't checked statically.
                    if (name.IndexOf('.') >= 0)
                    {
                        continue;
                    }

                    // Followed by '(': a function call, which must be a built-in function (including list*, sum, rate, PID, smooth).
                    bool isCall = i + 1 < tokens.Count && tokens[i + 1].kind == TokKind.LParen;
                    if (isCall)
                    {
                        if (Functions.Contains(name))
                        {
                            continue;
                        }
                        errStart = tk.start;
                        errEnd = tk.start + tk.length;
                        message = "Unknown function '" + name + "'";
                        return true;
                    }

                    // Bare name: valid if it's a constant, control, AGxx or defined variable.
                    if (Keywords.Contains(name) || ControlsProperties.Contains(name)
                        || variableNames.Contains(name) || IsActionGroupName(name))
                    {
                        continue;
                    }
                    errStart = tk.start;
                    errEnd = tk.start + tk.length;
                    message = "Unknown variable '" + name + "'";
                    return true;
                }
            }

            // ---- FlightData and OrbitData property checks ----
            // FlightData.X, FD.X, OrbitData.X and OD.X names must exist.
            for (int i = 0; i < tokens.Count; i++)
            {
                FunkToken tk = tokens[i];
                if (tk.kind != TokKind.Value || !tk.isName)
                {
                    continue;
                }
                string err;
                if (!IsValidDataPropertyAccess(tk.value, out err))
                {
                    errStart = tk.start;
                    errEnd = tk.start + tk.length;
                    message = err;
                    return true;
                }
            }

            // ---- Layer 3: function argument counts ----
            // Checks the argument count of each call to a known function (those in FunctionArgCounts).
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].kind != TokKind.LParen || i == 0)
                {
                    continue;
                }
                FunkToken prev = tokens[i - 1];
                if (prev.kind != TokKind.Value || !prev.isName)
                {
                    continue; // Not a function call (2( is already reported by the token stream layer)
                }
                int expected;
                if (!FunctionArgCounts.TryGetValue(prev.value.ToLowerInvariant(), out expected))
                {
                    continue; // Unknown or custom function: not checked
                }
                int lv = tokens[i].depth;
                int rp = -1;
                for (int j = i + 1; j < tokens.Count; j++)
                {
                    if (tokens[j].kind == TokKind.RParen && tokens[j].depth == lv)
                    {
                        rp = j;
                        break;
                    }
                }
                if (rp < 0)
                {
                    continue; // Unclosed: reported as Missing ')'
                }
                int commas = 0;
                bool hasAny = false;
                for (int j = i + 1; j < rp; j++)
                {
                    if (tokens[j].kind == TokKind.Comma && tokens[j].depth == lv + 1)
                    {
                        commas++;
                    }
                    if (tokens[j].kind != TokKind.Comma)
                    {
                        hasAny = true;
                    }
                }
                int args = hasAny ? commas + 1 : 0;
                if (args != expected)
                {
                    errStart = prev.start;
                    errEnd = prev.start + prev.length;
                    message = "Function " + prev.value + " expects " + expected
                        + " argument" + (expected == 1 ? "" : "s") + ", " + args + " given";
                    return true;
                }
                // List readers: the first argument is the list name as a string, which must be an existing list variable
                // (the game's GetListValueAndThrow looks it up at run time, and throws "Could not find
                //  Vizzy variable..." if it's missing or not a list. This marks that error in the editor first.)
                if (variableNames != null
                    && (prev.value == "listNum" || prev.value == "listVec" || prev.value == "listBool"
                        || prev.value == "listStr" || prev.value == "listLen"))
                {
                    FunkToken firstArg = tokens[i + 1];
                    if (firstArg.kind == TokKind.Value && !firstArg.isName
                        && firstArg.value.Length >= 2 && firstArg.value[0] == '"'
                        && firstArg.value[firstArg.value.Length - 1] == '"')
                    {
                        string listName = firstArg.value.Substring(1, firstArg.value.Length - 2);
                        listName = listName.Replace("\\\"", "\"").Replace("\\\\", "\\");
                        if (listName.Length > 0 && !variableNames.Contains(listName))
                        {
                            errStart = firstArg.start;
                            errEnd = firstArg.start + firstArg.length;
                            message = "Unknown list '" + listName + "'";
                            return true;
                        }
                    }
                }
            }
            if (endsWithBadToken)
            {
                errStart = badTokenPos;
                errEnd = text.Length;
                message = "Incomplete expression";
                return true;
            }
            if (depth > 0)
            {
                // Unclosed '(': red from the last unmatched opening bracket to the end of the line
                errStart = FindOpenParenStart(tokens);
                errEnd = text.Length;
                message = "Missing ')'";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks a FlightData or OrbitData property path:
        ///   FlightData.X, FD.X  -> X must be in FlightDataProperties
        ///   OrbitData.X, OD.X   -> X must be in OrbitDataProperties
        /// Other names (ordinary variables, custom names) return true, unchecked.
        /// </summary>
        private static bool IsValidDataPropertyAccess(string name, out string error)
        {
            error = null;
            string path = null;
            bool orbitRoot = false;
            
             if (name.StartsWith("OrbitData.", StringComparison.Ordinal))
            {
                path = name.Substring("OrbitData.".Length);
                orbitRoot = true;
            }
            else if (name.StartsWith("OD.", StringComparison.Ordinal))
            {
                path = name.Substring("OD.".Length);
                orbitRoot = true;
            }
            else if (name.StartsWith("FD.", StringComparison.Ordinal))
            {
                path = name.Substring("FD.".Length);
            }
            else if (name.StartsWith("FlightData.", StringComparison.Ordinal))
            {
                path = name.Substring("FlightData.".Length);
            }
            else
            {
                return true; // Not a data property prefix: not checked
            }
            if (path.Length == 0)
            {
                return true; // "FlightData." or "OD." on its own
            }
            string[] segs = path.Split('.');
            if (orbitRoot)
            {
                foreach (string seg in segs)
                {
                    if (!OrbitDataProperties.Contains(seg))
                    {
                        string s = FindClosestProperty(seg, OrbitDataProperties);
                        error = "Unknown OrbitData property '" + seg + "'"
                            + (s != null ? " (did you mean '" + s + "'?)" : "");
                        return false;
                    }
                }
                return true;
            }
            // FlightData path: the first segment must be in FlightDataProperties
            if (!FlightDataProperties.Contains(segs[0]))
            {
                string s = FindClosestProperty(segs[0], FlightDataProperties);
                error = "Unknown FlightData property '" + segs[0] + "'"
                    + (s != null ? " (did you mean '" + s + "'?)" : "");
                return false;
            }
            if (segs.Length > 1 && segs[0] == "Orbit")
            {
                for (int k = 1; k < segs.Length; k++)
                {
                    if (!OrbitDataProperties.Contains(segs[k]))
                    {
                        string s = FindClosestProperty(segs[k], OrbitDataProperties);
                        error = "Unknown OrbitData property '" + segs[k] + "'"
                            + (s != null ? " (did you mean '" + s + "'?)" : "");
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>The closest name within edit distance 2, for spelling suggestions, or null.</summary>
        private static string FindClosestProperty(string prop, HashSet<string> set)
        {
            string best = null;
            int bestDist = int.MaxValue;
            foreach (string candidate in set)
            {
                int dist = LevenshteinDistance(prop, candidate);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }
            return bestDist <= 2 ? best : null;
        }

        /// <summary>Levenshtein edit distance.</summary>
        private static int LevenshteinDistance(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++)
            {
                d[i, 0] = i;
            }
            for (int j = 0; j <= b.Length; j++)
            {
                d[0, j] = j;
            }
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }

        private static int FindOpenParenStart(List<FunkToken> tokens)
        {
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                if (tokens[i].kind == TokKind.LParen)
                {
                    return tokens[i].start;
                }
            }
            return 0;
        }

        private enum TokKind { None, Value, Op, LParen, RParen, Comma }

        private struct FunkToken
        {
            public int start;
            public int length;
            public TokKind kind;
            public string value;
            public bool isName;
            public bool isDataSlot;
            public int depth;

            public FunkToken(int start, int length, TokKind kind, string value, bool isName, bool isDataSlot, int depth)
            {
                this.start = start;
                this.length = length;
                this.kind = kind;
                this.value = value;
                this.isName = isName;
                this.isDataSlot = isDataSlot;
                this.depth = depth;
            }
        }

        private static bool TryToken(string text, ref int pos, Color32[] colors, Regex pattern, Color32 color)
        {
            Match m = pattern.Match(text, pos);
            if (!m.Success || m.Index != pos)
            {
                return false;
            }
            Fill(colors, pos, m.Length, color);
            pos += m.Length;
            return true;
        }

        private static void Fill(Color32[] colors, int start, int length, Color32 color)
        {
            for (int i = start; i < start + length; i++)
            {
                colors[i] = color;
            }
        }

        private static Color32 KindOfName(string name)
        {
            if (name.StartsWith("v:", StringComparison.Ordinal))
            {
                return ColorVector;
            }
            if (name.StartsWith("FD.", StringComparison.Ordinal) || name.StartsWith("FlightData.", StringComparison.Ordinal)
                || name.StartsWith("OD.", StringComparison.Ordinal) || name.StartsWith("OrbitData.", StringComparison.Ordinal))
            {
                return ColorFlightData;
            }
            if (name.IndexOf('.') >= 0 || name.IndexOf('$') >= 0)
            {
                return ColorPart;
            }
            if (Functions.Contains(name))
            {
                return ColorFunction;
            }
            if (Keywords.Contains(name) || (name.Length > 2 && name[0] == 'A' && name[1] == 'G' && IsDigits(name.Substring(2))))
            {
                return ColorKeyword;
            }
            return ColorVariable;
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            foreach (char c in s)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return true;
        }

        private static Color32 FromHex(string hex)
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return new Color32(r, g, b, 255);
        }
    }

    /// <summary>When a block starts, registers textComponent -> input field and subscribes to OnPreRenderText.</summary>
    [HarmonyPatch(typeof(TextInputElementScript), "Start")]
    public static class FunkHighlightRegisterPatch
    {
        private static void Postfix(TextInputElementScript __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }
                TMP_InputField input = __instance.GetComponent<TMP_InputField>();
                if (input != null)
                {
                    FunkHighlight.Register(input);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[FunkHighlight] Register Start: " + e.Message);
            }
        }
    }
}
