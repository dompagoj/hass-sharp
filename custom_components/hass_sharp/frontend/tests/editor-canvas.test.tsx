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
it('opens on empty canvas click at pointer position and ignores right clicks', () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const pane = host.querySelector('.solid-flow__pane')!
  pane.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 160, clientY: 170, button: 2 }))
  expect(host.querySelector('[aria-label="Search steps"]')).toBeNull()
  pane.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 160, clientY: 170, button: 0 }))
  button(host, 'Create Delay').click()
  expect(json(host).nodes.at(-1)).toMatchObject({ kind: 'delay', position: { x: 200, y: 200 } })
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
