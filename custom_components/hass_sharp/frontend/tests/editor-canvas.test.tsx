import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { analyzeGraph } from '../src/visual/contract'

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

it('renders same-count edits and undo positions through the real adapter; a stored size never sizes the card', async () => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  await vi.waitFor(() => expect(host.textContent).toContain('Entity changes'))
  // Graphs saved before D14 may still carry a size; cards are measured from their content instead.
  setGraph(updateNode(graph(), 'trigger-1', { label: 'Door opens', position: { x: 120, y: 80 }, size: { width: 300, height: 160 } } as any))
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  await vi.waitFor(() => expect(node.textContent).toContain('Door opens'))
  expect(node.style.transform).toContain('120px, 80px')
  expect(node.style.width).toBe('')
  expect(node.style.height).toBe('')
  setGraph(updateNode(graph(), 'trigger-1', { position: { x: 60, y: 100 } }))
  expect(node.style.transform).toContain('60px, 100px')
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
it('has no resize control: cards are measured from their content (D14)', async () => {
  const width = vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.width) || 800 })
  const height = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.height) || 570 })
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onChange={setGraph} />, host)
  await new Promise(resolve => setTimeout(resolve, 100)) // allow the library's queued layout measurement
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  expect(host.querySelector('.solid-flow__resize-control')).toBeNull()
  expect(node.style.width).toBe('')
  await vi.waitFor(() => expect(node.style.visibility).not.toBe('hidden'))
  width.mockRestore(); height.mockRestore()
})
it('selects the owning node when a connection handle receives focus', () => {
  const host = root(); const select = vi.fn()
  dispose = render(() => <FlowAdapter graph={createGraph('Heating')} onSelect={select} onChange={() => {}} />, host)
  host.querySelector('[data-id="trigger-1"] .solid-flow__handle')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  expect(select).toHaveBeenLastCalledWith('trigger-1')
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

const json = (host: Element) => { button(host, 'Graph JSON').click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) }
const focusNode = (host: Element, id: string) => host.querySelector(`[data-id="${id}"]`)!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
it('renders a source handle per backend port and names branch pins and wires', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.width) || 800 })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.height) || 570 })
  const host = root()
  const [graph, setGraph] = createSignal(connectNodes(addNode(createGraph('Heating'), 'delay', 'otherwise'), 'condition-1', 'otherwise', 'false'))
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, host)
  const handles = (id: string) => [...host.querySelectorAll(`[data-id="${id}"] .solid-flow__handle.source`)].map(h => h.getAttribute('data-handleid'))
  expect(handles('trigger-1')).toEqual(['next', 'data:data', 'data:entity_id', 'data:to_state', 'data:from_state'])
  expect(handles('condition-1')).toEqual(['true', 'false', 'error'])
  // The response output exists only while returnResponse is on (D9).
  expect(handles('service-1')).toEqual(['next', 'error'])
  // Branches are named on the card, beside their pins; wires carry the same names for assistive technology.
  expect(host.querySelector('[data-id="condition-1"] [data-output-id="false"]')?.textContent).toBe('If false')
  expect(host.querySelector('[data-id="condition-1"] [data-output-id="true"]')?.textContent).toBe('If true')
  expect(host.querySelector('[data-id="condition-1"] .hs-card__row--footer [data-output-id="error"]')?.textContent).toBe('On error')
  expect(host.querySelector('[data-id="condition-1"] [data-handleid="false"]')?.getAttribute('aria-label')).toBe('If false execution output, connected')
  expect(host.querySelector('[data-id="condition-1"] [data-handleid="error"]')?.getAttribute('aria-label')).toBe('On error execution output, not connected')
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge[data-id="condition-1:false->otherwise"]')).not.toBeNull())
  expect(host.querySelector('.solid-flow__edge[data-id="condition-1:false->otherwise"]')?.getAttribute('aria-label')).toBe('Execution wire from State condition · If false to Delay')
  expect(host.querySelector('.solid-flow__edge[data-id="condition-1:true->service-1"]')?.getAttribute('aria-label')).toBe('Execution wire from State condition · If true to Call service')
  expect(host.querySelector('.solid-flow__edge[data-id="trigger-1->condition-1"]')?.getAttribute('aria-label')).toBe('Execution wire from Entity changes · Next to State condition')
  expect(host.querySelector('.solid-flow__edge-labels')?.textContent ?? '').toBe('')
})
it('connects by dragging from a port handle and rejects a second edge on that port', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.width) || 800 })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function(this: HTMLElement) { return parseFloat(this.style.height) || 570 })
  const host = root()
  const [graph, setGraph] = createSignal(addNode(addNode(createGraph('Heating'), 'delay', 'otherwise'), 'service', 'recover'))
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, host)
  const handle = (id: string, handleId: string) => host.querySelector<HTMLElement>(`[data-id="${id}"] [data-handleid="${handleId}"]`)!
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  // happy-dom has no layout, so report the drop target the way a browser hit test would.
  let below: Element | null = null
  vi.spyOn(document, 'elementFromPoint').mockImplementation(() => below)
  const drag = (source: string, port: string, target: string) => {
    below = handle(target, 'in')
    handle(source, port).dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
    document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 200, clientY: 200 }))
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 200, clientY: 200 }))
  }
  drag('condition-1', 'false', 'otherwise')
  await vi.waitFor(() => expect(graph().edges.at(-1)).toMatchObject({ source: 'condition-1', target: 'otherwise', port: 'false' }))
  drag('condition-1', 'false', 'recover')
  drag('condition-1', 'true', 'recover')
  drag('service-1', 'error', 'recover')
  await vi.waitFor(() => expect(graph().edges.at(-1)).toMatchObject({ source: 'service-1', target: 'recover', port: 'error' }))
  // Dragging backwards, from a step's input onto a port handle, connects the same way.
  below = handle('otherwise', 'error')
  handle('recover', 'in').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 200, clientY: 200 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 10, clientY: 10 }))
  await vi.waitFor(() => expect(graph().edges.at(-1)).toMatchObject({ source: 'otherwise', target: 'recover', port: 'error' }))
  expect(graph().edges.filter(e => e.source === 'condition-1').map(e => [e.port, e.target])).toEqual([['true', 'service-1'], ['false', 'otherwise']])
  await vi.waitFor(() => expect(host.querySelectorAll('.solid-flow__edge')).toHaveLength(graph().edges.length))
})

it('opens a focused keyboard chooser and cancels without creating anything', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const before = json(host)
  button(host, 'Add step').click()
  await Promise.resolve()
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  expect(search).not.toBeNull(); expect(document.activeElement).toBe(search)
  search.value = 'Delay'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(host.querySelectorAll('[aria-label="Step choices"] [role="option"]')).toHaveLength(1)
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(json(host)).toEqual(before)
  button(host, 'Add step').click()
  document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  button(host, 'Add step').click()
  const next = host.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  next.value = 'Delay'; next.dispatchEvent(new Event('input', { bubbles: true }))
  next.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  next.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(json(host).nodes.at(-1).kind).toBe('delay')
  expect(host.querySelector<HTMLInputElement>('[aria-label="Node label"]')!.value).toBe('Delay')
  button(host, 'Undo').click(); expect(json(host)).toEqual(before)
})
it('drops palette nodes at flow coordinates accounting for pan and zoom', () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const data = new DataTransfer()
  button(host, 'About Delay').dispatchEvent(dragEvent('dragstart', data))
  const canvas = host.querySelector('[aria-label="Automation canvas"]')!
  canvas.dispatchEvent(dragEvent('drop', data, 160, 170))
  expect(json(host).nodes.at(-1)).toMatchObject({ kind: 'delay', position: { x: 200, y: 200 } })
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
})
it('clears the selection on an empty canvas click and opens quick-add on double-click, right-click and A', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const pane = host.querySelector('.solid-flow__pane')!
  const canvas = host.querySelector<HTMLElement>('.hs-flow')!
  const node = () => host.querySelector('.solid-flow__node[data-id="trigger-1"]')!
  await vi.waitFor(() => expect(node().classList).toContain('selected'))
  pane.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 160, clientY: 170, button: 0 }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  await vi.waitFor(() => expect(node().classList).not.toContain('selected'))
  expect(host.querySelector<HTMLInputElement>('[aria-label="Node label"]')).toBeNull()
  // Double-click: at the pointer, in flow coordinates (on empty space: nothing is in the way there).
  pane.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: 160, clientY: 470, button: 0 }))
  expect(host.querySelector('[role="dialog"][aria-label="Add a step"]')).not.toBeNull()
  button(host, 'Create Delay').click()
  expect(json(host).nodes.at(-1)).toMatchObject({ kind: 'delay', position: { x: 200, y: 600 } })
  expect(host.querySelector<HTMLInputElement>('[aria-label="Node label"]')!.value).toBe('Delay')
  // Right-click opens it too, and Escape closes it without adding anything.
  const count = json(host).nodes.length
  pane.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 160, clientY: 170, button: 2 }))
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  expect(document.activeElement).toBe(search)
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  // A, pressed on the canvas, opens it at the pointer; typed in a field it is just a letter.
  canvas.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: 160, clientY: 170 }))
  canvas.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true, cancelable: true }))
  expect(host.querySelector('[aria-label="Search steps"]')).not.toBeNull()
  button(host, 'Create Delay').click()
  expect(json(host).nodes).toHaveLength(count + 1)
})
it('opens quick-add on a long press of empty canvas, not after a moved touch', async () => {
  const host = root()
  const history = createGraphHistory(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  const pane = host.querySelector('.solid-flow__pane')!
  const touch = (x: number) => new Touch({ identifier: 1, target: pane, clientX: x, clientY: 470 })
  const send = (type: string, x: number) => pane.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches: type === 'touchend' ? [] : [touch(x)], changedTouches: [touch(x)] }))
  send('touchstart', 160); send('touchmove', 200)
  await new Promise(resolve => setTimeout(resolve, 650))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  send('touchend', 200)
  send('touchstart', 160)
  await vi.waitFor(() => expect(host.querySelector('[aria-label="Search steps"]')).not.toBeNull(), { timeout: 1500 })
  send('touchend', 160)
  button(host, 'Create Delay').click()
  // The first, moved touch panned the canvas 40 px to the right.
  expect(history.graph().nodes.at(-1)).toMatchObject({ kind: 'delay', position: { x: Math.round((160 - 10 - 40) / 0.75), y: 600 } })
})
it('offers valid steps after an output drag and undoes node plus branch in one operation', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const before = json(host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const pane = host.querySelector('.solid-flow__pane')!
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(pane)
  const handle = host.querySelector('[data-id="condition-1"] [data-handleid="false"]')!
  handle.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 160, clientY: 170 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 160, clientY: 170 }))
  expect(host.querySelector('[aria-label="Search steps"]')).not.toBeNull()
  // A named output says which one, with its step.
  expect(host.querySelector('.hs-quick-add')!.getAttribute('aria-label')).toBe('Connect State condition · If false to…')
  expect(host.querySelector('[aria-label="Create Entity changes"]')).toBeNull()
  button(host, 'Create Delay').click()
  const after = json(host)
  expect(after.edges.at(-1)).toMatchObject({ source: 'condition-1', port: 'false', target: after.nodes.at(-1).id })
  button(host, 'Undo').click(); expect(json(host)).toEqual(before)
  button(host, 'Redo').click(); expect(json(host)).toEqual(after)
})

// happy-dom exposes DragEvent as Event and omits DataTransfer/coordinates.
function dragEvent(type: string, data: DataTransfer, clientX = 0, clientY = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY })
  Object.defineProperty(event, 'dataTransfer', { value: data })
  return event
}

it('does not open a chooser after panning, node drags, resize or drops outside the canvas', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const pane = host.querySelector('.solid-flow__pane')!
  pane.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  pane.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, buttons: 1, clientX: 80, clientY: 80 }))
  pane.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0, clientX: 80, clientY: 80 }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  const data = new DataTransfer()
  button(host, 'About Delay').dispatchEvent(dragEvent('dragstart', data))
  document.body.dispatchEvent(dragEvent('drop', data, 1000, 1000))
  expect(json(host).nodes).toHaveLength(3)
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
})
it.each(['next', 'error'] as const)('preserves the %s output in the connection chooser and cancels pending drags with Escape', async port => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const pane = host.querySelector('.solid-flow__pane')!
  const hit = vi.spyOn(document, 'elementFromPoint').mockReturnValue(pane)
  const handle = host.querySelector(`[data-id="service-1"] [data-handleid="${port}"]`)!
  const start = () => {
    handle.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
    document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 160, clientY: 170 }))
  }
  const end = () => document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 160, clientY: 170 }))
  start(); handle.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, key: 'Escape' })); end()
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  start(); hit.mockReturnValue(document.body); end()
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  hit.mockReturnValue(pane); start(); end()
  button(host, 'Create Delay').click()
  expect(json(host).edges.at(-1)).toMatchObject({ source: 'service-1', ...(port === 'error' ? { port: 'error' } : {}) })
  start(); end() // now occupied
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
})
it('clears a touch connection canceled before a later touch release', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const host = root(); dispose = render(() => <VisualEditor name="Touch cancel" />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const pane = host.querySelector('.solid-flow__pane')!
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(pane)
  const handle = host.querySelector('[data-id="service-1"] [data-handleid="next"]')!
  const finger = { identifier: 1, clientX: 10, clientY: 10, target: handle } as Touch
  handle.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, pointerType: 'touch', clientX: 10, clientY: 10 }))
  const endFinger = { ...finger, clientX: 160, clientY: 170 } as Touch
  document.dispatchEvent(new TouchEvent('touchmove', { bubbles: true, touches: [endFinger], changedTouches: [endFinger] }))
  document.dispatchEvent(new TouchEvent('touchcancel', { bubbles: true, touches: [], changedTouches: [endFinger] }))
  document.dispatchEvent(new TouchEvent('touchend', { bubbles: true, touches: [], changedTouches: [endFinger] }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(json(host).nodes).toHaveLength(3)
})

const key = (target: Element, key: string, init: KeyboardEventInit = {}) =>
  target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }))
const click = (target: Element, init: MouseEventInit = {}) => {
  target.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: 0, view: window, ...init }))
  window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, button: 0, view: window, ...init }))
  target.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0, view: window, ...init }))
}
const fixedLayout = () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(200)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(100)
}

/** Drags from `pin` to empty canvas (which opens quick-add for it). */
const dropOnPane = (host: Element, pin: Element) => {
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(host.querySelector('.solid-flow__pane')!)
  pin.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 160, clientY: 170 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 160, clientY: 170 }))
}
const dataPin = (host: Element, node: string, side: 'source' | 'target', port: string) => host.querySelector(`[data-id="${node}"] .${side}[data-handleid="data:${port}"]`)!

it('drops a data pin on empty canvas: lists only steps that take its type, and runs the new step after it when that port is free', async () => {
  fixedLayout()
  const host = root()
  // The trigger's next is free: nothing runs after it yet.
  const start = createGraph('Heating')
  const history = createGraphHistory({ ...start, edges: start.edges.filter(e => e.source !== 'trigger-1') })
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const before = history.graph()
  dropOnPane(host, dataPin(host, 'trigger-1', 'source', 'entity_id'))
  expect(host.querySelector('[role="dialog"]')!.getAttribute('aria-label')).toBe('Connect Entity changes · Entity to…')
  // Only steps with an input that takes the pin's type: no trigger, no Delay (its only input is a duration).
  expect(host.querySelector('[aria-label="Create Entity changes"]')).toBeNull()
  expect(host.querySelector('[aria-label="Create Delay"]')).toBeNull()
  button(host, 'Create Set value').click()
  const after = history.graph()
  const created = after.nodes.at(-1)!
  expect(created.kind).toBe('set_value')
  expect(after.edges).toEqual([...before.edges, expect.objectContaining({ source: 'trigger-1', target: created.id }), expect.objectContaining({ kind: 'data', source: 'trigger-1', sourcePort: 'entity_id', target: created.id })])
  history.undo()
  expect(history.graph()).toEqual(before)
})

/** Renders `graph` in an adapter with undo, waiting for its wires. */
const adapter = async (graph: VisualGraph) => {
  fixedLayout()
  const host = root()
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  return { host, history, before: history.graph() }
}
const unreachable = (graph: VisualGraph) => analyzeGraph(graph).diagnostics.filter(d => d.code === 'node.unreachable').map(d => d.nodeId)
const title = (host: Element, id: string) => host.querySelector(`.solid-flow__node[data-id="${id}"] .hs-card__title`)?.textContent
const plain = (label: string, pin = '.+') => new RegExp(`^Added ${label}\\. Connect it into the flow, then connect ${pin}\\.$`)

it('puts a step with a next onto the one wire after a data output, then wires the value, and says where it went', async () => {
  const { host, history, before } = await adapter(createGraph('Heating'))
  dropOnPane(host, dataPin(host, 'trigger-1', 'source', 'entity_id'))
  button(host, 'Create Set value').click()
  const after = history.graph()
  const created = after.nodes.at(-1)!.id
  expect(after.edges).toEqual([
    ...before.edges.filter(e => e.id !== 'trigger-1->condition-1'),
    { id: `trigger-1->${created}`, source: 'trigger-1', target: created },
    { id: `${created}->condition-1`, source: created, target: 'condition-1' },
    expect.objectContaining({ kind: 'data', source: 'trigger-1', sourcePort: 'entity_id', target: created, targetPort: 'value' }),
  ])
  expect(unreachable(after)).toEqual([])
  expect(host.textContent).toContain(`Added Set value between ${title(host, 'trigger-1')} and ${title(host, 'condition-1')}`)
  expect(host.querySelector('[role="alert"]')).toBeNull()
  history.undo()
  expect(history.graph()).toEqual(before)
})

it('puts a step with a next onto the one wire into a data input\'s step, before it', async () => {
  const { host, history, before } = await adapter(connectNodes(addNode(createGraph('Heating'), 'log', 'log'), 'service-1', 'log'))
  dropOnPane(host, dataPin(host, 'log', 'target', 'value'))
  expect(host.querySelector('.hs-quick-add')!.getAttribute('aria-label')).toMatch(/^Get .+ from…$/)
  button(host, 'Create Set value').click()
  const after = history.graph()
  const created = after.nodes.at(-1)!.id
  expect(after.edges).toEqual([
    ...before.edges.filter(e => e.id !== 'service-1->log'),
    { id: `service-1->${created}`, source: 'service-1', target: created },
    { id: `${created}->log`, source: created, target: 'log' },
    expect.objectContaining({ kind: 'data', source: created, sourcePort: 'value', target: 'log', targetPort: 'value' }),
  ])
  expect(unreachable(after)).toEqual([])
  expect(host.textContent).toContain(`Added Set value between ${title(host, 'service-1')} and ${title(host, 'log')}`)
  history.undo()
  expect(history.graph()).toEqual(before)
})

it.each([
  ['a kind without a next on an occupied port', (g: VisualGraph) => g, 'trigger-1', 'source', 'entity_id', 'State condition', 'Entity'],
  ['a data input whose step has two incoming wires',
    (g: VisualGraph) => connectNodes(connectNodes(addNode(g, 'log', 'log'), 'service-1', 'log'), 'condition-1', 'log', 'false'), 'log', 'target', 'value', 'Set value', undefined],
  ['a data input on a step that does not run', (g: VisualGraph) => addNode(g, 'log', 'log'), 'log', 'target', 'value', 'Set value', undefined],
  ['a data output on a step that does not run',
    (g: VisualGraph) => updateNode(addNode(g, 'set_value', 'loose'), 'loose', { config: { type: 'string', value: { kind: 'literal', type: 'string', value: 'x' } } }), 'loose', 'source', 'value', 'Set value', 'Value'],
] as const)('adds the step with no existing wire changed for %s, and says in plain words what happened', async (_, build, node, side, port, label, pin) => {
  const { host, history, before } = await adapter(build(createGraph('Heating')))
  dropOnPane(host, dataPin(host, node, side, port))
  button(host, `Create ${label}`).click()
  const after = history.graph()
  const created = after.nodes.at(-1)!
  expect(after.nodes).toHaveLength(before.nodes.length + 1)
  expect(after.nodes.slice(0, -1).map(n => n.id)).toEqual(before.nodes.map(n => n.id))
  // Every wire is as it was; the only possible addition is the value wire itself, when the editor accepts it.
  expect(after.edges.slice(0, before.edges.length)).toEqual(before.edges)
  const added = after.edges.slice(before.edges.length)
  const ends = side === 'source' ? { source: node, sourcePort: port, target: created.id } : { source: created.id, target: node, targetPort: port }
  if (added.length) {
    expect(added).toEqual([expect.objectContaining({ kind: 'data', ...ends })])
    await vi.waitFor(() => expect(host.textContent).toMatch(new RegExp(`Added ${label} and connected ${pin ?? '.+'}\\. Connect ${label} into the flow to run it\\.`)))
  } else {
    const callout = side === 'source' ? created.id : node
    await vi.waitFor(() => expect(host.querySelector(`[data-id="${callout}"] [role="alert"]`)?.textContent).toMatch(plain(label, pin)))
  }
  expect([...host.querySelectorAll('[role="alert"], [role="status"]')].map(e => e.textContent).join(' ')).not.toMatch(/boundary|scope|branch/i)
  history.undo()
  expect(history.graph()).toEqual(before)
})

it('offers only steps that can be placed there: no Condition result outside a loop condition', async () => {
  fixedLayout()
  const host = root()
  let graph = addNode(createGraph('Heating'), 'and', 'both')
  graph = connectNodes(graph, 'service-1', 'both')
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  dropOnPane(host, dataPin(host, 'both', 'source', 'value'))
  expect(host.querySelector('[aria-label="Create If"]')).not.toBeNull()
  expect(host.querySelector('[aria-label="Create Condition result"]')).toBeNull()
  button(host, 'Create If').click()
  const created = history.graph().nodes.at(-1)!
  expect(history.graph().edges.slice(-2)).toEqual([
    expect.objectContaining({ source: 'both', target: created.id }),
    expect.objectContaining({ kind: 'data', source: 'both', sourcePort: 'value', target: created.id, targetPort: 'value' }),
  ])
})

it('selects several steps with Shift-click and removes them with Delete in one undo step', async () => {
  const host = root()
  const history = createGraphHistory(createGraph('Heating'))
  const selections: [string[], string | undefined][] = []
  const primary = vi.fn()
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={primary} onSelectionChange={(ids, edge) => selections.push([ids, edge])} onChange={history.commit} />, host)
  const node = (id: string) => host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  click(node('trigger-1'))
  expect(primary).toHaveBeenLastCalledWith('trigger-1')
  click(node('service-1'), { shiftKey: true })
  expect(selections.at(-1)).toEqual([['trigger-1', 'service-1'], undefined])
  // More than one step selected: no primary step for the inspector.
  expect(primary).toHaveBeenLastCalledWith(undefined)
  await vi.waitFor(() => expect(node('service-1').classList).toContain('selected'))
  expect(node('trigger-1').classList).toContain('selected')
  key(node('service-1'), 'Delete')
  expect(history.graph().nodes.map(n => n.id)).toEqual(['condition-1'])
  expect(selections.at(-1)).toEqual([[], undefined])
  history.undo()
  expect(history.graph().nodes.map(n => n.id)).toEqual(['trigger-1', 'condition-1', 'service-1'])
  expect(history.canUndo()).toBe(false)
  // Shift-click toggles a selected step off again.
  click(node('condition-1'))
  click(node('service-1'), { shiftKey: true })
  click(node('condition-1'), { shiftKey: true })
  expect(selections.at(-1)).toEqual([['service-1'], undefined])
  expect(primary).toHaveBeenLastCalledWith('service-1')
})

it('adds the cards a Shift-drag marquee touches to the selection, and the click that ends it keeps them', async () => {
  fixedLayout()
  const host = root()
  const selections: string[][] = []
  dispose = render(() => <FlowAdapter graph={createGraph('Heating')} onSelect={() => {}} onSelectionChange={ids => selections.push(ids)} onChange={() => {}} />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const pane = host.querySelector('.solid-flow__pane')!
  // The default viewport maps (x, y) on screen to ((x - 10) / 0.75, (y - 20) / 0.75): cards sit at x 60, 360, 660.
  pane.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, shiftKey: true, isPrimary: true, clientX: 0, clientY: 0 }))
  document.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: 300, clientY: 120 }))
  expect(host.querySelector('.hs-marquee')).not.toBeNull()
  document.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, clientX: 300, clientY: 120 }))
  pane.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0, shiftKey: true, clientX: 300, clientY: 120 }))
  expect(host.querySelector('.hs-marquee')).toBeNull()
  expect(selections.at(-1)).toEqual(['trigger-1', 'condition-1'])
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__node[data-id="condition-1"]')!.classList).toContain('selected'))
  expect(host.querySelector('.solid-flow__node[data-id="service-1"]')!.classList).not.toContain('selected')
})

it('copies, pastes and duplicates selected steps with their own wires, new ids, one undo step each', async () => {
  const host = root()
  const history = createGraphHistory(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  const node = (id: string) => host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  const canvas = host.querySelector<HTMLElement>('.hs-flow')!
  click(node('condition-1'))
  click(node('service-1'), { shiftKey: true })
  key(node('service-1'), 'c', { ctrlKey: true })
  expect(host.textContent).toContain('Copied 2 steps')
  key(canvas, 'v', { metaKey: true })
  await vi.waitFor(() => expect(history.graph().nodes).toHaveLength(5))
  const pasted = history.graph().nodes.slice(3)
  expect(pasted.map(n => n.kind)).toEqual(['condition', 'service'])
  expect(pasted.map(n => n.id)).not.toContain('condition-1')
  // 40 px right and down of the originals would cover them: the copies go 40 px below them instead (cards 120 high).
  expect(pasted.map(n => n.position)).toEqual([{ x: 400, y: 260 }, { x: 700, y: 260 }])
  // The wire between them is copied; the wire from the trigger, outside the copy, is not.
  expect(history.graph().edges.slice(2)).toEqual([expect.objectContaining({ source: pasted[0].id, target: pasted[1].id, port: 'true' })])
  // The pasted steps are now the selection; Ctrl+D duplicates them again.
  key(node(pasted[1].id), 'd', { ctrlKey: true })
  expect(history.graph().nodes).toHaveLength(7)
  expect(history.graph().nodes.slice(5).map(n => n.position)).toEqual([{ x: 440, y: 420 }, { x: 740, y: 420 }])
  history.undo(); expect(history.graph().nodes).toHaveLength(5)
  history.undo(); expect(history.graph().nodes).toHaveLength(3)
  expect(history.canUndo()).toBe(false)
})

it('tidies, snaps and shows a minimap from the toolbar', async () => {
  fixedLayout()
  const host = root()
  let graph = addNode(addNode(createGraph('Heating'), 'delay', 'later'), 'delay', 'after')
  graph = connectNodes(connectNodes(graph, 'service-1', 'later'), 'later', 'after')
  graph = updateNode(graph, 'after', { position: { x: 10, y: 600 } })
  const history = createGraphHistory(graph)
  const changes = vi.fn(history.commit)
  dispose = render(() => <FlowAdapter graph={history.graph()} selectedId="trigger-1" onSelect={() => {}} onChange={changes} />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__edge')).not.toBeNull())
  const tools = host.querySelector('[role="group"][aria-label="Canvas tools"]')!
  expect([...tools.querySelectorAll('button')].map(b => b.getAttribute('aria-label'))).toEqual(['Add a step', 'Zoom in', 'Zoom out', 'Fit to view', 'Tidy layout', 'Snap to grid', 'Minimap'])
  button(host, 'Tidy layout').click()
  expect(changes).toHaveBeenCalledTimes(1)
  const x = (id: string) => history.graph().nodes.find(n => n.id === id)!.position!.x
  // Left to right in run order, on the grid.
  expect(x('trigger-1')).toBeLessThan(x('condition-1'))
  expect(x('service-1')).toBeLessThan(x('later'))
  expect(x('later')).toBeLessThan(x('after'))
  expect(history.graph().nodes.every(n => n.position!.x % 20 === 0 && n.position!.y % 20 === 0)).toBe(true)
  button(host, 'Tidy layout').click()
  expect(changes).toHaveBeenCalledTimes(1)
  expect(host.textContent).toContain('Already tidy')
  // Snap: the button is a toggle, and keyboard moves go a grid step at a time.
  const snap = button(host, 'Snap to grid')
  expect(snap.getAttribute('aria-pressed')).toBe('false')
  snap.click()
  expect(snap.getAttribute('aria-pressed')).toBe('true')
  const start = x('trigger-1')
  key(host.querySelector('.solid-flow__node[data-id="trigger-1"]')!, 'ArrowRight')
  expect(x('trigger-1')).toBe(start + 20)
  expect(host.querySelector('.solid-flow__minimap')).toBeNull()
  button(host, 'Minimap').click()
  expect(button(host, 'Minimap').getAttribute('aria-pressed')).toBe('true')
  expect(host.querySelector('.solid-flow__minimap')).not.toBeNull()
})

it('keeps a read-only canvas inert while selection and viewport tools still work', async () => {
  const host = root()
  const changes = vi.fn()
  const selections: string[][] = []
  dispose = render(() => <FlowAdapter graph={createGraph('Heating')} readOnly onSelect={() => {}} onSelectionChange={ids => selections.push(ids)} onChange={changes} />, host)
  const tools = host.querySelector('[role="group"][aria-label="Canvas tools"]')!
  expect([...tools.querySelectorAll('button')].map(b => b.getAttribute('aria-label'))).toEqual(['Zoom in', 'Zoom out', 'Fit to view', 'Minimap'])
  const node = host.querySelector<HTMLElement>('.solid-flow__node[data-id="trigger-1"]')!
  const pane = host.querySelector('.solid-flow__pane')!
  click(node)
  expect(selections.at(-1)).toEqual(['trigger-1'])
  for (const [k, init] of [['Delete', {}], ['Backspace', {}], ['a', {}], ['d', { ctrlKey: true }], ['v', { ctrlKey: true }], ['ArrowRight', {}]] as const) key(node, k, init)
  pane.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: 160, clientY: 170 }))
  // The browser's own context menu stays: there is nothing to add here.
  const menu = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 160, clientY: 170 })
  pane.dispatchEvent(menu)
  expect(menu.defaultPrevented).toBe(false)
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(changes).not.toHaveBeenCalled()
  button(host, 'Minimap').click()
  expect(host.querySelector('.solid-flow__minimap')).not.toBeNull()
  button(host, 'Zoom in').click()
  button(host, 'Fit to view').click()
  expect(changes).not.toHaveBeenCalled()
})

it('leaves canvas keys to a card\'s inline field, the quick-add search and an open menu', async () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const card = () => host.querySelector<HTMLElement>('.solid-flow__node[data-id="condition-1"]')!
  const before = json(host)
  /** Each canvas shortcut, pressed on `target`: none may act or be swallowed. */
  const shortcuts = (target: Element) => {
    for (const [k, init] of [['a', {}], ['Delete', {}], ['Backspace', {}], ['v', { ctrlKey: true }], ['d', { ctrlKey: true }], ['ArrowRight', {}]] as const) {
      const event = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true, ...init })
      target.dispatchEvent(event)
      expect(event.defaultPrevented, `${k} on ${target.getAttribute('aria-label') ?? target.tagName}`).toBe(false)
    }
  }
  // A card's inline field (the condition's Entity).
  const field = await vi.waitFor(() => {
    const found = card().querySelector<HTMLElement>('[data-input-id="entity"] .hs-card-editor input')
    expect(found).not.toBeNull()
    return found!
  })
  shortcuts(field)
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  // An open menu on the card.
  const trigger = card().querySelector<HTMLButtonElement>('[data-input-id="entity"] button[aria-haspopup="menu"]')!
  trigger.click()
  const item = card().querySelector<HTMLElement>('[role="menu"] [role="menuitem"]')!
  expect(item).not.toBeNull()
  shortcuts(item)
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  // The quick-add search: typing A is a letter, Delete edits the text.
  button(host, 'Add step').click()
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  shortcuts(search)
  expect(host.querySelector('[aria-label="Search steps"]')).toBe(search)
  expect(json(host)).toEqual(before)
})

it('gives focus back when quick-add closes: to where it was after Escape, to the new card after a choice', async () => {
  const host = root()
  const history = createGraphHistory(createGraph('Heating'))
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  const card = (id: string) => host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  card('service-1').focus()
  key(card('service-1'), 'a')
  const search = host.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  expect(document.activeElement).toBe(search)
  key(search, 'Escape')
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(document.activeElement).toBe(card('service-1'))
  // Tab closes it the same way, instead of dropping focus on the page.
  key(card('service-1'), 'a')
  key(host.querySelector('[aria-label="Search steps"]')!, 'Tab')
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(document.activeElement).toBe(card('service-1'))
  // Opened from a toolbar button that a click did not focus: focus goes to the new card once it is there.
  ;(document.activeElement as HTMLElement).blur()
  // A card is hidden until measured, and a hidden card refuses focus: the first try on it does nothing here.
  const focus = HTMLElement.prototype.focus
  let refused = false
  vi.spyOn(HTMLElement.prototype, 'focus').mockImplementation(function (this: HTMLElement, options?: FocusOptions) {
    if (!refused && this.classList.contains('solid-flow__node')) return void (refused = true)
    focus.call(this, options)
  })
  button(host, 'Add a step').click()
  button(host, 'Create Delay').click()
  const created = history.graph().nodes.at(-1)!
  await vi.waitFor(() => expect(document.activeElement).toBe(card(created.id)))
  expect(refused).toBe(true)
  // A step that never appears (the parent refused it): focus is back on the canvas, never on the page body.
  dispose()
  const refusing = root()
  dispose = render(() => <FlowAdapter graph={createGraph('Heating')} onSelect={() => {}} onChange={() => {}} />, refusing)
  button(refusing, 'Add a step').click()
  button(refusing, 'Create Delay').click()
  expect(refusing.contains(document.activeElement)).toBe(true)
})

it('selects new, pasted and duplicated steps when a parent applies the change later, and reports the first selection once', async () => {
  const host = root()
  const [graph, setGraph] = createSignal(createGraph('Heating'))
  const selections: string[][] = []
  // The parent applies each change on a later task (a store round trip).
  const later = (next: typeof graph extends () => infer G ? G : never) => setTimeout(() => setGraph(next))
  dispose = render(() => <FlowAdapter graph={graph()} selectedId="trigger-1" onSelect={() => {}} onSelectionChange={ids => selections.push(ids)} onChange={later} />, host)
  expect(selections).toEqual([['trigger-1']])
  button(host, 'Add a step').click()
  button(host, 'Create Delay').click()
  await vi.waitFor(() => expect(graph().nodes).toHaveLength(4))
  const created = graph().nodes.at(-1)!.id
  await vi.waitFor(() => expect(selections.at(-1)).toEqual([created]))
  key(host.querySelector(`.solid-flow__node[data-id="${created}"]`)!, 'd', { ctrlKey: true })
  await vi.waitFor(() => expect(graph().nodes).toHaveLength(5))
  await vi.waitFor(() => expect(selections.at(-1)).toEqual([graph().nodes.at(-1)!.id]))
})

it('does not copy C# variable names into pasted steps, and says so; leaves Ctrl+C to selected text', async () => {
  const host = root()
  let start = addNode(createGraph('Heating'), 'set_value', 'named')
  start = updateNode(start, 'named', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 1 }, output: 'level' } })
  const history = createGraphHistory(start)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, host)
  const node = (id: string) => host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  click(node('named'))
  // Text selected on a card: the browser copies it, the canvas does not.
  const text = node('named').querySelector('.hs-card__title, h3, strong') ?? node('named')
  const range = document.createRange()
  range.selectNodeContents(text)
  document.getSelection()!.removeAllRanges()
  document.getSelection()!.addRange(range)
  const copyText = new KeyboardEvent('keydown', { key: 'c', ctrlKey: true, bubbles: true, cancelable: true })
  node('named').dispatchEvent(copyText)
  expect(copyText.defaultPrevented).toBe(false)
  expect(host.textContent).not.toContain('Copied')
  document.getSelection()!.removeAllRanges()
  key(node('named'), 'd', { ctrlKey: true })
  const copy = history.graph().nodes.at(-1)!
  expect(copy.id).not.toBe('named')
  expect(copy.config.output).toBeUndefined()
  expect(history.graph().nodes.find(n => n.id === 'named')!.config.output).toBe('level')
  expect(host.textContent).toContain('Duplicated 1 step; variable names were not copied')
  key(node('named'), 'c', { ctrlKey: true })
  expect(host.textContent).toContain('Copied 1 step')
  key(host.querySelector('.hs-flow')!, 'v', { ctrlKey: true })
  await vi.waitFor(() => expect(host.textContent).toContain('Pasted 1 step; variable names were not copied'))
  expect(history.graph().nodes.filter(n => n.config.output === 'level')).toHaveLength(1)
})

it('never puts a step added from a pin on another card: after Tidy, the spot beside the pin moves below the card there', async () => {
  const { host, history } = await adapter(createGraph('Heating'))
  button(host, 'Tidy layout').click()
  await new Promise(resolve => setTimeout(resolve, 0))
  const handle = host.querySelector<HTMLElement>('[data-id="condition-1"] .source[data-handleid="false"]')!
  handle.focus()
  key(handle, 'Enter')
  key(handle, 'a')
  button(host, 'Create Delay').click()
  const graph = history.graph()
  const created = graph.nodes.at(-1)!
  const at = (id: string) => graph.nodes.find(n => n.id === id)!.position!
  // Beside the pin is where Tidy put the next card; the new one sits 40 px below it, on the grid it was given.
  expect(at(created.id).x).toBe(at('service-1').x)
  expect(at(created.id).y).toBe(at('service-1').y + 100 + 40)
  // Cards here measure 200 × 100: none is within 20 px of another.
  for (const node of graph.nodes) if (node.id !== created.id) {
    const a = at(created.id), b = at(node.id)
    expect(a.x < b.x + 220 && b.x < a.x + 220 && a.y < b.y + 120 && b.y < a.y + 120, node.id).toBe(false)
  }
})

it('stops trying to focus a new card once the user presses on the canvas', async () => {
  const { host, history } = await adapter(createGraph('Heating'))
  // The card stays hidden (unmeasured) for a while, so it refuses focus.
  let hidden = true
  const focus = HTMLElement.prototype.focus
  vi.spyOn(HTMLElement.prototype, 'focus').mockImplementation(function (this: HTMLElement, options?: FocusOptions) {
    if (hidden && this.classList.contains('solid-flow__node')) return
    focus.call(this, options)
  })
  button(host, 'Add a step').click()
  button(host, 'Create Delay').click()
  const created = history.graph().nodes.at(-1)!.id
  host.querySelector('.solid-flow__pane')!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 5, clientY: 5 }))
  hidden = false
  await new Promise(resolve => setTimeout(resolve, 100))
  expect(document.activeElement).not.toBe(host.querySelector(`.solid-flow__node[data-id="${created}"]`))
})
