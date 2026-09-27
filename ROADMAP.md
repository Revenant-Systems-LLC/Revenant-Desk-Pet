# Revenant Desk Pet: roadmap

Future updates, as asked for by Dave (2026-09-26). None of this is built yet
unless it says so. Development starts from this list.

## The point of it

A little AI buddy on your desktop that you can love, poke, fling around or
torture: part Tamagotchi, part Kick the Buddy. It ships with RevBot. A
make-your-own-pet feature may come to the product later; for now that lives
only in the separate kid's edition (Desk-Pet-Maker).

## 1. A small LLM inside

- Around 1.5B parameters, so it runs on weak PCs.
- It talks, and it controls what the pet is doing: expressions and motions go
  through the pet's single entry point, `Pet.SetState()`.
- On Dave's own machine the VRAM budget for background apps is 0 (1 GB
  ceiling), so the model should run on the CPU, quantised; GPU is optional.
- Persona not chosen yet. Keep it a swappable prompt file.

## 2. Talking to it

- **Telegram:** a bot made with BotFather, long polling (outbound only, no
  ports opened). Token kept in the DPAPI secrets store, never in the repo.
  Replies can appear as a speech bubble over the pet.
- **Claude Code hooks:** a local-only command channel into the running pet
  (named pipe), fed by hooks such as UserPromptSubmit, PreToolUse,
  PermissionRequest, PostToolUseFailure, Stop, SubagentStart/SubagentStop. The
  hooks must print nothing, so they cost zero tokens. A fast sender, not
  PowerShell, so tool calls are not slowed down.
- **"Listening while you type":** no hook fires on keystrokes, but the pet can
  combine "time since last input" (never which keys) with "Claude or a
  terminal is the active window" to perk up while you type.

## 3. Detachable arm

- A hard landing knocks his arm off. The arm becomes its own object on the
  screen with its own physics: it flies, bounces and comes to rest.
- You can pick the arm up, move it, or throw it at him.
- Without it he keeps trying to get back to it: walk over, pick it up, click
  it back on. If he is thrown again before he gets there, the arm stays where
  it was and he has to walk back to it. It never teleports.
- No transforming into the mech while an arm is off.
- Art: arm-knocked-off, one-armed idle, one-armed walk, pick-up-and-reattach
  (video prompts written, videos 7, 9 and 10).

## 4. Mech mode

- Transform in, then about 30 seconds as a big, scary mech with its own idle,
  heavy walk and a stomp, then a different transform-out (steam, shrink,
  dizzy wobble), not the transform played backwards.
- Mech arms don't fall off; a throw ends in a heavy stomp.
- Triggers: rarely on his own, a "Power up" menu item, and later the AI (for
  example while several agents are working at once).
- Art: videos 8, 11 and 12 (prompts written).

## 5. Interacting with the desktop ("Animator vs. Animation")

- **The desktop is a 2D side-scroller** (Dave, 2026-09-27). The taskbar is the
  ground. Desktop icons are platforms he can climb onto. Open windows are
  terrain he can climb up and mess around on. He should be able to climb on
  everything.
- He reacts to what is behind him: climbs up window edges and desktop icons
  like walls, stands on title bars, peeks around corners.
- Built 2026-09-27 (branch feature/terrain): visible window tops are ledges
  (occlusion-aware, refreshed every 0.5 s, read-only). RevBot jumps up onto
  ledges within about 280 px, walks along them, rides along when the window
  moves, steps off the ends, and drops when it closes, minimises or gets
  covered; thrown, he lands on them. Damien floats over the highest window top
  beneath him. "Climb on windows" menu toggle. Still to do: climbing tall
  window sides waits for real "climb" art (Dave, 2026-09-27: no code-made
  stand-in); desktop icons as platforms (UI Automation, read-only); peeking
  around corners.
- Maybe, now and then, he grabs a window's scrollbar just to mess with you,
  without damaging anything. Mechanics to be talked through with Dave first;
  it would be the one exception to "pretend only" below, so nothing gets
  built for it until he decides.
- **Strictly pretend.** He never clicks, moves, closes, deletes or changes
  anything. He only reads where windows and icons are.
- Notes: window rectangles from EnumWindows plus DWM extended frame bounds;
  desktop icon positions read-only (UI Automation on the desktop list view).
  Skip minimised and cloaked windows.

## 6. Throw him from one screen to another

- Today a throw stops at the edge of the monitor he is on.
- Let the flight carry across a shared edge: switch the floor to the monitor
  he is over while in the air, rebuild the sprites when the DPI differs, and
  only stop at edges with no monitor beyond them.
- Built 2026-09-27 (branch feature/throw-across-screens). He only crosses if
  he is above the other screen's floor at the edge; lower than that, its side
  is a wall and he bounces. Works for Damien's floating throw too. Up and down
  edges (stacked monitors) are not handled yet.

## 7. Throwing, landing and shaking that feel physical

- **Hard landings.** Today he lands too cleanly after a big throw. Landing
  must scale with fall height: a small drop is a light hop, a big one is a
  hard landing where his legs bend deeply to catch the weight, he squashes,
  maybe stumbles, then straightens up. Needs new frames (a "hard landing"
  clip, legs bending under the weight). Until then, the code can fake some of
  it with squash-and-stretch on impact and a short stagger.
- **Shake him and he gets dizzy.** Detect shaking while he is being dragged
  (fast back-and-forth changes of direction in the drag path); when you let
  go he is dizzy: wobbling, swirly eyes, stumbling for a few seconds. Needs a
  "dizzy" clip.
- Built 2026-09-27 (branch feature/landing-and-dizzy), code-made until the
  art exists: every landing squashes and springs back, scaled by drop height
  (a hard one still plays the arm-off "failed" clip); four quick swings while
  held make him dizzy when he lands (wobbling tilt for about 3 s, over his
  idle frames or a future "dizzy" clip). Not applied to Damien.
- The arm popping off stays as the extreme end of a hard landing.
- **Sit on the taskbar.** Now and then he sits down on the top edge of the
  taskbar with his legs hanging over it, swinging his feet, then hops back
  up. The sprite's anchor moves from his feet to his seat for this clip, so
  his legs dangle in front of the taskbar. Needs a "sit" clip (sit down,
  sitting loop with swinging legs, stand up).

## 8. Chase mode (working name; "Animator vs. Animation" is taken)

- A mode where he goes after your mouse cursor: chases it along the taskbar,
  jumps for it, tries to grab it, and you can dodge or swat him. Pretend
  only: he never actually moves or clicks your real cursor.
- Name to pick, e.g. "Cursor Brawl" or "Mouse Hunt".

## 9. More than one pet

- Several pets on the desktop at once that notice and interact with each
  other: wave, chase, bump into each other, and later talk (each with its own
  LLM persona).
- Needs the single-instance rule relaxed to one process hosting several pets.

## 10. Art still to do (new orange/yellow RevBot)

- Reference drawing: `art/source/revbot-v3/reference-white.png`. Starting
  images for videos must be 16:9 with him small in the middle, or the video
  tools crop off his antenna and feet.
- Done in the new look: jump (idle, blink, jump).
- Still needed: walk, wave/think/shrug, search/read, waiting/error,
  hammering, plus the arm and mech sets above. The older grey clips could be
  recoloured instead of regenerated (untested, about 60% it looks right).
- The cutting tools are in `tools/` (`video_frames.py`, `remove_mouth.py`,
  `fix_antenna.py`).

## 11. Damien

- Second built-in pet (the red devil ball), picked from the "Pet" menu.
- **He floats; he has no legs.** Same 2D-side-scroll desktop as RevBot, but he
  drifts over it instead of walking and climbing.
- Thrown or dropped: no gravity. He slides through the air, slows to a stop,
  then eases back to his hover height.
- No squash-and-hop (the old "running" rows) while he drifts. Flips and spins
  from his art folder belong in there instead.
- Most of the time he has no mouth; the smile and the toothy grin are for
  moments.
- Built 2026-09-27 (branch feature/damien-float): hover with bob, drift on
  idle frames, no-gravity throw with the flip-and-squish (his d00, d01, d02,
  d04, d05, d06), then ease back to hover. Terrain (floating over windows and
  icons) comes with section 5.
- **Animating him is Dave's own job.** Code picks and times only frames Dave
  drew, in the order he numbered them; no invented frames. His wave (tail
  wiggle) was pulled 2026-09-27: its motion scribbles looked wrong and it
  needs redoing.

## Already built

- The desktop pet itself: walks the taskbar, follows the cursor, naps, hides
  under fullscreen apps, drag and throw, zero GPU.
- Every animation except idle is optional, with sensible fallbacks, so a pet
  with only a few animations still works.
