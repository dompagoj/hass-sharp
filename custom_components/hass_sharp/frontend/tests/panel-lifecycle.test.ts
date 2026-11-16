import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import '../src/hass-sharp'
import type { HassSharpView } from '../src/hass-sharp'
import type { HomeAssistant } from '../src/types'
import type { Window } from 'happy-dom'

let panel: HassSharpView
const browser = window as unknown as Window
let pendingCss: (() => void)[]

beforeEach(() => {
  // Hold the network boundary; each test controls when the real link receives load/error.
  pendingCss = []
  browser.happyDOM.settings.enableCSSFileLoading = true
  browser.happyDOM.settings.fetch.interceptor = {
    beforeAsyncRequest: ({ window: source }) => new Promise(resolve => {
      pendingCss.push(() => resolve(new source.Response('')))
    }),
  }
  history.replaceState(null, '', '/hass-sharp/automations')
  panel = document.createElement('ha-panel-hass-sharp-view') as HassSharpView
  panel.hass = {
    user: { is_admin: true },
    states: {},
    callWS: async ({ type }: { type: string }) => {
      if (type === 'hass_sharp/visual') return { version: 1, ok: true, result: [] }
      if (type === 'hass_sharp/get_entities') return { entities: [], areas: [], devices: [], floors: [], labels: [] }
      throw new Error(`Unexpected request: ${type}`)
    },
  } as unknown as HomeAssistant
})

afterEach(() => {
  panel.remove()
  pendingCss.forEach(resolve => resolve())
  browser.happyDOM.settings.enableCSSFileLoading = false
  browser.happyDOM.settings.fetch.interceptor = null
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  history.replaceState(null, '', '/')
})

const attach = async () => {
  document.body.append(panel)
  await panel.updateComplete
}
const css = () => panel.querySelector<HTMLLinkElement>('link[rel="stylesheet"]')!
const loadCss = async () => {
  css().dispatchEvent(new Event('load'))
  await panel.updateComplete
}

it('waits for CSS before mounting the app so icons never appear at their unstyled size', async () => {
  await attach()
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)
  expect(panel.querySelector('svg')).toBeNull()

  await loadCss()
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')
  expect(panel.querySelector('svg')).not.toBeNull()
})

it('remounts when Home Assistant reattaches the same panel after suspending a hidden tab', async () => {
  await attach()
  await loadCss()
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')

  for (let cycle = 0; cycle < 3; cycle++) {
    panel.remove()
    expect(panel.querySelector('#app')!.childElementCount).toBe(0)
    // A browser may retain the parsed stylesheet, without another load event.
    vi.spyOn(css(), 'sheet', 'get').mockReturnValue(new CSSStyleSheet())
    await attach()
    expect(panel.querySelector('h1')?.textContent).toBe('Automations')
    expect(panel.querySelectorAll('.hs-app')).toHaveLength(1)
  }
})

it('does not mount if the stylesheet finishes loading while the panel is detached', async () => {
  await attach()
  panel.remove()
  await loadCss()
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)

  await attach()
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)
  await loadCss()
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')
})

it('waits for CSS again when Chrome refetches the stylesheet on reattachment', async () => {
  await attach()
  await loadCss()
  panel.remove()
  await attach()
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)
  await loadCss()
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')
})

it('waits for Home Assistant if CSS arrives before the hass property', async () => {
  const hass = panel.hass
  panel.hass = undefined as unknown as HomeAssistant
  await attach()
  await loadCss()
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)

  panel.hass = hass
  await panel.updateComplete
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')
})

it('uses the latest hass for requests after a connection change without remounting the app', async () => {
  const connectedHass = panel.hass
  panel.hass = { ...connectedHass, callWS: async ({ type }: { type: string }) => {
    if (type === 'hass_sharp/visual') throw new Error('Connection lost')
    return connectedHass.callWS({ type })
  } } as HomeAssistant
  await attach()
  await loadCss()
  await vi.waitFor(() => expect(panel.querySelector('[role="alert"]')?.textContent).toContain('Connection lost'))
  const app = panel.querySelector('.hs-app')

  panel.hass = connectedHass
  await panel.updateComplete
  panel.querySelector<HTMLButtonElement>('[role="alert"] button')!.click()
  await vi.waitFor(() => expect(panel.querySelector('.hs-home-empty h2')?.textContent).toBe('No automations yet'))
  expect(panel.querySelector('.hs-app')).toBe(app)
})

it('shows a stylesheet failure and lets the user retry instead of leaving an empty panel', async () => {
  await attach()
  css().dispatchEvent(new Event('error'))
  await panel.updateComplete
  expect(panel.querySelector('[role="alert"]')?.textContent).toContain('Could not load')
  expect(panel.querySelector('#app')!.childElementCount).toBe(0)

  const failedLink = css()
  panel.querySelector<HTMLButtonElement>('[role="alert"] button')!.click()
  await panel.updateComplete
  expect(css()).not.toBe(failedLink)
  await loadCss()
  expect(panel.querySelector('[role="alert"]')).toBeNull()
  expect(panel.querySelector('h1')?.textContent).toBe('Automations')
})
