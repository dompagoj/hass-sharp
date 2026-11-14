import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityAutocomplete } from '../src/components/EntityAutocomplete'
let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
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
    expect(options()).toHaveLength(111)
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
  const far = options().at(-1)!
  expect(far.textContent).toContain('sensor.room_450')
  expect(far.getAttribute('aria-selected')).toBe('true')
  far.click()
  expect(values()).toEqual(['sensor.room_3'])
})
