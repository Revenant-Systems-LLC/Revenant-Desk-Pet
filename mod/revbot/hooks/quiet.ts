// The quiet contract as the terminal body sees it: what the desktop pet's
// state.json says about how much he talks, and the commands that change it.
// The desktop pet decides what gets said; this body only shows it and passes
// your commands on.

export type QuietState = { running: boolean; chatter: string; quiet: boolean; quietUntil: string | null; dismissed: boolean }

/** Read the quiet fields from a parsed state.json (an older pet has none of them). */
export function quietState(raw: Record<string, unknown>): QuietState {
  return {
    running: raw['running'] === true,
    chatter: typeof raw['chatter'] === 'string' ? (raw['chatter'] as string) : 'events',
    quiet: raw['quiet'] === true,
    quietUntil: typeof raw['quietUntil'] === 'string' ? (raw['quietUntil'] as string) : null,
    dismissed: raw['dismissed'] === true,
  }
}

/** A few words for the band: hidden, quiet until when, or silent; null when there is nothing to say. */
export function quietNote(q: QuietState): string | null {
  if (!q.running) return null
  if (q.dismissed) return 'hidden on your desk'
  if (q.quiet && q.quietUntil) {
    const t = new Date(q.quietUntil)
    if (!Number.isNaN(t.getTime())) return `quiet till ${t.getHours()}:${String(t.getMinutes()).padStart(2, '0')}`
    return 'quiet for now'
  }
  if (q.chatter === 'silent') return 'silent unless asked'
  return null
}

/**
 * /revbot quiet [MINUTES|off], chatter silent|events|chatty, hush, hide, show:
 * the line for the pet's pipe, or null when the words are not one of these.
 */
export function quietCommand(words: string[]): string | null {
  const verb = (words[0] ?? '').toLowerCase()
  const arg = (words[1] ?? '').toLowerCase()
  if (verb === 'quiet' && (arg === '' || arg === 'off' || arg === 'on' || /^\d{1,4}$/.test(arg))) return arg ? `quiet ${arg}` : 'quiet'
  if (verb === 'chatter' && ['silent', 'events', 'chatty'].includes(arg)) return `chatter ${arg}`
  if (verb === 'hush' || verb === 'hide' || verb === 'show') return verb
  return null
}
