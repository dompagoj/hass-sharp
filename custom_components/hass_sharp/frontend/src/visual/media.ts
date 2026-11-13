import { createSignal, onCleanup } from 'solid-js'

/** A media query as a signal (false where `matchMedia` is missing). */
export function media(query: string) {
  const list = typeof window.matchMedia === 'function' ? window.matchMedia(query) : undefined
  const [matches, setMatches] = createSignal(!!list?.matches)
  const update = () => setMatches(!!list?.matches)
  list?.addEventListener?.('change', update)
  onCleanup(() => list?.removeEventListener?.('change', update))
  return matches
}
