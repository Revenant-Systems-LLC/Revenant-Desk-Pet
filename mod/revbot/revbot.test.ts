// claude plugin test mod/revbot
import { expect, test } from 'claude-code/testing'

import { base64, clipFor, face, frameAt, isDone, packFrame, parseSheet, sizeFor } from './hooks/sprite'

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
