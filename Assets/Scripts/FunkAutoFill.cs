namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.Vizzy.UI.Elements;
    using ModApi.Craft.Program.Expressions;
    using TMPro;
    using UnityEngine;
    using UnityEngine.EventSystems;

    /// <summary>
    /// Suggests Funk names under a Funk field in any Vizzy editor, in the designer or in flight. Offers
    /// only what the game's Vizzy Funk context can read. FunkInputPatch hands it the keys while the list
    /// is open. All it ever changes is the text the player chooses to insert.
    /// </summary>
    public class FunkAutoFill : MonoBehaviour
    {
        // Survive a quickload, so the names just used stay on top through the tuning loop.
        private static readonly List<string> Recent = new List<string>();

        private TMP_InputField _field;
        private TextInputElementScript _element;
        private FunkCatalog _catalog;
        private string _program;
        private FunkCompletion _completion;
        private FunkList _list;
        private TMP_InputField _listField;
        private int _selected;
        private string _lastText;
        private int _lastCaret = -1;
        private bool _dismissed;
        private TMP_InputField _refocusing;
        private string _refocusText;
        private int _refocusCaret;
        private bool _refocusSelectAll;
        private KeyCode _heldKey = KeyCode.None;

        /// <summary>Gets the auto-fill of the current flight scene.</summary>
        public static FunkAutoFill Current { get; private set; }

        /// <summary>Called by the patch, before the field reads its keys. True when this took them.</summary>
        public bool TakeKeys(TMP_InputField field)
        {
            if (_heldKey != KeyCode.None)
            {
                if (UnityEngine.Input.GetKey(_heldKey))
                {
                    return field == _field;
                }

                _heldKey = KeyCode.None;
            }

            if (field != _field || _completion == null)
            {
                return false;
            }

            foreach (var key in new[] { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Escape })
            {
                if (UnityEngine.Input.GetKeyDown(key))
                {
                    _heldKey = key;
                    OnKey(key);
                    return true;
                }
            }

            return false;
        }

        private static bool IsEnabled()
        {
            return FunkInputPatch.Installed && ModSettings.Instance.FunkAutoFill.Value;
        }

        private static FunkCatalog BuildCatalog(VizzyUIScript editor)
        {
            var catalog = new FunkCatalog();
            catalog.TopLevel.AddRange(FunkNames.Functions);
            catalog.TopLevel.AddRange(FunkNames.Constants);
            catalog.TopLevel.AddRange(FunkNames.Controls);
            catalog.FlightData.AddRange(FunkNames.FlightData);
            catalog.OrbitData.AddRange(FunkNames.OrbitData);

            // As the editor has them now, including names typed straight into a block.
            var lists = editor.FlightProgram.GlobalVariables.Variables.Where(variable => variable.IsList).Select(variable => variable.Name);
            foreach (var name in FunkNames.ProgramVariables(editor).Except(lists))
            {
                catalog.Variables.Add(new FunkName(name));
            }

            catalog.VectorVariables.UnionWith(FunkNames.VectorVariables(editor));
            return catalog;
        }

        private void Awake()
        {
            Current = this;
        }

        private void OnDestroy()
        {
            _list?.Destroy();
            if (Current == this)
            {
                Current = null;
            }
        }

        private void LateUpdate()
        {
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                EditorLog.Error("Funk auto-fill failed: " + e);
                Close();
            }
        }

        private void Refresh()
        {
            var element = IsEnabled() ? SelectedFunkField() : null;
            var field = element == null ? null : element.GetComponent<TMP_InputField>();
            if (field != _field)
            {
                _field = field;
                _element = element;
                _catalog = null;
                _dismissed = false;
                _lastText = null;
                _lastCaret = -1;
                Close();
            }

            if (field == null)
            {
                return;
            }

            if (field == _refocusing)
            {
                FinishRefocus();
                return;
            }

            if (field.text == _lastText && field.stringPosition == _lastCaret)
            {
                return;
            }

            if (field.text != _lastText)
            {
                _dismissed = false;
            }

            _lastText = field.text;
            _lastCaret = field.stringPosition;
            if (_catalog == null)
            {
                var editor = (VizzyUIScript)_element.VizzyUI;
                _catalog = BuildCatalog(editor);

                // What the program's fields hold now. Not serialized: that renumbers the live blocks.
                _program = string.Join("\n", editor.ProgramTransform.GetComponentsInChildren<TextInputElementScript>(true).Select(input => input.Text));
            }

            _completion = _dismissed ? null : FunkCompletion.Find(field.text, field.stringPosition, _catalog, Recent, _program);
            _selected = 0;
            ShowList();
        }

        private static TextInputElementScript SelectedFunkField()
        {
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var field = selected == null ? null : selected.GetComponent<TMP_InputField>();
            var element = selected == null ? null : selected.GetComponent<TextInputElementScript>();
            return field != null && element != null && field.isFocused && element.Parent?.Node is EvaluationExpression ? element : null;
        }

        private void OnKey(KeyCode key)
        {
            var count = _completion.Suggestions.Count;
            if (key == KeyCode.UpArrow)
            {
                _selected = (_selected + count - 1) % count;
                ShowList();
            }
            else if (key == KeyCode.DownArrow)
            {
                _selected = (_selected + 1) % count;
                ShowList();
            }
            else if (key == KeyCode.Escape)
            {
                _dismissed = true;
                Close();
            }
            else
            {
                Accept();
            }
        }

        /// <summary>A click on a row. The press has already taken focus from the field, so it's given back.</summary>
        private void OnRowPressed(int index)
        {
            if (_field == null || _completion == null || index >= _completion.Suggestions.Count)
            {
                return;
            }

            var field = _field;
            _selected = index;
            Accept();
            _refocusing = field;
            _refocusText = field.text;
            _refocusCaret = field.stringPosition;
            _refocusSelectAll = field.onFocusSelectAll;
            field.onFocusSelectAll = false;
            EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        }

        private void OnRowHovered(int index)
        {
            if (_completion != null && index < _completion.Suggestions.Count && index != _selected)
            {
                _selected = index;
                ShowList();
            }
        }

        /// <summary>Taken as written once it has focus again, with the cursor after the name.</summary>
        private void FinishRefocus()
        {
            var field = _refocusing;
            _refocusing = null;
            field.onFocusSelectAll = _refocusSelectAll;
            if (field.text == _refocusText)
            {
                field.stringPosition = _refocusCaret;
                field.selectionStringAnchorPosition = _refocusCaret;
                field.selectionStringFocusPosition = _refocusCaret;
                _lastText = field.text;
                _lastCaret = _refocusCaret;
            }
        }

        private void Accept()
        {
            var suggestion = _completion.Suggestions[_selected];
            var name = suggestion.Insert.TrimEnd('(');
            Recent.Remove(name);
            Recent.Insert(0, name);

            var text = _completion.Apply(_field.text, suggestion, out var caret);
            _field.text = text;
            _field.stringPosition = caret;
            _field.selectionStringAnchorPosition = caret;
            _field.selectionStringFocusPosition = caret;

            // Taken as written: the list comes back once more is typed.
            _lastText = text;
            _lastCaret = caret;
            Close();
        }

        private void ShowList()
        {
            if (_completion == null)
            {
                _list?.Hide();
                return;
            }

            if (_list == null || _listField != _field)
            {
                _list?.Destroy();
                _list = new FunkList(_field, OnRowPressed, OnRowHovered);
                _listField = _field;
            }

            _list.Show(_field, _completion.Suggestions, _selected, _completion.Start);
        }

        private void Close()
        {
            _completion = null;
            _list?.Hide();
        }
    }
}
