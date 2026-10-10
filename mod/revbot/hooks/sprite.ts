// RevBot's terminal sprite: the frames tools/build_cells.py wrote into
// cells.json, packed into the Raster element's cell encoding on demand.

export type Frame = { g: string; f: string; b: string }
export type Clip = { fps: number; loop: boolean; frames: Frame[] }
export type Size = { columns: number; rows: number; clips: Record<string, Clip> }
export type Sheet = { pet: string; palette: number[]; sizes: Size[] }

const ALPHABET = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_'
const DEFAULT_COLOUR = 0x01000000 // the terminal's own colour (bit 24 alone)
const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'

const index: Record<string, number> = {}
for (let i = 0; i < ALPHABET.length; i++) index[ALPHABET[i]!] = i

/** Parse cells.json's text; the palette becomes 0x00RRGGBB numbers. */
export function parseSheet(text: string): Sheet {
  const raw = JSON.parse(text) as { pet: string; palette: string[]; sizes: Size[] }
  return {
    pet: raw.pet,
    palette: raw.palette.map(h => parseInt(h.slice(1), 16)),
    sizes: raw.sizes,
  }
}

/** The size with this many columns, or the nearest one the sheet has. */
export function sizeFor(sheet: Sheet, columns: number): Size {
  let best = sheet.sizes[0]!
  for (const s of sheet.sizes) {
    if (Math.abs(s.columns - columns) < Math.abs(best.columns - columns)) best = s
  }
  return best
}

/** A clip of this size, falling back the way the desktop pet does (Atlas.Clip). */
export function clipFor(size: Size, name: string): { name: string; clip: Clip } {
  const fallbacks: Record<string, string[]> = {
    wave: ['happy', 'jump'],
    happy: ['jump', 'wave'],
    shrug: ['think'],
    waiting: ['think'],
    review: ['think'],
    working: ['think'],
    failed: ['shrug'],
    doze_off: ['sleeping'],
    wake: ['idle'],
    fidget_blink: ['idle'],
    jump: ['happy'],
  }
  if (size.clips[name]) return { name, clip: size.clips[name]! }
  for (const alt of fallbacks[name] ?? []) {
    if (size.clips[alt]) return { name: alt, clip: size.clips[alt]! }
  }
  return { name: 'idle', clip: size.clips['idle']! }
}

/** Which frame of a clip plays `elapsedMs` after it started: looped, or held on the last one. */
export function frameAt(clip: Clip, elapsedMs: number): number {
  const n = clip.frames.length
  if (n === 0) return 0
  const i = Math.floor((elapsedMs / 1000) * clip.fps)
  return clip.loop ? i % n : Math.min(i, n - 1)
}

/** True once a one-off clip has played through. */
export function isDone(clip: Clip, elapsedMs: number): boolean {
  return !clip.loop && elapsedMs >= (clip.frames.length / clip.fps) * 1000
}

/** One frame as the Raster's `cells`: base64 of little-endian u32 [codePoint, fg, bg] per cell. */
export function packFrame(sheet: Sheet, size: Size, frame: Frame): string {
  const n = size.columns * size.rows
  const words = new Uint32Array(n * 3)
  for (let i = 0; i < n; i++) {
    const fi = index[frame.f[i] ?? '0'] ?? 0
    const bi = index[frame.b[i] ?? '0'] ?? 0
    words[i * 3] = frame.g.charCodeAt(i) || 0x20
    words[i * 3 + 1] = fi === 0 ? DEFAULT_COLOUR : (sheet.palette[fi - 1] ?? DEFAULT_COLOUR)
    words[i * 3 + 2] = bi === 0 ? DEFAULT_COLOUR : (sheet.palette[bi - 1] ?? DEFAULT_COLOUR)
  }
  return base64(new Uint8Array(words.buffer))
}

/** Standard padded base64; written by hand so no web API is assumed. */
export function base64(bytes: Uint8Array): string {
  let out = ''
  let i = 0
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!
    out += B64[(n >> 18) & 63]! + B64[(n >> 12) & 63]! + B64[(n >> 6) & 63]! + B64[n & 63]!
  }
  if (i < bytes.length) {
    const n = (bytes[i]! << 16) | ((bytes[i + 1] ?? 0) << 8)
    out += B64[(n >> 18) & 63]! + B64[(n >> 12) & 63]!
    out += i + 1 < bytes.length ? B64[(n >> 6) & 63]! : '='
    out += '='
  }
  return out
}

/**
 * A one-line face for the spinner's compact mode: his two eyes and an antenna,
 * blinking now and then, with a hint of what he is doing.
 */
export function face(state: string | null, tick: number): string {
  const blink = tick % 40 === 0 || tick % 40 === 1
  const eyes = blink ? '-_-' : state === 'waiting' ? 'o_o' : state === 'working' ? '>_<' : state === 'review' ? 'o_O' : '^_^'
  return `[${eyes}]`
}
