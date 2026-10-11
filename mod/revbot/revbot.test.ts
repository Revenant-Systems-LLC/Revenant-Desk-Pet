// claude plugin test mod/revbot
import { expect, test } from 'claude-code/testing'

import { quietCommand, quietNote, quietState } from './hooks/quiet'
import { base64, clipFor, face, frameAt, isDone, packFrame, parseSheet, pick, sizeFor, spinnerShows } from './hooks/sprite'

const TINY = JSON.stringify({
  pet: 'revbot',
  palette: ['#ff0000', '#00ff00'],
  sizes: [
    {
      columns: 2,
      rows: 1,
      clips: {
        idle: { fps: 8, loop: true, frames: [{ g: '▀ ', f: '10', b: '20' }, { g: ' ▄', f: '02', b: '00' }] },
        wave: { fps: 4, loop: false, frames: [{ g: '██', f: '11', b: '00' }] },
      },
    },
  ],
})

test('frames pack into the Raster cell encoding', async () => {
  const sheet = parseSheet(TINY)
  expect(sheet.palette).toEqual([0xff0000, 0x00ff00])
  const size = sizeFor(sheet, 16)
  expect(size.columns).toBe(2)
  const cells = packFrame(sheet, size, size.clips['idle']!.frames[0]!)
  // 2 cells x 3 u32 = 24 bytes = 32 base64 characters
  expect(cells.length).toBe(32)
  expect(base64(new Uint8Array([0x20, 0x00, 0x00, 0x00]))).toBe('IAAAAA==')
})

test('clips loop, one-offs end, missing clips fall back', async () => {
  const sheet = parseSheet(TINY)
  const size = sizeFor(sheet, 2)
  const idle = clipFor(size, 'idle').clip
  expect(frameAt(idle, 0)).toBe(0)
  expect(frameAt(idle, 125)).toBe(1)
  expect(frameAt(idle, 250)).toBe(0)
  const wave = clipFor(size, 'wave').clip
  expect(isDone(wave, 100)).toBe(false)
  expect(isDone(wave, 250)).toBe(true)
  expect(clipFor(size, 'happy').name).toBe('wave') // happy -> jump -> wave
  expect(clipFor(size, 'working').name).toBe('idle') // working -> think -> idle
})

test('sizes are picked by crop and rows, falling back when a crop is missing', async () => {
  const sheet = parseSheet(TINY)
  sheet.sizes.push({ crop: 'head', columns: 3, rows: 2, clips: sheet.sizes[0]!.clips })
  sheet.sizes.push({ crop: 'body', columns: 5, rows: 4, clips: sheet.sizes[0]!.clips })
  expect(pick(sheet, 'head', 8).columns).toBe(3)
  expect(pick(sheet, 'body', 3).columns).toBe(5) // 4 rows is nearer 3 than 1 row (the uncropped size counts as body)
  expect(pick(sheet, 'body', 1).columns).toBe(2)
  const old = parseSheet(TINY)
  expect(pick(old, 'head', 8).columns).toBe(2) // a sheet without crops still gives something
})

test('auto spinner: his head only when the band is off, so there is never two of him', async () => {
  expect(spinnerShows('auto', 'small')).toBe('face')
  expect(spinnerShows('auto', 'large')).toBe('face')
  expect(spinnerShows('auto', 'off')).toBe('sprite')
  expect(spinnerShows('sprite', 'small')).toBe('sprite')
  expect(spinnerShows('off', 'off')).toBe('off')
})

test('the spinner face changes with the state and blinks', async () => {
  expect(face('working', 5)).toBe('[>_<]')
  expect(face('waiting', 5)).toBe('[o_o]')
  expect(face(null, 5)).toBe('[^_^]')
  expect(face(null, 40)).toBe('[-_-]')
})

test('the command answers on its own and the state follows a tool call', async ($, on) => {
  const answer = await $.command.run({ command: 'revbot', args: '' })
  expect(String(answer.text)).toContain('RevBot')
  const band = await $.command.run({ command: 'revbot', args: 'band off' })
  expect(String(band.text)).toContain('off')
})

test('quiet: the band notes a quiet spell, hiding or silence, and an older pet shows nothing', async () => {
  const at = new Date(2026, 9, 10, 16, 5).toISOString()
  expect(quietNote(quietState({ running: true, quiet: true, quietUntil: at }))).toBe('quiet till 16:05')
  expect(quietNote(quietState({ running: true, dismissed: true, quiet: true, quietUntil: at }))).toBe('hidden on your desk')
  expect(quietNote(quietState({ running: true, chatter: 'silent' }))).toBe('silent unless asked')
  expect(quietNote(quietState({ running: true, chatter: 'events' }))).toBe(null)
  expect(quietNote(quietState({ running: false, dismissed: true }))).toBe(null)
  expect(quietNote(quietState({ running: true }))).toBe(null)
})

test('quiet: commands become pipe lines, nonsense does not', async () => {
  expect(quietCommand(['quiet'])).toBe('quiet')
  expect(quietCommand(['quiet', '30'])).toBe('quiet 30')
  expect(quietCommand(['Quiet', 'OFF'])).toBe('quiet off')
  expect(quietCommand(['quiet', 'soon'])).toBe(null)
  expect(quietCommand(['chatter', 'chatty'])).toBe('chatter chatty')
  expect(quietCommand(['chatter', 'loud'])).toBe(null)
  expect(quietCommand(['hush'])).toBe('hush')
  expect(quietCommand(['hide'])).toBe('hide')
  expect(quietCommand(['band', 'off'])).toBe(null)
})

test('quiet: without a desktop pet the commands say so', async ($, on) => {
  const answer = await $.command.run({ command: 'revbot', args: 'quiet 30' })
  expect(String(answer.text)).toContain('No desktop pet')
})
