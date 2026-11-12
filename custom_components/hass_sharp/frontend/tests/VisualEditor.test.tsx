import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { createGraph, updateNode } from '../src/visual/graph'

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

it('renders same-count edits, resizing and undo positions through the real adapter', async () => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  await vi.waitFor(() => expect(host.textContent).toContain('Entity changes'))
  setGraph(updateNode(graph(), 'trigger-1', { label: 'Door opens', position: { x: 120, y: 80 }, size: { width: 300, height: 160 } }))
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  await vi.waitFor(() => expect(node.textContent).toContain('Door opens'))
  expect(node.style.transform).toContain('120px, 80px')
  expect(node.style.width).toBe('300px')
  setGraph(updateNode(graph(), 'trigger-1', { position: { x: 60, y: 100 } }))
  expect(node.style.transform).toContain('60px, 100px')
})

it('offers palette, inspector, connection editing and undo/redo while requesting a server preview', async () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  button(host, 'Add Wait').click()
  await vi.waitFor(() => expect(host.querySelector('input[aria-label="Node label"]')?.getAttribute('value') ?? (host.querySelector('input[aria-label="Node label"]') as HTMLInputElement)?.value).toBe('Wait'))
  input(host, 'Node label', 'Wait for warmth')
  input(host, 'Seconds', '90')
  button(host, 'Undo').click()
  expect((host.querySelector('input[aria-label="Seconds"]') as HTMLInputElement).value).toBe('60')
  button(host, 'Redo').click()
  expect((host.querySelector('input[aria-label="Seconds"]') as HTMLInputElement).value).toBe('90')
  input(host, 'Connect to', 'service-1')
  button(host, 'Connect').click()
  expect(host.querySelector('[aria-label="Connections"]')?.textContent).toContain('Wait for warmth → Call service')
  button(host, 'Remove Wait for warmth → Call service').click()
  expect(host.querySelector('[aria-label="Connections"]')?.textContent).not.toContain('Wait for warmth → Call service')
  expect(host.textContent).toContain('Not saved')
  expect(host.textContent).toContain('Generated C#')
  expect(host.textContent).not.toContain('SAMPLE ONLY')
})

it('uses keyboard history only outside text controls, and keeps JSON available', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  button(host, 'Add Wait').click()
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }))
  expect(label.value).toBe('Wait')
  host.querySelector('[aria-label="Visual automation editor"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }))
  expect(button(host, 'Redo').disabled).toBe(false)
  button(host, 'Graph JSON').click()
  expect(host.querySelector('pre[aria-label="Graph JSON"]')?.textContent).toContain('"version": 2')
})
it('commits a completed mouse drag into graph JSON and restores it through undo', async () => {
  const host = root()
  let changed = createGraph('Heating')
  const [graph, setGraph] = createSignal(changed)
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={next => { changed = next; setGraph(next) }} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const before = JSON.stringify(graph())
  node.dispatchEvent(new MouseEvent('mousedown', { clientX: 80, clientY: 110, button: 0, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 140, clientY: 140, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 200, clientY: 170, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mouseup', { clientX: 200, clientY: 170, button: 0, bubbles: true, view: window }))
  await vi.waitFor(() => expect(changed.nodes[0].position).not.toEqual({ x: 60, y: 100 }))
  expect(JSON.parse(before).nodes[0].position).toEqual({ x: 60, y: 100 })
  setGraph(JSON.parse(before))
  expect(node.style.transform).toContain('60px, 100px')
})
it('commits keyboard node movement while leaving text input alone', async () => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  node.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))
  node.dispatchEvent(new KeyboardEvent('keyup', { key: 'ArrowRight', bubbles: true }))
  await vi.waitFor(() => expect(graph().nodes[0].position.x).toBeGreaterThan(60))
})
it('retains a single-touch drag in the versioned graph', async () => {
  const host = root()
  const descriptor = Object.getOwnPropertyDescriptor(navigator, 'maxTouchPoints')
  Object.defineProperty(navigator, 'maxTouchPoints', { configurable: true, value: 1 })
  try {
    const [graph, setGraph] = createSignal(createGraph('Heating'))
    dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
    const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
    const touch = (x: number, y: number) => new Touch({ identifier: 1, target: node, clientX: x, clientY: y })
    const send = (type: string, x: number, y: number) => node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: type === 'touchend' ? [] : [touch(x, y)], changedTouches: [touch(x, y)] }))
    send('touchstart', 80, 110); send('touchmove', 140, 140); send('touchmove', 200, 170); send('touchend', 200, 170)
    await vi.waitFor(() => expect(graph().nodes[0].position.x).toBeGreaterThan(60))
  } finally {
    if (descriptor) Object.defineProperty(navigator, 'maxTouchPoints', descriptor)
    else Reflect.deleteProperty(navigator, 'maxTouchPoints')
  }
})
it('writes resize-handle changes back to JSON', async () => {
  const width = vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.width) || 800 })
  const height = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.height) || 570 })
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  await new Promise(resolve => setTimeout(resolve, 100)) // allow the library's queued layout measurement
  const handle = host.querySelector<HTMLElement>('[data-id="trigger-1"] .solid-flow__resize-control.bottom.right.handle')!
  expect(handle).not.toBeNull()
  handle.dispatchEvent(new MouseEvent('mousedown', { clientX: 200, clientY: 200, button: 0, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 260, clientY: 240, buttons: 1, bubbles: true, view: window }))
  expect(parseFloat(host.querySelector<HTMLElement>('[data-id="trigger-1"]')!.style.width)).toBeGreaterThan(220)
  window.dispatchEvent(new MouseEvent('mouseup', { clientX: 260, clientY: 240, button: 0, bubbles: true, view: window }))
  const resized = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  expect(parseFloat(resized.style.width)).toBeGreaterThan(220)
  await vi.waitFor(() => expect(graph().nodes[0].size.width).toBeGreaterThan(220))
  width.mockRestore(); height.mockRestore()
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
it('selects the owning node when a connection handle receives focus', () => {
  const host = root(); const select = vi.fn()
  dispose = render(() => <FlowAdapter graph={createGraph('Heating')} onSelect={select} onChange={() => {}} />, host)
  host.querySelector('[data-id="trigger-1"] .solid-flow__handle')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  expect(select).toHaveBeenLastCalledWith('trigger-1')
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
it('never deletes a different node when Delete is pressed on a canvas edge', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.width) || 800 })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.height) || 570 })
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const edge = host.querySelector('.solid-flow__edge')!
  edge.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, cancelable: true }))
  button(host, 'Graph JSON').click()
  const graph = JSON.parse(host.querySelector('pre')!.textContent!)
  expect(graph.nodes).toHaveLength(3)
  expect(graph.edges).toHaveLength(1)
})
it('retains movement when a second finger interrupts a touch drag', async () => {
  vi.spyOn(navigator, 'maxTouchPoints', 'get').mockReturnValue(1)
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const touch = (x: number, y: number, identifier = 1) => new Touch({ identifier, target: node, clientX: x, clientY: y })
  const send = (type: string, x: number, y: number, extra = false) => node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true,
    touches: type === 'touchend' ? [] : [touch(x, y), ...(extra ? [touch(x + 10, y + 10, 2)] : [])], changedTouches: [touch(x, y)] }))
  send('touchstart', 80, 110); send('touchmove', 140, 140); send('touchmove', 200, 170)
  send('touchmove', 210, 180, true); send('touchend', 210, 180)
  await vi.waitFor(() => expect(graph().nodes[0].position.x).toBeGreaterThan(60))
  expect(node.style.transform).toContain(`${graph().nodes[0].position.x}px`)
})
it('allows node resizing from the keyboard-accessible inspector and rejects invalid sizes', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  input(host, 'Width', '310'); input(host, 'Height', '170')
  button(host, 'Graph JSON').click()
  expect(JSON.parse(host.querySelector('pre')!.textContent!).nodes[0].size).toEqual({ width: 310, height: 170 })
  input(host, 'Width', '-10')
  expect(JSON.parse(host.querySelector('pre')!.textContent!).nodes[0].size.width).toBe(310)
})
