import { createContext, untrack, useContext, type Accessor } from 'solid-js'
import type { HomeAssistant } from './types'

export const HassCtx = createContext<HomeAssistant>()

export const useHass = () => useContext(HassCtx)!

/**
 * The `hass` object Home Assistant gives the panel, always the latest one: every property is read from `current` when
 * it is read. Home Assistant hands over a new object on each state change (many times a second), so reading never
 * subscribes to it: `hass.states` is the states of that moment, and nothing re-renders because a state changed.
 */
export function liveHass(current: Accessor<HomeAssistant>): HomeAssistant {
  const now = () => untrack(current) as unknown as object
  return new Proxy({} as HomeAssistant, {
    get: (_, key) => Reflect.get(now(), key),
    has: (_, key) => Reflect.has(now(), key),
  })
}
