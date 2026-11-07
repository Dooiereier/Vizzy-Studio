using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;

/// <summary>Offline tests of what auto-fill offers and how it inserts it.</summary>
public static class FunkCompletionTests
{
    public static void Run(Action<string, bool> check)
    {
        var catalog = new FunkCatalog();
        catalog.TopLevel.Add(new FunkName("PID", "PID(target, current, p, i, d)"));
        catalog.TopLevel.Add(new FunkName("pow", "pow(x, y)"));
        catalog.TopLevel.Add(new FunkName("pi"));
        catalog.TopLevel.Add(new FunkName("Pitch"));
        catalog.TopLevel.Add(new FunkName("Throttle"));
        catalog.FlightData.Add(new FunkName("Pitch"));
        catalog.FlightData.Add(new FunkName("AltitudeAboveSeaLevel"));
        catalog.OrbitData.Add(new FunkName("Inclination"));
        catalog.Variables.Add(new FunkName("Kp"));
        catalog.Variables.Add(new FunkName("Target"));

        FunkCompletion Find(string text, IList<string> recent = null, string program = null) => FunkCompletion.Find(text, text.Length, catalog, recent, program);
        List<string> Names(string text, IList<string> recent = null, string program = null) => Find(text, recent, program)?.Suggestions.Select(s => s.Insert).ToList() ?? new List<string>();

        check("PID ranks first for p", Names("P").First() == "PID(");
        check("functions show their arguments", Find("PI").Suggestions.First().Display == "PID(target, current, p, i, d)");
        check("variables are offered bare", Names("K").Contains("Kp"));
        check("FD. lists flight data", Names("FD.").SequenceEqual(new[] { "FD.AltitudeAboveSeaLevel", "FD.Pitch" }));
        check("FD. filters", Names("FD.Alt").SequenceEqual(new[] { "FD.AltitudeAboveSeaLevel" }));
        check("v: lists variables", Names("v:").SequenceEqual(new[] { "v:Kp", "v:Target" }));
        check("OD. lists orbit data", Names("OD.In").SequenceEqual(new[] { "OD.Inclination" }));
        check("fd. in lower case filters too", Names("fd.alt").SequenceEqual(new[] { "FD.AltitudeAboveSeaLevel" }));
        check("od. in lower case filters too", Names("od.in").SequenceEqual(new[] { "OD.Inclination" }));
        check("a lower-case complete name is still offered in its own case", Names("fd.pitch").SequenceEqual(new[] { "FD.Pitch" }));
        var lower = FunkCompletion.Find("1 + fd.alt", 10, catalog, null, null);
        check("fd. is replaced by FD.", lower.Apply("1 + fd.alt", lower.Suggestions.First(), out _) == "1 + FD.AltitudeAboveSeaLevel");
        check("flight names appear with their prefix", Names("Alt").Contains("FD.AltitudeAboveSeaLevel"));
        check("flight names wait for two letters", !Names("A").Contains("FD.AltitudeAboveSeaLevel"));
        check("start-of-name beats contains", Names("t").IndexOf("Throttle") < Names("t").IndexOf("Pitch"));
        check("recent use ranks first", Names("P", new[] { "pow" }).First() == "pow(");
        check("use in this program ranks up", Names("t", null, "<x text=\"Target\"/>").First() == "Target");
        check("nothing inside text", Find("\"Pi") == null);
        check("after closed text it works", Names("\"a\" + Pi").Any());
        check("nothing for a number", Find("1.5") == null);
        check("nothing for an empty word", Find("clamp(") == null);
        check("part references aren't offered", Find("Tank.Fuel") == null);
        check("nothing once the word is complete", Find("FD.AltitudeAboveSeaLevel") == null);

        var names = FunkCompletion.NamesIn("PID(Target, FD.Pitch, v:Gain, 0) + listNum(\"my list\", 1) + [Space Capsule.VZ.other var] + Part.VZ.far").ToList();
        check("a plain variable counts as used", names.Contains("Target"));
        check("a v: variable counts as used", names.Contains("Gain"));
        check("a quoted list name counts as used", names.Contains("my list"));
        check("a bracketed variable counts as used", names.Contains("other var"));
        check("a dotted variable counts as used", names.Contains("far"));
        check("empty Funk text has no names", !FunkCompletion.NamesIn("").Any());

        var completion = FunkCompletion.Find("max(Pi, 1)", 6, catalog, null, null);
        var text = completion.Apply("max(Pi, 1)", completion.Suggestions.First(s => s.Insert == "Pitch"), out var caret);
        check("replaces the word and keeps the rest", text == "max(Pitch, 1)" && caret == 9);

        var inWord = FunkCompletion.Find("PIxx", 2, catalog, null, null);
        check("replaces the rest of the word", inWord.Apply("PIxx", inWord.Suggestions.First(), out _) == "PID(");

        var bracket = FunkCompletion.Find("PI(1)", 2, catalog, null, null);
        check("no second bracket", bracket.Suggestions.First().Insert == "PID");

        var vectorData = new[] { "FD.Position" };
        check("vec() is a vector", FunkCompletion.IsVectorFunk(" vec(0,1,1) ", vectorData));
        check("nested call is a vector", FunkCompletion.IsVectorFunk("cross(vec(1,0,0), FD.Position)", vectorData));
        check("v:name is a vector", FunkCompletion.IsVectorFunk("v:aaa", vectorData));
        check("vector data is a vector", FunkCompletion.IsVectorFunk("FD.Position", vectorData));
        check("a number isn't", !FunkCompletion.IsVectorFunk("FD.Pitch", vectorData));
        check("a part of a vector isn't", !FunkCompletion.IsVectorFunk("x(vec(0,1,1))", vectorData));
        check("more after the call isn't certain", !FunkCompletion.IsVectorFunk("vec(0,1,1) > 1", vectorData));

        var vectors = new FunkCatalog();
        vectors.Variables.Add(new FunkName("LaunchNormal"));
        vectors.Variables.Add(new FunkName("LaunchAlt"));
        vectors.VectorVariables.Add("LaunchNormal");
        var vectorInserts = FunkCompletion.Find("laun", 4, vectors, null, null).Suggestions.Select(s => s.Insert).ToList();
        check("a vector variable comes with v:", vectorInserts.Contains("v:LaunchNormal") && !vectorInserts.Contains("LaunchNormal"));
        check("a number variable doesn't", vectorInserts.Contains("LaunchAlt"));
        var typedPrefix = FunkCompletion.Find("v:laun", 6, vectors, null, null);
        check("no second v:", typedPrefix.Apply("v:laun", typedPrefix.Suggestions.First(s => s.Insert.EndsWith("LaunchNormal")), out _) == "v:LaunchNormal");
    }
}
