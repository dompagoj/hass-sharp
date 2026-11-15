import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityAutocomplete } from '../src/components/EntityAutocomplete'
import type { ComponentProps } from 'solid-js'
import type { EntityInfo } from '../src/entities'
import { createRegistry, entityQueryKey } from '../src/registry'
let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })

const entityInfo = (entity_id: string, name: string, area_id = 'hall'): EntityInfo => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id, device_id: null, device_class: null,
  supported_features: 0, unit: null, icon: null, disabled: false, state_options: null,
})
function mountEntities(entities: EntityInfo[], props: ComponentProps<typeof EntityAutocomplete> = {}) {
  const host = document.createElement('div'); document.body.append(host)
  client = new QueryClient()
  client.setQueryData(entityQueryKey, createRegistry({ entities }))
  const callWS = vi.fn(async () => ({ entities, areas: [], devices: [], floors: [], labels: [] }))
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS } as any}>
    <EntityAutocomplete {...props} />
  </HassCtx.Provider></QueryClientProvider>, host)
  host.querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
  return host
}
const resultIds = (host: Element) => [...host.querySelectorAll('[role="option"] small:first-of-type')].map(item => item.textContent)
function searchEntities(host: Element, query: string) {
  const search = host.querySelector<HTMLInputElement>('input[aria-label="Search entities"]')!
  search.value = query; search.dispatchEvent(new Event('input', { bubbles: true }))
  return search
}

it.each(['hll lmp', 'LGHT.HL'])('finds entity names and ids with the abbreviation %s', query => {
  const host = mountEntities([entityInfo('sensor.temperature', 'Temperature'), entityInfo('light.hall', 'Hall Lamp')])
  searchEntities(host, query)
  expect(resultIds(host)).toEqual(['light.hall'])
})

it('ranks the closest entity first, keeps blank catalog order and only selects on keyboard confirmation', async () => {
  const [value, setValue] = createSignal('missing.old')
  const host = mountEntities([
    entityInfo('light.long', 'Hallway Lamp'), entityInfo('light.near', 'Hall Lamps'), entityInfo('light.exact', 'Hall Lamp'),
  ], { get value() { return value() }, onChange: setValue })
  const original = ['light.long', 'light.near', 'light.exact']
  expect(resultIds(host)).toEqual(original)
  const search = searchEntities(host, ' HALL LAMP ')
  expect(resultIds(host)).toEqual(['light.exact', 'light.near', 'light.long'])
  expect(value()).toBe('missing.old')
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(value()).toBe('light.exact')
  const trigger = host.querySelector<HTMLButtonElement>('.hs-picker-field')!
  trigger.focus(); trigger.click()
  searchEntities(host, '   ')
  expect(resultIds(host)).toEqual(original)
  searchEntities(host, 'hll lmp').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  await Promise.resolve()
  expect(host.querySelector('dialog[open]')).toBeNull()
  expect(document.activeElement).toBe(trigger)
  expect(value()).toBe('light.exact')
})

it('applies reactive entity predicates and domains before fuzzy search and resets navigation after catalog changes', async () => {
  const [area, setArea] = createSignal('hall')
  const change = vi.fn()
  const host = mountEntities([
    entityInfo('light.hall', 'Hall Lamp'), entityInfo('sensor.hall', 'Hall Lamp'), entityInfo('light.den', 'Hall Lamp', 'den'),
  ], { filter: entity => entity.area_id === area(), onChange: change })
  const search = searchEntities(host, 'hll lmp')
  expect(resultIds(host)).toEqual(['light.hall', 'sensor.hall'])
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  expect(search.getAttribute('aria-activedescendant')).toBeTruthy()
  const domain = host.querySelector<HTMLSelectElement>('select')!
  domain.value = 'light'; domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(resultIds(host)).toEqual(['light.hall'])
  expect(search.getAttribute('aria-activedescendant')).toBeNull()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  setArea('den')
  expect(resultIds(host)).toEqual(['light.den'])
  expect(search.getAttribute('aria-activedescendant')).toBeNull()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  client.setQueryData(entityQueryKey, createRegistry({ entities: [entityInfo('light.new', 'Hall Lamp', 'den'), entityInfo('sensor.new', 'Hall Lamp', 'den')] }))
  await vi.waitFor(() => expect(resultIds(host)).toEqual(['light.new']))
  expect(search.getAttribute('aria-activedescendant')).toBeNull()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(change).not.toHaveBeenCalled()
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(change).toHaveBeenCalledWith('light.new')
})

it('searches and filters in a dialog without mutating selection until keyboard confirmation', async () => {
 const host = document.createElement('div'); document.body.append(host)
 const [value,setValue] = createSignal('missing.old')
 const entity = (entity_id: string, name: string | null) => ({entity_id,name,domain:entity_id.split('.')[0],area_id:'hall',device_id:null,device_class:null,supported_features:0,unit:null,icon:null,disabled:false,state_options:null})
 const callWS = vi.fn(async () => ({entities:[entity('light.hall','Hall Lamp'),entity('sensor.hall','Hall Meter'),entity('sensor.raw',null)],areas:[],devices:[],floors:[],labels:[]}))
 client = new QueryClient()
 dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{callWS} as any}><EntityAutocomplete value={value()} onChange={setValue}/></HassCtx.Provider></QueryClientProvider>,host)
 host.querySelector<HTMLButtonElement>('button')!.click()
 await vi.waitFor(() => expect(host.querySelectorAll('[role="option"]')).toHaveLength(3))
 expect(host.querySelector('[role="option"]')!.textContent).toContain('Hall Lamp')
 const search = host.querySelector<HTMLInputElement>('input[aria-label="Search entities"]')!
 search.value='hall'; search.dispatchEvent(new Event('input',{bubbles:true}))
 expect(value()).toBe('missing.old')
 const domain=host.querySelector<HTMLSelectElement>('select')!; domain.value='light';domain.dispatchEvent(new Event('change',{bubbles:true}))
 expect(host.querySelectorAll('[role="option"]')).toHaveLength(1)
 search.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}))
 search.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}))
 expect(value()).toBe('light.hall')
 host.querySelector<HTMLButtonElement>('button')!.click()
 search.value='unavailable.custom';search.dispatchEvent(new Event('input',{bubbles:true}))
 host.querySelector<HTMLButtonElement>('[aria-label="Close dialog"]')!.click()
 expect(value()).toBe('light.hall')
})

it('draws the first 200 matches with a line to refine the search, and asks for the entities again only when they are old', async () => {
  vi.useFakeTimers({ toFake: ['Date'] })
  try {
    const host = document.createElement('div'); document.body.append(host)
    const entity = (i: number) => ({ entity_id: `sensor.room_${i}`, name: `Room ${i}`, domain: 'sensor', area_id: null, device_id: null, device_class: null, supported_features: 0, unit: null, icon: null, disabled: false, state_options: null })
    const callWS = vi.fn(async () => ({ entities: Array.from({ length: 500 }, (_, i) => entity(i)), areas: [], devices: [], floors: [], labels: [] }))
    client = new QueryClient()
    dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS } as any}><EntityAutocomplete value="" onChange={() => {}} /></HassCtx.Provider></QueryClientProvider>, host)
    const open = () => host.querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
    const close = () => host.querySelector<HTMLButtonElement>('[aria-label="Close dialog"]')!.click()
    const options = () => host.querySelectorAll('[role="option"]')
    const more = () => host.querySelector('.hs-picker-more')?.textContent
    open()
    await vi.waitFor(() => expect(options()).toHaveLength(200))
    expect(more()).toBe('Showing the first 200 of 500 matches. Refine the search to find others.')
    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search entities"]')!
    search.value = 'room_4'; search.dispatchEvent(new Event('input', { bubbles: true }))
    const ids = [...options()].map(option => option.querySelector('small')!.textContent)
    expect(ids[0]).toBe('sensor.room_4')
    expect(ids).toContain('sensor.room_14')
    expect(more()).toBeUndefined()
    close()
    // Open again soon after: the entities already here are used.
    open()
    expect(callWS).toHaveBeenCalledTimes(1)
    const first = options()[0]
    close()
    // Later they are asked for again; the same answer keeps the list (and every row) as it was.
    vi.setSystemTime(Date.now() + 61_000)
    open()
    await vi.waitFor(() => expect(callWS).toHaveBeenCalledTimes(2))
    await vi.waitFor(() => expect(client.isFetching()).toBe(0))
    expect(options()[0]).toBe(first)
  } finally { vi.useRealTimers() }
})

it('always draws what is selected, also beyond the first 200 matches, so it can be unticked', async () => {
  const host = document.createElement('div'); document.body.append(host)
  const entity = (i: number) => ({ entity_id: `sensor.room_${i}`, name: `Room ${i}`, domain: 'sensor', area_id: null, device_id: null, device_class: null, supported_features: 0, unit: null, icon: null, disabled: false, state_options: null })
  const callWS = vi.fn(async () => ({ entities: Array.from({ length: 500 }, (_, i) => entity(i)), areas: [], devices: [], floors: [], labels: [] }))
  const [values, setValues] = createSignal(['sensor.room_3', 'sensor.room_450'])
  client = new QueryClient()
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS } as any}>
    <EntityAutocomplete multiple values={values()} onChangeValues={setValues} />
  </HassCtx.Provider></QueryClientProvider>, host)
  host.querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
  const options = () => [...host.querySelectorAll<HTMLElement>('[role="option"]')]
  await vi.waitFor(() => expect(options().length).toBeGreaterThan(200))
  // The first 200, then the selected one after them.
  expect(options()).toHaveLength(201)
  searchEntities(host, 'rm_')
  expect(options()).toHaveLength(201)
  expect(host.querySelector('.hs-picker-more')?.textContent).toContain('200 of 500 matches')
  const far = options().at(-1)!
  expect(far.textContent).toContain('sensor.room_450')
  expect(far.getAttribute('aria-selected')).toBe('true')
  far.click()
  expect(values()).toEqual(['sensor.room_3'])
})
