import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createSignal } from 'solid-js'
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
function mount(value: string, options: { localize?: boolean; onRefresh?: () => void; services?: ServicesSection } = {}) {
  const host = document.createElement('div')
  document.body.append(host)
  const callWS = vi.fn(async () => ({}))
  const change = vi.fn()
  const hass = { callWS, ...(options.localize ? { localize: (key: string) => translations[key] ?? key } : {}) }
  dispose = render(() => <HassCtx.Provider value={hass as any}><ServicePicker value={value} services={options.services ?? section} onChange={change} onRefresh={options.onRefresh} /></HassCtx.Provider>, host)
  return { host, callWS, change }
}
const options = (host: Element) => [...host.querySelectorAll<HTMLElement>('[role="option"]')]
const resultIds = (host: Element) => options(host).map(option => option.querySelector('small')!.textContent)
function searchServices(host: Element, query: string) {
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search services"]')!
  search.value = query; search.dispatchEvent(new Event('input', { bubbles: true }))
  return search
}
const serviceSection = (entries: [id: string, name: string, description?: string][]): ServicesSection => ({
  servicesGeneration: 'test',
  items: Object.fromEntries(entries.map(([id, name, description = '']) => [id, {
    domain: id.split('.')[0], service: id.split('.')[1], name, description, target: null, response: 'none', fields: {},
  }])),
})

it.each(['TRN_ON', 'trn on', 'adjsts thr prprts'])('fuzzy-searches service ids, translated names and descriptions with %s', query => {
  const { host, change } = mount('climate.set_hvac_mode', { localize: true, services: serviceSection([
    ['climate.set_hvac_mode', 'Set HVAC mode'], ['light.turn_on', 'turn_on'],
  ]) })
  host.querySelector<HTMLButtonElement>('button[aria-label="Service"]')!.click()
  searchServices(host, query)
  expect(resultIds(host)).toEqual(['light.turn_on'])
  expect(change).not.toHaveBeenCalled()
})

it('ranks service matches for keyboard selection and restores catalog order for whitespace', () => {
  const { host, change } = mount('missing.old', { services: serviceSection([
    ['script.a_long', 'Turn slowly on'], ['script.b_near', 'Turn on lights'], ['script.z_exact', 'Turn on'],
  ]) })
  const trigger = host.querySelector<HTMLButtonElement>('button[aria-label="Service"]')!
  trigger.click()
  const original = ['script.a_long', 'script.b_near', 'script.z_exact']
  expect(resultIds(host)).toEqual(original)
  const search = searchServices(host, ' TURN ON ')
  expect(resultIds(host)).toEqual(['script.z_exact', 'script.b_near', 'script.a_long'])
  expect(change).not.toHaveBeenCalled()
  searchServices(host, '   ')
  expect(resultIds(host)).toEqual(original)
  searchServices(host, 'turn on')
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(change).toHaveBeenCalledWith('script.z_exact')
})

it('updates fuzzy results when the service catalog changes and resets keyboard navigation', () => {
  const host = document.createElement('div'); document.body.append(host)
  const [services, setServices] = createSignal(serviceSection([['light.old', 'Hall Lamp'], ['switch.old', 'Hall Lamp']]))
  const change = vi.fn()
  dispose = render(() => <HassCtx.Provider value={{ callWS: vi.fn() } as any}>
    <ServicePicker value="missing.old" services={services()} onChange={change} />
  </HassCtx.Provider>, host)
  host.querySelector<HTMLButtonElement>('button[aria-label="Service"]')!.click()
  const search = searchServices(host, 'hll lmp')
  const domain = host.querySelector<HTMLSelectElement>('select')!
  domain.value = 'light'; domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(resultIds(host)).toEqual(['light.old'])
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  setServices(serviceSection([['light.new', 'Hall Lamp'], ['switch.new', 'Hall Lamp']]))
  expect(resultIds(host)).toEqual(['light.new'])
  expect(search.getAttribute('aria-activedescendant')).toBeNull()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(change).not.toHaveBeenCalled()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(change).toHaveBeenCalledWith('light.new')
})

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
