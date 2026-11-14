import { createContext, untrack, useContext, type Accessor } from 'solid-js'
import type { HomeAssistant } from './types'

export const HassCtx = createContext<HomeAssistant>()

export const useHass = () => useContext(HassCtx)!

/**
 * The `hass` object Home Assistant gives the panel, always the latest one: every property is read from `current` when
 * it is read. Home Assistant hands over a new object on each state change (many times a second), so reading never
 * subscribes to it: `hass.states` is the states of that moment, and nothing re-renders because a state changed.
 *
 * Only reads by name work (`hass.states`, `'user' in hass`): the proxy has no `ownKeys` or `getOwnPropertyDescriptor`
 * trap, so `{ ...hass }`, `Object.keys(hass)` and `JSON.stringify(hass)` see nothing. Never hand it to a Home Assistant
 * element (`.hass=`) either: its identity never changes, so the element would not update. Read the property needed.
 */
export function liveHass(current: Accessor<HomeAssistant>): HomeAssistant {
  const now = () => untrack(current) as unknown as object
  return new Proxy({} as HomeAssistant, {
    get: (_, key) => Reflect.get(now(), key),
    has: (_, key) => Reflect.has(now(), key),
  })
}
