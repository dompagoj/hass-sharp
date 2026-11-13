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
