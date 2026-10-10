# Revenant Desk Pet

RevBot, a little robot that lives on the Windows taskbar. It walks around,
fidgets, watches the mouse cursor, naps when you step away, and hides itself
whenever a fullscreen app or game is in front of it.

He is one pet with several bodies: the desktop (this app), your Claude Code
terminal (`mod/revbot`, see "RevBot in the terminal"), PowerShell
(`shell/RevBot.psm1`), and a web page the pet serves on this PC
(http://localhost:7474/). The desktop pet owns his state and his conversation;
the other bodies show the same pet and talk to him through it. Where it's
going (a web body, an LLM inside, a detachable arm, mech mode, and more) is in
[ROADMAP.md](ROADMAP.md).

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

For the `Stop` hook use `revbot-hook.exe --speak`: besides the event, the pet
says the opening of Claude's last reply (the first sentence or two, up to about
160 characters, markdown stripped) in its speech bubble. It reads that reply
from the transcript Claude Code points the hook to, sends it only over the
local pipe, and writes it nowhere. When Claude waits on a permission prompt he
says "Hey Dave, I need your OK on something." (at most every 45 s).

Events used: `SessionStart`, `UserPromptSubmit`, `PreToolUse` (matcher `*`),
`PostToolUse` (matcher `*`), `PostToolUseFailure`, `PermissionRequest`,
`Notification`, `SubagentStart`, `SubagentStop`, `Stop`, `StopFailure`,
`SessionEnd`.

Send commands by hand: `revbot-hook --send "state working"`, `"play wave"`,
`"pet damien"`, `"fling 900 -700"` (throw it at that speed, px/s), `"jumpup"`
(jump onto a window top within reach), `"chase on"` / `"chase off"`, `"invite damien"`, `"sendhome"`, `"ledges"` (log the window tops it can
see, positions only), `"clear"`, `"ping"`.

## Controls

| Input | What happens |
|---|---|
| Left click | a random reaction (jump, wave, shrug, sometimes a mech move) |
| Left drag | pick it up; let go to drop it (big drops end in a fall-apart). Throw it hard at a side where another monitor sits and it flies onto that screen, if it is high enough to clear that screen's floor |
| Shake it while holding it | four quick swings back and forth: when you let go and it lands, it's dizzy and wobbles for a few seconds |
| Right click, or the tray icon | menu: actions, held states, size, pet, friends, chase the mouse, stay put, climb on windows, come here, exit |
| Friends > Invite Damien | a second pet joins, in its own process next to this one: they look at each other, wave when they meet, sometimes go over to visit, and stop to say hello instead of walking through each other (then walk on past). "Send friends home" closes them; they also leave when the main pet exits |
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

All go next to the exe, never anywhere else:

- `settings.json`: size, stay-put, last position, which pet (a friend pet
  keeps its own `settings.<pet>.json` and `logs\pet.<pet>.log`)
- `logs\pet.log`: state changes and errors, capped at two 1 MB files
- `state.json`: what he is doing right now, for his other bodies (below).
  Rewritten only when something changes, at most ten times a second, and
  marked `running: false` on exit. It holds the clip playing, his mode, the
  held state, what Claude Code is doing across sessions, the line in his speech
  bubble, his chat's last answer and a counter of answers, which brain he talks
  with, which bodies have announced themselves, and where his feet are. The
  bubble line and the last answer are the one place his words touch the disk;
  they are overwritten by the next ones and cleared when he exits. The log
  still never holds his words.

## One pet, several bodies

The desktop pet is the hub. Anything else that shows him is a body: it reads
`state.json` to show the same clip, mood and words, and talks back over the
pipe. A body says hello with `surface NAME on session=ID` (or `pid=N`),
repeats it now and then as a heartbeat, and `surface NAME off ...` when it
goes; he waves at a new one, and a body silent for half an hour is stood down.
`state.json` lists the bodies present, so each can know he is in more than
one place. `Pet.SetState(name)` in `src/DesktopPet/Pet.cs` stays the single
entry point for his state: `working`, `waiting`, `review`, `failed`, `idle`.

**Follow me into the terminal** (menu, off by default; `follow on|off` over the
pipe): when a terminal body announces itself from the window in front, and
that window is a terminal on his monitor, he wakes if he must, walks over,
hops up and is gone from the desk; the terminal's band says he came in from
the desk. He drops back out of the bottom of that window when the session
ends or the window closes, on "Come here" (tray double-click), on `comeback`
over the pipe, or when the option is turned off. While he is in, his links
keep running and `state.json` says `where: terminal:<session>`.

### RevBot in the terminal (Claude Code)

`mod/revbot` is a Claude Code mod: he animates in the band above the prompt
and takes the place of the thinking spinner while a turn runs, with the same
reactions as on the desktop. It links to the desktop pet when its `petDir`
option points at this folder, and then shows whatever he is saying. Load it
with `claude --plugin-dir <repo>\mod
evbot`, or for every session (the
desktop app's Code tab included) with `CLAUDE_CODE_PLUGIN_DIRS` in
`~/.claude/settings.json`. `/revbot` inside a session lists what it can do.
Details and options: [mod/revbot/README.md](mod/revbot/README.md). His
terminal frames come from `python toolsuild_cells.py` (rerun after the art
changes).

### RevBot in the browser

The pet serves one page on `http://localhost:7474/` (menu: Open in browser;
`WebEnabled` and `WebPort` in `settings.json`). It draws him from the same
sprite sheet the desktop uses, animating whatever clip `state.json` names,
shows what he is doing and saying and which bodies he is in, and has a chat
box that asks his brain and prints the answer. Only connections from this PC
are answered, and writes need the page's own header and origin, so another
site cannot make him talk or spend his Claude. Each open tab counts as a body
until 45 seconds after it closes.

### RevBot in PowerShell

```powershell
Import-Module <repo>\shell\RevBot.psd1   # or set $env:REVBOT_HOME to the pet's folder
revbot                      him, in colour, with what he is doing
revbot ask "question"       ask his Claude; the answer prints here and in his bubble
revbot chat                 a back-and-forth until /bye
revbot say "text"           he says it
revbot play wave            any clip; revbot state working holds a state; revbot clear lets go
revbot talk                 opens his chat box on the desktop
revbot start                starts the desktop pet
revbot follow on|off        follow you into the terminal; revbot back brings him out
Enable-RevBotPrompt         his face and state in front of your prompt (Disable-RevBotPrompt undoes it)
```

Put the `Import-Module` line (and `Enable-RevBotPrompt` if you like) in your
`$PROFILE` to have him in every shell. The module finds the pet's folder from
`$env:REVBOT_HOME`, else `dist\` next to it in the repo.

## Talking

When he says something, a comic speech bubble appears at the upper right above
his head. The text types out a letter at a time (about 16 a second, with
pauses at commas and full stops) and each letter makes a soft wooden "tock",
like the old Animal Crossing text boxes; the bubble grows as it fills, stays
up long enough to read, then goes. Lines wait their turn. "Do something > Say
hello" tries it; "Talking sounds" in the menu turns the tocks off. Anything on
this PC can make him talk through the AI link: `revbot-hook --send "say Hello
there!"`. The log records only that he spoke, never the words.

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

RevBot's current look (charcoal, yellow hazard stripes, orange limbs, no mouth)
comes from Dave's videos. `tools/video_frames.py NAME VIDEO` cuts a
white-background video into matted frames in `art/video/NAME/` (`--fix-eyes
--v3` repaints cartoon eyes as his solid eyes with the one highlight), and
`tools/build_v3.py` picks each move's frames, scales every video to the same
size, anchors his feet and writes `atlas.png`/`atlas.json`/`pet.ico`, with a QA
sheet in `art/build/v3-contact.png`. The videos, frame ranges and speeds are
listed at the top of `build_v3.py`; the prompts that made them are in
`art/VIDEO-PROMPTS-v3.md`. He has no look-around frames in this look yet, so
he doesn't turn his head toward the cursor.

The older red RevBot was built by `tools/build_atlas.py` from the strips in
`art/source/`:

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
