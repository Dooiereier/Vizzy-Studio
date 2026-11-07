namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using Assets.Scripts.Vizzy.UI;
    using UnityEngine;

    /// <summary>
    /// The designer's Vizzy editor reopens where it was left, per part and program, until the game closes.
    /// EditorSession does the same for the flight editor.
    /// </summary>
    public sealed class EditorViewMemory : MonoBehaviour
    {
        private static readonly Dictionary<string, View> Views = new Dictionary<string, View>();

        private VizzyUIController _controller;
        private VizzyUIScript _editor;
        private ProgramContainerScript _container;
        private string _key;

        public void Connect(VizzyUIController controller)
        {
            _controller = controller;
        }

        private void Update()
        {
            if (_container != null || _controller == null || _controller.VizzyUI == null || _controller.VizzyUI.FlightProgram == null)
            {
                return;
            }

            var container = _controller.ProgramContainer;
            if (container == null || container.VizzyUI == null)
            {
                return;
            }

            _container = container;
            _editor = _controller.VizzyUI;
            EditorDefaultView.Capture(_editor as VizzyUIScript, container);
            _key = Key(_editor);
            _editor.Closed += OnEditorClosed;
            if (Views.TryGetValue(_key, out var view))
            {
                // Zoom first: it moves the canvas too.
                _container.Zoom = view.Zoom;
                _editor.ProgramTransform.localPosition = view.Position;
            }
        }

        private void OnEditorClosed(object sender, EventArgs e)
        {
            _editor.Closed -= OnEditorClosed;
            if (_container != null && _editor.ProgramTransform != null)
            {
                Views[_key] = new View { Position = _editor.ProgramTransform.localPosition, Zoom = _container.Zoom };
            }
        }

        private static string Key(VizzyUIScript editor)
        {
            var part = Game.Instance.Designer?.SelectedPart?.Data?.Id.ToString() ?? string.Empty;
            return part + "|" + editor.FlightProgram.Name;
        }

        private struct View
        {
            public Vector3 Position;
            public float Zoom;
        }
    }
}
