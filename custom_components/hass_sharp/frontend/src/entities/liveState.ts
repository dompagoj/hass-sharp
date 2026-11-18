import { createEffect, createMemo, createSignal, on, onCleanup, type Accessor } from 'solid-js'
import type { HomeAssistant } from '../types'

/** An entity's state as Home Assistant has it now; `available` is false while its state is `unavailable`. */
export interface LiveState { state: string; available: boolean }
export type LiveStates = Record<string, LiveState>

/**
 * Home Assistant's compressed `subscribe_entities` event: `a` adds entities (`s` is the state), `c` changes them
 * (`'+'` holds what changed, `s` only when the state did), `r` removes them.
 */
interface EntitiesEvent {
  a?: Record<string, { s?: unknown }>
  c?: Record<string, { '+'?: { s?: unknown } }>
  r?: string[]
}

const live = (state: string): LiveState => ({ state, available: state !== 'unavailable' })

/** `current` with the event applied; the same object when the event changes no state. */
function applied(current: LiveStates, event: EntitiesEvent): LiveStates {
  let next = current
  const write = () => (next === current ? (next = { ...current }) : next)
  for (const [id, entity] of Object.entries(event.a ?? {}))
    if (typeof entity?.s === 'string') write()[id] = live(entity.s)
  for (const [id, change] of Object.entries(event.c ?? {}))
    if (typeof change?.['+']?.s === 'string') write()[id] = live(change['+'].s)
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
    Promise.resolve()
      .then(() => hass.connection.subscribeMessage<EntitiesEvent>(
        event => { if (!closed) setStates(current => applied(current, event)) },
        { type: 'subscribe_entities', entity_ids: ids },
      ))
      .then(
        stop => { if (closed) close(stop); else unsubscribe = stop },
        () => { if (!closed) setStates({}) },
      )
  }))
  return states
}
