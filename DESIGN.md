# VizzyStudio: design

Pause in flight, open a craft's program in the stock Vizzy editor, change it and apply. The program
carries on from where it was, so you can repeat quickload, edit, fly to tune things like PID gains.

## Rules

- **Apply never restarts.** It always carries the running threads over to the edited program. A new
  On Start doesn't run: "on start" only fires when a program starts from the top. The one restart is the
  Restart Vizzy button, for the selected part's program, after a confirmation.
- **A quickload keeps the latest applied edit.** Craft state and thread positions come from the
  quicksave; the program is the latest edit. A key returns to the quicksave's program.
- **Crafts never need the mod.** No part modifiers; programs stay ordinary stock XML.
- **Harmony is required**, from the Juno Harmony mod, and never inside the mod file. Without it the mod logs
  an error, shows one message and stays off. Patches: auto-fill keys and coloring on each Funk field; each
  has no public alternative, and each applies on its own.
- **In the designer**, only auto-fill, coloring, Clean Variables, Export Block, Jump To and
  reopening at the last view differ from stock.
- **Other mods stay unaffected.** Only an editor this mod opens gets its Apply and Cancel replaced,
  so another mod that opens the stock editor in flight keeps the stock buttons.

## Native mechanisms

| Need | Game mechanism |
|---|---|
| Open the editor | Prefab `Ui/Vizzy/Vizzy`, public `VizzyUIScript.Initialize` |
| Apply | Public `ApplyChangesToCraft`, `Close`; new XML through the apply callback |
| Capture and resume | Public `FlightProgramScript.UpdateXml`, `StartProgram`; `FlightProgramData.ProcessXml` |
| Pause | `ITimeManager.RequestPauseChange` |
| Menu button | `AddBuildUserInterfaceXmlAction(UserInterfaceIds.Flight.FlightSceneUI, …)` |
| Program picker | `IUserInterface.CreateInspectorPanel`, the game's own panel style |
| Settings | `SettingsCategory` |

The stock Apply and Cancel handlers reach for the designer after closing, which is null in flight.
For an editor this mod opens, `FlightEditorUi` removes their `onClick` from the XML and adds its own
listeners that do the same without the designer.

## Remapping (`ThreadRemapper`)

Saving renumbers every block, and threads point at blocks by id (`nextInstruction`, `returnId`,
`states`). Old and new program XML are compared, never ids on live objects (other mods can serialize
the live program, which renumbers it).

Unchanged stacks pair first, then edited stacks by best in-order match, then single blocks dragged
elsewhere when exactly one block could be the one. A thread on a deleted block continues at the next
surviving block in its chain, leaving frames as the game would at a chain's end. It ends only when
nothing after it survives, and a flight message says so. Saved variable values stay; variables the
edit added are created with the program's values.

The frames of the loops and ifs around a thread's block are rebuilt from the new program, so wrapping
or unwrapping blocks keeps the thread's nesting right. Loops return to themselves; ifs return to the
block after their else-ifs.

Known limits:

- Edited one-off blocks that already ran don't run again, so gains need setting inside a loop or in
  `PID(...)` in a Funk expression inside one.
- A new `For` wrapped around a running thread has no loop variable until its next pass.
- Renaming a variable while it's in use loses its running value.
- Each Apply recompiles Funk expressions, so `PID`, `smooth`, `sum` and `rate` restart their memory.
- Events queued but not yet started at the moment of Apply are dropped.
- A beep sounding at Apply is silenced.

## Files

- `Mod`: startup and scene hooks. `ModSettings`, `EditorLog`: settings and logging.
- `FlightEditorController`: per-flight component: keys, requests, restoring edits after a load.
- `EditorSession`: one open editor: flight UI, canvas position, closing. `FlightPause`: pause and keys.
- `FlightEditorUi`: XML changes to the flight menu and the Vizzy editor.
- `ProgramPicker`: choice panel for crafts with several programs.
- `CraftPrograms`: which programs a craft has. `ProgramSwapper`: puts a program in place.
- `ThreadRemapper`, `LatestEdits`, `FunkCompletion`: no game code, tested offline.
- `FunkAutoFill`, `FunkList`, `FunkInputPatch`: auto-fill's game side.
- `FunkHighlight`, `FlightButtons`, `VizzyVariableCleaner`: from Vizzy Debugger.
- `Exporter/`, `VizzyOrganizer/`: from Vizzy Exporter and Vizzy McBlinky.

## Lifecycle

- **Pause:** the game pauses as soon as the key or menu button is used, including while the picker is
  open, and returns to its previous state when the picker or editor closes. Programs don't update
  while paused.
- **Time warp:** opening is refused, with a message. This also covers physics on rails.
- **Quicksave:** stores the edited program and positions, through the game's own code.
- **Quickload:** the edit goes back in right after the load. A save without running threads is a new
  launch (including Retry and Relaunch): it keeps its own program and ends that part's edit history.
- **Scene change:** the editor closes. `LatestEdits` clears on leaving flight; canvas positions stay
  until the game quits. Nothing else is kept across scenes.
- **Input:** the game's keys still fire while paused, so `FlightPause` turns them off (except Undo and
  Redo) while choosing or editing. Esc is read raw, so `EditorSession` re-hides the flight UI each frame
  and `FlightPause` re-asserts the pause.
- **Craft switch, EVA, docking:** blocked with the other keys while editing. Docking is handled by the
  stock `OnMovedToNewCraft`. A part's id changes when it moves to another craft, so kept edits are found
  by program content, with the id only choosing between several matches. Two crafts running the same program
  can still share a history. Selected parts must be on the player's craft.
- **Failed Apply:** the editor stays open with a message, so the work isn't lost. That includes a part
  that's gone.
- **Several programs:** a picker lists them, one editor at a time. Each keeps its own latest edit and
  canvas position. Other programs keep running.
- **Execution order:** Apply runs from a UI event while paused, so not alongside a program update.

## Funk auto-fill

A list under a Funk field while typing: Up and Down choose, Enter takes it, Esc closes the list.

- **Only what works in Vizzy Funk.** Names come at runtime from the game's Vizzy Funk context: `Context`
  defaults, `CraftControls`, `CraftFlightData`, `CraftOrbitData` properties of value types, the Vizzy list
  functions and the program's variables (a bare name for a number, `v:` for a vector). It runs only in
  Funk fields (`EvaluationExpression`) of a Vizzy editor, in the designer or in flight, whichever opened it.
  It only ever changes text the player chooses to insert. Part references aren't offered, nor are
  `Vector3` properties: `FD.AccelerationFrame` stopped a running program with `TargetParameterCountException`.
- **Ranking:** start-of-name matches first, then recent use, use in this program, a short common list,
  then alphabetical. Flight and orbit names also appear, with their prefix, after 2 typed characters.
- **Keys:** a prefix on `TMP_InputField.OnUpdateSelected`. The field reads queued key events itself, so
  without a patch Enter has already ended the edit by the time a mod can see it. The patch only acts while
  the list is open for its field, or while a key it took is still held.

## Merged from Vizzy Debugger

Code by Oxeter and Torifune, merged with their agreement.

- **Funk coloring and error marks** (`FunkHighlight`): colors and red marks through
  `TMP_Text.OnPreRenderText`. Names come from `FunkNames`, shared with auto-fill.
- **Flight buttons** (`FlightButtons`): a Vizzy button with the editor's icon at the bottom of the nav panel,
  opening a flyout styled like the nav sphere's, with Restart Vizzy and Edit Vizzy. Both act on the selected
  part's program straight away, otherwise on the craft's only program or a choice of them. Restart asks first.
  For an MFD it removes the widgets too (`MfdRestart`), as they'd stay and each creation block would log an error. Show Flight Buttons turns them off.
- **MFD programs:** custom ones are editable. Stock ones are excluded, because the game reloads them.

Known risk to test: widgets belong to `MfdScript` and survive an Apply, so widgets from deleted blocks can
stay on screen. Known limit: `FindError` is a second parser and differs from the game's on rare forms such
as `+a` and `((a)`.

**Startup:** checks and patches run at the first `SceneLoaded`, because a mod's `OnModLoaded` runs as soon as
that mod loads, before later mods. `_loadPriority` is above Juno Harmony's.

- **Variable cleaner** (`VizzyVariableCleaner`): the Clean Variables button in every Vizzy editor, and the
  console command `CleanUnusedVariables`, list the global variables the open program doesn't use and remove
  them after asking, as one Undo step. Names in Funk text count as used, and it removes nothing if a Funk block's
  text is built by other blocks. It can't see another part reading a variable through `Part.VZ.Name`.

## Arrange MFD

`MfdEditorSession`, from MFD Runtime Editor (source: Dooiereier/MFD-Widget-Arranger), behind the Arrange MFD setting, off by default.

- **Panel:** a stock inspector panel, reached from the flight flyout. Holds the flight with `FlightPause`, so a program can't overwrite values being typed. Widgets come from the public `MfdScript.GetMfdChildWidgets`, the canvas's children and then theirs, as the game's own "destroy all widgets" block finds them.
- **List:** only widgets whose position or size can be saved, checked with a dry run before the panel is built. It shows what exists when opened, so a program that builds its screen later needs the panel opened again then.
- **Writing:** `MfdFormulaWriter` (XML only, tested offline) changes a plain number, the one number on an axis in `+ - * /` blocks over known sizes, the one number in a Funk formula (`VizzyExpressionSolver`), or a helper custom instruction's argument. Blocks setting a widget to the same value, ignoring ids and places, change together; different values are refused. It follows a global variable only if one block sets it and, without the override, one thing reads it. It only writes when the block still gives where the widget was when the panel opened, so a widget another block also moves (a later Set Parent, anchors, another setter) is refused, and only blocks in stacks that can run count. Numbers are written with float precision, an axis that didn't move isn't rewritten, a helper's axes are saved one by one, and rotation saves the turn made. Anything else is refused with a reason.
- **Save:** first the running program through `ProgramSwapper` and `LatestEdits`, as Edit Vizzy's Apply does. Then `MfdCraftFiles` writes `CraftDesigns.EditorCraftId` (the working copy the designer and a relaunch read) and the design named after the craft, through `CraftDesigns.SaveCraft`, which keeps the game's cached working copy right. A file counts only if its root name is the flying craft's and the writer does exactly the same to it as to the running program; otherwise no file changes, and the panel says the save is for this flight.
- **Outline:** one object moved to the selected widget, with `raycastTarget` off so MFD buttons still work, and `VizzyStudioOverlay.shader` so it doesn't fight with the widget's background.

Lifecycle: paused throughout; warp is refused at opening; closing, a scene end or a craft loss reverts what wasn't saved. Stock MFD programs aren't offered, as the game reloads them. Known limits: a value a program sets every tick jumps back unless it was saved; widgets reading a shared variable only move after a restart; after a quickload widgets show the quicksave's values until the program sets them again.

## Block search

`BlockSearchPanel`: Shift+F or a left bar button in any Vizzy editor opens a stock-style flyout with a text box. Results are real toolbox
blocks, built by the game's private `VizzyUIController.LoadNodes`, so dragging, cloning and tooltips are the game's own
and no drag starts from code. Entries come from `VizzyToolbox.Categories` and `NodeFormat.Tokenize`. Each choice in a
block's first dropdown (`GetListItems`) is its own result, built on a copy made with `ProgramSerializer`, so the toolbox's
block keeps its default. `BlockSearch` does the matching, tested offline. The program's variables, lists and custom blocks are added when it opens, built the way `UpdateCategory` builds them. Results are shown by group (toolbox blocks, variables, custom expressions, custom instructions, lists), closest match first within each, up to 30 in all. The top result starts selected; Up and Down move a highlight over the results and scroll them (a press wraps, a held key repeats and stops at the end), and Enter places the selected block as a double-click does. `BlockPlacement` picks the spot, tested offline, in what the flyout leaves visible of the canvas. The target is a third of the way across, halfway down. First choice: below the nearest column (a stack of two or more, or the block last placed here), left-aligned, a gap down, if that's near the target (within 30% of the visible size), in view and clear of everything. Then the free spot nearest the target within that reach. With neither, the view counts as very full: to the left of the column nearest the target's line, halfway down, overlapping if it has to. Placing or dragging a block out empties the box and refocuses it; a drag let go over the panel adds nothing, so it keeps the search. A real drag leaves `IsDragging` on the result itself (`StartClone`), which is what `WatchDrag` polls; a vertical drag goes to the list and doesn't set it. The highlight is a plain image behind the results, the color of the game's dropdown hover row, that takes no clicks. Unity's single line input moves its caret on Up and Down and ends editing on Enter, so `LateUpdate` puts the caret back at the end and refocuses the box. Keys are taken unless another text field is selected: `IUserInterface.IsTextInputFocused` only asks whether the selected object is an input field, so it stays true after Enter while the box's `isFocused` is false.

## Update check

`UpdateCheck`: at the first scene, with Check For Updates on, reads the "Mod Version" from the mod's SimpleRockets.com
page and offers to open the page if it's newer. Nothing is downloaded automatically. `ModPageUrl` is empty until
the mod is published, which skips the check.

## Merged from Vizzy Exporter and Vizzy McBlinky

Both by Dooiereier. Replace the separate mods, and the mod warns if they're also loaded.

- **Export** (`Exporter/`): click a block to select it, then Export Block in the Vizzy menu opens the game's Load
  Program list (`LoadProgramViewModel`). It writes to the target program's file on disk, with the variables and
  custom blocks it needs. Selecting needs a patch on `OnPointerClick`, as there's no public click event.
- **Jump To** (`VizzyOrganizer/`): a left-bar button and stock flyout built in the editor's XML, listing
  `//Path/To` tag comments. Selecting a row pans the canvas. Tags are plain comments, so crafts don't need the mod.

## Testing


## Languages

Players' text lives in `Assets/Content/Languages/EN-US.xml` (the baseline), `pt.xml` and `zh-Hans.xml`, the languages the game itself ships. The files are Other Assets in `ModData`, so they're packed; at startup `ModManagerScript.LoadModLanguages` installs each `.xml` under a `languages/` path as that language's `Strings_VizzyStudio.xml`. Code reads them through `Text.Get` (the game's `Locale.GetString`, so the game's language applies and a missing key falls back to English), with `{0}` placeholders and `.One`/`.Many` keys for counts. Settings use `{Key}` names, resolved by the settings screen, and keep their explicit XML names. Log lines stay English. The offline tests read the English file and check that every key the code names exists, and that each language has every key, no empty text and the same placeholders.

## Editor culling

`EditorCulling`, on every Vizzy editor, behind the experimental Editor Culling setting (off by default). The stock editor keeps every block as live UI: each block and text field (`BlockElementScript` and its subclasses) has an `Update`, and Unity lays out and draws them all, on screen or not, as the view (`program-container`) has no mask. So every 0.25 s it switches whole stacks (the program transform's direct children) inactive while their box is more than half a view's width or height outside the view, and active again as they come near, and puts a `RectMask2D` on the view while more of the switched-on blocks lie outside the view than inside it (off again below half). The mask clips every block it covers, so zoomed out, with nearly everything in view, it only cost time: a tester saw about 3 fps fewer than without culling. Boxes are measured every 2 s in the program's own space, so panning and zooming don't need a new measurement. Nothing changes while a mouse button is held, and the stack holding the selected object stays on. A loaded block is created and set up at once (`NodeBuilderScript.CreateBlock` → `Initialize`), but lays out (`Update` → `LayoutElement`, which sets its `Size`) and generates its text only in its first active frame. The game also builds the blocks inside a stack (its expressions) only when the stack is first active: right after a load only about a tenth of the blocks exist. A first version switched far stacks off straight after loading, which kept them unbuilt, but their size wasn't known yet (a block not laid out has no `Size`), so a stack starting well above the view could stay off while its lower part was in view. So culling now waits after connecting and after every load (`EditorCullingLoadPatch`, a postfix on `LoadFlightProgram`, which also logs the load time) until the program is built: no block without a size and the block count unchanged between two measurements, or 30 s. Stacks with blocks not laid out still get a generous estimated size (2000 units wide, 60 units taller per such block) for stacks that appear later. `EditorTimingPatch` logs how long `SaveFlightProgram`, `ApplyChangesToCraft` and `Close` take, with culling on or off. Saving reads the node tree, so it's unaffected.

The risk is stock code that collects blocks in a way that skips inactive objects (`GetComponent(s)InChildren()` without `includeInactive`, `FindObject(s)OfType`) and so misses hidden stacks. Four layers guard against it:

- **Patches.** `EditorCullingLookupPatch`, a transpiler, replaces each `GetComponentsInChildren<T>()` in the stock methods that do this over the whole program with one that also returns blocks in switched-off stacks (other inactive objects are still skipped, so with culling off the result is stock): `VizzyUIScript.GetVariableElements` (behind `RenameVariable` and `DeleteVariable`, so also Clean Variables), `CanDeleteSelection` (at every drag's end; finds calls of a custom instruction being deleted), and `LoadFlightProgram(XElement, bool, bool, Vector2)`, which destroys the old program's blocks before loading a new one (Load, Import, New, Undo and Redo; without it hidden stacks stayed behind). A first version switched every stack on before these instead, which built every block not yet built: freezes of seconds on an 18,000-block program, at every drag. A block's node is set when it's created (`NodeBuilderScript.CreateBlock` → `Initialize`), so a block never switched on is found and renamed too; `OnChildSizeChanged` only queues a layout, done when it's on again. If a method no longer has the call, the patch fails, and culling stays off.
- **The list.** `tools/check_editor_lookups.py` finds every method in the `Assets.Scripts.Vizzy` namespaces that makes such a lookup and checks it against `tools/editor_lookups.txt`, where each one is marked patched or harmless with the reason (most look inside one block, a dropdown or a flyout). It fails on a method that isn't listed, so it's run after every Juno update. It only sees direct calls in those namespaces, not a helper elsewhere that does the lookup.
- **Safety net.** `EditorCullingVariablePatch`, a postfix on `RenameVariable` and `DeleteVariable`, looks through every block, switched off or not, for a global variable block still using the old name, and does to it what the stock method does to the blocks it found (sets `VariableName` and calls `OnChildSizeChanged`, or `Destroy`), with a warning in the log. Normally there's none. Only while culling is on.
- **Stepping aside.** If either patch fails at startup, `EditorCulling.Unavailable` keeps culling off everywhere. When an editor opens, Harmony's patch list is read; if another mod patches a method in the `Assets.Scripts.Vizzy` namespaces, culling stays off in that editor, since that code can't be checked. Either way the log says why, once, when the setting is on.

`DragSelection.IdentifyTargetConnectionPoints` only needs what's in view. Measured before stacks were switched off (scripts off plus mask): about 45 ms per frame against 60 on an 18,358-block program.

## Program image (design note, not built)

Saves the whole program as one image at a readable size, from the editor's menu.

**Native mechanism.** The game takes its own pictures by rendering a camera into a `RenderTexture` and reading it back: `ScreenshotDialogScript.TakeScreenShotWithCamera` (`ReadPixels`), `PartViewerScript.TakePicture` and `SavePartPicture` (`EncodeToPNG`) (IL, 2026-11-07). The Vizzy editor itself has no camera to borrow: `VizzyUIScript.Camera` returns null, so its canvas is presumably Screen Space - Overlay (to confirm in the prototype). Zoom and pan are plain transform values: `ProgramContainerScript.Zoom` sets `ProgramTransform.localScale` (clamped to `MinZoom`–`MaxZoom`), and dragging moves `ProgramTransform.localPosition`. So zoom 1 is the blocks' own size, readable as in the editor at 100 %. No Harmony patch is needed.

**How.** One capture at a time, as a coroutine on the editor:
1. A full-screen blocker with "Saving image… n %" covers the editor (it also stops clicks and drags), and Editor Culling switches everything on and pauses. A frame passes, so blocks never shown get built.
2. The program's box is measured as Editor Culling does (every block, in the program's own space).
3. `ProgramTransform` moves for the capture under a capture canvas of our own (Screen Space - Camera, our own camera, its `targetTexture` a `RenderTexture`, far from the scene and rendering only that canvas), at zoom 1, with the editor's background colour behind it. Our canvas sees only the blocks: no toolbox, flyouts or grid.
4. Tile by tile it's rendered with `Camera.Render()` and read with `ReadPixels`, a band of rows at a time, and the rows are written straight to a PNG file by a small streaming encoder (`DeflateStream`). Memory stays at one band (a 40,000-pixel-wide program: about 60 MB), and the image can be larger than Unity's 16,384-pixel texture limit. A few tiles per frame keep the game responsive and the percentage moving.
5. `ProgramTransform` goes back to its parent with its own position and scale, culling resumes, and a message gives the file's path.

**Where it lives.** `ProgramImage.cs` (the capture, a MonoBehaviour on the editor like `EditorCulling`) and `PngStreamWriter.cs` (the encoder, with offline tests). A button in the editor's menu next to Block Search and Jump To; no key.

**File.** PNG, in a `Program Images` folder beside the game's flight programs folder (`VizzyUIScript.FlightProgramsFolderPath`), named after the program and the time.

**Built (prototype), as measured:** the editor canvas is Screen Space - Overlay (scale factor 1.111), and moving the program onto our world-space canvas renders it correctly. Rendering 400 megapixels took about 2.5 s of 13–16 s; the rest was building every block and encoding on the main thread. So the capture now waits until every block is built and laid out (as culling does), and encodes on a background thread with two band buffers, the main thread rendering one band while the other is written. The PNG uses deflate's strongest level. With the Program Image Colours setting on 256 colours (the default), a first pass renders everything to count colours (`PaletteQuantizer`: a 64-level cube; the 64 most common cells keep their exact average colour, the rest by median cut and a few k-means rounds, no dithering), and the second writes an indexed PNG (`PngStreamWriter` with a `PLTE` chunk), about twice the rendering for a much smaller file. The log gives the time for building, rendering and waiting for the encoder.

**Choices for the user:**
- Size: zoom 1 (as the editor at 100 %). Recommended: no limit, but above about 400 megapixels it asks first, and says the size; a smaller scale (0.5) as the alternative.
- What: the whole program. A later option: only the selected stacks.
- Background: the editor's plain colour; the grid isn't drawn.

**Lifecycle.**
- Pause and time warp: irrelevant; the editor runs on unscaled time, and so does the capture.
- Quicksave and quickload, scene change, craft switching or EVA, the craft or part destroyed: each closes the editor. Closing or `OnDisable` during a capture stops it at once, puts `ProgramTransform` back, deletes the unfinished file and resumes culling.
- Several editors: one capture at a time; a second click says it's busy.
- Order: after the stock editor's own `Update` doesn't matter, since editing is blocked during the capture. Funk coloring runs as text renders, so it's in the image.
- Errors: any exception ends the capture the same way and logs it; the editor is always restored.

**Stock content.** Nothing changes unless the button is used. During a capture the stock objects are only moved and moved back.

**To prove first (a prototype with logging):** the editor canvas's render mode; that a `ProgramTransform` moved under another canvas renders there and keeps working when moved back (selection, drags, culling, Funk coloring); that the stock editor doesn't keep a reference that breaks (for example a cached canvas); and how long a capture of the 18,000-block program takes.
