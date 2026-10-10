# Night of 2026-10-09: RevBot across surfaces

Dave's goal: one entity in the computer. Continuity across desktop, terminal,
PowerShell and later the web. "Clippy 2.0 but not annoying."

## Architecture (one self, many bodies)
- The desktop pet process is the hub: owns state, conversation, memory.
- Hub -> bodies: `state.json` next to the exe, written atomically on change.
- Bodies -> hub: the existing named pipe (`revbot-hook --send`), new verbs
  `surface NAME on|off ...` and the existing `ask`, `say`.
- Every body also derives Claude's state on its own from the same rules, so it
  works alone when the pet is not running.

## Tonight, in order
- [x] 1. Pet writes `state.json` (clip, hold, mood, saying, sessions, surfaces)
- [x] 2. Pet: `surface` verb, presence registry, opt-in hand-off (Follow me into the terminal)
- [x] 3. `tools/build_cells.py`: atlas clips -> half-block cell frames (JSON)
- [x] 4. Claude Code mod `revbot`: Spinner + AbovePrompt band draw RevBot,
        state from turn/tool events, announces presence over the pipe
- [x] 5. PowerShell: `shell/RevBot.psm1` (prompt sprite + state, `revbot` cmds)
- [ ] 6. Console body `revbot.exe` (animated terminal pet + chat) if time allows (not started)
- [x] 7. README/ROADMAP, merge to main, push (main fast-forwarded and pushed; deployed to M:\Projects\Desktop-Pet\dist)
- [x] 8. Fold in the market research (docs/MARKET-RESEARCH.md, PRODUCT-PLAN.md)
- [x] 9. Web body on http://localhost:7474/ (added mid-session)

## Rules in force
- No deletes. No edits outside M:\ and the .claude carve-out.
- No GPU inference started. No new models pulled.
- Secrets never in the repo.

## Review
All of 1-5, 7-9 built, tested and pushed to main; 6 (a standalone console
body) not started. Next steps and what waits on Dave: HANDOFF.md.
