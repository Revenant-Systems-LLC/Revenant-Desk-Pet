/** What the band and the spinner draw from: the clip playing, what Claude is doing, what the desktop pet is saying. */
export type Shown = { clip: string; state: string | null; saying: string | null; where: string | null }

declare module 'claude-code' {
  interface PluginState {
    revbot: { shown: Shown; bandSize: string; spinnerStyle: string }
  }
}
