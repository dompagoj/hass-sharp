import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { ServicePicker } from '../src/components/ServicePicker'
let dispose:()=>void; let client:QueryClient
afterEach(()=>{dispose?.();client?.clear();document.body.innerHTML=''})
it('fetches live core metadata on opening and reopening, searches descriptions and returns exact service IDs',async()=>{
 const host=document.createElement('div');document.body.append(host)
 let fail=false
 const callWS=vi.fn(async()=>{if(fail)throw Error('offline');return {light:{turn_on:{name:'Illuminate',description:'Brighten the room'}},climate:{set_temperature:{name:'Temperature',description:'Warm the room'}}}})
 const change=vi.fn();client=new QueryClient()
 dispose=render(()=><QueryClientProvider client={client}><HassCtx.Provider value={{callWS} as any}><ServicePicker value="light.unavailable" onChange={change}/></HassCtx.Provider></QueryClientProvider>,host)
 host.querySelector<HTMLButtonElement>('button')!.click()
 await vi.waitFor(()=>expect(host.querySelectorAll('[role="option"]')).toHaveLength(2))
 expect(callWS).toHaveBeenCalledWith({type:'get_services'})
 const search=host.querySelector<HTMLInputElement>('[aria-label="Search services"]')!;search.value='brighten';search.dispatchEvent(new Event('input',{bubbles:true}))
 expect(host.querySelectorAll('[role="option"]')).toHaveLength(1)
 host.querySelector<HTMLButtonElement>('[role="option"]')!.click();expect(change).toHaveBeenCalledWith('light.turn_on')
 fail=true;host.querySelector<HTMLButtonElement>('button')!.click()
 await vi.waitFor(()=>expect(host.querySelector('[role="alert"]')).not.toBeNull())
 fail=false;[...host.querySelectorAll<HTMLButtonElement>('button')].find(b=>b.textContent==='Retry')!.click()
 await vi.waitFor(()=>expect(host.querySelector('[role="alert"]')).toBeNull())
 expect(callWS.mock.calls.length).toBeGreaterThanOrEqual(3)
})
it('Escape preserves value, returns focus and does not reopen or refetch',async()=>{
 const host=document.createElement('div');document.body.append(host)
 const callWS=vi.fn(async()=>({light:{turn_on:{name:'On'}}}));const change=vi.fn();client=new QueryClient()
 dispose=render(()=><QueryClientProvider client={client}><HassCtx.Provider value={{callWS} as any}><ServicePicker value="light.turn_on" onChange={change}/></HassCtx.Provider></QueryClientProvider>,host)
 const trigger=host.querySelector<HTMLButtonElement>('button')!;trigger.focus();trigger.click()
 await vi.waitFor(()=>expect(host.querySelector('[role="option"]')).not.toBeNull())
 const calls=callWS.mock.calls.length
 host.querySelector('dialog')!.dispatchEvent(new Event('cancel',{cancelable:true}))
 await Promise.resolve()
 expect(host.querySelector('dialog[open]')).toBeNull()
 expect(document.activeElement).toBe(trigger)
 expect(callWS).toHaveBeenCalledTimes(calls)
 expect(change).not.toHaveBeenCalled()
})
