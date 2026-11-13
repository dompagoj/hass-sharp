/**
 * Runs `attempt` once the update that shows its target has rendered: in a microtask, then on the next few turns
 * until it returns true. With `guard` (any node of the page), it gives up as soon as focus has moved somewhere else
 * since the call, so a late retry never takes focus back from where the user put it.
 */
export function soon(attempt: () => boolean, guard?: Node) {
  const root = guard?.getRootNode() as Document | ShadowRoot | undefined
  const start = root?.activeElement
  let tries = 0
  const run = () => {
    const now = root?.activeElement
    if (root && now !== start && now instanceof HTMLElement && now.isConnected && now !== now.ownerDocument.body) return
    if (!attempt() && ++tries < 4) setTimeout(run, 0)
  }
  queueMicrotask(run)
}

/** Focuses what `find` returns once it is on the page (see {@link soon}); stops if focus moved meanwhile. */
export function focusSoon(find: () => HTMLElement | null | undefined, guard: Node, options: { scroll?: boolean } = {}) {
  soon(() => {
    const target = find()
    if (!target?.isConnected) return false
    target.focus({ preventScroll: options.scroll === false })
    if (options.scroll !== false) target.scrollIntoView?.({ block: 'nearest' })
    return true
  }, guard)
}
