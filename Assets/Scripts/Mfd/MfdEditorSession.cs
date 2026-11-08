namespace Assets.Scripts.Mfd
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Craft.Parts.Modifiers;
    using Assets.Scripts.Craft.Parts.Modifiers.Mfd;
    using ModApi.Craft.Program.Craft;
    using ModApi.Ui.Inspector;
    using TMPro;
    using UnityEngine;

    /// <summary>
    /// The Arrange MFD panel for one MFD. The flight is held still while it's open, so a running program
    /// can't overwrite what's being typed. Edits change the live widgets at once; Save writes them into
    /// the program and the craft's files, and closing puts back what wasn't saved.
    /// </summary>
    public sealed class MfdEditorSession
    {
        private const int PanelWidth = 340;
        private const float MaxScreenHeight = 0.9f;
        private const int LabelLineHeight = 22;
        private const int LabelCharactersPerLine = 40;
        private const float MinSize = 1f;
        private const double CascadeTolerance = 0.01;
        private const double MatchTolerance = 0.01;
        private static readonly string[] Steps = { "0.2", "2", "20" };

        // Rotation's degrees need bigger clicks than position and size.
        private const float RotationStepFactor = 5f;

        private readonly FlightProgramData _data;
        private readonly MfdWidgetOutline _outline = new MfdWidgetOutline();
        private readonly InspectorRowPaint _rowPaint = new InspectorRowPaint();
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<ItemModel> _selectedValues = new List<ItemModel>();
        private readonly List<LabelModel> _wrappedLabels = new List<LabelModel>();

        private MfdScript _mfd;
        private IInspectorPanel _panel;
        private GroupModel _selectedGroup;
        private LabelModel _selectedName;
        private GroupModel _changesGroup;
        private GroupModel _listGroup;
        private string _shownList;
        private Row _selected;
        private float _step = 2f;
        private bool _allowShared = true;
        private string _message;
        private int _cascaded;

        public MfdEditorSession(FlightProgramData data)
        {
            _data = data;
        }

        public bool IsOpen => _panel != null;

        public void Open()
        {
            _mfd = _data.Part.PartScript.GetModifier<MfdScript>();
            if (_mfd == null)
            {
                Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Text.Get("VizzyStudio.Mfd.NoMfd"));
                return;
            }

            var widgets = MfdRestart.Widgets(_mfd);
            FlightPause.Hold();
            try
            {
                this.BuildPanel(widgets);
            }
            catch
            {
                FlightPause.Release();
                throw;
            }
        }

        private void BuildPanel(List<IMfdWidget> widgets)
        {
            foreach (var widget in widgets.Where(item => item.Name != MfdRestart.ScreenWidgetName).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                _rows.Add(new Row(widget));
            }

            var model = new InspectorModel("VizzyStudioArrangeMfd", Text.Get("VizzyStudio.Buttons.ArrangeMfd"));
            _selectedGroup = this.BuildSelectedGroup();
            model.AddGroup(_selectedGroup);
            _changesGroup = this.BuildChangesGroup();
            model.AddGroup(_changesGroup);

            // Before the panel exists: replacing a group before its Start leaves a dead element behind,
            // which throws in the panel's update every frame and stops the rows after it from updating.
            this.ClassifyAll();
            _listGroup = this.BuildListGroup();
            model.AddGroup(_listGroup);

            var info = new InspectorPanelCreationInfo
            {
                StartPosition = InspectorPanelCreationInfo.InspectorStartPosition.UpperLeft,
                Resizable = true,
                PanelWidth = PanelWidth,
                PanelMaxHeight = MaxScreenHeight,
            };
            _panel = Game.Instance.UserInterface.CreateInspectorPanel(model, info);
            _panel.Closed += this.OnPanelClosed;
        }

        public void Close()
        {
            var panel = _panel;
            if (panel != null)
            {
                try
                {
                    panel.Close();
                }
                catch (Exception e)
                {
                    // At the game's end the panel may already be gone.
                    EditorLog.Debug("MFD panel not closed: " + e.Message);
                }

                this.Finish();
            }
        }

        /// <summary>Each frame from the flight controller.</summary>
        public void Update()
        {
            if (_panel == null)
            {
                return;
            }

            if (_mfd == null || _data.Script == null)
            {
                this.Close();
                return;
            }

            // The program could remove a widget before the flight was held, or after a load.
            if (_selected != null && !_selected.IsAlive)
            {
                this.Select(null);
            }

            // Building rows can reset their colors, and they appear after the panel opens.
            if (_selected?.Button != null)
            {
                _rowPaint.Paint(_selected.Button, true);
            }

            foreach (var label in _wrappedLabels)
            {
                var text = label.ItemElement?.GameObject == null ? null : label.ItemElement.GameObject.GetComponentInChildren<TMP_Text>(true);
                if (text != null && !text.enableWordWrapping)
                {
                    text.enableWordWrapping = true;
                }
            }
        }

        private static string Number(float value)
        {
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Works out what each widget can save. The flight is held, so a moment on a large program does no harm.</summary>
        private void ClassifyAll()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var row in _rows)
            {
                row.CanArrange = false;
                if (row.IsAlive)
                {
                    var lines = this.Classify(row);
                    row.CanArrange = lines.Any(line => line.Property != MfdProperty.Rotation && (line.Applied || line.NeedsSharedOverride));
                    row.Label = row.Name + StatusSuffix(lines);
                    row.Tooltip = string.Join("\n", lines.Select(line => line.ToString()));
                }
            }

            this.ScreenSize(out var width, out var height);
            EditorLog.Debug("MFD list: " + _rows.Count(row => row.CanArrange) + " of " + _rows.Count + " widgets listed, screen " + width + " x " + height + ", " + clock.ElapsedMilliseconds + " ms.");
            foreach (var row in _rows.Where(item => !item.CanArrange))
            {
                EditorLog.Debug("MFD widget " + row.Name + " not listed: " + (row.Tooltip ?? "it's gone"));
            }
        }

        /// <summary>Only the widgets that saving can move or resize.</summary>
        private GroupModel BuildListGroup()
        {
            var shown = _rows.Where(row => row.CanArrange).ToList();
            _shownList = string.Join("\n", shown.Select(row => row.Label));
            var group = new GroupModel(Text.Get("VizzyStudio.Mfd.Widgets"));
            foreach (var row in _rows)
            {
                row.Button = null;
            }

            foreach (var row in shown)
            {
                row.Button = group.Add(new TextButtonModel(row.Label, _ => this.Select(row)));
                row.Button.Tooltip = row.Tooltip;
            }

            if (shown.Count == 0)
            {
                this.AddWrappedTo(group, Text.Get("VizzyStudio.Mfd.NoWidgets"));
            }

            return group;
        }

        /// <summary>After a save, only if the list differs, as rebuilding it resets its rows.</summary>
        private void RefreshList()
        {
            if (string.Join("\n", _rows.Where(row => row.CanArrange).Select(row => row.Label)) != _shownList)
            {
                var group = this.BuildListGroup();
                _panel.ReplaceGroup(_listGroup, group);
                _listGroup = group;
            }
        }

        private List<MfdLine> Classify(Row row)
        {
            this.ScreenSize(out var width, out var height);
            return MfdFormulaWriter.Apply(_data.FlightProgramXml, row.ProbeChanges(), width, height, false, true);
        }

        /// <summary>Nothing when position and size both save: only the exceptions need saying. The tooltip has the details.</summary>
        private static string StatusSuffix(List<MfdLine> lines)
        {
            bool Saves(MfdProperty property) => lines.Any(line => line.Property == property && (line.Applied || line.NeedsSharedOverride));
            var parts = new List<string>();
            if (!Saves(MfdProperty.Size))
            {
                parts.Add(Text.Get("VizzyStudio.Mfd.OnlyPosition"));
            }
            else if (!Saves(MfdProperty.Position))
            {
                parts.Add(Text.Get("VizzyStudio.Mfd.OnlySize"));
            }

            if (lines.Any(line => line.Property != MfdProperty.Rotation && line.NeedsSharedOverride))
            {
                parts.Add(Text.Get("VizzyStudio.Mfd.Shared"));
            }

            return parts.Count == 0 ? string.Empty : " " + Text.Get("VizzyStudio.Mfd.Limits", string.Join(Text.Get("VizzyStudio.Mfd.LimitSeparator"), parts));
        }

        private void ScreenSize(out double width, out double height)
        {
            var screen = _mfd.GetWidget(MfdRestart.ScreenWidgetName);
            width = screen?.Size.x ?? 0;
            height = screen?.Size.y ?? 0;
        }

        private void Select(Row row)
        {
            if (_selected?.Button != null)
            {
                _rowPaint.Paint(_selected.Button, false);
            }

            _selected = row;
            if (row != null)
            {
                _outline.Show(row.Widget);
            }
            else
            {
                _outline.Hide();
            }

            if (_selectedName != null)
            {
                _selectedName.Label = row?.Name ?? string.Empty;
            }

            foreach (var value in _selectedValues)
            {
                value.Update();
            }
        }

        private void ReplaceChangesGroup()
        {
            var group = this.BuildChangesGroup();
            _panel.ReplaceGroup(_changesGroup, group);
            _changesGroup = group;
        }

        /// <summary>
        /// Built once and shown or hidden: replacing a group with icon rows leaves their buttons' dead
        /// elements in the panel, whose update then throws every frame and stops the fields updating.
        /// </summary>
        private GroupModel BuildSelectedGroup()
        {
            _selectedValues.Clear();
            var group = new GroupModel(Text.Get("VizzyStudio.Mfd.SelectedWidget"));
            bool Chosen() => _selected != null;
            void Add(ItemModel item) => group.Add(item).DetermineVisibility = Chosen;

            group.Add(new LabelModel(Text.Get("VizzyStudio.Mfd.ChooseWidget"), ElementAlignment.Left)).DetermineVisibility = () => _selected == null;
            _selectedName = new LabelModel(string.Empty, ElementAlignment.Left);
            Add(_selectedName);
            this.AddValue(Add, Text.Get("VizzyStudio.Mfd.X"), widget => widget.LocalPosition.x, (row, value) => row.SetPosition(new Vector2(value, row.Widget.LocalPosition.y)));
            this.AddValue(Add, Text.Get("VizzyStudio.Mfd.Y"), widget => widget.LocalPosition.y, (row, value) => row.SetPosition(new Vector2(row.Widget.LocalPosition.x, value)));
            Add(this.IconRow(
                Icon("Ui/Sprites/Design/IconSpinnerArrowLeft", Text.Get("VizzyStudio.Mfd.MoveLeft"), row => row.Move(new Vector2(-_step, 0f))),
                Icon("Ui/Sprites/Design/IconSpinnerArrowRight", Text.Get("VizzyStudio.Mfd.MoveRight"), row => row.Move(new Vector2(_step, 0f))),
                Icon("Ui/Sprites/Common/IconMoveUp", Text.Get("VizzyStudio.Mfd.MoveUp"), row => row.Move(new Vector2(0f, _step))),
                Icon("Ui/Sprites/Common/IconMoveDown", Text.Get("VizzyStudio.Mfd.MoveDown"), row => row.Move(new Vector2(0f, -_step)))));

            this.AddValue(Add, Text.Get("VizzyStudio.Mfd.Width"), widget => widget.Size.x, (row, value) => row.SetSize(new Vector2(value, row.Widget.Size.y)));
            this.AddValue(Add, Text.Get("VizzyStudio.Mfd.Height"), widget => widget.Size.y, (row, value) => row.SetSize(new Vector2(row.Widget.Size.x, value)));
            Add(this.IconRow(
                Icon("Ui/Sprites/Design/IconSpinnerArrowLeft", Text.Get("VizzyStudio.Mfd.Narrower"), row => row.Grow(new Vector2(-_step, 0f))),
                Icon("Ui/Sprites/Design/IconSpinnerArrowRight", Text.Get("VizzyStudio.Mfd.Wider"), row => row.Grow(new Vector2(_step, 0f))),
                Icon("Ui/Sprites/Common/IconMoveUp", Text.Get("VizzyStudio.Mfd.Taller"), row => row.Grow(new Vector2(0f, _step))),
                Icon("Ui/Sprites/Common/IconMoveDown", Text.Get("VizzyStudio.Mfd.Shorter"), row => row.Grow(new Vector2(0f, -_step)))));

            this.AddValue(Add, Text.Get("VizzyStudio.Mfd.Rotation"), widget => widget.LocalRotation, (row, value) => row.Widget.LocalRotation = value);
            Add(this.IconRow(
                Icon("Ui/Sprites/Design/IconSpinnerArrowLeft", Text.Get("VizzyStudio.Mfd.RotationDown"), row => row.Widget.LocalRotation -= _step * RotationStepFactor),
                Icon("Ui/Sprites/Design/IconSpinnerArrowRight", Text.Get("VizzyStudio.Mfd.RotationUp"), row => row.Widget.LocalRotation += _step * RotationStepFactor)));

            Add(new DropdownModel(
                Text.Get("VizzyStudio.Mfd.Step"),
                () => Number(_step),
                value => _step = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                Steps));
            return group;
        }

        /// <summary>The selected widget's value; nothing while none is selected.</summary>
        private void AddValue(Action<ItemModel> add, string label, Func<IMfdWidget, float> get, Action<Row, float> set)
        {
            var model = new FloatInputModel(
                label,
                () => _selected != null && _selected.IsAlive ? get(_selected.Widget) : 0f,
                value => this.Run(() =>
                {
                    if (_selected != null)
                    {
                        set(_selected, value);
                    }
                }),
                null,
                null,
                Number);
            add(model);
            _selectedValues.Add(model);
        }

        private IconButtonRowModel IconRow(params IconButtonModel[] icons)
        {
            var row = new IconButtonRowModel();
            foreach (var icon in icons)
            {
                row.Add(icon);
            }

            return row;
        }

        private IconButtonModel Icon(string sprite, string tooltip, Action<Row> action)
        {
            return new IconButtonModel(sprite, _ => this.Run(() =>
            {
                if (_selected != null)
                {
                    action(_selected);
                }
            }), tooltip);
        }

        /// <summary>A value changed: the fields and the list of changes follow.</summary>
        private void Run(Action action)
        {
            try
            {
                action();
                _message = null;
                this.UpdateCascade();
                foreach (var value in _selectedValues)
                {
                    value.Update();
                }

                this.ReplaceChangesGroup();
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't change the widget: " + e);
            }
        }

        /// <summary>
        /// Moves the other widgets that follow a change, through "get widget … position" or a shared variable
        /// when that's allowed, to where their own blocks would put them, by the difference, so the effect
        /// shows before saving. Widgets whose blocks can't be worked out stay until a restart.
        /// </summary>
        private void UpdateCascade()
        {
            foreach (var row in _rows.Where(item => item.IsAlive))
            {
                row.ClearCascade();
            }

            _cascaded = 0;
            var changes = this.PendingChanges();
            if (changes.Count == 0)
            {
                return;
            }

            this.ScreenSize(out var width, out var height);
            var preview = new XElement(_data.FlightProgramXml);
            if (!MfdFormulaWriter.Apply(preview, changes, width, height, _allowShared).Any(line => line.Applied))
            {
                return;
            }

            var before = MfdFormulaWriter.Evaluator(_data.FlightProgramXml, width, height);
            var after = MfdFormulaWriter.Evaluator(preview, width, height);
            var edited = new HashSet<(string, MfdProperty)>(changes.Select(change => (change.Widget, change.Property)));
            foreach (var row in _rows.Where(item => item.IsAlive))
            {
                var moved = false;
                foreach (var property in new[] { MfdProperty.Position, MfdProperty.Size, MfdProperty.Rotation })
                {
                    if (edited.Contains((row.Name, property)))
                    {
                        continue;
                    }

                    // Only where its own block still gives where it is: a child placed and then parented, for one,
                    // already moves with its parent, and moving it as well would move it twice.
                    var was = before(row.Name, property);
                    var now = was == null || !row.IsAt(property, was) ? null : after(row.Name, property);
                    if (now != null && (Math.Abs(now[0] - was[0]) > CascadeTolerance || Math.Abs(now[1] - was[1]) > CascadeTolerance))
                    {
                        row.ApplyCascade(property, (float)(now[0] - was[0]), (float)(now[1] - was[1]));
                        moved = true;
                    }
                }

                if (moved)
                {
                    _cascaded++;
                }
            }
        }

        /// <summary>
        /// Whether something reads a changed widget's position or size where the preview can't follow it: in a
        /// block whose widget name is built while running, in a custom instruction call or a variable, or in a
        /// block whose value the panel can't work out. A shared variable's change counts too, as its readers
        /// may be anywhere.
        /// </summary>
        private bool HasFollowersNotShown(List<MfdChange> changes, List<MfdLine> lines)
        {
            if (lines.Any(line => line.Applied && line.Shared))
            {
                return true;
            }

            var names = new HashSet<string>(changes.Where(change => change.Property != MfdProperty.Rotation).Select(change => change.Widget));
            this.ScreenSize(out var width, out var height);
            Func<string, MfdProperty, double[]> evaluate = null;
            foreach (var reader in _data.FlightProgramXml.Descendants("CraftProperty"))
            {
                var property = (string)reader.Attribute("property");
                if ((property != "Mfd.Position" && property != "Mfd.Size") || !names.Contains((string)reader.Elements("Constant").FirstOrDefault()?.Attribute("text")))
                {
                    continue;
                }

                var setter = reader.Ancestors("SetCraftProperty").FirstOrDefault();
                var name = setter?.Elements().FirstOrDefault();
                if (name == null || name.Name != "Constant")
                {
                    return true;
                }

                var target = (string)setter.Attribute("property");
                var targetProperty = target == "Mfd.SetPosition" ? MfdProperty.Position : target == "Mfd.SetSize" ? MfdProperty.Size : target == "Mfd.SetRotation" ? MfdProperty.Rotation : (MfdProperty?)null;
                evaluate ??= MfdFormulaWriter.Evaluator(_data.FlightProgramXml, width, height);
                if (targetProperty.HasValue && evaluate((string)name.Attribute("text"), targetProperty.Value) == null)
                {
                    return true;
                }
            }

            return false;
        }

        private List<MfdChange> PendingChanges()
        {
            return _rows.Where(row => row.IsAlive).SelectMany(row => row.PendingChanges()).ToList();
        }

        private GroupModel BuildChangesGroup()
        {
            _wrappedLabels.Clear();
            var group = new GroupModel(Text.Get("VizzyStudio.Mfd.Changes"));
            if (_message != null)
            {
                this.AddWrappedTo(group, _message);
            }

            var changes = this.PendingChanges();
            if (changes.Count == 0)
            {
                if (_message == null)
                {
                    group.Add(new LabelModel(Text.Get("VizzyStudio.Mfd.NothingChanged"), ElementAlignment.Left));
                }

                group.Add(new TextButtonModel(Text.Get("VizzyStudio.Mfd.RestartMfd"), _ => this.AskToRestart()));
                return group;
            }

            this.ScreenSize(out var width, out var height);
            var lines = MfdFormulaWriter.Apply(_data.FlightProgramXml, changes, width, height, _allowShared, true);
            foreach (var line in lines)
            {
                this.AddWrappedTo(group, line.Applied ? line.ToString() : Text.Get("VizzyStudio.Mfd.WontSave", line));
            }

            // On by default, so it's only shown when a shared variable is involved.
            if (lines.Any(line => line.Shared || line.NeedsSharedOverride))
            {
                group.Add(new ToggleModel(
                    Text.Get("VizzyStudio.Mfd.AllowShared"),
                    () => _allowShared,
                    value =>
                    {
                        _allowShared = value;
                        this.UpdateCascade();
                        this.ReplaceChangesGroup();
                    },
                    Text.Get("VizzyStudio.Mfd.AllowSharedTooltip")));
            }

            if (_cascaded > 0)
            {
                this.AddWrappedTo(group, Text.Count("VizzyStudio.Mfd.MovedFollowers", _cascaded));
            }

            if (this.HasFollowersNotShown(changes, lines))
            {
                this.AddWrappedTo(group, Text.Get("VizzyStudio.Mfd.FollowersNotShown"));
            }

            group.Add(new TextButtonModel(Text.Get("VizzyStudio.Mfd.Save"), _ => this.Save()));
            group.Add(new TextButtonModel(Text.Get("VizzyStudio.Mfd.Revert"), _ => this.Run(this.RevertAll)));
            group.Add(new TextButtonModel(Text.Get("VizzyStudio.Mfd.RestartMfd"), _ => this.AskToRestart()));
            return group;
        }

        private void AddWrappedTo(GroupModel group, string text)
        {
            var label = group.Add(new LabelModel(text, ElementAlignment.Left));
            label.PreferredHeight = LabelLineHeight * Mathf.Max(1, Mathf.CeilToInt((float)text.Length / LabelCharactersPerLine));
            _wrappedLabels.Add(label);
        }

        private void RevertAll()
        {
            foreach (var row in _rows.Where(item => item.IsAlive))
            {
                row.Revert();
            }
        }

        /// <summary>
        /// The running program first, the way Edit Vizzy's Apply does it, so threads carry on and a quickload
        /// keeps the edit. The craft's files only follow if that worked.
        /// </summary>
        private void Save()
        {
            try
            {
                var changes = this.PendingChanges();
                if (changes.Count == 0)
                {
                    return;
                }

                this.ScreenSize(out var width, out var height);
                var before = _data.FlightProgramXml;
                var program = new XElement(before);
                var lines = MfdFormulaWriter.Apply(program, changes, width, height, _allowShared);
                foreach (var line in lines)
                {
                    EditorLog.Debug((line.Applied ? "MFD save: " : "MFD not saved: ") + line);
                }

                if (!lines.Any(line => line.Applied))
                {
                    _message = Text.Get("VizzyStudio.Mfd.NothingSaved");
                    this.ReplaceChangesGroup();
                    return;
                }

                if (!ProgramSwapper.Swap(_data, program, out var swapMessage))
                {
                    _message = swapMessage;
                    this.ReplaceChangesGroup();
                    return;
                }

                LatestEdits.Remember(CraftPrograms.Key(_data), before, program);
                _message = CraftWorkingCopy.Write(_data, (string)before.Attribute("name"), program) ?? Text.Get("VizzyStudio.MfdSave.SavedToWorkingCopy");

                if (lines.Any(line => line.Applied && line.Shared))
                {
                    _message += " " + Text.Get("VizzyStudio.Mfd.SharedAfterRestart");
                }

                // What couldn't be saved stays listed, so it can still be reverted. Widgets moved to show a
                // shared change are where the program now puts them.
                foreach (var row in _rows.Where(item => item.IsAlive))
                {
                    row.AcceptCascade();
                    row.AcceptSaved(lines);
                }

                _cascaded = 0;

                this.ClassifyAll();
                this.RefreshList();
                this.ReplaceChangesGroup();
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't save the MFD changes: " + e);
                _message = Text.Get("VizzyStudio.Mfd.SaveFailed");
                this.ReplaceChangesGroup();
            }
        }

        /// <summary>The panel stays until the answer, so the flight stays held. Then it closes: the program can't build anything while paused.</summary>
        private void AskToRestart()
        {
            var dialog = Game.Instance.UserInterface.CreateMessageDialog(ModApi.Ui.MessageDialogType.OkayCancel, null, true);
            dialog.UseDangerButtonStyle = true;
            dialog.MessageText = MfdRestart.Question(_data) + " " + Text.Get("VizzyStudio.Mfd.UnsavedLost");
            dialog.OkayClicked += _ =>
            {
                dialog.Close();
                try
                {
                    this.Close();
                    if (!MfdRestart.Run(_data))
                    {
                        Game.Instance.FlightScene.FlightSceneUI.ShowMessage(Text.Get("VizzyStudio.Mfd.RestartFailed"));
                    }
                }
                catch (Exception e)
                {
                    EditorLog.Error("Couldn't restart the MFD program: " + e);
                }
            };
        }

        private void OnPanelClosed(object sender)
        {
            this.Finish();
        }

        /// <summary>Whichever way the panel closed, once. At a scene's end the widgets and the flight may be gone.</summary>
        private void Finish()
        {
            if (_panel == null)
            {
                return;
            }

            _panel = null;
            try
            {
                this.RevertAll();
                _outline.Hide();
            }
            catch (Exception e)
            {
                EditorLog.Debug("MFD panel closed while the scene ends: " + e.Message);
            }

            try
            {
                FlightPause.Release();
            }
            catch (Exception e)
            {
                EditorLog.Debug("Flight not released while the scene ends: " + e.Message);
            }
        }

        /// <summary>One widget, and its values when the panel opened or last saved.</summary>
        private sealed class Row
        {
            private readonly HashSet<MfdProperty> _cascade = new HashSet<MfdProperty>();
            private Vector2 _position;
            private Vector2 _size;
            private float _rotation;

            public Row(IMfdWidget widget)
            {
                this.Widget = widget;
                this.Name = widget.Name;
                _position = widget.LocalPosition;
                _size = widget.Size;
                _rotation = widget.LocalRotation;
            }

            public IMfdWidget Widget { get; }

            public string Name { get; }

            /// <summary>Its row in the list, or null when it isn't listed.</summary>
            public TextButtonModel Button { get; set; }

            public bool CanArrange { get; set; }

            public string Label { get; set; }

            public string Tooltip { get; set; }

            /// <summary>A destroyed widget's own properties throw, so its Unity object is asked first.</summary>
            public bool IsAlive => (!(this.Widget is UnityEngine.Object unity) || unity != null) && this.Widget.Transform != null;

            public void SetPosition(Vector2 value)
            {
                this.Widget.LocalPosition = value;
            }

            public void Move(Vector2 delta)
            {
                this.Widget.LocalPosition += delta;
            }

            public void SetSize(Vector2 value)
            {
                this.Widget.Size = new Vector2(Mathf.Max(MinSize, value.x), Mathf.Max(MinSize, value.y));
            }

            public void Grow(Vector2 delta)
            {
                this.SetSize(this.Widget.Size + delta);
            }

            public void Revert()
            {
                _cascade.Clear();
                this.Widget.LocalPosition = _position;
                this.Widget.Size = _size;
                this.Widget.LocalRotation = _rotation;
            }

            /// <summary>Whether a value is where the property was when the panel opened or last saved.</summary>
            public bool IsAt(MfdProperty property, double[] value)
            {
                switch (property)
                {
                    case MfdProperty.Position:
                        return Close(value[0], _position.x) && Close(value[1], _position.y);
                    case MfdProperty.Size:
                        return Close(value[0], _size.x) && Close(value[1], _size.y);
                    default:
                        return Math.Abs(MfdFormulaWriter.DeltaAngle(value[0], _rotation)) <= MatchTolerance;
                }
            }

            private static bool Close(double a, double b)
            {
                return Math.Abs(a - b) <= MatchTolerance + (1e-5 * Math.Abs(b));
            }

            /// <summary>Moves a property from where it was by a shared change's effect; it isn't a change of its own.</summary>
            public void ApplyCascade(MfdProperty property, float dx, float dy)
            {
                _cascade.Add(property);
                switch (property)
                {
                    case MfdProperty.Position:
                        this.Widget.LocalPosition = _position + new Vector2(dx, dy);
                        break;
                    case MfdProperty.Size:
                        this.Widget.Size = new Vector2(Mathf.Max(MinSize, _size.x + dx), Mathf.Max(MinSize, _size.y + dy));
                        break;
                    default:
                        this.Widget.LocalRotation = _rotation + dx;
                        break;
                }
            }

            public void ClearCascade()
            {
                foreach (var property in _cascade)
                {
                    switch (property)
                    {
                        case MfdProperty.Position:
                            this.Widget.LocalPosition = _position;
                            break;
                        case MfdProperty.Size:
                            this.Widget.Size = _size;
                            break;
                        default:
                            this.Widget.LocalRotation = _rotation;
                            break;
                    }
                }

                _cascade.Clear();
            }

            /// <summary>After a save the program puts it here, so this is the new starting point.</summary>
            public void AcceptCascade()
            {
                if (_cascade.Contains(MfdProperty.Position))
                {
                    _position = this.Widget.LocalPosition;
                }

                if (_cascade.Contains(MfdProperty.Size))
                {
                    _size = this.Widget.Size;
                }

                if (_cascade.Contains(MfdProperty.Rotation))
                {
                    _rotation = this.Widget.LocalRotation;
                }

                _cascade.Clear();
            }

            /// <summary>A small move of every axis from where it was, for a dry run that finds out what could be saved.</summary>
            public IEnumerable<MfdChange> ProbeChanges()
            {
                const float probe = 1f;
                yield return new MfdChange { Widget = this.Name, Property = MfdProperty.Position, X = _position.x + probe, Y = _position.y + probe, FromX = _position.x, FromY = _position.y };
                yield return new MfdChange { Widget = this.Name, Property = MfdProperty.Size, X = _size.x + probe, Y = _size.y + probe, FromX = _size.x, FromY = _size.y };
                yield return new MfdChange { Widget = this.Name, Property = MfdProperty.Rotation, X = _rotation + probe, FromX = _rotation };
            }

            public IEnumerable<MfdChange> PendingChanges()
            {
                if (!_cascade.Contains(MfdProperty.Position) && this.Widget.LocalPosition != _position)
                {
                    yield return this.Change(MfdProperty.Position);
                }

                if (!_cascade.Contains(MfdProperty.Size) && this.Widget.Size != _size)
                {
                    yield return this.Change(MfdProperty.Size);
                }

                if (!_cascade.Contains(MfdProperty.Rotation) && !Mathf.Approximately(this.Widget.LocalRotation, _rotation))
                {
                    yield return this.Change(MfdProperty.Rotation);
                }
            }

            /// <summary>What was saved becomes the new starting point; an axis that wasn't stays changed, so it can still be reverted.</summary>
            public void AcceptSaved(IEnumerable<MfdLine> lines)
            {
                foreach (var line in lines.Where(item => item.Applied && item.Widget == this.Name))
                {
                    switch (line.Property)
                    {
                        case MfdProperty.Position:
                            _position = Accept(_position, this.Widget.LocalPosition, line.Axis);
                            break;
                        case MfdProperty.Size:
                            _size = Accept(_size, this.Widget.Size, line.Axis);
                            break;
                        default:
                            _rotation = this.Widget.LocalRotation;
                            break;
                    }
                }
            }

            private static Vector2 Accept(Vector2 start, Vector2 now, string axis)
            {
                return axis == "x" ? new Vector2(now.x, start.y) : axis == "y" ? new Vector2(start.x, now.y) : now;
            }

            private MfdChange Change(MfdProperty property)
            {
                switch (property)
                {
                    case MfdProperty.Position:
                        return new MfdChange { Widget = this.Name, Property = property, X = this.Widget.LocalPosition.x, Y = this.Widget.LocalPosition.y, FromX = _position.x, FromY = _position.y };
                    case MfdProperty.Size:
                        return new MfdChange { Widget = this.Name, Property = property, X = this.Widget.Size.x, Y = this.Widget.Size.y, FromX = _size.x, FromY = _size.y };
                    default:
                        return new MfdChange { Widget = this.Name, Property = property, X = this.Widget.LocalRotation, FromX = _rotation };
                }
            }
        }
    }
}
