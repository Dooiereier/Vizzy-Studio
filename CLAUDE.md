# Vizzy Studio

A Juno: New Origins mod: edit a running craft's Vizzy program in the stock editor in flight, and
continue from where it was. Design: `DESIGN.md`.

Follow the `juno-modding` skill (`~/.claude/skills/juno-modding/`): design note before code, native
Juno mechanisms first, lifecycle checklist, independent review at milestones. Record new verified
facts in its `reference/juno-facts.md`.

## Hard constraints

- Apply never restarts a program; only the Restart Vizzy button does, after asking. A quickload keeps the
  latest applied edit.
- Crafts never need the mod: programs stay ordinary stock XML. No part modifiers.
- Apart from auto-fill, coloring, Clean Variables, Export Block, Jump To and reopening at the last view, the designer's editor stays stock, and an editor another
  mod opens keeps its stock buttons.
- Harmony comes from the Juno Harmony mod and is never inside the built mod file: check after each build.
- No code copied from other mods without permission.

## Working here

- Unity 2022.3.62f3. Mod name "Vizzy Studio"; the root asmdef has the same name.
- Commit as `Dooiereier <323592728+Dooiereier@users.noreply.github.com>`. One branch per phase or
  feature; merge to `main` only after the user has tested in the game.
- `python3 tools/compile_check.py <a Juno project's .csproj>` compiles the scripts
  without Unity. `python3 tools/run_tests.py` (same argument) runs the offline tests.
- `python3 tools/check_editor_lookups.py` (needs `dnfile`) lists the stock Vizzy editor methods that skip
  inactive blocks, which Editor Culling must patch or prove harmless in `tools/editor_lookups.txt`. Run it
  after every Juno update.
- On a fresh clone, put a Harmony 2.4.2.0 package in `References/com.pardeike.harmony` (gitignored) before opening
  Unity or compiling. In-game, the Juno Harmony mod provides it.
- Replaces Vizzy Debugger, whose code by Oxeter and Torifune is merged here with their agreement, and Vizzy
  Exporter and Vizzy McBlinky.
- Logs: `~/Library/Logs/Jundroo/SimpleRockets 2/Player.log` (lines start with `[VizzyStudio]`).
  The game keeps only the last two sessions: read them right after a test.
