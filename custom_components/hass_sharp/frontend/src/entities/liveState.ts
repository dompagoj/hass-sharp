import { createEffect, createMemo, createSignal, on, onCleanup, type Accessor } from 'solid-js'
import type { HomeAssistant } from '../types'

/**
 * An entity's state as Home Assistant has it now; `available` is false while its state is `unavailable`; `unit` is its
 * `unit_of_measurement` attribute, when it has one.
 */
export interface LiveState { state: string; available: boolean; unit?: string }
export type LiveStates = Record<string, LiveState>

/**
 * Home Assistant's compressed `subscribe_entities` event: `a` adds entities (`s` is the state, `a` the attributes), `c`
 * changes them (`'+'` holds what changed, `s` only when the state did, `a` the attributes that did; `'-'.a` the
 * attributes removed), `r` removes them.
 */
interface EntitiesEvent {
  a?: Record<string, { s?: unknown; a?: Record<string, unknown> }>
  c?: Record<string, { '+'?: { s?: unknown; a?: Record<string, unknown> }; '-'?: { a?: unknown } }>
  r?: string[]
}

const unitKey = 'unit_of_measurement'
const live = (state: string, unit: unknown): LiveState =>
  ({ state, available: state !== 'unavailable', ...(typeof unit === 'string' && unit ? { unit } : {}) })

/** `current` with the event applied; the same object when the event changes no state and no unit. */
function applied(current: LiveStates, event: EntitiesEvent): LiveStates {
  let next = current
  const write = () => (next === current ? (next = { ...current }) : next)
  for (const [id, entity] of Object.entries(event.a ?? {}))
    if (typeof entity?.s === 'string') write()[id] = live(entity.s, entity.a?.[unitKey])
  for (const [id, change] of Object.entries(event.c ?? {})) {
    const old = next[id], added = change?.['+'], removed = change?.['-']?.a
    const state = typeof added?.s === 'string' ? added.s : old?.state
    if (state === undefined) continue
    const unit = added?.a && Object.hasOwn(added.a, unitKey) ? added.a[unitKey]
      : Array.isArray(removed) && removed.includes(unitKey) ? undefined : old?.unit
    const changed = live(state, unit)
    if (!old || old.state !== changed.state || old.unit !== changed.unit) write()[id] = changed
  }
  for (const id of event.r ?? []) if (id in next) delete write()[id]
  return next
}

/**
 * The live states of `entityIds`, through one `subscribe_entities` subscription. It subscribes again only when the set
 * of ids changes (in any order, an id given twice counted once), keeping the states of ids still asked for until the
 * new subscription answers; with no ids it holds none. A subscription that is refused, or a `hass` without a
 * connection, leaves the states empty. Closed when the owner is cleaned up.
 */
export function liveState(hass: Pick<HomeAssistant, 'connection'>, entityIds: () => string[]): Accessor<LiveStates> {
  const [states, setStates] = createSignal<LiveStates>({})
  // A string, so the same set compares equal and does not subscribe again.
  const key = createMemo(() => [...new Set(entityIds().filter(id => typeof id === 'string' && id))].sort().join('\n'))
  createEffect(on(key, joined => {
    const ids = joined ? joined.split('\n') : []
    setStates(current => Object.fromEntries(ids.flatMap(id => (current[id] ? [[id, current[id]]] : []))))
    if (!ids.length || typeof hass.connection?.subscribeMessage !== 'function') return
    let closed = false
    let unsubscribe: (() => unknown) | undefined
    const close = (stop: () => unknown) => {
      try {
        void Promise.resolve(stop()).catch(() => {})
      } catch {
        // Already closed with the connection.
      }
    }
    onCleanup(() => { closed = true; if (unsubscribe) close(unsubscribe) })
    // Sent a moment later (a call that throws becomes a refusal), and not at all once cleaned up meanwhile.
    Promise.resolve()
      .then(() => closed ? undefined : hass.connection.subscribeMessage<EntitiesEvent>(
        event => { if (!closed) setStates(current => applied(current, event)) },
        { type: 'subscribe_entities', entity_ids: ids },
      ))
      .then(
        stop => { if (!stop) return; if (closed) close(stop); else unsubscribe = stop },
        () => { if (!closed) setStates({}) },
      )
  }))
  return states
}
