import { createEffect, createMemo, createSignal, on, onCleanup, type Accessor } from 'solid-js'
import type { HomeAssistant } from '../types'

/**
 * An entity's state as Home Assistant has it now; `available` is false while its state is `unavailable`. The attributes
 * a card shows, each only when the entity has it: `unit` (`unit_of_measurement`), a light's `brightness` (0–255), a
 * cover's `position` (`current_position`, 0–100) and a text's `mode` (`text` or `password`).
 */
export interface LiveState { state: string; available: boolean; unit?: string; brightness?: number; position?: number; mode?: string }
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

/** The attributes kept, by Home Assistant's name: the member of {@link LiveState} each goes to, and the type it has there. */
const kept = {
  unit_of_measurement: { member: 'unit', type: 'string' },
  brightness: { member: 'brightness', type: 'number' },
  current_position: { member: 'position', type: 'number' },
  mode: { member: 'mode', type: 'string' },
} as const
type Kept = Partial<Pick<LiveState, 'unit' | 'brightness' | 'position' | 'mode'>>
/** The kept attributes of `attributes`, each of its type (a text not empty, a number finite). */
function keptOf(attributes: Record<string, unknown>): Kept {
  const result: Record<string, unknown> = {}
  for (const [name, { member, type }] of Object.entries(kept)) {
    const value = attributes[name]
    if (type === 'string' ? typeof value === 'string' && value !== '' : typeof value === 'number' && Number.isFinite(value)) result[member] = value
  }
  return result as Kept
}
const live = (state: string, attributes: Kept): LiveState => ({ state, available: state !== 'unavailable', ...attributes })
const same = (a: LiveState, b: LiveState) =>
  a.state === b.state && a.unit === b.unit && a.brightness === b.brightness && a.position === b.position && a.mode === b.mode

/** `current` with the event applied; the same object when the event changes no state and no kept attribute. */
function applied(current: LiveStates, event: EntitiesEvent): LiveStates {
  let next = current
  const write = () => (next === current ? (next = { ...current }) : next)
  for (const [id, entity] of Object.entries(event.a ?? {}))
    if (typeof entity?.s === 'string') write()[id] = live(entity.s, keptOf(entity.a ?? {}))
  for (const [id, change] of Object.entries(event.c ?? {})) {
    const old = next[id], added = change?.['+'], removed = change?.['-']?.a
    const state = typeof added?.s === 'string' ? added.s : old?.state
    if (state === undefined) continue
    // The attributes it had, less those removed, with those changed.
    const attributes: Record<string, unknown> = {}
    for (const [name, { member }] of Object.entries(kept))
      if (old && old[member] !== undefined && !(Array.isArray(removed) && removed.includes(name))) attributes[name] = old[member]
    const changed = live(state, keptOf({ ...attributes, ...(added?.a ?? {}) }))
    if (!old || !same(old, changed)) write()[id] = changed
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
