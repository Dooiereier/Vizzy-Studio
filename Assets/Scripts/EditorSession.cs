namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Linq;
    using Assets.Scripts.Craft.Parts.Modifiers;
    using Assets.Scripts.Vizzy.UI;
    using ModApi;
    using ModApi.Ui;
    using UnityEngine;

    /// <summary>One open editor: pauses the game, hides the flight UI, puts the canvas back and closes.</summary>
    public sealed class EditorSession
    {
        // Survives a quickload's scene reload, so the canvas stays put through the tuning loop.
        private static readonly Dictionary<string, EditorView> Views = new Dictionary<string, EditorView>();

        private readonly FlightProgramData _data;
        private readonly string _key;
        private VizzyUIScript _editor;
        private bool _wasUiVisible;
        private string _closingMessage;
        private string _applyFailure;
        private bool _applied;
        private bool _restoreViewPending;
        private ProgramContainerScript _viewContainer;

        private EditorSession(FlightProgramData data)
        {
            _data = data;
            _key = CraftPrograms.Key(data);
        }

        public bool IsOpen => _editor != null;

        public static EditorSession Open(FlightProgramData data)
        {
            var session = new EditorSession(data);
            session.Open();
            return session;
        }

        /// <summary>Puts the canvas back where it was last time for this part, once its UI exists.</summary>
        public void Update()
        {
            if (!IsOpen)
            {
                return;
            }

            // Esc shows the flight UI whatever else is open, and the next Esc would then open its menu.
            var flightUi = Game.Instance.FlightScene.FlightSceneUI;
            if (flightUi.Visible)
            {
                flightUi.Visible = false;
            }

            if (!_restoreViewPending)
            {
                return;
            }

            _viewContainer = UnityEngine.Object.FindObjectOfType<ProgramContainerScript>();
            if (_viewContainer == null || _viewContainer.VizzyUI == null)
            {
                return;
            }

            _restoreViewPending = false;
            EditorDefaultView.Capture(_editor as VizzyUIScript, _viewContainer);
            if (Views.TryGetValue(_key, out var view))
            {
                // Zoom first: it moves the canvas too.
                _viewContainer.Zoom = view.Zoom;
                _editor.ProgramTransform.localPosition = view.Position;
            }
        }

        /// <summary>The shortcut's use in the editor: Apply, unless the key was being typed somewhere.</summary>
        public void ApplyFromShortcut()
        {
            var ui = Game.Instance.UserInterface;
            if (!ui.AnyDialogsOpen && !ui.IsTextInputFocused)
            {
                ApplyAndClose();
            }
        }

        /// <summary>Stays open if the edit couldn't be applied, so the work isn't lost.</summary>
        public void ApplyAndClose()
        {
            _applied = false;
            _applyFailure = null;
            try
            {
                _editor.ApplyChangesToCraft();
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't apply the edit: " + e);
            }

            if (_applied)
            {
                _editor.Close();
            }
            else
            {
                _editor.ShowMessage(_applyFailure ?? Text.Get("VizzyStudio.Editor.ApplyFailed"));
            }
        }

        /// <summary>The editor's Cancel: asks first, in the stock dialog.</summary>
        public void ConfirmDiscard()
        {
            var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.OkayCancel, null, true);
            dialog.UseDangerButtonStyle = true;
            dialog.MessageText = Locale.GetString("Vizzy.UI.DiscardChanges.Confirm");
            dialog.OkayClicked += _ =>
            {
                dialog.Close();
                Close();
            };
        }

        public void Close()
        {
            if (IsOpen)
            {
                _editor.Close();
            }
        }

        private void Open()
        {
            var ui = Game.Instance.UserInterface;
            var scene = Game.Instance.FlightScene;
            FlightPause.Hold();
            _wasUiVisible = scene.FlightSceneUI.Visible;
            scene.FlightSceneUI.Visible = false;

            try
            {
                FlightEditorUi.BuildingOurEditor = true;
                _editor = Game.Instance.ResourceLoader.InstantiatePrefab<VizzyUIScript>("Ui/Vizzy/Vizzy");
                _editor.Initialize(ui.Transform, _data.FlightProgramXml, CraftPrograms.IsMfd(_data), OnApply);
                _editor.Closed += OnEditorClosed;
                _restoreViewPending = true;
            }
            catch
            {
                if (_editor != null)
                {
                    UnityEngine.Object.Destroy(_editor.gameObject);
                    _editor = null;
                }

                RestoreFlight();
                throw;
            }
            finally
            {
                FlightEditorUi.BuildingOurEditor = false;
            }

            EditorLog.Debug("Editor open for part " + _key + ".");
        }

        private void OnApply(XElement newProgram)
        {
            try
            {
                if (_data.Script == null)
                {
                    _applyFailure = Text.Get("VizzyStudio.Editor.PartGoneNotApplied");
                    return;
                }

                var before = _data.FlightProgramXml;
                if (XNode.DeepEquals(before, newProgram))
                {
                    _applied = true;
                }
                else if (ProgramSwapper.Swap(_data, newProgram, out var message))
                {
                    _applied = true;
                    _closingMessage = message;
                    LatestEdits.Remember(_key, before, newProgram);
                }
                else
                {
                    _applyFailure = message;
                }
            }
            catch (Exception e)
            {
                EditorLog.Error("Apply failed: " + e);
            }
        }

        private void OnEditorClosed(object sender, EventArgs e)
        {
            // Still alive here: Close only destroys the editor at the end of the frame.
            if (!_restoreViewPending && _viewContainer != null && _editor.ProgramTransform != null)
            {
                Views[_key] = new EditorView { Position = _editor.ProgramTransform.localPosition, Zoom = _viewContainer.Zoom };
            }

            _restoreViewPending = false;
            _viewContainer = null;
            RestoreFlight();
            _editor = null;
            EditorLog.Debug("Editor closed.");
        }

        /// <summary>Scene teardown may have removed the flight UI already: the next scene doesn't need it.</summary>
        private void RestoreFlight()
        {
            try
            {
                var scene = Game.Instance.FlightScene;
                if (scene == null)
                {
                    return;
                }

                scene.FlightSceneUI.Visible = _wasUiVisible;
                FlightPause.Release();

                if (_closingMessage != null)
                {
                    scene.FlightSceneUI.ShowMessage(_closingMessage);
                }
            }
            catch (Exception e)
            {
                EditorLog.Debug("Flight UI not restored while the scene ends: " + e.Message);
            }
        }

        private struct EditorView
        {
            public Vector3 Position;
            public float Zoom;
        }
    }
}
