import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { HassCtx } from '../src/context'
import { ServicePicker } from '../src/components/ServicePicker'
import type { ServicesSection } from '../src/visual/contract'
import services from '../../../../tests/fixtures/visual-services.json'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })
const section = services as unknown as ServicesSection
const translations: Record<string, string> = {
  'component.light.services.turn_on.name': 'Turn on',
  'component.light.services.turn_on.description': 'Turns on one or more lights and adjusts their properties.',
}
function mount(value: string, options: { localize?: boolean; onRefresh?: () => void } = {}) {
  const host = document.createElement('div')
  document.body.append(host)
  const callWS = vi.fn(async () => ({}))
  const change = vi.fn()
  const hass = { callWS, ...(options.localize ? { localize: (key: string) => translations[key] ?? key } : {}) }
  dispose = render(() => <HassCtx.Provider value={hass as any}><ServicePicker value={value} services={section} onChange={change} onRefresh={options.onRefresh} /></HassCtx.Provider>, host)
  return { host, callWS, change }
}
const options = (host: Element) => [...host.querySelectorAll<HTMLElement>('[role="option"]')]

it('lists the services section with translated names and ids, searches descriptions and returns the exact id, without asking Home Assistant', () => {
  const refresh = vi.fn()
  const { host, callWS, change } = mount('climate.set_hvac_mode', { localize: true, onRefresh: refresh })
  // The field shows the current service by name and id.
  const field = host.querySelector<HTMLButtonElement>('button[aria-label="Service"]')!
  expect(field.textContent).toContain('Set hvac mode (climate.set_hvac_mode)')
  field.click()
  expect(refresh).toHaveBeenCalledTimes(1)
  expect(options(host)).toHaveLength(Object.keys(section.items).length)
  const light = options(host).find(o => o.textContent?.includes('light.turn_on'))!
  expect(light.textContent).toContain('Turn on')
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search services"]')!
  search.value = 'adjusts their properties'
  search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(options(host)).toHaveLength(1)
  options(host)[0].click()
  expect(change).toHaveBeenCalledWith('light.turn_on')
  expect(callWS).not.toHaveBeenCalled()
})

it('falls back to the section\'s names when Home Assistant has no translation', () => {
  const { host } = mount('todo.get_items')
  expect(host.querySelector('button[aria-label="Service"]')!.textContent).toContain('Get to-do list items (todo.get_items)')
})

it('Escape preserves the value and returns focus', async () => {
  const { host, change } = mount('light.turn_on')
  const trigger = host.querySelector<HTMLButtonElement>('button[aria-label="Service"]')!
  trigger.focus()
  trigger.click()
  expect(options(host).length).toBeGreaterThan(0)
  host.querySelector('dialog')!.dispatchEvent(new Event('cancel', { cancelable: true }))
  await Promise.resolve()
  expect(host.querySelector('dialog[open]')).toBeNull()
  expect(document.activeElement).toBe(trigger)
  expect(change).not.toHaveBeenCalled()
})
