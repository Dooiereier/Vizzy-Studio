# Vizzy Studio

A mod for [Juno: New Origins](https://www.simplerockets.com/) that lets you edit a running craft's Vizzy program in flight, in the game's own editor, without restarting the program.

Pause, change a PID gain, apply, and the program carries on from where it was. Quickload and the latest edit is still there, with threads back at their quicksave positions, so you can tune the same phase of flight over and over.

Vizzy Studio is still in development and has no release yet. It also includes the features of Vizzy Debugger, Vizzy Exporter and Vizzy McBlinky, so turn those mods off when using it.

## Requirements

- Juno: New Origins, tested on 1.4.105 and 1.4.201
- The **Juno Harmony** mod. Without it, Vizzy Studio stays off and tells you why.

Crafts never need the mod: their programs stay ordinary Vizzy.

## Editing in flight

Press **Option+A** (Alt+A on Windows), use **Edit Vizzy** in the flight menu, or use the Vizzy button at the bottom of the right-hand bar. The flight pauses while you edit.

- With a part selected, its program opens straight away. Otherwise you choose from the craft's programs, or the only one opens. In the list, the one you opened last is blue: Up and Down move the selection, Enter opens it.
- **Option+A** in the editor applies your edit and returns to flight, and the editor reopens where you left it.
- **Option+Shift+A** returns to the quicksave's program.
- The letter and the modifier are settings. Shift and Ctrl also change the throttle in the game's flight controls, which is why Option/Alt is the default.
- **Restart Vizzy**, on the same button, restarts a program, and it asks first. For an MFD it also removes the widgets, so the program builds its screen again.
- Custom MFD programs can be edited. Stock ones can't, because the game reloads them in flight.

### What to expect

- Apply never restarts a program, and a newly added On Start doesn't run.
- A block that has already run doesn't run again, so put values you want to tune inside a loop.
- If you delete the block a thread is on, it continues at the next block that's left in its chain.
- Each Apply restarts the memory of `PID`, `smooth`, `sum` and `rate` in Funk.
- Renaming a variable while it's in use loses its running value.

### Arrange MFD

Off by default: turn it on in the mod's settings. A third button in the flight Vizzy flyout opens a panel for a custom MFD program. The list shows the widgets that exist now and that saving can move or resize. Choose one, then move, resize or rotate it by typing values or with arrows in steps of 0.2, 2 or 20 (rotation 1, 10 or 100). The flight is paused meanwhile. The panel shows the old and new value or formula for each change, or why it can't be saved. Save writes them into the running program and the craft's files, after backing those up as `.bak` files next to them. Closing the panel puts back what wasn't saved. It changes plain numbers, a formula with one number to change on each axis (`+ - * /` blocks over `_Screen`'s size, or Funk), or the arguments of a custom instruction. Several blocks setting a widget to the same value change together. A widget that another block also moves, such as a later Set Parent, is left alone. Widgets that follow the one you move, through "get widget … position" or "size" or a shared variable, move on screen with it straight away, where the panel can work out their blocks. Also change shared variables is on by default; turn it off to keep shared variables as they are. It leaves anything else alone and says so.

## In the Vizzy editor

These work in the designer and in flight.

- **Funk auto-fill:** suggests functions, variables, `FD.` and `OD.` data as you type. Up and Down choose, Enter or a click accepts, Esc closes. Variables known to hold a vector come with `v:`.
- **Funk coloring:** colors each kind of name and marks errors in red.
- **Clean Variables:** in the menu, removes global variables the program doesn't use, after listing them. Another part's program or an InputController could still read them by name.
- **Export Block:** in the menu. Click a block, then press this to copy it to another saved program, with the variables and custom blocks it needs. An instruction brings the blocks below it.
- **Block Search:** press **Shift+F** or the search icon on the left bar, type part of a block's name, a dropdown choice, a variable, a list or a custom block, such as `+`, `wait` or `agl`, then drag the block out of the results, or double-click it to place it in view, without covering other blocks when there's room. Up and Down move the highlight through the results, starting at the top one, and Enter places the highlighted block the same way. The box empties after each block, ready for the next search, and Shift+F puts the cursor back in it if you clicked elsewhere.
- **Jump To:** on the left bar. Start a comment with `//` to tag the blocks after it, such as `//Launch` or `//Launch/Pitch Program`, then select the tag to move the view there. Tags are plain comments.

## Settings

In the game's mod settings: the update check, debug logging, the editor key and its modifier, the flight buttons, Arrange MFD, Funk coloring and auto-fill. Export Block and Block Search are always on.

The update check reads the latest version from the mod's page once per game start and offers to open the page. Nothing is downloaded automatically.

## Large programs

The game's Vizzy editor gives every block and text field its own per-frame update, even far off screen, so large programs get slow to edit. **Editor Culling** (experimental, off by default) pauses that work for blocks well outside the view and switches it back on as they come near. With Debug Logging on, `Player.log` shows the average frame time and how many blocks are paused every few seconds, so you can compare it on and off.

## Languages

English, Portuguese and Simplified Chinese, following the game's language setting. The translations are in `Assets/Content/Languages`; corrections from native speakers are welcome.

## Building

You need Unity 2022.3.62f3 and the Juno ModTools. Open the project and save the mod from the ModTools window. Harmony must never end up inside the mod file: it comes from Juno Harmony.

```bash
python3 tools/compile_check.py
```

```bash
python3 tools/run_tests.py
```

These compile the scripts without Unity and run the offline tests. How it all works is in [DESIGN.md](DESIGN.md).

## Credits

By Dooiereier, with code by Oxeter and Torifune from Vizzy Debugger and UI textures by QiQi, merged with their agreement.
