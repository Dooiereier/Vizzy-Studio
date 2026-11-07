namespace Assets.Scripts
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    /// <summary>One thing Funk can read or call.</summary>
    public sealed class FunkName
    {
        public FunkName(string name, string signature = null)
        {
            Name = name;
            Signature = signature;
        }

        public string Name { get; }

        /// <summary>Gets the call form for a function, such as PID(target, current, p, i, d), otherwise null.</summary>
        public string Signature { get; }
    }

    /// <summary>Everything a Vizzy Funk field can use, by how it's written.</summary>
    public sealed class FunkCatalog
    {
        public List<FunkName> TopLevel { get; } = new List<FunkName>();

        public List<FunkName> FlightData { get; } = new List<FunkName>();

        public List<FunkName> OrbitData { get; } = new List<FunkName>();

        public List<FunkName> Variables { get; } = new List<FunkName>();

        /// <summary>Variables known to hold a vector. A plain name would read only their length.</summary>
        public HashSet<string> VectorVariables { get; } = new HashSet<string>();
    }

    /// <summary>One entry of the list.</summary>
    public sealed class FunkSuggestion
    {
        public string Display { get; set; }

        public string Insert { get; set; }

        public bool IsFunction { get; set; }
    }

    /// <summary>What's being typed and what could complete it. Plain text logic, tested offline.</summary>
    public sealed class FunkCompletion
    {
        private const int MaxSuggestions = 30;

        private static readonly string[] Common =
        {
            "PID", "smooth", "clamp", "abs", "min", "max", "sin", "cos", "atan2", "lerp", "Deg2Rad", "Rad2Deg",
            "FD.", "OD.", "v:", "Throttle", "Pitch", "Roll", "Yaw",
            "FD.AltitudeAboveSeaLevel", "FD.Pitch", "FD.Heading", "FD.BankAngle", "FD.AngleOfAttack", "FD.SideSlip",
            "FD.VelocityMagnitude", "FD.SurfaceVelocityMagnitude", "FD.VerticalSurfaceVelocity", "FD.MachNumber",
        };

        private static readonly HashSet<string> VectorFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vec", "cross", "normalize", "project", "projectOnPlane", "reflect", "lerp3", "slerp", "moveTowards",
            "clampMagnitude", "exclude", "max3", "min3", "scale", "listVec",
        };

        public int Start { get; private set; }

        public int End { get; private set; }

        public List<FunkSuggestion> Suggestions { get; } = new List<FunkSuggestion>();

        /// <summary>Returns null where nothing should be suggested: no word at the cursor, in text or in a number.</summary>
        public static FunkCompletion Find(string text, int caret, FunkCatalog catalog, IList<string> recent, string program)
        {
            caret = Math.Max(0, Math.Min(caret, text.Length));
            if (IsInsideText(text, caret))
            {
                return null;
            }

            var start = caret;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            var end = caret;
            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }

            var word = text.Substring(start, caret - start);
            if (word.Length == 0 || char.IsDigit(word[0]) || word[0] == '.')
            {
                return null;
            }

            var completion = new FunkCompletion { Start = start, End = end };
            var nextIsBracket = end < text.Length && text[end] == '(';
            foreach (var (name, rank) in Candidates(word, catalog)
                .OrderBy(pair => pair.rank)
                .ThenBy(pair => RecentRank(pair.name, recent))
                .ThenBy(pair => program != null && program.Contains(pair.name.Insert) ? 0 : 1)
                .ThenBy(pair => CommonRank(pair.name.Insert))
                .ThenBy(pair => pair.name.Insert, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions))
            {
                var isFunction = name.Signature != null;
                completion.Suggestions.Add(new FunkSuggestion
                {
                    Display = name.Signature ?? name.Insert,
                    Insert = isFunction && !nextIsBracket ? name.Insert + "(" : name.Insert,
                    IsFunction = isFunction,
                });
            }

            // Nothing to add once the word is the only option.
            if (completion.Suggestions.Count == 1 && completion.Suggestions[0].Insert == word)
            {
                completion.Suggestions.Clear();
            }

            return completion.Suggestions.Count > 0 ? completion : null;
        }

        /// <summary>Every name Funk text could mean: each word, and the whole of each quoted string for list names.</summary>
        public static IEnumerable<string> NamesIn(string funk)
        {
            if (string.IsNullOrEmpty(funk))
            {
                yield break;
            }

            foreach (Match word in Regex.Matches(funk, "[A-Za-z_$][A-Za-z0-9_$]*"))
            {
                yield return word.Value;
            }

            foreach (Match quoted in Regex.Matches(funk, "\"((?:[^\\\\\"]|\\\\.)*)\""))
            {
                yield return quoted.Groups[1].Value;
            }

            // A bracketed part reference can hold a variable name with spaces.
            foreach (Match bracketed in Regex.Matches(funk, "\\.(?:VZ|FlightProgram)\\.(?:v:)?([^\\]]+)\\]"))
            {
                yield return bracketed.Groups[1].Value;
            }
        }

        /// <summary>Whether Funk text is certainly a vector: v:name, a vector data name, or one whole call to a function returning a vector.</summary>
        public static bool IsVectorFunk(string funk, ICollection<string> vectorData)
        {
            var text = funk?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (Regex.IsMatch(text, @"^v:[A-Za-z_$][A-Za-z0-9_$]*$") || vectorData.Contains(text))
            {
                return true;
            }

            var call = Regex.Match(text, @"^([A-Za-z0-9_]+)\(");
            if (!call.Success || !VectorFunctions.Contains(call.Groups[1].Value))
            {
                return false;
            }

            // The call's bracket has to close at the very end: vec(1,2,3).x or x(...) isn't a vector.
            var depth = 0;
            for (var i = call.Length - 1; i < text.Length; i++)
            {
                depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
                if (depth == 0)
                {
                    return i == text.Length - 1;
                }
            }

            return false;
        }

        /// <summary>The field's text with a suggestion in place of the word.</summary>
        public string Apply(string text, FunkSuggestion suggestion, out int caret)
        {
            caret = Start + suggestion.Insert.Length;
            return text.Substring(0, Start) + suggestion.Insert + text.Substring(End);
        }

        private static IEnumerable<(Candidate name, int rank)> Candidates(string word, FunkCatalog catalog)
        {
            foreach (var (prefix, names) in new[]
            {
                ("FD.", catalog.FlightData), ("FlightData.", catalog.FlightData),
                ("OD.", catalog.OrbitData), ("OrbitData.", catalog.OrbitData),
                ("v:", catalog.Variables),
            })
            {
                // Any case: fd.pos offers FD.Position, and the suggestion puts the prefix in its own case.
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Match(word.Substring(prefix.Length), names, prefix, 0, true);
                }
            }

            if (word.Contains('.') || word.Contains(':'))
            {
                return Enumerable.Empty<(Candidate, int)>();
            }

            var results = Match(word, catalog.TopLevel, string.Empty, 0, false)
                .Concat(Match(word, catalog.Variables.Where(name => !catalog.VectorVariables.Contains(name.Name)), string.Empty, 0, false))
                .Concat(Match(word, catalog.Variables.Where(name => catalog.VectorVariables.Contains(name.Name)), "v:", 0, false))
                .Concat(Match(word, new[] { new FunkName("FD."), new FunkName("OD."), new FunkName("v:") }, string.Empty, 0, false));
            if (word.Length >= 2)
            {
                results = results
                    .Concat(Match(word, catalog.FlightData, "FD.", 2, false))
                    .Concat(Match(word, catalog.OrbitData, "OD.", 2, false));
            }

            return results.GroupBy(pair => pair.name.Insert).Select(group => group.OrderBy(pair => pair.rank).First());
        }

        /// <summary>Start-of-name matches rank before names that merely contain the word.</summary>
        private static IEnumerable<(Candidate name, int rank)> Match(string word, IEnumerable<FunkName> names, string prefix, int baseRank, bool allowEmpty)
        {
            foreach (var name in names)
            {
                var index = word.Length == 0 && allowEmpty ? 0 : name.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (word.Length > 0 || allowEmpty))
                {
                    yield return (new Candidate(prefix + name.Name, name.Signature), baseRank + (index == 0 ? 0 : 1));
                }
            }
        }

        private static int RecentRank(Candidate name, IList<string> recent)
        {
            var index = recent == null ? -1 : recent.IndexOf(name.Insert);
            return index < 0 ? int.MaxValue : index;
        }

        private static int CommonRank(string name)
        {
            var index = Array.IndexOf(Common, name);
            return index < 0 ? int.MaxValue : index;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':';
        }

        private static bool IsInsideText(string text, int caret)
        {
            var inside = false;
            for (var i = 0; i < caret; i++)
            {
                if (text[i] == '\\' && inside)
                {
                    i++;
                }
                else if (text[i] == '"')
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private sealed class Candidate
        {
            public Candidate(string insert, string signature)
            {
                Insert = insert;
                Signature = signature;
            }

            public string Insert { get; }

            public string Signature { get; }
        }
    }
}
