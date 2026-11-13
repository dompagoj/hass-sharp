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
function input(root: Element, label: string, value: string) {
  const control = root.querySelector<HTMLInputElement>(`[aria-label="${label}"]`) ?? root.querySelector<HTMLInputElement>(`#${[...root.querySelectorAll('label')].find(l => l.textContent === label)?.htmlFor}`)
  expect(control, label).not.toBeNull(); control!.value = value; control!.dispatchEvent(new Event('change', { bubbles: true })); control!.dispatchEvent(new Event('input', { bubbles: true }))
}

it('uses keyboard history only outside text controls, and keeps JSON available', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  button(host, 'Add step').click(); button(host, 'Create Delay').click()
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }))
  expect(label.value).toBe('Delay')
  host.querySelector('[aria-label="Visual automation editor"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }))
  expect(button(host, 'Redo').disabled).toBe(false)
  button(host, 'Graph JSON').click()
  expect(host.querySelector('pre[aria-label="Graph JSON"]')?.textContent).toContain('"version": 5')
})
it('updates the draft on input before blur and warns before the page is closed', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.value = 'Door opens'; label.dispatchEvent(new Event('input', { bubbles: true }))
  button(host, 'Graph JSON').click()
  expect(host.querySelector('pre')?.textContent).toContain('Door opens')
  const closing = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(closing)
  expect(closing.defaultPrevented).toBe(true)
})
it('keeps canvas keyboard shortcuts inside the Home Assistant panel', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const shortcut = vi.fn(); window.addEventListener('keydown', shortcut)
  try {
    host.querySelector('[data-id="trigger-1"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'c', bubbles: true }))
    expect(shortcut).not.toHaveBeenCalled()
  } finally { window.removeEventListener('keydown', shortcut) }
})
it('blocks Home Assistant native ancestor shortcuts while keeping node keyboard movement', () => {
  const host = root(); const ancestor = vi.fn(); host.addEventListener('keydown', ancestor)
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  node.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))
  node.dispatchEvent(new KeyboardEvent('keyup', { key: 'ArrowRight', bubbles: true }))
  expect(ancestor).not.toHaveBeenCalled()
  button(host, 'Graph JSON').click()
  expect(JSON.parse(host.querySelector('pre')!.textContent!).nodes[0].position.x).toBeGreaterThan(60)
})
const json = (host: Element) => { button(host, 'Graph JSON').click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) }
const focusNode = (host: Element, id: string) => host.querySelector(`[data-id="${id}"]`)!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
function dragEvent(type: string, data: DataTransfer, clientX = 0, clientY = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY })
  Object.defineProperty(event, 'dataTransfer', { value: data })
  return event
}

it('opens visual help with node examples without changing the graph', () => {
  const host = root(); dispose = render(() => <VisualEditor name="Help" />, host)
  const help = host.querySelector<HTMLButtonElement>('button[aria-label="Visual automation help"]')
  expect(help).not.toBeNull()
  help!.click()
  expect(host.querySelector('dialog[open]')?.textContent).toContain('Build and connect steps')
  expect(button(host, 'Undo').disabled).toBe(true)
})

it('fetches current entity suggestions when reopening an editor with a fresh cache', async () => {
  const host = root()
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  // The shared registry query (src/registry.ts) caches the whole get_entities reply.
  client.setQueryData(['hass-entities'], { entities: [{ entity_id: 'sensor.old' }], areas: [], devices: [], floors: [], labels: [] })
  const fixture = visualFixture()
  const callWS = vi.fn((message: any) => message.type === 'hass_sharp/get_entities'
    ? Promise.resolve({ entities: [{ entity_id: 'sensor.new' }] }) : fixture.callWS(message))
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, callWS } as any}>
    <RealVisualEditor name="Reopened" />
  </HassCtx.Provider></QueryClientProvider>, host)
  await vi.waitFor(() => expect((client.getQueryData(['hass-entities']) as any)?.entities).toEqual([{ entity_id: 'sensor.new' }]))
  expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/get_entities' })
})
it('keeps advanced C# collapsed by default and retains helpers across collapse', () => {
 const host=root();dispose=render(()=><VisualEditor name="Advanced"/>,host)
 const section=host.querySelector<HTMLDetailsElement>('[aria-label="Graph C# sections"]')!
 expect(section.open).toBe(false)
 section.open=true;section.dispatchEvent(new Event('toggle'))
 input(host,'C# members','private int Count = 4;')
 section.open=false;section.dispatchEvent(new Event('toggle'))
 section.open=true;section.dispatchEvent(new Event('toggle'))
 expect(host.querySelector<HTMLTextAreaElement>('[aria-label="C# members"]')!.value).toBe('private int Count = 4;')
 expect(json(host).members).toBe('private int Count = 4;')
})
it('reveals and focuses Advanced C# when selecting an imports or members diagnostic', async () => {
 const host=root();const fixture=visualFixture()
 fixture.controls.preview={valid:false,source:'using Broken;\nclass VisualAutomation {\n invalid;\n}',sourceMap:[{nodeId:'$imports',startLine:1,endLine:1},{nodeId:'$members',startLine:3,endLine:3}],diagnostics:[{nodeId:'$imports',message:'Broken import',severity:3,startLine:1},{nodeId:'$members',message:'Broken member',severity:3,startLine:3}]}
 dispose=render(()=><QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={fixture as any}><RealVisualEditor name="Diagnostics"/></HassCtx.Provider></QueryClientProvider>,host)
 await vi.waitFor(()=>expect(host.querySelector('[aria-label="Graph diagnostics"]')?.textContent).toContain('Broken import'))
 const section=host.querySelector<HTMLDetailsElement>('[aria-label="Graph C# sections"]')!
 for (const [message,label] of [['Broken import · line 1','C# imports'],['Broken member · line 3','C# members']]) {
  section.open=false;section.dispatchEvent(new Event('toggle'))
  button(host,message).click();await Promise.resolve()
  expect(section.open).toBe(true)
  expect(document.activeElement).toBe(host.querySelector(`[aria-label="${label}"]`))
 }
})
