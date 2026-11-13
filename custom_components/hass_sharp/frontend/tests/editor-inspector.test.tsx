import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode } from '../src/visual/graph'

import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { visualFixture } from './visual-fixture'
const VisualEditor = (props: { name: string }) => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={ { ...visualFixture(), user: { is_admin: true } } as any}><RealVisualEditor {...props} /></HassCtx.Provider></QueryClientProvider>

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.(); vi.restoreAllMocks(); document.body.innerHTML = ''
  // D3 clears its click suppression on the next macrotask after a drag.
  await new Promise(resolve => setTimeout(resolve, 0))
})
function root() { const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root); return root }
function button(root: Element, label: string) {
  const result = [...root.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === label || button.getAttribute('aria-label') === label)
  expect(result, label).toBeDefined(); return result!
}
// Inspector fields first: node cards carry inline editors with the same accessible names.
function input(root: Element, label: string, value: string) {
  const control = root.querySelector<HTMLInputElement>(`.hs-inspector [aria-label="${label}"]`) ?? root.querySelector<HTMLInputElement>(`[aria-label="${label}"]`) ?? root.querySelector<HTMLInputElement>(`#${[...root.querySelectorAll('label')].find(l => l.textContent === label)?.htmlFor}`)
  expect(control, label).not.toBeNull(); control!.value = value; control!.dispatchEvent(new Event('change', { bubbles: true })); control!.dispatchEvent(new Event('input', { bubbles: true }))
  // Inspector text fields commit on blur.
  control!.dispatchEvent(new FocusEvent('blur'))
}

it('offers palette, inspector, connection editing and undo/redo while requesting a server preview', async () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  button(host, 'Add step').click(); button(host, 'Create Delay').click()
  await vi.waitFor(() => expect(host.querySelector('input[aria-label="Node label"]')?.getAttribute('value') ?? (host.querySelector('input[aria-label="Node label"]') as HTMLInputElement)?.value).toBe('Delay'))
  input(host, 'Node label', 'Wait for warmth')
  input(host, 'Seconds', '90')
  button(host, 'Undo').click()
  expect((host.querySelector('.hs-inspector input[aria-label="Seconds"]') as HTMLInputElement).value).toBe('60')
  button(host, 'Redo').click()
  expect((host.querySelector('.hs-inspector input[aria-label="Seconds"]') as HTMLInputElement).value).toBe('90')
  // Wires are made on the canvas (here from the keyboard), never in the inspector.
  const wait = json(host).nodes.at(-1).id
  press(pin(host, wait, 'next')); press(pin(host, 'service-1', 'in'))
  expect(host.querySelector('[aria-label="Connections"]')?.textContent).toContain('Wait for warmth → Call service')
  button(host, 'Remove Wait for warmth → Call service').click()
  expect(host.querySelector('[aria-label="Connections"]')?.textContent).not.toContain('Wait for warmth → Call service')
  expect(host.textContent).toContain('Not saved')
  expect(host.textContent).toContain('Generated C#')
  expect(host.textContent).not.toContain('SAMPLE ONLY')
})

it('has no node size fields: cards are sized by their content (D14)', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  expect(host.querySelector('[aria-label="Appearance"]')).toBeNull()
  expect(host.querySelector('[aria-label="Width"]')).toBeNull()
  button(host, 'Add step').click(); button(host, 'Create Delay').click()
  expect(json(host).nodes.some((node: any) => 'size' in node)).toBe(false)
  expect(host.querySelector('.solid-flow__resize-control')).toBeNull()
})

const json = (host: Element) => { button(host, 'Graph JSON').click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) }
const press = (target: Element) => target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
const pin = (host: Element, id: string, handle: string) => host.querySelector(`[data-id="${id}"] [data-handleid="${handle}"]`)!
const focusNode = (host: Element, id: string) => host.querySelector(`[data-id="${id}"]`)!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
it('wires condition false and error branches on the canvas, while the inspector offers no destination', () => {
 const host=root();dispose=render(()=><VisualEditor name="Heating"/>,host)
 button(host,'Add step').click();button(host,'Create Delay').click();input(host,'Node label','Otherwise')
 button(host,'Add step').click();button(host,'Create Call service').click();input(host,'Node label','Recover')
 const draft=json(host);focusNode(host,'condition-1')
 const inspector=host.querySelector('.hs-inspector')!
 expect(inspector.querySelector('[aria-label$="destination"]')).toBeNull()
 expect([...inspector.querySelectorAll('button')].some(b=>/connect/i.test(b.textContent ?? ''))).toBe(false)
 press(pin(host,'condition-1','false'));press(pin(host,draft.nodes.find((n:any)=>n.label==='Otherwise').id,'in'))
 press(pin(host,'condition-1','error'));press(pin(host,draft.nodes.find((n:any)=>n.label==='Recover').id,'in'))
 expect(json(host).edges.filter((e:any)=>e.source==='condition-1').map((e:any)=>e.port)).toEqual(['true','false','error'])
 button(host,'Undo').click();expect(json(host).edges.filter((e:any)=>e.source==='condition-1').map((e:any)=>e.port)).toEqual(['true','false'])
})
it('shows a wire selected on the canvas read only in the inspector', async () => {
 vi.spyOn(HTMLElement.prototype,'offsetWidth','get').mockReturnValue(800);vi.spyOn(HTMLElement.prototype,'offsetHeight','get').mockReturnValue(570)
 const host=root();dispose=render(()=><VisualEditor name="Heating"/>,host)
 const edge=await vi.waitFor(()=>{const found=[...host.querySelectorAll<HTMLElement>('.solid-flow__edge')].find(e=>e.getAttribute('data-id')==='trigger-1->condition-1');expect(found).toBeDefined();return found!})
 edge.dispatchEvent(new FocusEvent('focusin',{bubbles:true}))
 const wire=await vi.waitFor(()=>{const found=host.querySelector('.hs-inspector .hs-insp-wire');expect(found).not.toBeNull();return found!})
 expect(wire.textContent).toContain('Entity changes')
 expect(wire.textContent).toContain('State condition')
 expect(wire.querySelector('input, select, textarea')).toBeNull()
 // Its ends select their steps.
 wire.querySelectorAll<HTMLButtonElement>('.hs-insp-wire__end')[1].click()
 await vi.waitFor(()=>expect(host.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Node label"]')?.placeholder).toBe('State condition'))
})
it('palette clicks describe configuration and connections without editing history', () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const before = json(host)
  button(host, 'About State condition').click()
  const inspector = host.querySelector('[aria-label="Node inspector"]')!
  expect(inspector.textContent).toContain('Branch on an entity state')
  expect(inspector.textContent).toContain('If true')
  expect(inspector.textContent).toContain('If false')
  expect(inspector.textContent).toContain('On error')
  expect(inspector.querySelector('input')).toBeNull()
  expect(json(host)).toEqual(before)
  expect(button(host, 'Undo').disabled).toBe(true)
})
function dragEvent(type: string, data: DataTransfer, clientX = 0, clientY = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY })
  Object.defineProperty(event, 'dataTransfer', { value: data })
  return event
}

it('entity picker keyboard selection works inside the editor shortcut boundary', async () => {
  const host = root()
  const fixture = visualFixture()
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ ...fixture,
    callWS: (message: any) => message.type === 'hass_sharp/get_entities' ? Promise.resolve({ entities: [{ entity_id: 'sensor.fresh', name: 'New sensor' }] }) : fixture.callWS(message),
  } as any}><RealVisualEditor name="Keys" /></HassCtx.Provider></QueryClientProvider>, host)
  const inspector = host.querySelector('.hs-inspector')!
  inspector.querySelector<HTMLButtonElement>('[data-input-id="entity"] [aria-label="Entity"]')!.click()
  const field = inspector.querySelector<HTMLInputElement>('[aria-label="Search entities"]')!
  field.focus(); input(inspector, 'Search entities', 'new')
  await vi.waitFor(() => expect(host.querySelector('[role="option"]')).not.toBeNull())
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(json(host).nodes[0].config.entity).toBe('sensor.fresh')
})
