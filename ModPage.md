*A general upgrade of the Vizzy user experience.*

Edit a running craft's Vizzy program in flight, in the game's own editor, without restarting it. Pause, change a PID gain, apply, and the program carries on from where it was. Quickload and your latest edit is still there, so you can tune the same phase of flight over and over. On top of that, the Vizzy editor gets search, navigation and Funk help, in the designer and in flight.

###Editing in flight

* **Edit Vizzy** with **Option+A** (Alt+A on Windows), the flight menu or the Vizzy button on the right-hand bar. The flight pauses while you edit.
* **Apply and carry on:** threads keep their place. Delete the block a thread is on and it continues at the next one.
* **Back to the quicksave:** Option+Shift+A returns to the quicksave's program.
* **Restart Vizzy:** restarts a program after asking. For an MFD it also clears the widgets, so the screen is built again.
* **Arrange MFD:** move, resize and rotate the widgets of a custom MFD program on screen, then save the new values straight into the program and the craft's files. Widgets that follow the one you move go with it.

###In the Vizzy editor

* **Block Search:** press Shift+F, type part of a block's name, a variable or a custom block, and drag the result out or press Enter to place it in view.
* **Jump To:** start a comment with `//` to tag the blocks after it, such as `//Launch/Pitch Program`, then jump there from the left bar.
* **Funk auto-fill:** suggests functions, variables and `FD.`/`OD.` data as you type.
* **Funk coloring:** colors each kind of name and marks errors in red.
* **Export Block:** copies a block, with what's below it, to another saved program, with the variables and custom blocks it needs.
* **Clean Unused Variables:** lists the global variables the program doesn't use, then removes them.
* **Export Image:** saves the whole program as one PNG, readable at full size, to share or print.
* **Editor Culling:** large programs get slow to edit because every block keeps working even far off screen. This pauses blocks well outside the view and wakes them as they come near, for a much higher frame rate.

**Tip:** a block that has already run doesn't run again after Apply, so put the values you want to tune inside a loop.

**Requires the Juno Harmony mod.** Crafts never need VizzyStudio: their programs stay ordinary Vizzy. It includes the features of Vizzy Debugger, Vizzy Exporter and Vizzy McBlinky, so turn those mods off. In English, Portuguese and Simplified Chinese, following the game's language. Keys and most features can be changed or switched off in the mod's settings.

By Dooiereier and Torifune, with code from Vizzy Debugger, merged with agreement.

___

Version Control:
v1.0 Initial Release
