namespace Assets.Scripts
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Xml.Linq;
    using Assets.Scripts.Craft.Parts.Modifiers;

    /// <summary>What became of a write to the designer's working copy.</summary>
    public enum WorkingCopyResult
    {
        Written,
        NotFound,
        Failed,
    }

    /// <summary>
    /// Puts a program edited in flight into the designer's working copy of the craft (what the designer shows and
    /// a relaunch uses), so it's there on return to the designer. The saved craft is left alone: loading it
    /// gets the original back. The working copy is whatever is in the designer now, which needn't be this flight's
    /// craft, so a program is only replaced where it's still exactly the one this edit started from. That also
    /// finds it after staging, when a separated piece has no craft name and its parts have new ids.
    /// </summary>
    public static class CraftWorkingCopy
    {
        // The working copy as this last wrote it. A backup is only made of anything else, so it keeps the
        // version from before the edits through any number of Applies, quickloads and relaunches.
        private static int _lastWritten;

        public static WorkingCopyResult Write(FlightProgramData data, XElement before, XElement program)
        {
            try
            {
                var designs = Game.Instance.CraftDesigns;
                var id = CraftDesigns.EditorCraftId;
                if (string.IsNullOrEmpty(id) || !designs.HasCraft(id) || CraftDesigns.IsStock(id))
                {
                    return WorkingCopyResult.NotFound;
                }

                // A copy: the game keeps the working copy's XML in memory.
                var craft = new XElement(designs.GetCraftDesign(id));

                // A separated piece has no name of its own; one that has must be the craft in the designer.
                var craftName = data.Part.PartScript.CraftScript.Data.Name;
                if (!string.IsNullOrEmpty(craftName) && (string)craft.Attribute("name") != craftName)
                {
                    EditorLog.Debug("Working copy not changed: it holds another craft.");
                    return WorkingCopyResult.NotFound;
                }

                var target = FindProgram(craft, before, data.Part.Id);
                if (target == null)
                {
                    EditorLog.Debug("Working copy not changed: no part there has this program as it was before the edit.");
                    return WorkingCopyResult.NotFound;
                }

                if (Fingerprint(craft) != _lastWritten)
                {
                    var file = designs.GetCraftFile(id);
                    File.Copy(file.FullName, file.FullName + ".bak", true);
                }

                target.ReplaceWith(new XElement(program));
                designs.SaveCraft(id, craft);
                _lastWritten = Fingerprint(craft);
                EditorLog.Debug("Wrote the program to the working copy.");
                return WorkingCopyResult.Written;
            }
            catch (Exception e)
            {
                EditorLog.Error("Couldn't write the working copy: " + e);
                return WorkingCopyResult.Failed;
            }
        }

        /// <summary>The program identical to the one being replaced. Several alike: the part's id settles it, or none is chosen.</summary>
        private static XElement FindProgram(XElement craft, XElement before, int partId)
        {
            var wanted = ThreadRemapper.WithoutWhitespace(before);
            var programs = craft.Descendants("Program").Where(item => XNode.DeepEquals(ThreadRemapper.WithoutWhitespace(item), wanted)).ToList();
            return programs.Count <= 1
                ? programs.FirstOrDefault()
                : programs.FirstOrDefault(item => (string)item.Ancestors("Part").FirstOrDefault()?.Attribute("id") == partId.ToString());
        }

        private static int Fingerprint(XElement craft)
        {
            return craft.ToString(SaveOptions.DisableFormatting).GetHashCode();
        }
    }
}
