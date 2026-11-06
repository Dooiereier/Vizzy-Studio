namespace Assets.Scripts
{
    using System;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Flight.UI;
    using Assets.Scripts.Vizzy;
    using Assets.Scripts.Vizzy.UI;
    using ModApi.Ui;
    using UI.Xml;
    using UnityEngine.UI;

    /// <summary>
    /// XML changes to stock UI: an Edit Vizzy button in the flight menu, and Apply and Cancel that
    /// work without the designer in an editor this mod opens. Other editors stay stock, including
    /// one another mod opens in flight.
    /// </summary>
    public static class FlightEditorUi
    {
        private const string MenuButtonId = "vizzy-studio-menu-button";
        private const string ApplyButtonId = "vizzy-studio-apply";
        private const string CancelButtonId = "vizzy-studio-cancel";
        private const string CleanButtonId = "vizzy-studio-clean-variables";
        private const string ExportButtonId = "vizzy-studio-export";

        /// <summary>Gets or sets a value indicating whether the editor being built is one of this mod's.</summary>
        public static bool BuildingOurEditor { get; set; }

        public static void Register()
        {
            var ui = Game.Instance.UserInterface;
            ui.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.FlightSceneUI, OnBuildFlightUi);
            ui.AddBuildUserInterfaceXmlAction(UserInterfaceIds.Vizzy, OnBuildVizzyUi);
        }

        private static void OnBuildFlightUi(BuildUserInterfaceXmlRequest request)
        {
            try
            {
                XNamespace ns = XmlLayoutConstants.XmlNamespace;
                var quickSaveRow = FindButton(request, "OnQuickSaveButtonClicked();").Parent;
                quickSaveRow.AddAfterSelf(
                    new XElement(ns + "Button",
                        new XAttribute("id", MenuButtonId),
                        new XAttribute("class", "btn menu-button"),
                        new XElement(ns + "TextMeshPro", new XAttribute("text", Text.Get("VizzyStudio.Buttons.EditVizzy")))));

                request.AddOnLayoutRebuiltAction(controller =>
                {
                    Listen(controller, MenuButtonId, () =>
                    {
                        (controller as FlightSceneUiController)?.ToggleMenu();
                        FlightEditorController.Current?.RequestOpen();
                    });
                });
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't add the flight menu button: " + e);
            }
        }

        private static void OnBuildVizzyUi(BuildUserInterfaceXmlRequest request)
        {
            AddCleanVariablesButton(request);
            try
            {
                Assets.Scripts.VizzyOrganizer.VizzyJumpPanel.AddTo(request);
            }
            catch (Exception e)
            {
                EditorLog.Warning("Couldn't add the Jump To flyout: " + e);
            }

            try
            {
                BlockSearchPanel.AddTo(request);
            }
            catch (Exception e)
            {
                EditorLog.Warning("Couldn't add the block search: " + e);
            }

            request.AddOnLayoutRebuiltAction(controller =>
            {
                if (controller is VizzyUIController vizzy && vizzy.GetComponent<EditorCulling>() == null)
                {
                    vizzy.gameObject.AddComponent<EditorCulling>().Connect(vizzy);
                }
            });

            if (BuildingOurEditor)
            {
                ReplaceApplyAndCancel(request);
            }
            else
            {
                request.AddOnLayoutRebuiltAction(controller =>
                {
                    if (controller is VizzyUIController vizzy && vizzy.GetComponent<EditorViewMemory>() == null)
                    {
                        vizzy.gameObject.AddComponent<EditorViewMemory>().Connect(vizzy);
                    }
                });
            }
        }

        /// <summary>In every Vizzy editor, as the game's own console can't be reached in flight.</summary>
        private static void AddCleanVariablesButton(BuildUserInterfaceXmlRequest request)
        {
            try
            {
                XNamespace ns = XmlLayoutConstants.XmlNamespace;
                var gridRow = request.XmlDocument.Descendants(ns + "Toggle")
                    .First(toggle => (string)toggle.Attribute("id") == "grid-toggle").Parent.Parent;
                gridRow.AddAfterSelf(
                    new XElement(ns + "Button",
                        new XAttribute("id", CleanButtonId),
                        new XAttribute("class", "btn"),
                        new XAttribute("tooltip", Text.Get("VizzyStudio.Editor.CleanVariablesTooltip")),
                        new XElement(ns + "TextMeshPro", new XAttribute("text", Text.Get("VizzyStudio.Editor.CleanVariables")))));
                gridRow.Parent.Elements().First(e => (string)e.Attribute("id") == CleanButtonId).AddAfterSelf(
                    new XElement(ns + "Button",
                        new XAttribute("id", ExportButtonId),
                        new XAttribute("class", "btn"),
                        new XAttribute("tooltip", Text.Get("VizzyStudio.Editor.ExportBlockTooltip")),
                        new XElement(ns + "TextMeshPro", new XAttribute("text", Text.Get("VizzyStudio.Editor.ExportBlock")))));

                request.AddOnLayoutRebuiltAction(controller =>
                {
                    Listen(controller, CleanButtonId, () =>
                    {
                        if (controller is VizzyUIController vizzy)
                        {
                            VizzyVariableCleaner.AskAndClean(vizzy.VizzyUI);
                        }
                    });
                    Listen(controller, ExportButtonId, () =>
                    {
                        if (controller is VizzyUIController vizzy)
                        {
                            Exporter.ExportBlock.Run(vizzy.VizzyUI);
                        }
                    });
                });
            }
            catch (Exception e)
            {
                EditorLog.Warning("Couldn't add the Clean Variables button: " + e);
            }
        }

        private static void ReplaceApplyAndCancel(BuildUserInterfaceXmlRequest request)
        {
            try
            {
                // Both found first: a missing button then leaves both stock.
                var apply = FindButton(request, "OnButtonApplyClicked();");
                var cancel = FindButton(request, "OnButtonCancelClicked();");
                apply.SetAttributeValue("id", ApplyButtonId);
                apply.SetAttributeValue("onClick", null);
                cancel.SetAttributeValue("id", CancelButtonId);
                cancel.SetAttributeValue("onClick", null);

                request.AddOnLayoutRebuiltAction(controller =>
                {
                    Listen(controller, ApplyButtonId, () => FlightEditorController.Current?.Session?.ApplyAndClose());
                    Listen(controller, CancelButtonId, () => FlightEditorController.Current?.Session?.ConfirmDiscard());
                });
            }
            catch (Exception e)
            {
                // The stock buttons still work: they only log an error after closing.
                EditorLog.Warning("Couldn't replace the editor's Apply and Cancel: " + e);
            }
        }

        private static XElement FindButton(BuildUserInterfaceXmlRequest request, string onClick)
        {
            XNamespace ns = XmlLayoutConstants.XmlNamespace;
            return request.XmlDocument.Descendants(ns + "Button").First(button => (string)button.Attribute("onClick") == onClick);
        }

        private static void Listen(IXmlLayoutController controller, string id, Action action)
        {
            var element = ((XmlLayout)controller.XmlLayout).GetElementById(id);
            var button = element == null ? null : element.GetComponent<Button>();
            if (button == null)
            {
                EditorLog.Error("Couldn't find button " + id + ".");
                return;
            }

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    EditorLog.Error("Button " + id + " failed: " + e);
                }
            });
        }
    }
}
