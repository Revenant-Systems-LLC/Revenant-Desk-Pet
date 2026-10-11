// RevBot in the Claude Code terminal: his sprite animates in the band above the
// prompt and stands in for the thinking spinner, reacting to what this session
// is doing the way the desktop pet does. When the desktop pet is running too
// (options.petDir points at its folder), this body announces itself to it, so he
// knows he is in two places, and shows whatever the desktop pet is saying.
//
// Nothing here reads prompts, file contents, commands or tool output: only the
// name of the tool being called, whether it failed, and when turns start and end.

import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Shown } from '../types'
import { quietCommand, quietNote, quietState } from './quiet'
import { clipFor, face, frameAt, isDone, packFrame, parseSheet, pick, spinnerShows } from './sprite'
import type { Sheet, Size } from './sprite'

const PLUGIN = 'revbot'
const TICK_MS = 1000 / 12 // the fastest clip is 12 fps
const FIDGET_MIN_MS = 6000
const FIDGET_MAX_MS = 14000
const SLEEP_AFTER_MS = 5 * 60 * 1000
const HEARTBEAT_MS = 15 * 1000 // the pet stands a terminal body down after 45 s of silence (a killed session never says goodbye)
const SAYING_POLL_MS = 1500

// What the drawings read. A change here redraws the band and the spinner.
const shown = atom({ plugin: 'revbot', key: 'shown' } as const, { clip: 'idle', state: null, saying: null, where: null, note: null } as Shown)
const bandSize = atom({ plugin: 'revbot', key: 'bandSize' } as const, 'small')
const spinnerStyle = atom({ plugin: 'revbot', key: 'spinnerStyle' } as const, 'auto') // 'auto': never two of him at once

type Desk = { running: boolean; saying: string | null; claude: string; where: string; note: string | null; dismissed: boolean }

// The pet's moment, module-wide (a hot reload starts him over, which is fine).
const S = {
  hookExe: '',
  statePath: '',
  sheet: null as Sheet | null,
  hold: null as string | null,       // 'review' | 'working' | 'waiting' | null
  oneShot: null as string | null,    // a clip playing once, over the held state
  clipStartedAt: 0,                  // in ticks
  tick: 0,
  lastEventTick: 0,
  nextFidgetTick: 0,
  asleep: false,
  subagents: 0,
  turnStartedTick: 0,
  sessionId: '',
  desktopSaying: null as string | null,
  deskWhere: null as string | null,  // where the desktop pet says he is ('desktop', 'terminal:<id>'), null when not running
  deskNote: null as string | null,   // the quiet contract: 'quiet till 4:58', 'hidden on your desk', ...
  pseudo: 12345,
  // where the sprites are mounted, so the frame timer can repaint them in place
  bandId: null as string | null,
  spinnerId: null as string | null,
  bandSizeUsed: null as Size | null,
  spinnerSizeUsed: null as Size | null,
  lastBandFrame: -1,
  lastSpinnerFrame: -1,
}

function rand(lo: number, hi: number): number {
  S.pseudo = (S.pseudo * 1103515245 + 12345) & 0x7fffffff
  return lo + (S.pseudo / 0x7fffffff) * (hi - lo)
}

function currentClip(): string {
  if (S.oneShot) return S.oneShot
  if (S.asleep) return 'sleeping'
  return S.hold ?? 'idle'
}

function stateForTool(tool: string): string {
  if (tool === 'AskUserQuestion' || tool === 'ExitPlanMode') return 'waiting'
  const t = tool.toLowerCase()
  const looking = ['read', 'grep', 'glob', 'search', 'fetch', 'find', 'list', 'get', 'query', 'view', 'screenshot', 'snapshot']
  return looking.some(w => t.includes(w)) ? 'review' : 'working'
}

async function show($: EngineInterface, clip: string): Promise<void> {
  S.clipStartedAt = S.tick
  S.lastBandFrame = S.lastSpinnerFrame = -1
  const state = S.hold
  const saying = S.desktopSaying
  const where = S.deskWhere
  const note = S.deskNote
  await update($, shown, s => (s.clip === clip && s.state === state && s.saying === saying && s.where === where && s.note === note ? s : { clip, state, saying, where, note }))
}

async function playOnce($: EngineInterface, clip: string): Promise<void> {
  S.oneShot = clip
  await show($, clip)
}

async function setHold($: EngineInterface, state: string | null): Promise<void> {
  S.lastEventTick = S.tick
  if (S.asleep) {
    S.asleep = false
    S.oneShot = 'wake'
  }
  S.hold = state
  if (!S.oneShot) await show($, currentClip())
  else await update($, shown, s => (s.state === S.hold ? s : { ...s, state: S.hold }))
}

// ---------------------------------------------------------------- the desktop pet

async function tellDesk($: EngineInterface, line: string): Promise<void> {
  if (!S.hookExe) return
  try {
    await $.process.run([S.hookExe, '--send', line], { timeoutMs: 3000 })
  } catch {
    // the pet is not running, or the folder moved: nothing to do
  }
}

async function readDesk($: EngineInterface): Promise<Desk | null> {
  if (!S.statePath) return null
  try {
    const raw = JSON.parse(await $.fs.read(S.statePath)) as Record<string, unknown>
    return {
      running: raw['running'] === true,
      saying: typeof raw['saying'] === 'string' ? (raw['saying'] as string) : null,
      claude: typeof raw['claude'] === 'string' ? (raw['claude'] as string) : 'idle',
      where: typeof raw['where'] === 'string' ? (raw['where'] as string) : 'desktop',
      note: quietNote(quietState(raw)),
      dismissed: raw['dismissed'] === true,
    }
  } catch {
    return null
  }
}

async function pollDesk($: EngineInterface): Promise<void> {
  const desk = await readDesk($)
  const saying = desk?.running ? desk.saying : null
  const note = desk?.running ? desk.note : null
  const where = desk?.running && !desk.dismissed ? desk.where : null // hidden: he is not 'also on your desk'
  if (saying !== S.desktopSaying || where !== S.deskWhere || note !== S.deskNote) {
    S.desktopSaying = saying
    S.deskWhere = where
    S.deskNote = note
    await update($, shown, s => ({ ...s, saying, where, note }))
  }
}

// ---------------------------------------------------------------- the frame clock

async function frame($: EngineInterface): Promise<void> {
  const sheet = S.sheet
  if (!sheet) return
  const elapsedMs = (S.tick - S.clipStartedAt) * TICK_MS
  const size = S.bandSizeUsed ?? S.spinnerSizeUsed ?? pick(sheet, 'body', 12)
  const { clip } = clipFor(size, currentClip())

  if (S.oneShot && isDone(clip, elapsedMs)) {
    const was = S.oneShot
    S.oneShot = null
    if (was === 'doze_off') S.asleep = true
    await show($, currentClip())
    return
  }
  if (!S.oneShot && !S.hold && !S.asleep) {
    if (S.tick - S.lastEventTick > SLEEP_AFTER_MS / TICK_MS) {
      await playOnce($, 'doze_off')
      return
    }
    if (S.tick >= S.nextFidgetTick) {
      S.nextFidgetTick = S.tick + Math.round(rand(FIDGET_MIN_MS, FIDGET_MAX_MS) / TICK_MS)
      await playOnce($, 'fidget_blink')
      return
    }
  }

  const idx = frameAt(clip, elapsedMs)
  if (S.bandId && S.bandSizeUsed && idx !== S.lastBandFrame) {
    S.lastBandFrame = idx
    const c = clipFor(S.bandSizeUsed, currentClip()).clip
    const r = await $.ui.blit({ requestId: S.bandId, key: 'pet', cells: packFrame(sheet, S.bandSizeUsed, c.frames[frameAt(c, elapsedMs)]!), columns: S.bandSizeUsed.columns, rows: S.bandSizeUsed.rows })
    if (r.deny) S.bandId = null // not mounted any more; the next render mounts it again
  }
  if (S.spinnerId && S.spinnerSizeUsed && idx !== S.lastSpinnerFrame) {
    S.lastSpinnerFrame = idx
    const c = clipFor(S.spinnerSizeUsed, currentClip()).clip
    const r = await $.ui.blit({ requestId: S.spinnerId, key: 'pet', cells: packFrame(sheet, S.spinnerSizeUsed, c.frames[frameAt(c, elapsedMs)]!), columns: S.spinnerSizeUsed.columns, rows: S.spinnerSizeUsed.rows })
    if (r.deny) S.spinnerId = null
  }
  if (S.spinnerId && !S.spinnerSizeUsed && S.tick % 4 === 0) $.ui.invalidate('ui.render') // the face blinks
}

function doingWords(state: string | null): string {
  if (state === 'waiting') return 'needs your OK'
  if (state === 'working') return 'hammering away'
  if (state === 'review') return 'looking into it'
  return S.asleep ? 'asleep' : 'here with you'
}

/** Where the desktop pet is, relative to this session: inside this terminal, on the desk, in another body. */
function whereWords(where: string | null): string | null {
  if (!where) return null
  if (where === `terminal:${S.sessionId.replace(/[^A-Za-z0-9]/g, '')}`) return 'came in from the desk'
  if (where === 'desktop') return 'also on your desk'
  return `also in ${where.replace(':', ' ')}`
}

// ---------------------------------------------------------------- hooks

export const register: Register = (on, options) => {
  const petDir = String(options['petDir'] ?? '').replace(/\\/g, '/').replace(/\/+$/, '')
  S.hookExe = petDir ? `${petDir}/hook/revbot-hook.exe` : ''
  S.statePath = petDir ? `${petDir}/state.json` : ''

  on('session.start', async ($, e, next) => {
    try {
      S.sheet = parseSheet(await $.fs.read(`${$.plugin.root}/cells.json`))
    } catch (err) {
      $.ui.log(`${PLUGIN}: cells.json did not load: ${String(err)}`)
    }
    S.sessionId = await $.session.id()
    S.nextFidgetTick = Math.round(rand(FIDGET_MIN_MS, FIDGET_MAX_MS) / TICK_MS)

    await $.command.register({
      name: 'revbot',
      description: 'RevBot in your terminal: band size, spinner style, say something, where he is',
      argumentHint: '[band off|small|large] [spinner auto|sprite|face|off] [say TEXT] [quiet [MIN|off]] [chatter silent|events|chatty] [hush|hide|show] [desk]',
    })

    if (e.isInteractive && e.surface === 'terminal') {
      await tellDesk($, `surface terminal on session=${S.sessionId}`)
      $.clock.every(HEARTBEAT_MS, () => {
        void tellDesk($, `surface terminal on session=${S.sessionId}`)
      })
      $.clock.every(SAYING_POLL_MS, () => {
        void pollDesk($)
      })
    }

    // The frame clock: advances the sprite in place; a finished one-off clip
    // falls back to the held state; a long quiet spell puts him to sleep.
    $.clock.every(TICK_MS, () => {
      S.tick++
      void frame($)
    })
    return next(e)
  })

  on('session.end', async ($, e, next) => {
    await tellDesk($, `surface terminal off session=${S.sessionId}`)
    return next(e)
  })

  on('turn.start', async ($, e, next) => {
    S.turnStartedTick = S.tick
    await setHold($, 'review') // reading what you asked, thinking it over
    return next(e)
  })

  on('tool.call', async ($, e, next) => {
    await setHold($, stateForTool(String(e.tool)))
    const ran = await next(e)
    S.lastEventTick = S.tick
    if (ran.deny === undefined && ran.isError === true) await playOnce($, 'failed')
    else if (S.hold === 'waiting') await setHold($, stateForTool(String(e.tool))) // you answered: hand down
    return ran
  }).catch(($, e, next) => next(e)) // our failure never stops a tool call

  on('turn.complete', async ($, e, next) => {
    S.subagents = 0
    S.hold = null
    S.lastEventTick = S.tick
    await playOnce($, e.isAborted ? 'shrug' : e.reason === 'error' ? 'failed' : 'happy')
    return next(e)
  })

  on('classic.PermissionRequest', async ($, e, next) => {
    await setHold($, 'waiting')
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.Notification', async ($, e, next) => {
    if (e.notification_type === 'permission_prompt') await setHold($, 'waiting')
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.SubagentStart', async ($, e, next) => {
    S.subagents++
    if (S.subagents === 3) await playOnce($, 'jump') // a crowd of agents
    return next(e)
  }).catch(($, e, next) => next(e))

  on('classic.SubagentStop', async ($, e, next) => {
    S.subagents = Math.max(0, S.subagents - 1)
    return next(e)
  }).catch(($, e, next) => next(e))

  on('command.run', { command: 'revbot' }, async ($, e) => {
    const words = e.args.trim().split(/\s+/).filter(Boolean)
    const verb = (words[0] ?? '').toLowerCase()
    const arg = (words[1] ?? '').toLowerCase()
    if (verb === 'band' && ['off', 'small', 'large'].includes(arg)) {
      await update($, bandSize, () => arg)
      return { text: `RevBot's band above the prompt: ${arg}.` }
    }
    if (verb === 'spinner' && ['auto', 'sprite', 'face', 'off'].includes(arg)) {
      await update($, spinnerStyle, () => arg)
      return { text: `RevBot's spinner: ${arg}.` }
    }
    if (verb === 'say' && words.length > 1) {
      if (!S.hookExe) return { text: 'No desktop pet is configured (set petDir in /config).' }
      await tellDesk($, `say ${words.slice(1).join(' ')}`)
      return { text: 'Sent to the desktop pet.' }
    }
    const line = quietCommand(words)
    if (line) {
      if (!S.hookExe) return { text: 'No desktop pet is configured (set petDir in /config).' }
      await tellDesk($, line)
      await pollDesk($)
      return { text: `Sent to the desktop pet: ${line}.` }
    }
    if (verb === 'desk') {
      const desk = await readDesk($)
      if (!desk) return { text: S.hookExe ? 'The desktop pet has no state file yet (is it running?).' : 'No desktop pet is configured (set petDir in /config).' }
      return { text: desk.running ? `Desktop pet: ${desk.where}, Claude ${desk.claude}${desk.note ? `, ${desk.note}` : ''}${desk.saying ? `, saying "${desk.saying}"` : ''}.` : 'The desktop pet is not running.' }
    }
    const band = await read($, bandSize)
    const spin = await read($, spinnerStyle)
    return {
      text: [
        `RevBot: ${currentClip()}${S.hold ? ` (Claude ${S.hold})` : ''}. Band ${band}, spinner ${spin}${S.hookExe ? ', desktop pet linked' : ''}.`,
        '/revbot band off|small|large    /revbot spinner auto|sprite|face|off',
        '/revbot say TEXT (the desktop pet says it)    /revbot desk (where he is)',
        '/revbot quiet [MINUTES|off]    /revbot chatter silent|events|chatty    /revbot hush|hide|show',
      ].join('\n'),
    }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const sheet = S.sheet
    if (e.surface !== 'terminal' || e.props.hasSurvey || !sheet) return next(e)
    const mode = await read($, bandSize)
    if (mode === 'off') return next(e)
    const s = await read($, shown)
    const { Box, Text, Raster } = $.ui.resolve(e)
    const size = pick(sheet, 'body', mode === 'large' ? 16 : 12)
    S.bandId = e.requestId
    S.bandSizeUsed = size
    S.lastBandFrame = -1
    const { clip } = clipFor(size, s.clip)
    const cells = packFrame(sheet, size, clip.frames[frameAt(clip, (S.tick - S.clipStartedAt) * TICK_MS)]!)
    return (
      <Box flexDirection="row" gap={1}>
        <Raster key="pet" columns={size.columns} rows={size.rows} cells={cells} />
        <Box flexDirection="column" justifyContent="flex-end">
          <Text bold color="claude">RevBot</Text>
          <Text dimColor>{doingWords(s.state)}{whereWords(s.where) ? `, ${whereWords(s.where)}` : ''}{s.note ? `, ${s.note}` : ''}</Text>
          {s.saying ? <Text wrap="wrap">"{s.saying}"</Text> : null}
        </Box>
      </Box>
    )
  })

  on('ui.render', { component: 'Spinner' }, async ($, e, next) => {
    const sheet = S.sheet
    if (e.surface !== 'terminal' || !sheet) return next(e)
    const style = spinnerShows(await read($, spinnerStyle), await read($, bandSize))
    if (style === 'off') return next(e)
    const s = await read($, shown)
    const label = e.props.message ?? e.props.word
    const suffix = e.props.suffix
    const seconds = Math.max(0, Math.round(((S.tick - S.turnStartedTick) * TICK_MS) / 1000))
    const { Box, Text, Raster } = $.ui.resolve(e)
    S.spinnerId = e.requestId
    if (style === 'face') {
      S.spinnerSizeUsed = null
      return (
        <Box flexDirection="row" gap={1}>
          <Text color="claude">{face(s.state, S.tick)}</Text>
          <Text color="claude">{label}{suffix}</Text>
          <Text dimColor>({seconds}s)</Text>
        </Box>
      )
    }
    const size = pick(sheet, 'head', 8) // only his head: a whole body at spinner size is a smudge
    S.spinnerSizeUsed = size
    S.lastSpinnerFrame = -1
    const { clip } = clipFor(size, s.clip)
    const cells = packFrame(sheet, size, clip.frames[frameAt(clip, (S.tick - S.clipStartedAt) * TICK_MS)]!)
    return (
      <Box flexDirection="row" gap={1}>
        <Raster key="pet" columns={size.columns} rows={size.rows} cells={cells} />
        <Box flexDirection="column" justifyContent="flex-end">
          <Text color="claude">{label}{suffix}</Text>
          <Text dimColor>({seconds}s)</Text>
        </Box>
      </Box>
    )
  })
}
