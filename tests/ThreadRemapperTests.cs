using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Assets.Scripts;

/// <summary>Offline tests of the thread remapping. Blocks are named by their test text.</summary>
public static class ThreadRemapperTests
{
    private static int _failed;
    private static int _passed;

    public static int Main()
    {
        // Players' text comes from the game's Locale; here, from the English file.
        Assets.Scripts.Text.Resolve = EnglishText.Load();
        EnglishText.Run(Check);
        ValueEditKeepsEverything();
        ReorderedStacksFollowTheBlock();
        NewOnStartBeforeEverythingDoesNotMoveThreads();
        InsertedBlockShiftsThread();
        IndentsFromAFileDontLookLikeEdits();
        EditedCurrentBlockKeepsThread();
        DeletedCurrentBlockGoesToNextBlock();
        DeletedLastBlockInLoopReturnsToLoop();
        DeletedLoopContinuesAfterIt();
        DeletedStackEndsOnlyThatThread();
        StatesFollowBlocksAndDeletedOnesDrop();
        ReturnBlockFollowsAndDeletedOneMovesOn();
        TwinWaitsKeepTheirOrder();
        BlockDraggedToAnotherStackIsFollowed();
        NewVariableIsAddedAndSavedValuesStay();
        FinishedThreadIsLeftAlone();
        RealProgramsSurviveReorderedStacks();
        WrappingInALoopAddsItsFrame();
        UnwrappingFromALoopRemovesItsFrame();
        BlockAfterCurrentIfRuns();
        RetryEndsTheEditHistory();
        SameProgramOnTwoPartsUsesTheId();
        LoadedProgramFollowsEachLoad();
        QuickloadKeepsLatestEdit();
        NewLaunchKeepsItsOwnProgram();
        UnrelatedProgramIsLeftAlone();
        LatestEditAlreadyLoadedIsLeftAlone();
        RevertGivesTheLoadedProgram();

        FunkCompletionTests.Run(Check);
        ModPageVersionTests.Run(Check);
        BlockSearchTests.Run(Check);
        BlockPlacementTests.Run(Check);
        PngStreamWriterTests.Run(Check);
        MfdFormulaWriterTests.Run(Check);

        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void ValueEditKeepsEverything()
    {
        var before = Program(Stack(1, Event(0, "FlightStart"), Wait(1, "a"), Wait(2, "b")));
        var after = Program(Stack(1, Event(0, "FlightStart"), Wait(1, "a"), Wait(2, "CHANGED")));
        var process = Process(Thread(1, Frame()));
        var result = ThreadRemapper.Remap(before, after, process);
        Check("value edit leaves the state identical", XNode.DeepEquals(process, result.Process));
    }

    private static void ReorderedStacksFollowTheBlock()
    {
        var first = Stack(1, Event(0, "One"), Wait(1, "a"));
        var second = Stack(2, Event(2, "Two"), Wait(3, "b"));
        var before = Program(first, second);
        var after = Program(Stack(2, Event(0, "Two"), Wait(1, "b")), Stack(1, Event(2, "One"), Wait(3, "a")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(1, Frame()), Thread(3, Frame())));
        CheckNext("first stack's thread follows", result, 0, 3);
        CheckNext("second stack's thread follows", result, 1, 1);
    }

    private static void NewOnStartBeforeEverythingDoesNotMoveThreads()
    {
        var before = Program(Stack(1, Event(0, "FlightStart"), Wait(1, "a")));
        var after = Program(
            Stack(5, Event(0, "FlightStart"), Wait(1, "new")),
            Stack(1, Event(2, "FlightStart"), Wait(3, "a")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(1, Frame())));
        Check("no new thread starts", result.Process.Elements("Thread").Count() == 1);
        CheckNext("thread stays on its block", result, 0, 3);
    }

    private static void IndentsFromAFileDontLookLikeEdits()
    {
        // A message event has its text inside it, like the stacks whose threads ended in game.
        var message = new XElement("Event", new XAttribute("event", "ReceiveMessage"), new XAttribute("id", 5), new XElement("Constant", new XAttribute("text", "Go")));
        var program = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "a"), Wait(3, "b")), Wait(4, "c")), Stack(2, message, Wait(6, "d"), Wait(7, "e")));
        var fromFile = XElement.Parse(program.ToString(), LoadOptions.PreserveWhitespace);
        var result = ThreadRemapper.Remap(fromFile, program, Process(Thread(3, Frame(), Frame(returnId: 1)), Thread(7, Frame())));
        Check("indents: both threads stay", result.Process.Elements("Thread").Count() == 2);
        if (result.Process.Elements("Thread").Count() != 2)
        {
            return;
        }

        CheckNext("indents: the thread in a loop stays", result, 0, 3);
        CheckNext("indents: the other stack's thread stays", result, 1, 7);
        Check("indents: no thread ends", result.Notes.All(note => !note.Contains("deleted")));

        LatestEdits.Clear();
        LatestEdits.Remember("part", program, Program(Stack(1, Event(0, "One"), Wait(1, "edit"))));
        Check("indents: a loaded program with indents is still known", LatestEdits.ProgramToRestore("part", fromFile, Process()) != null);
        LatestEdits.Clear();
    }

    private static void InsertedBlockShiftsThread()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "new"), Wait(2, "a"), Wait(3, "b")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame())));
        CheckNext("thread follows past the inserted block", result, 0, 3);
    }

    private static void EditedCurrentBlockKeepsThread()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "new"), Wait(2, "CHANGED"), Wait(3, "b")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(1, Frame())));
        var next = (int)result.Process.Element("Thread").Attribute("nextInstruction");
        Check("thread stays on an edited block's stretch", next == 1 || next == 2);
    }

    private static void DeletedCurrentBlockGoesToNextBlock()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "b")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(1, Frame())));
        CheckNext("goes to the next block", result, 0, 1);
    }

    private static void DeletedLastBlockInLoopReturnsToLoop()
    {
        var before = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "keep"), Wait(3, "gone"))));
        var after = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "keep"))));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(3, Frame(), Frame(returnId: 1))));
        CheckNext("returns to the loop", result, 0, 1);
        CheckFrames("loop frame left", result, 0, 1);
    }

    private static void DeletedLoopContinuesAfterIt()
    {
        var before = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "inner")), Wait(3, "after")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "after")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame(), Frame(returnId: 1))));
        CheckNext("continues after the deleted loop", result, 0, 1);
        CheckFrames("loop frame left", result, 0, 1);
        Check("no thread ended", result.ThreadsEnded == 0);
    }

    private static void DeletedStackEndsOnlyThatThread()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a")), Stack(2, Event(2, "Two"), Wait(3, "b")));
        var after = Program(Stack(2, Event(0, "Two"), Wait(1, "b")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(1, Frame()), Thread(3, Frame())));
        Check("one thread ended", result.ThreadsEnded == 1 && result.Process.Elements("Thread").Count() == 1);
        CheckNext("the other carries on", result, 0, 1);
        Check("the ending is reported", result.Notes.Any(note => note.Contains("ended")));
    }

    private static void StatesFollowBlocksAndDeletedOnesDrop()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b"), Wait(3, "c")));
        var after = Program(Stack(1, Event(0, "One"), Comment(1, "new"), Wait(2, "a"), Wait(3, "c")));
        var process = Process(Thread(1, Frame(states: "1=2.5,2=9,")));
        var result = ThreadRemapper.Remap(before, after, process);
        var states = (string)result.Process.Element("Thread").Element("CallStack").Element("StackFrame").Attribute("states");
        Check("state follows and deleted block's state drops, got " + states, states == "2=2.5");
    }

    private static void ReturnBlockFollowsAndDeletedOneMovesOn()
    {
        var before = Program(
            Stack(1, Event(0, "One"), Call(1, "f"), Wait(2, "a"), Wait(3, "b")),
            Stack(2, Custom(4, "f"), Wait(5, "inner")));
        var after = Program(
            Stack(2, Custom(0, "f"), Wait(1, "inner")),
            Stack(1, Event(2, "One"), Call(3, "f"), Wait(4, "b")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(5, Frame(), Frame(returnId: 2))));
        CheckNext("inside the custom instruction", result, 0, 1);
        var frame = result.Process.Element("Thread").Element("CallStack").Elements("StackFrame").Last();
        Check("return goes to the next surviving block", (int)frame.Attribute("returnId") == 4);
    }

    private static void TwinWaitsKeepTheirOrder()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "same"), Wait(2, "same"), Wait(3, "same")));
        var after = Program(Stack(1, Event(0, "One"), Comment(1, "new"), Wait(2, "same"), Wait(3, "same"), Wait(4, "same")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame())));
        CheckNext("second twin stays second", result, 0, 3);
    }

    private static void BlockDraggedToAnotherStackIsFollowed()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "keep"), Wait(2, "dragged")), Stack(2, Event(3, "Two"), Wait(4, "b")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "keep")), Stack(2, Event(2, "Two"), Wait(3, "b"), Wait(4, "dragged")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame())));
        CheckNext("follows the dragged block", result, 0, 4);
    }

    private static void NewVariableIsAddedAndSavedValuesStay()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "a")));
        after.Element("Variables").Add(new XElement("Variable", new XAttribute("name", "Kp"), new XAttribute("number", "9")));
        after.Element("Variables").Add(new XElement("Variable", new XAttribute("name", "New"), new XAttribute("number", "1")));
        var process = Process(Thread(1, Frame()));
        process.Element("Variables").Add(new XElement("Variable", new XAttribute("name", "Kp"), new XAttribute("number", "5")));
        var variables = ThreadRemapper.Remap(before, after, process).Process.Element("Variables").Elements().ToList();
        Check("saved value stays", variables.Single(v => (string)v.Attribute("name") == "Kp").Attribute("number").Value == "5");
        Check("new variable is added", variables.Any(v => (string)v.Attribute("name") == "New"));
    }

    private static void FinishedThreadIsLeftAlone()
    {
        var program = Program(Stack(1, Event(0, "One")));
        var process = Process(new XElement("Thread", new XElement("CallStack", Frame())));
        var result = ThreadRemapper.Remap(program, program, process);
        Check("no next block stays that way", result.Process.Element("Thread").Attribute("nextInstruction") == null && result.ThreadsEnded == 0);
    }

    /// <summary>The player's own saved programs, when there are any: every block must be found again.</summary>
    private static void RealProgramsSurviveReorderedStacks()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            "Library/Application Support/com.jundroo.SimpleRockets2/UserData/FlightPrograms");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var programs = 0;
        var blocks = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in Directory.GetFiles(folder, "*.xml").Where(path => !Path.GetFileName(path).StartsWith(".")))
        {
            var before = XDocument.Load(file).Root;

            // Earlier exports left blocks without ids until the game next saves the file; a running program always has them.
            if (before.Descendants("Instructions").Elements().Any(block => block.Attribute("id") == null))
            {
                continue;
            }

            if (before.Name != "Program")
            {
                continue;
            }

            var after = new XElement(before);
            var stacks = after.Elements("Instructions").ToList();
            stacks.ForEach(stack => stack.Remove());
            stacks.Reverse();
            after.Add(stacks);

            var next = 0;
            var numbered = after.Descendants().Where(element => element.Attribute("id") != null).ToList();
            var originals = before.Descendants().Where(element => element.Attribute("id") != null)
                .ToDictionary(element => (int)element.Attribute("id"));
            var ids = new System.Collections.Generic.Dictionary<int, XElement>();
            foreach (var element in numbered)
            {
                element.SetAttributeValue("id", next);
                ids[next++] = element;
            }

            var result = ThreadRemapper.Remap(before, after, Process());
            programs++;
            blocks += originals.Count;
            Check(Path.GetFileName(file) + ": every block found", result.Ids.Count == originals.Count);
            Check(Path.GetFileName(file) + ": same type", result.Ids.All(pair => originals[pair.Key].Name == ids[pair.Value].Name));
        }

        Console.WriteLine($"Real programs: {programs}, {blocks} blocks, {clock.ElapsedMilliseconds} ms.");
    }

    private static void WrappingInALoopAddsItsFrame()
    {
        var before = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b"), Wait(3, "c")));
        var after = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "a"), Wait(3, "b")), Wait(4, "c")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame())));
        CheckNext("follows into the loop", result, 0, 3);
        CheckFrames("loop frame added", result, 0, 2);
        var top = result.Process.Element("Thread").Element("CallStack").Elements("StackFrame").Last();
        Check("loop frame returns to the loop", (int?)top.Attribute("returnId") == 1 && top.Element("Variables") != null);
    }

    private static void UnwrappingFromALoopRemovesItsFrame()
    {
        var before = Program(Stack(1, Event(0, "One"), Loop(1, "x", Wait(2, "a"), Wait(3, "b")), Wait(4, "c")));
        var after = Program(Stack(1, Event(0, "One"), Wait(1, "a"), Wait(2, "b"), Wait(3, "c")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame(), Frame(returnId: 1))));
        CheckNext("follows out of the loop", result, 0, 1);
        CheckFrames("loop frame removed", result, 0, 1);
    }

    private static void BlockAfterCurrentIfRuns()
    {
        var before = Program(Stack(1, Event(0, "One"), Branch(1, "x", Wait(2, "a")), Wait(3, "c")));
        var after = Program(Stack(1, Event(0, "One"), Branch(1, "x", Wait(2, "a")), Wait(3, "new"), Wait(4, "c")));
        var result = ThreadRemapper.Remap(before, after, Process(Thread(2, Frame(), Frame(returnId: 3, states: "2=1.5"))));
        var top = result.Process.Element("Thread").Element("CallStack").Elements("StackFrame").Last();
        Check("if frame returns to the new block", (int?)top.Attribute("returnId") == 3);
        Check("if frame keeps its states", (string)top.Attribute("states") == "2=1.5");
    }

    private static void RetryEndsTheEditHistory()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        LatestEdits.Remember("part", saved, Program(Stack(1, Event(0, "One"), Wait(1, "edit"))));
        LatestEdits.ProgramToRestore("part", saved, null);
        Check("a quickload after a new launch keeps its program", LatestEdits.ProgramToRestore("part", saved, Process()) == null);
    }

    private static void SameProgramOnTwoPartsUsesTheId()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        var first = Program(Stack(1, Event(0, "One"), Wait(1, "first")));
        var second = Program(Stack(1, Event(0, "One"), Wait(1, "second")));
        LatestEdits.Remember("a", saved, first);
        LatestEdits.Remember("b", saved, second);
        LatestEdits.Clear();
        LatestEdits.Remember("a", saved, first);
        Check("one history is found by program alone", XNode.DeepEquals(first, LatestEdits.ProgramToRestore("z", saved, Process())));
    }

    private static void LoadedProgramFollowsEachLoad()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        var edit = Program(Stack(1, Event(0, "One"), Wait(1, "edit")));
        LatestEdits.Remember("part", saved, edit);
        LatestEdits.ProgramToRestore("part", saved, Process());
        LatestEdits.ProgramToRestore("part", edit, Process());
        Check("a save already holding the edit has nothing to go back to", LatestEdits.LoadedProgram("part") == null);
    }

    private static void QuickloadKeepsLatestEdit()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        var first = Program(Stack(1, Event(0, "One"), Wait(1, "first")));
        var second = Program(Stack(1, Event(0, "One"), Wait(1, "second")));
        LatestEdits.Remember("part", saved, first);
        LatestEdits.Remember("part", first, second);
        Check("quickload gets the latest edit", XNode.DeepEquals(second, LatestEdits.ProgramToRestore("part", saved, Process())));
    }

    private static void NewLaunchKeepsItsOwnProgram()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        LatestEdits.Remember("part", saved, Program(Stack(1, Event(0, "One"), Wait(1, "edit"))));
        Check("no saved threads means a new launch", LatestEdits.ProgramToRestore("part", saved, null) == null);
    }

    private static void UnrelatedProgramIsLeftAlone()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        LatestEdits.Remember("part", saved, Program(Stack(1, Event(0, "One"), Wait(1, "edit"))));
        var other = Program(Stack(1, Event(0, "Other"), Wait(1, "other")));
        Check("a program outside the history stays", LatestEdits.ProgramToRestore("part", other, Process()) == null);
        Check("a new id still finds the history, as after a separation", LatestEdits.ProgramToRestore("other part", saved, Process()) != null);
    }

    private static void LatestEditAlreadyLoadedIsLeftAlone()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        var edit = Program(Stack(1, Event(0, "One"), Wait(1, "edit")));
        LatestEdits.Remember("part", saved, edit);
        Check("quicksave holding the edit needs nothing", LatestEdits.ProgramToRestore("part", edit, Process()) == null);
    }

    private static void RevertGivesTheLoadedProgram()
    {
        LatestEdits.Clear();
        var saved = Program(Stack(1, Event(0, "One"), Wait(1, "saved")));
        LatestEdits.Remember("part", saved, Program(Stack(1, Event(0, "One"), Wait(1, "edit"))));
        LatestEdits.ProgramToRestore("part", saved, Process());
        Check("revert has the quicksave's program", XNode.DeepEquals(saved, LatestEdits.LoadedProgram("part")));
        LatestEdits.Forget("part");
        Check("after reverting the quickload stays", LatestEdits.ProgramToRestore("part", saved, Process()) == null);
    }

    private static XElement Program(params XElement[] stacks)
    {
        return new XElement("Program", new XElement("Variables"), stacks);
    }

    private static XElement Stack(int position, params XElement[] blocks)
    {
        blocks[0].SetAttributeValue("pos", position * 100 + ",0");
        return new XElement("Instructions", blocks);
    }

    private static XElement Event(int id, string name)
    {
        return new XElement("Event", new XAttribute("event", name), new XAttribute("id", id));
    }

    private static XElement Custom(int id, string name)
    {
        return new XElement("CustomInstruction", new XAttribute("name", name), new XAttribute("id", id));
    }

    private static XElement Call(int id, string name)
    {
        return new XElement("CallCustomInstruction", new XAttribute("call", name), new XAttribute("id", id));
    }

    private static XElement Comment(int id, string text)
    {
        return new XElement("Comment", new XAttribute("id", id), new XElement("Constant", new XAttribute("text", text)));
    }

    private static XElement Wait(int id, string text)
    {
        return new XElement("WaitSeconds", new XAttribute("id", id), new XElement("Constant", new XAttribute("text", text)));
    }

    private static XElement Loop(int id, string text, params XElement[] body)
    {
        return new XElement("While", new XAttribute("id", id), new XElement("Constant", new XAttribute("text", text)), new XElement("Instructions", body));
    }

    private static XElement Branch(int id, string text, params XElement[] body)
    {
        return new XElement("If", new XAttribute("id", id), new XElement("Constant", new XAttribute("text", text)), new XElement("Instructions", body));
    }

    private static XElement Process(params XElement[] threads)
    {
        return new XElement("Process", new XElement("Variables"), threads);
    }

    private static XElement Thread(int next, params XElement[] frames)
    {
        return new XElement("Thread", new XAttribute("nextInstruction", next), new XElement("CallStack", frames));
    }

    private static XElement Frame(int? returnId = null, string states = null)
    {
        var frame = new XElement("StackFrame", new XElement("Variables"));
        if (states != null)
        {
            frame.SetAttributeValue("states", states);
        }

        if (returnId != null)
        {
            frame.SetAttributeValue("returnId", returnId);
        }

        return frame;
    }

    private static void CheckNext(string name, ThreadRemapper.Result result, int thread, int expected)
    {
        var actual = (int?)result.Process.Elements("Thread").ElementAt(thread).Attribute("nextInstruction");
        Check(name + $" (block {actual}, expected {expected})", actual == expected);
    }

    private static void CheckFrames(string name, ThreadRemapper.Result result, int thread, int expected)
    {
        var actual = result.Process.Elements("Thread").ElementAt(thread).Element("CallStack").Elements("StackFrame").Count();
        Check(name + $" ({actual} frames, expected {expected})", actual == expected);
    }

    private static void Check(string name, bool passed)
    {
        if (passed)
        {
            _passed++;
        }
        else
        {
            _failed++;
            Console.WriteLine("FAILED: " + name);
        }
    }
}
