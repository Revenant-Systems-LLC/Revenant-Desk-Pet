# Revenant Desk Pet

RevBot, a little robot that lives on the Windows taskbar. It walks around,
fidgets, watches the mouse cursor, naps when you step away, and hides itself
whenever a fullscreen app or game is in front of it.

Where it's going (an LLM inside, a detachable arm, mech mode, climbing on your
windows, and more) is in [ROADMAP.md](ROADMAP.md).

Built on WinForms with a per-pixel-alpha layered window (`UpdateLayeredWindow`).
It uses no GPU and no Direct3D. The only GPU cost is DWM compositing one small
window, the same cost as any other small window.

## Run

```
dist\DesktopPet.exe              normal
dist\DesktopPet.exe --demo       cycles every animation, state and look direction
dist\DesktopPet.exe --selftest   loads the art, builds every scale, resolves every
                                 animation, logs the result and exits (works while
                                 the pet is already running)
```

It needs the .NET 10 Desktop Runtime. Only one copy runs at a time.

Only the idle animation is required; every other animation is optional and
falls back to the closest one the pet has (see `Atlas.Clip`).

## AI link: Claude Code drives the pet

The pet listens on a local named pipe (`\\.\pipe\RevenantDeskPet-<windows user>`,
current user only, no network). `revbot-hook.exe` (`src/RevBotHook`) is run by
Claude Code hooks: it reads the hook event and forwards only the event name,
session id, tool name and notification type (never prompts, paths, commands or
output), prints nothing, and always exits 0, even when the pet isn't running.

| Claude Code event | RevBot |
|---|---|
| you send a prompt | looks around (thinking it over) |
| reading or searching tools | looks around |
| editing, running commands, other tools | hammers away |
| asking you a question, needing permission | hand up, waiting |
| a tool fails | oops, then back to work |
| three subagents at once | flexes |
| done | celebrates, then idle |
| Claude Code went quiet for 90 s (30 min while waiting on you) | stands down |

Several sessions at once are tracked separately; the pet shows the most
important state (waiting on you, then working, then looking around).

Wire it up with async hooks in `~/.claude/settings.json`, one entry per event
(`async` means Claude Code never waits for it):

```json
"UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "\"M:/Projects/Desktop-Pet/dist/revbot-hook.exe\"", "async": true, "timeout": 10 }] }]
```

Events used: `SessionStart`, `UserPromptSubmit`, `PreToolUse` (matcher `*`),
`PostToolUse` (matcher `*`), `PostToolUseFailure`, `PermissionRequest`,
`Notification`, `SubagentStart`, `SubagentStop`, `Stop`, `StopFailure`,
`SessionEnd`.

Send commands by hand: `revbot-hook --send "state working"`, `"play wave"`,
`"pet damien"`, `"fling 900 -700"` (throw it at that speed, px/s), `"jumpup"`
(jump onto a window top within reach), `"chase on"` / `"chase off"`, `"ledges"` (log the window tops it can
see, positions only), `"clear"`, `"ping"`.

## Controls

| Input | What happens |
|---|---|
| Left click | a random reaction (jump, wave, shrug, sometimes a mech move) |
| Left drag | pick it up; let go to drop it (big drops end in a fall-apart). Throw it hard at a side where another monitor sits and it flies onto that screen, if it is high enough to clear that screen's floor |
| Shake it while holding it | four quick swings back and forth: when you let go and it lands, it's dizzy and wobbles for a few seconds |
| Right click, or the tray icon | menu: actions, held states, size, pet, chase the mouse, stay put, climb on windows, come here, exit |
| "Chase the mouse" | he runs after the cursor along whatever he stands on, jumps for it when it's just above his head, and cheers when he reaches it; click him to swat him away (in mid-jump too). Damien floats after it. Pretend only: the real cursor is never moved. Overrides "Stay put" while on |
| Tray icon double-click | "come here": drops it onto the middle of the main monitor |

With nothing to do it walks, fidgets, and follows the cursor with its head
when the cursor gets close. While you type into a terminal or Claude it stops
wandering and turns to watch that window, and carries on a few seconds after
you stop. It only knows that a key was pressed (input happened while the mouse
stayed still) and which program is in front; it never reads the keys, and
logs only "watching you type" on and off. After 5 minutes with no keyboard or mouse input it
falls asleep, and it wakes up when you come back.

## Files it writes

Both go next to the exe, never anywhere else:

- `settings.json`: size, stay-put, last position, which pet
- `logs\pet.log`: state changes and errors, capped at two 1 MB files

## The AI hookup (later)

`Pet.SetState(name)` in `src/DesktopPet/Pet.cs` is the single entry point. A
looping state stays until cleared; a one-off clip just plays:

| State | Clip |
|---|---|
| `working` | hammering away |
| `waiting` | chin-tapping, hands folded |
| `review` | looking around at its surroundings |
| `failed` | arm pops off, gets reattached |
| `idle` or null | back to normal |

The "Hold a state" menu drives the same call, so the states can be previewed
now. The missing piece is a channel into the running process (a named pipe or
a `--state working` argument forwarded to the running copy) so that something
like a Claude Code hook can call it.

## Windows are terrain

The desktop works like a 2D side-scroller: the taskbar is the ground and the
visible top edges of open windows are ledges. RevBot jumps up onto ledges
within reach, walks along them, rides along when the window is moved, and
drops off when he walks off the end or the window closes, is minimised or gets
covered. Throw him onto a window and he lands on it. Damien floats over the
highest window top beneath him. Turn it off with "Climb on windows" in the
menu.

It only reads where windows are (EnumWindows plus DWM frame bounds, every half
second); it never moves, clicks, focuses or sends anything to them. Maximised
windows leave no room above them, so on a screen full of maximised windows
there is nothing to climb yet. Windows taller than his jump need climbing,
which waits for climbing art; desktop icons are not platforms yet.

## Pets

Two pets are built in: RevBot and Damien (the red devil ball). Pick one from
the "Pet" menu; the choice is saved. Damien's atlas is built from his sheet by
`python toolsuild_codex_pet.py damien`.

Landings squash RevBot down and spring him back, harder the further he fell,
and shaking him makes him dizzy. Both are code-made (a squash/stretch and a
tilt of his existing frames) until "hard landing" and "dizzy" clips are drawn;
a drawn "dizzy" clip replaces the idle frames under the wobble. Floating pets
get neither: their motion is drawn, not faked.

Damien floats (`"floats": true` in his atlas): he hovers a little above the
floor with a slow bob, drifts instead of walking (his idle frames, never the
walk rows), and has no gravity. Thrown, he plays his flip-and-squish, slides
to a stop and eases back to hover height. The hover, bob, drift and drag
numbers are the `Float*`/`Hover*`/`Bob*` constants at the top of `Pet.cs`.

## Art pipeline

`tools/build_atlas.py` turns `art/source/` into the embedded
`src/DesktopPet/Assets/atlas.png` + `atlas.json` + `pet.ico`, and writes a QA
sheet to `art/build/contact.png`. Rerun it after changing the art, then
rebuild.

- Strips 01 to 12 carry real alpha but were drawn at different scales (up to
  about 40% apart). Each strip is scaled to match the idle strip, using the
  geometric mean of an eye-size ratio and a height ratio.
- The walk cycle exists only in `13-atlas-preview.webp`, a labelled contact
  sheet on a checkerboard. The script cuts the walk rows out of it and removes
  the checkerboard (including holes between the legs). The walk frames are the
  lowest-resolution art in the set; a clean full-resolution walk sheet can be
  dropped in later.
- Every frame is positioned relative to an anchor: the ground point under the
  pet. The art is drawn for 150% scaling (144 dpi) and rescaled per monitor.

```
python tools\build_atlas.py
dotnet publish src\DesktopPet\DesktopPet.csproj -c Release -o dist
```
