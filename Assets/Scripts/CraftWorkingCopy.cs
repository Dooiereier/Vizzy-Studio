namespace Assets.Scripts
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Craft.Parts.Modifiers;

    /// <summary>
    /// Puts a program edited in flight into the designer's working copy of the craft (what the designer shows and
    /// a relaunch uses), so it's there on return to the designer. The saved craft is left alone: loading it
    /// gets the original back. One backup per flight, made before that flight's first write.
    /// </summary>
    public static class CraftWorkingCopy
    {
        private static object _backupFlight;

        /// <summary>Why it couldn't be written, for the player, or null when it was.</summary>
        public static string Write(FlightProgramData data, string programName, XElement program)
        {
            try
            {
                var designs = Game.Instance.CraftDesigns;
                var id = CraftDesigns.EditorCraftId;
                var craftName = data.Part.PartScript.CraftScript.Data.Name;
                if (string.IsNullOrEmpty(id) || !designs.HasCraft(id) || CraftDesigns.IsStock(id))
                {
                    return Text.Get("VizzyStudio.MfdSave.NoFile");
                }

                // A copy: the game keeps the working copy's XML in memory.
                var craft = new XElement(designs.GetCraftDesign(id));

                // The working copy holds another craft when this flight didn't start from the designer.
                var target = (string)craft.Attribute("name") == craftName ? FindProgram(craft, programName, data.Part.Id) : null;
                if (target == null || target.Name != program.Name)
                {
                    EditorLog.Debug("Working copy not changed: it doesn't hold this craft's program " + programName + ".");
                    return Text.Get("VizzyStudio.MfdSave.NoFile");
                }

                target.ReplaceWith(new XElement(program));
                if (!ReferenceEquals(_backupFlight, Game.Instance.FlightScene))
                {
                    var file = designs.GetCraftFile(id);
                    File.Copy(file.FullName, file.FullName + ".bak", true);
                    _backupFlight = Game.Instance.FlightScene;
                }

                designs.SaveCraft(id, craft);
                EditorLog.Debug("Wrote program " + programName + " to the working copy.");
                return null;
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't write the working copy: " + e);
                return Text.Get("VizzyStudio.MfdSave.WriteFailed");
            }
        }

        /// <summary>By name; the part's id only settles it when several parts have that program.</summary>
        private static XElement FindProgram(XElement craft, string name, int partId)
        {
            var programs = craft.Descendants("Program").Where(item => (string)item.Attribute("name") == name).ToList();
            if (programs.Count <= 1)
            {
                return programs.FirstOrDefault();
            }

            return programs.FirstOrDefault(item => (string)item.Ancestors("Part").FirstOrDefault()?.Attribute("id") == partId.ToString());
        }
    }
}
