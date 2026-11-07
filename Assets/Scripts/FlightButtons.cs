namespace Assets.Scripts
{
    using System;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Flight.UI;
    using ModApi.Ui;
    using UI.Xml;
    using UnityEngine;

    /// <summary>A Vizzy button at the bottom of the flight nav panel, opening a flyout with Restart and Edit.</summary>
    public static class FlightButtons
    {
        private const string NavPanelId = "Ui/Xml/Flight/NavPanel";
        private const string StockFlyoutId = "nav-sphere-panel";
        private const string ToggleId = "vizzy-studio-toggle";
        private const string ToggleIconId = "vizzy-studio-toggle-icon";
        private const string FlyoutId = "vizzy-studio-flyout";
        private const string EditId = "vizzy-studio-edit";
        private const string RestartId = "vizzy-studio-restart";
        private const string ArrangeMfdId = "vizzy-studio-arrange-mfd";
        private const string ToggledClass = "panel-button-icon-toggled";
        private const int FramesBetweenSearches = 30;

        private static XmlElement _connectedToggle;

        public static void Register()
        {
            Game.Instance.UserInterface.AddBuildUserInterfaceXmlAction(NavPanelId, OnBuildNavPanel);
        }

        private static void OnBuildNavPanel(BuildUserInterfaceXmlRequest request)
        {
            try
            {
                if (!ModSettings.Instance.ShowFlightButtons.Value)
                {
                    return;
                }

                XNamespace ns = XmlLayoutConstants.XmlNamespace;
                var layouts = request.XmlDocument.Descendants(ns + "VerticalLayout").ToList();
                var bar = layouts.FirstOrDefault(layout => (string)layout.Attribute("name") == "NavPanel");
                var stockFlyout = layouts.FirstOrDefault(layout => (string)layout.Attribute("id") == StockFlyoutId);
                if (bar == null || stockFlyout == null)
                {
                    EditorLog.Warning("Couldn't find where to put the flight buttons.");
                    return;
                }

                var arrangeMfd = ModSettings.Instance.ArrangeMfd.Value;
                bar.Add(new XElement(ns + "ContentButton",
                    new XAttribute("id", ToggleId),
                    new XAttribute("class", "panel-button audio-btn-click"),
                    new XAttribute("tooltip", Text.Get("VizzyStudio.Buttons.Vizzy")),
                    new XElement(ns + "Image",
                        new XAttribute("id", ToggleIconId),
                        new XAttribute("class", "panel-button-icon"),
                        new XAttribute("sprite", "Ui/Sprites/Design/IconButtonEditProgram"))));

                stockFlyout.AddAfterSelf(new XElement(ns + "VerticalLayout",
                    new XAttribute("id", FlyoutId),
                    new XAttribute("class", "flight-panel"),
                    new XAttribute("active", "false"),
                    new XAttribute("padding", "5"),
                    new XAttribute("childForceExpandWidth", "false"),
                    new XAttribute("childForceExpandHeight", "false"),
                    new XAttribute("offsetXY", "-60 0"),
                    new XAttribute("childAlignment", "UpperRight"),
                    new XAttribute("spacing", "0"),
                    new XAttribute("rectAlignment", "UpperRight"),
                    new XAttribute("width", arrangeMfd ? "145" : "100"),
                    new XAttribute("pivot", "1 1"),
                    new XAttribute("contentSizeFitter", "vertical"),
                    new XElement(ns + "HorizontalLayout",
                        new XAttribute("class", "nav-sphere-row"),
                        arrangeMfd ? FlyoutButton(ns, ArrangeMfdId, Text.Get("VizzyStudio.Buttons.ArrangeMfd"), "VizzyStudio/Sprites/IconMfd") : null,
                        FlyoutButton(ns, RestartId, Text.Get("VizzyStudio.Buttons.RestartVizzy"), "Ui/Sprites/Flight/IconRetry"),
                        FlyoutButton(ns, EditId, Text.Get("VizzyStudio.Buttons.EditVizzy"), "VizzyStudio/Sprites/IconVariables"))));
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't add the flight buttons: " + e);
            }
        }

        /// <summary>
        /// Each frame in flight. NavPanelController overrides LayoutRebuilt without calling the base,
        /// so a request's rebuilt action never runs for this layout.
        /// </summary>
        public static void ConnectIfNeeded()
        {
            if (_connectedToggle != null || Time.frameCount % FramesBetweenSearches != 0 || !ModSettings.Instance.ShowFlightButtons.Value)
            {
                return;
            }

            var controller = UnityEngine.Object.FindObjectOfType<NavPanelController>();
            if (controller != null)
            {
                Connect((XmlLayout)((IXmlLayoutController)controller).XmlLayout);
            }
        }

        private static void Connect(XmlLayout layout)
        {
            var toggle = layout.GetElementById(ToggleId);
            var icon = layout.GetElementById(ToggleIconId);
            var flyout = layout.GetElementById(FlyoutId);
            var edit = layout.GetElementById(EditId);
            var restart = layout.GetElementById(RestartId);
            if (toggle == null || icon == null || flyout == null || edit == null || restart == null)
            {
                EditorLog.Error("Couldn't find the flight buttons.");

                // Only says so once for this layout.
                _connectedToggle = toggle ?? layout.XmlElement;
                return;
            }

            _connectedToggle = toggle;

            flyout.gameObject.AddComponent<FlyoutPlacement>().Connect(
                (RectTransform)toggle.transform, layout.GetElementById(StockFlyoutId));

            void Hide()
            {
                icon.RemoveClass(ToggledClass);
                flyout.Hide();
            }

            EditorLog.Debug("Flight buttons connected.");
            toggle.AddOnClickEvent(() =>
            {
                EditorLog.Debug("Vizzy button clicked, flyout " + (flyout.gameObject.activeSelf ? "open" : "closed") + ".");
                if (flyout.gameObject.activeSelf)
                {
                    Hide();
                }
                else
                {
                    icon.AddClass(ToggledClass);
                    flyout.Show();
                }
            });
            edit.AddOnClickEvent(() =>
            {
                EditorLog.Debug("Edit Vizzy clicked.");
                Hide();
                FlightEditorController.Current?.EditSelected();
            });
            layout.GetElementById(ArrangeMfdId)?.AddOnClickEvent(() =>
            {
                EditorLog.Debug("Arrange MFD clicked.");
                Hide();
                FlightEditorController.Current?.ArrangeMfd();
            });
            restart.AddOnClickEvent(() =>
            {
                EditorLog.Debug("Restart Vizzy clicked.");
                Hide();
                FlightEditorController.Current?.RestartSelected();
            });
        }

        private static XElement FlyoutButton(XNamespace ns, string id, string tooltip, string sprite)
        {
            return new XElement(ns + "ContentButton",
                new XAttribute("id", id),
                new XAttribute("class", "panel-button audio-btn-click"),
                new XAttribute("tooltip", tooltip),
                new XElement(ns + "Image",
                    new XAttribute("class", "panel-button-icon"),
                    new XAttribute("sprite", sprite)));
        }

        /// <summary>Keeps the flyout level with the Vizzy button, and clear of the nav sphere flyout when they'd overlap.</summary>
        private sealed class FlyoutPlacement : MonoBehaviour
        {
            private const float Gap = 10f;
            private const float Padding = 5f;
            private const float StockOffset = -60f;

            private readonly Vector3[] _corners = new Vector3[4];
            private RectTransform _button;
            private RectTransform _stockFlyout;

            public void Connect(RectTransform button, XmlElement stockFlyout)
            {
                _button = button;
                _stockFlyout = stockFlyout != null ? (RectTransform)stockFlyout.transform : null;
            }

            private void LateUpdate()
            {
                if (_button == null)
                {
                    return;
                }

                var rect = (RectTransform)transform;
                var parent = (RectTransform)rect.parent;
                _button.GetWorldCorners(_corners);
                var buttonTop = parent.InverseTransformPoint(_corners[1]).y;

                // Its row count varies, so it's only in the way when it reaches down this far.
                var x = StockOffset;
                if (_stockFlyout != null && _stockFlyout.gameObject.activeSelf)
                {
                    _stockFlyout.GetWorldCorners(_corners);
                    if (parent.InverseTransformPoint(_corners[0]).y < buttonTop + Padding)
                    {
                        x -= _stockFlyout.rect.width + Gap;
                    }
                }

                // Anchored to the parent's top right corner, with the row's padding above the button's top.
                rect.anchoredPosition = new Vector2(x, buttonTop - parent.rect.yMax + Padding);
            }
        }
    }
}
