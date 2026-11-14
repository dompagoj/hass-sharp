import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { separateCards } from '../src/visual/layout'
import { nodeCatalog } from '../src/visual/contract'
import { addNode, createGraph, updateNode, type VisualGraph } from '../src/visual/graph'

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.()
  dispose = undefined
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})

it('moves a card that overlaps a taller one above it down below it, and what that one then overlaps after it', () => {
  const box = (id: string, x: number, y: number, height = 120, width = 260) => ({ id, x, y, width, height })
  // The 180 px grid of a graph saved without positions, with a card now 400 px tall above two others in its column.
  const moved = separateCards([box('state', 60, 100, 400), box('compare', 60, 280), box('if', 60, 460), box('beside', 360, 280)], 24)
  expect(moved).toEqual(new Map([['compare', { x: 60, y: 524 }], ['if', { x: 60, y: 668 }]]))
  // Cards that only sit close, or side by side, stay; nothing overlapping gives nothing to move.
  expect(separateCards([box('a', 0, 0), box('b', 0, 130), box('c', 270, 0, 500)])).toEqual(new Map())
  // The input order does not matter.
  expect(separateCards([box('if', 60, 460), box('compare', 60, 280), box('state', 60, 100, 400)], 24)).toEqual(moved)
})

/** A tall step above a value node in the same column, as a graph saved through the API lays them out. */
function crowded(): VisualGraph {
  let g = createGraph('Crowded')
  g = updateNode(g, 'trigger-1', { position: { x: 60, y: 100 } })
  g = addNode(g, 'constant', 'value', nodeCatalog)
  g = updateNode(g, 'value', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 1 } }, position: { x: 60, y: 280 } })
  return { ...g, nodes: g.nodes.filter(n => n.id === 'trigger-1' || n.id === 'value'), edges: [] }
}
const mountSized = (graph: VisualGraph, readOnly: boolean) => {
  // Every card 260 × 120 except the trigger, which is 400 px tall.
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(260)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList?.contains('solid-flow__node') && this.dataset.id === 'trigger-1' ? 400 : 120
  })
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  const changes: VisualGraph[] = []
  dispose = render(() => <FlowAdapter graph={graph} readOnly={readOnly} onSelect={() => {}} onChange={next => changes.push(next)} />, el)
  return { el, changes }
}
const top = (el: Element, id: string) => {
  const node = el.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  const match = /translate\(\s*(-?[\d.]+)px,\s*(-?[\d.]+)px\)/.exec(node.style.transform)
  return match ? Number(match[2]) : NaN
}

it('keeps cards apart in the read-only trace view, without changing the graph', async () => {
  const { el, changes } = mountSized(crowded(), true)
  await vi.waitFor(() => expect(top(el, 'value')).toBe(524))
  expect(top(el, 'trigger-1')).toBe(100)
  expect(changes).toEqual([])
})

it('leaves stored positions as they are in the editor (where Tidy is the way to rearrange)', async () => {
  const { el } = mountSized(crowded(), false)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__node[data-id="value"]')?.getAttribute('style')).toContain('translate'))
  await new Promise(resolve => setTimeout(resolve, 50))
  expect(top(el, 'value')).toBe(280)
})

it('opens the trace view again once cards have moved apart, so none starts outside it', async () => {
  // A canvas 1000 × 520; every card 260 wide, the trigger 400 tall and the others 120.
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList?.contains('solid-flow__node') ? 260 : 1000
  })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) {
    return !this.classList?.contains('solid-flow__node') ? 520 : this.dataset.id === 'trigger-1' ? 400 : 120
  })
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1000)
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(520)
  // jsdom has no ResizeObserver: the canvas learns its size when it starts observing, as in a browser.
  vi.stubGlobal('ResizeObserver', class {
    constructor(private readonly report: (entries: unknown[], observer: unknown) => void) {}
    observe(target: Element) { queueMicrotask(() => this.report([{ target, contentRect: { width: 1000, height: 520 } }], this)) }
    unobserve() {}
    disconnect() {}
  })
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  dispose = render(() => <FlowAdapter graph={crowded()} readOnly onSelect={() => {}} onChange={() => {}} />, el)
  await vi.waitFor(() => expect(top(el, 'value')).toBe(524))
  const view = () => {
    const match = /translate\(\s*(-?[\d.]+)px,\s*(-?[\d.]+)px\)\s*scale\(\s*([\d.]+)\s*\)/.exec(el.querySelector<HTMLElement>('.solid-flow__viewport')!.style.transform)
    return match ? { y: Number(match[2]), zoom: Number(match[3]) } : undefined
  }
  // Every card, where the view puts it on screen, lies inside the canvas's 520 px.
  const inside = () => {
    const v = view()!
    return [['trigger-1', 400], ['value', 120]].every(([id, height]) => {
      const y = top(el, id as string) * v.zoom + v.y
      return y >= 0 && y + (height as number) * v.zoom <= 520
    })
  }
  await vi.waitFor(() => expect(inside()).toBe(true))
})
