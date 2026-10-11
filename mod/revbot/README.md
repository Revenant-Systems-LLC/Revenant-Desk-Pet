# RevBot for Claude Code

RevBot, the Revenant desk pet, inside your Claude Code terminal. He animates in
the band above the prompt and takes the place of the thinking spinner while a
turn runs, reacting to what Claude is doing: looking around while it reads,
hammering while it edits or runs commands, a hand up when it needs your OK, an
oops when a tool fails, a little celebration when a turn ends, and a doze after
five quiet minutes.

If the desktop pet is running too, the two are one: this body tells the pet it
is here, and whatever the desktop pet says shows up next to him in the band.

## Install

From a terminal session of Claude Code:

```
/plugin install revbot --marketplace <owner>/<repo>
```

Or run it from this folder while developing:

```
claude --plugin-dir <repo>\mod\revbot
```

To load it in every session, including the desktop app's Code tab, name the
folder in `~/.claude/settings.json`:

```json
"env": { "CLAUDE_CODE_PLUGIN_DIRS": "M:/Projects/Desktop-Pet/dist/mod/revbot" }
```

## Options (`/config`, or `pluginConfigs` in settings.json)

| Option | What |
|---|---|
| `petDir` | The desktop pet's folder (holds `DesktopPet.exe`, `hook\revbot-hook.exe`, `state.json`). Empty: no desktop link; the mod works on its own. |

```json
"pluginConfigs": { "revbot": { "options": { "petDir": "M:/Projects/Desktop-Pet/dist" } } }
```

## In the session

| Command | What |
|---|---|
| `/revbot` | where he is, and this list |
| `/revbot band off`, `small`, `large` | the band above the prompt: none, his whole figure 12 rows tall, 16 rows tall |
| `/revbot spinner auto`, `sprite`, `face`, `off` | the thinking spinner. `auto` (the default) shows his head there only when the band is off, and a one-line face otherwise, so there is never two of him; `sprite` always his head; `face` always the one-line face; `off` Claude Code's own |
| `/revbot say TEXT` | the desktop pet says it in his bubble |
| `/revbot quiet`, `quiet 30`, `quiet off` | a quiet spell on the desktop pet (an hour, or minutes): he says nothing you did not ask for until it ends; the band shows "quiet till 4:58" |
| `/revbot chatter silent`, `events`, `chatty` | how much he says unasked: only answers, also when something needs you (default), also Claude's replies |
| `/revbot hush`, `hide`, `show` | stop his current line; take him off the desk and bring him back |
| `/revbot desk` | what the desktop pet is doing |

The band folds away with its `[-]` like any band.

## What it reads

Only the name of each tool Claude calls, whether the call failed, and when
turns start and end. Never prompts, file contents, commands or tool output.
With `petDir` set it also reads the pet's `state.json` (his state and what he
is saying) and runs `revbot-hook.exe --send` to announce itself.

## Files

- `hooks/register.tsx`: the hooks module (state machine, drawing, the command)
- `hooks/sprite.ts`: frame lookup and packing into the terminal's cell grid
- `cells.json`: the frames, built by `python tools/build_cells.py` from the
  pet's atlas (rerun after the art changes): his head at 8 rows, his whole
  figure at 12 and 16. `python tools/preview_cells.py` draws them as a PNG
  the way a terminal shows them. They are shrunk from the painted art, so they
  are a stand-in until there is hand-drawn pixel art at this size.
- `types/index.d.ts`: the state the drawings read

In the desktop app's Code tab the cell grid cannot be drawn; the band and the
spinner fall back to Claude Code's own there.
