import { afterEach, expect, it } from 'vitest'
import { createEffect, createRoot, createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { HassCtx, liveHass } from '../src/context'
import { useServiceText } from '../src/visual/serviceText'
import type { HomeAssistant } from '../src/types'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

it('reads the latest hass Home Assistant gave the panel, without re-running anything when it changes', () => {
  const states = (on: string) => ({ 'light.hall': { entity_id: 'light.hall', state: on } })
  const [hass, setHass] = createSignal({ states: states('off'), callWS: async () => 'first' } as unknown as HomeAssistant)
  const live = liveHass(hass)
  let runs = 0
  createRoot(stop => {
    dispose = stop
    createEffect(() => { void live.states; runs++ })
  })
  expect(runs).toBe(1)
  // Home Assistant sends a new object on every state change.
  for (const state of ['on', 'off', 'on']) setHass({ ...hass(), states: states(state) } as unknown as HomeAssistant)
  expect(runs).toBe(1)
  expect(live.states['light.hall'].state).toBe('on')
  setHass({ ...hass(), callWS: async () => 'latest' } as unknown as HomeAssistant)
  return expect(live.callWS({ type: 'x' })).resolves.toBe('latest')
})

it('names services in the language Home Assistant has now, also after loading their translations', async () => {
  const localize = (language: 'en' | 'de') => (key: string) =>
    key === 'component.light.services.turn_on.name' ? (language === 'en' ? 'Turn on' : 'Einschalten') : key
  const english = localize('en')
  const [hass, setHass] = createSignal({ localize: english, loadBackendTranslation: async () => english } as unknown as HomeAssistant)
  const [shown, setShown] = createSignal(0)
  const host = document.createElement('div'); document.body.append(host)
  const Name = () => { const text = useServiceText(); return <p>{(shown(), text.serviceName('light.turn_on'))}</p> }
  dispose = render(() => <HassCtx.Provider value={liveHass(hass)}><Name /></HassCtx.Provider>, host)
  await Promise.resolve(); await Promise.resolve()
  expect(host.textContent).toBe('Turn on')
  // The language changes: Home Assistant gives a new localize, and the next time names are shown they follow it.
  setHass({ ...hass(), localize: localize('de') } as unknown as HomeAssistant)
  setShown(1)
  expect(host.textContent).toBe('Einschalten')
})
