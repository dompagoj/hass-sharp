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
  input(host, 'Next destination', 'service-1')
  button(host, 'Connect Next').click()
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
const focusNode = (host: Element, id: string) => host.querySelector(`[data-id="${id}"]`)!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
it('authors condition false and error branches from distinct typed inspector rows and undoes them', () => {
 const host=root();dispose=render(()=><VisualEditor name="Heating"/>,host)
 button(host,'Add step').click();button(host,'Create Delay').click();input(host,'Node label','Otherwise')
 button(host,'Add step').click();button(host,'Create Call service').click();input(host,'Node label','Recover')
 const draft=json(host);focusNode(host,'condition-1')
 expect(host.querySelector('[aria-label="If true destination"]')).toBeNull()
 input(host,'If false destination',draft.nodes.find((n:any)=>n.label==='Otherwise').id);button(host,'Connect If false').click()
 input(host,'On error destination',draft.nodes.find((n:any)=>n.label==='Recover').id);button(host,'Connect On error').click()
 expect(json(host).edges.filter((e:any)=>e.source==='condition-1').map((e:any)=>e.port)).toEqual(['true','false','error'])
 button(host,'Undo').click();expect(json(host).edges.filter((e:any)=>e.source==='condition-1').map((e:any)=>e.port)).toEqual(['true','false'])
})
it('blocks a second outgoing execution edge on a used inspector port',()=>{
 const host=root();dispose=render(()=><VisualEditor name="Heating"/>,host)
 button(host,'Add step').click();button(host,'Create Delay').click();const wait=json(host).nodes.at(-1).id;focusNode(host,'service-1')
 input(host,'Next destination',wait);button(host,'Connect Next').click()
 expect(host.querySelector('[aria-label="Next destination"]')).toBeNull()
 expect(host.querySelector('[aria-label="On error destination"]')).not.toBeNull()
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
  inspector.querySelector<HTMLButtonElement>('[aria-label="Entity ID"]')!.click()
  const field = inspector.querySelector<HTMLInputElement>('[aria-label="Search entities"]')!
  field.focus(); input(inspector, 'Search entities', 'new')
  await vi.waitFor(() => expect(host.querySelector('[role="option"]')).not.toBeNull())
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(json(host).nodes[0].config.entity).toBe('sensor.fresh')
})
