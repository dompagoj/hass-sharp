import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { createGraph, createGraphHistory, updateNode } from '../src/visual/graph'

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
  // The library establishes its origin on the first move past its drag threshold.
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 83, clientY: 113, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 143, clientY: 143, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mousemove', { clientX: 203, clientY: 173, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mouseup', { clientX: 203, clientY: 173, button: 0, bubbles: true, view: window }))
  await vi.waitFor(() => expect(changed.nodes[0].position).toEqual({ x: 220, y: 180 }))
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
    await vi.waitFor(() => expect(graph().nodes[0].position).toEqual({ x: 220, y: 180 }))
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
  await vi.waitFor(() => expect(graph().nodes[0].position).toEqual({ x: 220, y: 180 }))
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


it('commits each complete touch gesture once, survives selection/diagnostic updates and supports undo/redo', async () => {
  vi.spyOn(navigator, 'maxTouchPoints', 'get').mockReturnValue(1)
  const host = root()
  const history = createGraphHistory(createGraph('Heating'))
  const [selectedId, setSelectedId] = createSignal('condition-1')
  const [diagnostics, setDiagnostics] = createSignal<any[]>([])
  const commits: unknown[] = []
  dispose = render(() => <FlowAdapter graph={history.graph()} selectedId={selectedId()} diagnostics={diagnostics()} onSelect={setSelectedId}
    onChange={next => { commits.push(next); history.commit(next) }} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const send = (type: string, x: number, y: number) => {
    const touch = new Touch({ identifier: 1, target: node, clientX: x, clientY: y })
    node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: type === 'touchend' ? [] : [touch], changedTouches: [touch] }))
  }
  send('touchstart', 80, 110); send('touchmove', 140, 140)
  expect(selectedId()).toBe('trigger-1')
  expect(node.style.transform).toContain('140px, 140px')
  setDiagnostics([{ nodeId: 'trigger-1', message: 'Warning', severity: 2, startLine: null, startColumn: null }])
  expect(node.style.transform).toContain('140px, 140px')
  send('touchmove', 200, 170); send('touchend', 200, 170)
  await vi.waitFor(() => expect(history.graph().nodes[0].position).toEqual({ x: 220, y: 180 }))
  expect(commits).toHaveLength(1)
  history.undo(); expect(history.graph().nodes[0].position).toEqual({ x: 60, y: 100 })
  expect(history.canUndo()).toBe(false)
  history.redo(); expect(node.style.transform).toContain('220px, 180px')
  send('touchstart', 200, 170); send('touchmove', 230, 200); send('touchend', 230, 200)
  await vi.waitFor(() => expect(history.graph().nodes[0].position).toEqual({ x: 260, y: 220 }))
  expect(commits).toHaveLength(2)
})

it('keeps the last touch position on cancel, ignores remaining fingers and allows a fresh drag', async () => {
  vi.spyOn(navigator, 'maxTouchPoints', 'get').mockReturnValue(1)
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  const commits: unknown[] = []
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={next => { commits.push(next); setGraph(next) }} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const touch = (id: number, x: number) => new Touch({ identifier: id, target: node, clientX: x, clientY: 110 })
  const send = (type: string, touches: Touch[], changed = touches) => node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches, changedTouches: changed }))
  send('touchstart', [touch(1, 80)]); send('touchmove', [touch(1, 140)]); send('touchmove', [touch(1, 200)])
  send('touchstart', [touch(1, 200), touch(2, 220)], [touch(2, 220)])
  expect(graph().nodes[0].position).toEqual({ x: 220, y: 100 })
  send('touchend', [touch(1, 200)], [touch(2, 220)])
  send('touchmove', [touch(1, 260)]); send('touchend', [], [touch(1, 260)])
  expect(graph().nodes[0].position).toEqual({ x: 220, y: 100 })
  expect(commits).toHaveLength(1)
  send('touchstart', [touch(3, 200)]); send('touchmove', [touch(3, 230)]); send('touchcancel', [], [touch(3, 230)])
  expect(graph().nodes[0].position).toEqual({ x: 260, y: 100 })
  expect(commits).toHaveLength(2)
})

it('does not overwrite an external graph replacement when a touch gesture ends', () => {
  vi.spyOn(navigator, 'maxTouchPoints', 'get').mockReturnValue(1)
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  const commits: unknown[] = []
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={next => { commits.push(next); setGraph(next) }} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const send = (type: string, x: number) => {
    const touch = new Touch({ identifier: 1, target: node, clientX: x, clientY: 110 })
    node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: type === 'touchend' ? [] : [touch], changedTouches: [touch] }))
  }
  send('touchstart', 80); send('touchmove', 140)
  setGraph(updateNode(graph(), 'trigger-1', { position: { x: 300, y: 200 } }))
  send('touchmove', 200); send('touchend', 200)
  expect(commits).toHaveLength(0)
  expect(graph().nodes[0].position).toEqual({ x: 300, y: 200 })
  expect(node.style.transform).toContain('300px, 200px')
})

it('releases touch ownership when the last finger ends outside the canvas', () => {
  vi.spyOn(navigator, 'maxTouchPoints', 'get').mockReturnValue(1)
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const touch = (id: number, target: Element, x: number) => new Touch({ identifier: id, target, clientX: x, clientY: 110 })
  const send = (target: Element, type: string, touches: Touch[], changed: Touch[]) => target.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches, changedTouches: changed }))
  send(node, 'touchstart', [touch(1, node, 80)], [touch(1, node, 80)])
  send(node, 'touchmove', [touch(1, node, 140)], [touch(1, node, 140)])
  send(document.body, 'touchstart', [touch(1, node, 140), touch(2, document.body, 400)], [touch(2, document.body, 400)])
  send(node, 'touchend', [touch(2, document.body, 400)], [touch(1, node, 140)])
  send(document.body, 'touchend', [], [touch(2, document.body, 400)])
  send(node, 'touchstart', [touch(3, node, 140)], [touch(3, node, 140)])
  send(node, 'touchmove', [touch(3, node, 200)], [touch(3, node, 200)])
  send(node, 'touchend', [], [touch(3, node, 200)])
  expect(graph().nodes[0].position).toEqual({ x: 220, y: 100 })
})

it('allows a new touch drag after the touched node is removed externally', () => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, host)
  const first = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const send = (node: Element, type: string, x: number, id: number) => {
    const touch = new Touch({ identifier: id, target: node, clientX: x, clientY: 110 })
    node.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: type === 'touchend' ? [] : [touch], changedTouches: [touch] }))
  }
  send(first, 'touchstart', 80, 1); send(first, 'touchmove', 140, 1)
  setGraph({ ...graph(), nodes: graph().nodes.filter(n => n.id !== 'trigger-1'), edges: [] })
  expect(first.isConnected).toBe(false)
  send(first, 'touchend', 140, 1)
  const second = host.querySelector<HTMLElement>('[data-id="service-1"]')!
  send(second, 'touchstart', 200, 2); send(second, 'touchmove', 260, 2); send(second, 'touchend', 260, 2)
  expect(graph().nodes.find(n => n.id === 'service-1')?.position).toEqual({ x: 740, y: 100 })
})

it.each(['touchend', 'touchcancel'])('keeps the primary drag active when an unrelated finger emits %s', type => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  const commits: unknown[] = []
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={next => { commits.push(next); setGraph(next) }} />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  const touch = (id: number, target: Element, x: number) => new Touch({ identifier: id, target, clientX: x, clientY: 110 })
  const send = (target: Element, eventType: string, touches: Touch[], changed: Touch[]) => target.dispatchEvent(new TouchEvent(eventType, { bubbles: true, cancelable: true, touches, changedTouches: changed }))
  send(node, 'touchstart', [touch(1, node, 80)], [touch(1, node, 80)])
  send(node, 'touchmove', [touch(1, node, 140)], [touch(1, node, 140)])
  // The second finger starts and ends outside the canvas, with no multi-touch
  // node move/start to cancel the primary gesture.
  send(document.body, 'touchstart', [touch(1, node, 140), touch(2, document.body, 400)], [touch(2, document.body, 400)])
  send(document.body, type, [touch(1, node, 140)], [touch(2, document.body, 400)])
  expect(commits).toHaveLength(0)
  expect(node.style.transform).toContain('140px, 100px')
  send(node, 'touchmove', [touch(1, node, 200)], [touch(1, node, 200)])
  expect(node.style.transform).toContain('220px, 100px')
  expect(commits).toHaveLength(0)
  send(node, 'touchend', [], [touch(1, node, 200)])
  expect(commits).toHaveLength(1)
  expect(graph().nodes[0].position).toEqual({ x: 220, y: 100 })
})
