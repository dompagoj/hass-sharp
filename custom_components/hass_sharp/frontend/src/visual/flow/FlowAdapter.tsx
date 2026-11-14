import { createEffect, createMemo, createSignal, getOwner, on, onCleanup, onMount, runWithOwner, Show, untrack, useContext, type Component, type JSX } from 'solid-js'
import { createStore, produce, reconcile } from 'solid-js/store'
import { QueryClientContext } from '@tanstack/solid-query'
import { Background, getViewportForBounds, MiniMap, SolidFlow, useSolidFlow, type Edge, type Node } from '@dschz/solid-flow'
import flowStyles from '@dschz/solid-flow/styles?inline'
import { Icon } from '../../components/Controls'
import { QuickAdd } from '../QuickAdd'
import type { VisualDiagnostic } from '../api'
import { HassCtx } from '../../context'
import { useLoadedRegistry } from '../../registry'
import { connectData, nodeCatalog, setInput as writeInput, type Operand, type VisualCatalogSnapshot } from '../contract'
import { canConnect, catalogPalette, connectNodes, createNodeAt, dataConnectionError, removeEdge, removeNode, updateNode, type NodeKind, type VisualGraph } from '../graph'
import { connectionIndex, graphTypes } from '../connectionIndex'
import { media } from '../media'
import { NODE_DRAG_TYPE } from '../nodeDrag'
import { copySteps, pasteSteps, readSteps, writeSteps, type StepClipboard } from '../clipboard'
import { applyLayout, separateCards, tidyLayout, type LayoutBox } from '../layout'
import type { SummaryNames } from '../nodeSummary'
import { registryNames, type NameResolver } from '../widgets'
import { addConnected, canAddFor, type PinRef } from './autoConnect'
import { CanvasToolbar } from './CanvasToolbar'
import { edgeTypes } from './Edges'
import { NodeCard } from './NodeCard'
import {
  buildEdgeView,
  buildNodeView,
  dataPortOf,
  FlowContext,
  keepUnrendered,
  nodeTitle,
  renderableGraph,
  traceText,
  viewKey,
  type FlowContextValue,
  type FlowTrace,
  type NodeView,
  type PinState,
  type PinView,
  type ViewContext,
} from './flowModel'

export interface FlowAdapterProps {
  graph: VisualGraph
  catalog?: VisualCatalogSnapshot
  /** A run or a saved version on view: nothing moves, connects, deletes or edits. */
  readOnly?: boolean
  selectedId?: string
  /** The primary node: the selected node when exactly one is selected, else undefined. */
  onSelect(id?: string): void
  /** Every change of the selection: the selected nodes, in the order they were selected, and a selected wire. */
  onSelectionChange?(ids: string[], edgeId?: string): void
  onChange?(graph: VisualGraph, options?: { coalesce?: string }): void
  diagnostics?: VisualDiagnostic[]
  addStepRequest?: number
  /** Each new value fits the view to the graph. */
  fitViewKey?: unknown
  trace?: FlowTrace
  /** Rendered at the end of the canvas toolbar (the editor page's "Full screen" button). */
  toolbarExtra?: JSX.Element
  /** Receives the canvas's own actions for the page around it (the inspector's Duplicate, the library's click-to-add). */
  controls?(controls: FlowControls): void
  /**
   * Opens the code editor of a Custom C# node: its card's "Edit code" ("View code" when read only) control, a
   * double-click on the card and E on the focused card. Without it, cards have no code control.
   */
  onEditCode?(id: string): void
}

/** What the page may ask of the canvas: the same placement the canvas uses for its own additions. */
export interface FlowControls {
  /** Duplicates the steps the way the canvas's Duplicate does (below any card in the way) and returns the copies' ids. */
  duplicate(ids: string[]): string[]
  /** Where a step added at the centre of the view goes: there, or below the cards in the way. */
  placeAtCentre(): { x: number; y: number }
  /** Pans, if needed, so the step's card is visible above `bottom` (a client y, such as the top of a bottom sheet). */
  reveal(id: string, bottom: number): void
  /**
   * Shows a message in the canvas's notice (why a step could not be added, for example) for a few seconds. One to
   * `keep` (what a change removed) stays until it is dismissed or the graph changes again.
   */
  notify(text: string, options?: { keep?: boolean }): void
}

type Point = { x: number; y: number }
type Connection = { source: string; target: string; sourceHandle?: string | null; targetHandle?: string | null }
type Selection = { nodes: string[]; edge?: string }
type Flow = ReturnType<typeof useSolidFlow>

const GRID = 20
const LONG_PRESS_MS = 550
const fitOptions = { padding: 0.15, maxZoom: 1, minZoom: 0.5 }
/**
 * A run's path as it opens, all of it, however far out a 360 px screen needs: the trace canvas zooms out further than
 * the editor's (a map of the path, to tap a step on), and the person zooms in from there.
 */
const TRACE_MIN_ZOOM = 0.1
const pathFitOptions = { padding: 0.1, maxZoom: 1, minZoom: TRACE_MIN_ZOOM }
/** The zoom below which cards are hard to read: on a wide screen a run never opens further out than this. */
const READABLE_ZOOM = 0.5
const sameSelection = (a: Selection, b: Selection) => a.edge === b.edge && a.nodes.length === b.nodes.length && a.nodes.every((id, i) => id === b.nodes[i])
const samePin = (a: PinRef | undefined, b: PinRef | undefined) => a === b || (!!a && !!b && a.nodeId === b.nodeId && a.handle === b.handle && a.type === b.type)
const clamp = (value: number, low: number, high: number) => Math.max(low, Math.min(value, high))
const motion = () => (typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 160)
/** Focus is somewhere keys mean text or a list choice: canvas shortcuts leave those keys alone. */
const typing = (target: Element) =>
  !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="menu"], [role="listbox"], .hs-card-editor, .hs-quick-add')


/**
 * The canvas. The rest of the app depends only on `VisualGraph`, never on Solid Flow stores or events: cards read
 * their view from a per-node cache keyed by node content, so a drag or a hover never re-derives ports or summaries.
 * The adapter owns the selection (the library's would be wiped by every graph change, D1) and every way of making
 * and removing wires, so the canvas is the one place connections are edited.
 */
export const FlowAdapter: Component<FlowAdapterProps> = props => {
  let canvas!: HTMLDivElement
  let flow: Flow | undefined
  const catalog = () => props.catalog ?? nodeCatalog
  const readOnly = () => !!props.readOnly
  const graph = createMemo(() => renderableGraph(props.graph))
  const palette = () => catalogPalette(catalog())
  const editable = !!useContext(QueryClientContext) && !!useContext(HassCtx)
  // Registry names for summaries; only inside the app's providers (a bare adapter in a test has none).
  const names = useLoadedRegistry()
  // Value chips name registry items the way the inline editors do (one resolver per registry snapshot).
  const nameResolver = createMemo<NameResolver>(() => registryNames(names()))
  /** Every canvas edit is made on what the canvas shows and applied to the original graph (malformed entries stay). */
  const change = (next: VisualGraph, options?: { coalesce?: string }) => {
    if (readOnly() || !props.onChange || next === graph()) return
    props.onChange(graph() === props.graph ? next : keepUnrendered(props.graph, next), options)
  }

  const [rejected, setRejected] = createSignal<{ nodeId: string; handle: string; message: string }>()
  const [activeHandle, setActiveHandle] = createSignal<PinRef>()
  const [dragFrom, setDragFrom] = createSignal<PinRef>()
  createEffect(
    on(
      () => [props.graph, catalog(), readOnly()] as const,
      () => {
        setRejected(undefined)
        setActiveHandle(undefined)
        setDragFrom(undefined)
      },
      { defer: true },
    ),
  )
  const [notice, setNotice] = createSignal<{ text: string; keep: boolean }>()
  let noticeTimer: ReturnType<typeof setTimeout> | undefined
  /** The graph a kept notice is about: the next change ends it. */
  let keptFor: VisualGraph | undefined
  const notify = (text: string, options: { keep?: boolean } = {}) => {
    clearTimeout(noticeTimer)
    const keep = options.keep === true
    setNotice({ text, keep })
    keptFor = keep ? untrack(() => props.graph) : undefined
    if (!keep) noticeTimer = setTimeout(() => setNotice(undefined), 5000)
  }
  const dismissNotice = () => { clearTimeout(noticeTimer); keptFor = undefined; setNotice(undefined) }
  createEffect(on(() => props.graph, current => { if (keptFor && current !== keptFor) dismissNotice() }, { defer: true }))
  onCleanup(() => clearTimeout(noticeTimer))

  // Per-node views, rebuilt only for nodes whose content, wires, resolved types or diagnostics changed.
  let cache = new Map<string, { key: string; view: NodeView }>()
  let cacheFor: [VisualCatalogSnapshot, SummaryNames | undefined] | undefined
  const views = createMemo(() => {
    const ctx: ViewContext = { graph: graph(), catalog: catalog(), diagnostics: props.diagnostics ?? [], names: names(), rejected: rejected() }
    if (!cacheFor || cacheFor[0] !== ctx.catalog || cacheFor[1] !== ctx.names) cache = new Map()
    cacheFor = [ctx.catalog, ctx.names]
    const index = connectionIndex(ctx.graph, ctx.catalog)
    const next = new Map<string, { key: string; view: NodeView }>()
    for (const node of ctx.graph.nodes) {
      const key = viewKey(node, ctx, index)
      const hit = cache.get(node.id)
      next.set(node.id, hit?.key === key ? hit : { key, view: buildNodeView(node, ctx) })
    }
    cache = next
    return next
  })
  const pinView = (pin: PinRef): PinView | undefined => {
    const view = views().get(pin.nodeId)?.view
    if (!view) return
    const side = pin.type === 'source' ? 'out' : 'in'
    const pins = [view.execIn, view.next, view.footer, ...view.rows.flatMap(row => [row.input?.pin, row.output])]
    return pins.find(p => p?.id === pin.handle && p.side === side)
  }

  // ---- Selection: several nodes, or one wire. The last node selected is the primary one.
  const [selection, setSelection] = createSignal<Selection>({ nodes: props.selectedId ? [props.selectedId] : [] }, { equals: sameSelection })
  const selectedSet = createMemo(() => new Set(selection().nodes))
  const primary = (current: Selection) => (current.nodes.length === 1 ? current.nodes[0] : undefined)
  // Steps just added, pasted or duplicated, waiting for a parent that applies `onChange` later to show them.
  let pendingSelect: string[] | undefined
  const present = (ids: string[]) => ids.every(id => graph().nodes.some(n => n.id === id))
  const select = (nodes: string[], edge?: string) => {
    pendingSelect = undefined
    const next: Selection = { nodes: [...new Set(nodes)].filter(id => graph().nodes.some(n => n.id === id)), ...(edge ? { edge } : {}) }
    setSelection(next)
    props.onSelect(primary(next))
  }
  /** Selects new steps now, or as soon as they appear in the graph. */
  const selectAdded = (ids: string[]) => {
    if (present(ids)) return select(ids)
    pendingSelect = ids
  }
  const toggle = (id: string) => {
    const current = selection().nodes
    select(current.includes(id) ? current.filter(other => other !== id) : [...current, id])
  }
  // The parent moved the primary selection (the inspector, a problem, a new node).
  createEffect(
    on(
      () => props.selectedId,
      id => {
        if (id === primary(untrack(selection))) return
        setSelection(id ? { nodes: [id] } : { nodes: [] })
      },
      { defer: true },
    ),
  )
  // Removed nodes and wires leave the selection.
  createEffect(
    on(
      graph,
      current => {
        if (pendingSelect && present(pendingSelect)) return select(pendingSelect)
        const before = untrack(selection)
        const nodes = before.nodes.filter(id => current.nodes.some(n => n.id === id))
        const edge = before.edge && current.edges.some(e => e.id === before.edge) ? before.edge : undefined
        if (nodes.length === before.nodes.length && edge === before.edge) return
        setSelection({ nodes, ...(edge ? { edge } : {}) })
        if (primary(before) !== primary({ nodes })) props.onSelect(primary({ nodes }))
      },
      { defer: true },
    ),
  )
  // Reported once on mount (the initial selection), then on every change.
  createEffect(on(selection, current => props.onSelectionChange?.(current.nodes, current.edge)))

  // ---- Connections. Hover asks the connection index only; a drop runs the full check once, inside connectData (D12).
  const quickError = (connection: Connection): string | undefined => {
    const current = graph()
    const source = current.nodes.find(n => n.id === connection.source),
      target = current.nodes.find(n => n.id === connection.target)
    if (!source || !target) return 'Connection endpoint missing'
    const output = dataPortOf(connection.sourceHandle),
      input = dataPortOf(connection.targetHandle)
    if (output !== undefined && input !== undefined) return dataConnectionError(current, source.id, output, target.id, input, catalog(), { quick: true })
    if (output !== undefined || input !== undefined) return 'Execution and data pins cannot connect'
    if (connection.targetHandle !== 'in' || !canConnect(current, source.id, target.id, connection.sourceHandle ?? undefined, catalog()))
      return 'This output is already connected, or the wire would make a loop'
  }
  const reject = (target: { nodeId: string; handle: string }, message: string) =>
    setRejected(current => (current?.nodeId === target.nodeId && current.handle === target.handle && current.message === message ? current : { ...target, message }))
  const connect = (connection: Connection) => {
    if (readOnly()) return
    const refusedAt = { nodeId: connection.target, handle: connection.targetHandle ?? 'in' }
    const message = quickError(connection)
    if (message) return reject(refusedAt, message)
    const output = dataPortOf(connection.sourceHandle),
      input = dataPortOf(connection.targetHandle)
    let next: VisualGraph
    try {
      next =
        output !== undefined && input !== undefined
          ? connectData(graph(), connection.source, output, connection.target, input, catalog())
          : connectNodes(graph(), connection.source, connection.target, connection.sourceHandle ?? undefined, catalog())
    } catch (error) {
      return reject(refusedAt, error instanceof Error ? error.message : String(error))
    }
    setRejected(undefined)
    setActiveHandle(undefined)
    change(next)
  }
  const activateHandle = (nodeId: string, handle: string, type: 'source' | 'target') => {
    if (readOnly()) return
    const start = activeHandle()
    if (!start || start.type === type) {
      setActiveHandle({ nodeId, handle, type })
      setRejected(undefined)
      return
    }
    const from = type === 'target' ? start : { nodeId, handle, type },
      to = type === 'target' ? { nodeId, handle, type } : start
    connect({ source: from.nodeId, sourceHandle: from.handle, target: to.nodeId, targetHandle: to.handle })
  }
  // While a wire is dragged (or a pin is picked from the keyboard), pins it could end on glow; others dim. The
  // states are worked out once per starting pin, never per pointer move.
  const origin = createMemo(() => dragFrom() ?? activeHandle(), undefined, { equals: samePin })
  const pinStates = createMemo(() => {
    const from = origin()
    const states = new Map<string, PinState>()
    if (!from) return states
    // Keys carry the side: inputs and outputs share handle ids (`data:value` on both sides of Not).
    states.set(`${from.nodeId}|${from.type}|${from.handle}`, 'origin')
    const side = from.type === 'source' ? 'target' : 'source'
    const data = dataPortOf(from.handle) !== undefined
    for (const [id, entry] of untrack(views)) {
      if (id === from.nodeId) continue
      const view = entry.view
      const pins = from.type === 'source'
        ? [...(view.execIn ? [view.execIn] : []), ...view.rows.flatMap(r => (r.input ? [r.input.pin] : []))]
        : [...(view.next ? [view.next] : []), ...view.rows.flatMap(r => (r.output ? [r.output] : [])), ...(view.footer ? [view.footer] : [])]
      for (const pin of pins) {
        if ((pin.kind === 'data') !== data) continue
        const connection = from.type === 'source'
          ? { source: from.nodeId, sourceHandle: from.handle, target: id, targetHandle: pin.id }
          : { source: id, sourceHandle: pin.id, target: from.nodeId, targetHandle: from.handle }
        let ok = false
        try {
          ok = !quickError(connection)
        } catch {
          ok = false
        }
        states.set(`${id}|${side}|${pin.id}`, ok ? 'compatible' : 'incompatible')
      }
    }
    return states
  })

  const setNodeInput = (nodeId: string, port: string, operand: Operand, options?: { coalesce?: string }) => {
    const node = graph().nodes.find(n => n.id === nodeId)
    if (!node || readOnly()) return
    const next = structuredClone(node)
    writeInput(next, port, operand, catalog())
    change(updateNode(graph(), nodeId, { config: next.config }), options)
  }
  const locateProblem = (nodeId: string) => {
    select([nodeId])
    const problem = views().get(nodeId)?.view.problems[0]
    const port = problem?.inputId ?? problem?.portId
    // The inspector renders the selected node after this; focus its field for the problem.
    queueMicrotask(() =>
      queueMicrotask(() => {
        const root = canvas.closest('.hs-visual') ?? canvas.ownerDocument
        const field = [...root.querySelectorAll<HTMLElement>('[data-input-id]')].find(el => !canvas.contains(el) && (!port || el.dataset.inputId === port))
        const target = field?.querySelector<HTMLElement>('input,select,textarea,button') ?? field
        target?.focus()
        target?.scrollIntoView?.({ block: 'nearest' })
      }),
    )
  }
  const focusCanvas = () => canvas.focus({ preventScroll: true })
  const removeWire = (id: string) => {
    if (readOnly()) return
    if (selection().edge === id) select([])
    change(removeEdge(graph(), id, catalog()))
    focusCanvas()
  }

  // Alt-click removes a wire.
  const edgeClick = (event: MouseEvent) => {
    const edge = (event.target as Element).closest('.solid-flow__edge')
    if (!edge || !event.altKey || event.button !== 0 || readOnly()) return
    const id = edge.getAttribute('data-id')
    if (!id) return
    event.preventDefault()
    event.stopImmediatePropagation()
    pending = undefined
    setActiveHandle(undefined)
    closeQuickAdd()
    suppressPaneClick = true
    removeWire(id)
    queueMicrotask(() => {
      suppressPaneClick = false
    })
  }

  // ---- Quick-add: double-click, right-click, long-press, `A`, the toolbar, "Add step", or a pin dropped on empty space.
  /** `returnTo`: what had focus when it opened, which gets it back when it closes from the keyboard. */
  const [quickAdd, setQuickAdd] = createSignal<{ screen: Point; position: Point; pin?: PinRef; returnTo?: HTMLElement }>()
  const toFlow = (point: Point) => (flow ? flow.screenToFlowPosition(point, { snapToGrid: false }) : point)
  const [snap, setSnap] = createSignal(false)
  const [minimap, setMinimap] = createSignal(false)
  const place = (point: Point) => (snap() ? { x: Math.round(point.x / GRID) * GRID, y: Math.round(point.y / GRID) * GRID } : { x: Math.round(point.x), y: Math.round(point.y) })
  let pending: PinRef | undefined
  const cancelPending = () => {
    pending = undefined
    setDragFrom(undefined)
  }
  let suppressPaneClick = false
  const [gestureMoved, setGestureMoved] = createSignal(false)
  /** A person has panned or zoomed this canvas (the canvas's own fits and centring do not count). */
  let viewMoved = false
  let gestureStart: Point | undefined
  let pointerHeld = false
  let lastPointer: Point | undefined
  const activeElement = () => (canvas.getRootNode() as Document | ShadowRoot).activeElement
  /** Focus back to `element` while it is still on the page, else to the canvas: never to the page body. */
  const refocus = (element?: HTMLElement) => (element?.isConnected ? element.focus({ preventScroll: true }) : focusCanvas())
  /**
   * Closes the palette. Focus inside it (its search field) would fall to the page body when it unmounts, so it goes
   * back to where it was when the palette opened; so it does when `restore` asks (Escape, Tab, a choice).
   */
  const closeQuickAdd = (restore = false) => {
    const request = quickAdd()
    if (!request) return
    const focused = !!canvas.querySelector('.hs-quick-add')?.contains(activeElement())
    setQuickAdd(undefined)
    if (restore || focused) refocus(request.returnTo)
  }
  let focusFrame = 0
  /** Pointer presses on the canvas so far: a press after a step was added means the user has moved on. */
  let presses = 0
  onCleanup(() => cancelAnimationFrame(focusFrame))
  /** Focuses a new step's card once it is on the canvas (a parent may apply the change a little later). */
  const focusCard = (id: string) => {
    cancelAnimationFrame(focusFrame)
    let frames = 0
    const from = activeElement()
    const pressed = presses
    const attempt = () => {
      const card = [...canvas.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.getAttribute('data-id') === id)
      // The user has moved on meanwhile (focus went elsewhere, or they pressed on the canvas): leave it alone.
      if (presses !== pressed || (activeElement() !== from && activeElement() !== card)) return
      // A card is hidden until it has been measured, and a hidden card does not take focus: try again next frame.
      card?.focus({ preventScroll: true })
      if ((!card || activeElement() !== card) && ++frames < 60) focusFrame = requestAnimationFrame(attempt)
    }
    attempt()
  }
  const centre = (): Point => {
    const rect = canvas.getBoundingClientRect()
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
  }
  const openQuickAdd = (screen: Point, pin?: PinRef, position?: Point) => {
    if (readOnly()) return
    const focused = activeElement()
    const returnTo = focused instanceof HTMLElement && focused !== canvas.ownerDocument.body && !focused.closest('.hs-quick-add') ? focused : quickAdd()?.returnTo
    setQuickAdd({ screen, position: position ?? toFlow(screen), pin, returnTo })
  }
  // ---- Room for new steps: an added step never covers a card already there.
  type Rect = Point & { width: number; height: number }
  const near = (a: Rect, b: Rect, margin = 20) =>
    a.x < b.x + b.width + margin && b.x < a.x + a.width + margin && a.y < b.y + b.height + margin && b.y < a.y + a.height + margin
  const cards = (): Rect[] =>
    graph().nodes.map((node, index) => {
      const internal = flow?.getInternalNode(node.id)
      const at = internal?.internals.positionAbsolute ?? node.position ?? { x: 60 + index * 300, y: 100 }
      return { x: at.x, y: at.y, width: internal?.measured.width || 260, height: internal?.measured.height || 120 }
    })
  /** The size of a typical card on this canvas (the median measured one), for a step not drawn yet. */
  const typicalSize = () => {
    const measured = graph().nodes.flatMap(node => {
      const size = flow?.getInternalNode(node.id)?.measured
      return size?.width && size.height ? [size as { width: number; height: number }] : []
    })
    if (!measured.length) return { width: 260, height: 120 }
    const middle = (values: number[]) => values.sort((a, b) => a - b)[Math.floor(values.length / 2)]
    return { width: middle(measured.map(m => m.width)), height: middle(measured.map(m => m.height)) }
  }
  /**
   * How far down `boxes` (moved together) must go so that none comes within 20 px of a card already there: each
   * time one does, they move to 40 px below that card. On the grid when snapping is on.
   */
  const roomBelow = (boxes: Rect[]) => {
    const others = cards()
    let dy = 0
    for (let i = 0; i < others.length; i++) {
      let hit: [Rect, Rect] | undefined
      for (const box of boxes) {
        const other = others.find(o => near({ ...box, y: box.y + dy }, o))
        if (other) { hit = [box, other]; break }
      }
      if (!hit) break
      dy = hit[1].y + hit[1].height + 40 - hit[0].y
    }
    return snap() ? Math.ceil(dy / GRID) * GRID : Math.round(dy)
  }
  /** Where a new step asked for at `point` goes: there, or below the cards in the way. */
  const placeNew = (point: Point) => {
    const at = place(point)
    return { x: at.x, y: at.y + roomBelow([{ ...at, ...typicalSize() }]) }
  }
  /** Beside the pin's card: to its right for an output, to its left for an input. */
  const besidePin = (pin: PinRef): Point | undefined => {
    const node = flow?.getInternalNode(pin.nodeId)
    if (!node) return
    const { x, y } = node.internals.positionAbsolute
    return pin.type === 'source' ? { x: x + (node.measured.width ?? 260) + 100, y } : { x: x - 360, y }
  }
  const quickAddTitle = (pin?: PinRef) => {
    if (!pin) return 'Add a step'
    const label = pinView(pin)?.label ?? ''
    const title = nodeTitle(graph().nodes.find(n => n.id === pin.nodeId), catalog())
    // A named output says which one: "Connect State condition · If true to…".
    if (pin.type === 'source') return `Connect ${pin.handle === 'next' || !label ? title : `${title} · ${label}`} to…`
    return dataPortOf(pin.handle) !== undefined ? `Get ${label} from…` : `Connect a step to ${title}`
  }
  const create = (kind: NodeKind) => {
    const request = quickAdd()
    closeQuickAdd(true)
    if (readOnly() || !request) return
    const position = placeNew(request.position)
    if (!request.pin) {
      const next = createNodeAt(graph(), kind, position, undefined, catalog())
      if (next === graph()) return
      const id = next.nodes.at(-1)!.id
      change(next)
      selectAdded([id])
      focusCard(id)
      return
    }
    const result = addConnected(graph(), kind, position, request.pin, catalog())
    if (!result) return
    setActiveHandle(undefined)
    change(result.graph)
    selectAdded([result.nodeId])
    focusCard(result.nodeId)
    if (result.error && result.refused) reject(result.refused, result.error)
    if (result.notice) notify(result.notice)
  }

  const Viewport = () => {
    const api = useSolidFlow()
    flow = api
    const owner = getOwner()
    /** Runs once every card has been measured (their sizes are unknown on the first frames), or after a second. */
    const whenMeasured = (run: () => void) => {
      let frames = 0
      let frame = 0
      const step = () => {
        const ready = graph().nodes.every(n => api.getInternalNode(n.id)?.measured.width)
        if (!ready && ++frames < 60) {
          frame = requestAnimationFrame(step)
          return
        }
        run()
      }
      frame = requestAnimationFrame(step)
      onCleanup(() => cancelAnimationFrame(frame))
    }
    createEffect(
      on(
        () => props.addStepRequest,
        request => {
          if (request) openQuickAdd(centre())
        },
      ),
    )
    createEffect(
      on(
        () => props.fitViewKey,
        key => {
          if (key !== undefined) whenMeasured(() => void api.fitView(fitOptions))
        },
      ),
    )
    const wide = media('(min-width: 900px)')
    const centreOn = (id: string, zoom: (current: number) => number, duration?: number) => {
      const node = api.getInternalNode(id)
      const width = node?.measured.width, height = node?.measured.height
      if (!node || !width || !height) return
      const { x, y } = node.internals.positionAbsolute
      void api.setCenter(x + width / 2, y + height / 2, { zoom: zoom(api.getZoom()), duration })
    }
    // A run opens on its path (the steps it reached), once: a live run reaching more steps leaves the view to the
    // person. The whole path when that keeps the cards readable (zoom 0.5 or more). Otherwise, on a screen 900 px or
    // wider, at zoom 0.5 centred on the step that failed, or on the path's first step (its trigger); narrower, the
    // whole path however far out, except that a run with a failed step opens centred on it (the focus, below).
    let opening: 'pending' | 'done' | undefined
    // How the view opened (the canvas's own fit unless a run's opening view or focus took over), to take it again once
    // the trace view has moved overlapping cards apart (below): that can push cards out of a view fitted before.
    let openingView: () => void = () => void api.fitView(fitOptions)
    createEffect(
      on(
        () => props.trace,
        trace => {
          if (opening || !trace) return
          const reached = Object.keys(trace.nodes).filter(id => graph().nodes.some(n => n.id === id))
          if (!reached.length) return
          opening = 'done'
          if (!wide() && trace.focusNodeId) return
          opening = 'pending'
          openingView = () => {
            const focus = untrack(() => props.trace?.focusNodeId)
            // The zoom the whole path needs in this canvas, as fitView works it out.
            const { zoom } = getViewportForBounds(api.getNodesBounds(reached), canvas.clientWidth, canvas.clientHeight, pathFitOptions.minZoom, pathFitOptions.maxZoom, pathFitOptions.padding)
            if (zoom >= READABLE_ZOOM || !wide()) return void api.fitView({ ...pathFitOptions, nodes: reached.map(id => ({ id })) })
            const first = reached.find(id => graph().nodes.find(n => n.id === id)?.kind === 'trigger') ?? reached[0]
            centreOn(focus ?? first, () => READABLE_ZOOM)
          }
          whenMeasured(() => {
            opening = 'done'
            openingView()
          })
        },
      ),
    )
    let separated = false
    // In the read-only trace view a card never overlaps another: one that has grown taller than the stored layout
    // allowed for (a run's values, a newer card design) pushes the cards below it down, for this view only. Once every
    // card is measured, and again when one changes size; the graph keeps its positions.
    createEffect(() => {
      if (!readOnly()) return untrack(() => spread().size && setSpread(new Map()))
      const boxes: LayoutBox[] = []
      for (const [index, node] of graph().nodes.entries()) {
        const size = api.getInternalNode(node.id)?.measured
        if (!size?.width || !size.height) return
        boxes.push({ id: node.id, ...basePosition(node, index), width: size.width, height: size.height })
      }
      const next = separateCards(boxes)
      const current = untrack(spread)
      if (next.size !== current.size || [...next].some(([id, at]) => current.get(id)?.x !== at.x || current.get(id)?.y !== at.y)) {
        setSpread(next)
        // The first time cards move apart, the view opened before may leave some outside it: open it again, on the
        // moved cards, unless the person has already moved the view. Once only; later size changes leave the view be.
        if (next.size && !separated) {
          separated = true
          // Owned by the canvas, not this effect: its next run (a card measured again) must not cancel it.
          if (!viewMoved) runWithOwner(owner, () => whenMeasured(() => { if (!viewMoved) openingView() }))
        }
      }
    })
    // Centre the run's focus node once it is measured, then leave the viewport to the user: a new trace object for
    // the same node (a live run polling) does not move the view again. On a wide screen the opening view (above) takes
    // in the focus a run opens with.
    const focusNode = createMemo(() => props.trace?.focusNodeId)
    createEffect(
      on(focusNode, id => {
        if (!id || (opening === 'pending' && wide())) return
        if (!separated) openingView = () => centreOn(id, zoom => Math.max(zoom, 0.8))
        whenMeasured(() => centreOn(id, zoom => Math.max(zoom, 0.8), 200))
      }),
    )
    return null
  }
  const emptyPane = (element: EventTarget | null) => element instanceof Element && canvas.contains(element) && element.classList.contains('solid-flow__pane')
  const quickAddOffset = (screen: Point) => {
    const rect = canvas.getBoundingClientRect()
    const width = Math.min(320, Math.max(0, rect.width - 16)), height = Math.min(420, Math.max(0, rect.height - 16))
    return { x: clamp(screen.x - rect.left, 8, rect.width - width - 8), y: clamp(screen.y - rect.top, 8, rect.height - height - 8) }
  }

  let touchDrag: { id: string; graph: VisualGraph } | undefined
  const [nodes, setNodes] = createStore<Node[]>([])
  /** Where a stored graph puts a card (one without a position goes in a row, as it always has). */
  const basePosition = (node: VisualGraph['nodes'][number], index: number): Point => (node.position ? { ...node.position } : { x: 60 + index * 300, y: 100 })
  /** Read-only only: the cards moved down so that none overlaps another (see the Viewport). */
  const [spread, setSpread] = createSignal<ReadonlyMap<string, Point>>(new Map())
  const [edges, setEdges] = createStore<Edge[]>([])
  /** One graph change for a finished drag, however many nodes it moved. */
  const commitGeometry = () => {
    if (readOnly()) return
    const current = graph()
    const moved = current.nodes.map(node => {
      const flowNode = nodes.find(n => n.id === node.id)
      if (!flowNode) return node
      const same = node.position?.x === flowNode.position.x && node.position?.y === flowNode.position.y
      return same ? node : { ...node, position: { ...flowNode.position } }
    })
    if (moved.some((node, i) => node !== current.nodes[i])) change({ ...current, nodes: moved })
  }
  // Solid Flow 0.2.6 rebinds D3 touch handlers on every node update. Own only
  // single-touch node-body movement here; mouse and handles stay native.
  const TouchNodeDrag = () => {
    const api = useSolidFlow()
    let gesture: { identifier: number; origin: Point; start: Point; moved: boolean } | undefined
    let claimed = false
    const point = (touch: Touch) => api.screenToFlowPosition({ x: touch.clientX, y: touch.clientY }, { snapToGrid: false })
    const consume = (event: TouchEvent) => {
      event.stopPropagation()
      event.preventDefault()
    }
    const finish = () => {
      const current = gesture
      gesture = undefined
      const sameGraph = touchDrag?.graph === props.graph
      touchDrag = undefined
      if (current?.moved && sameGraph) commitGeometry()
    }
    const start = (event: TouchEvent) => {
      if (claimed) {
        consume(event)
        if (event.touches.length > 1) finish()
        return
      }
      if (readOnly()) return
      const target = event.target as Element
      if (
        event.touches.length !== 1 ||
        target.closest('.solid-flow__handle,.solid-flow__resize-control,.nodrag,input,textarea,select,button,a,[contenteditable="true"]')
      )
        return
      const id = target.closest('.solid-flow__node')?.getAttribute('data-id')
      const node = nodes.find(node => node.id === id)
      if (!node) return
      const touch = event.touches[0]
      claimed = true
      consume(event)
      touchDrag = { id: node.id, graph: props.graph }
      gesture = { identifier: touch.identifier, origin: { ...node.position }, start: point(touch), moved: false }
      if (!selectedSet().has(node.id) || selection().nodes.length !== 1) select([node.id])
    }
    const move = (event: TouchEvent) => {
      if (!claimed) return
      consume(event)
      // A second finger ends this node drag; background pinch/pan is untouched.
      if (event.touches.length !== 1 || touchDrag?.graph !== props.graph) {
        finish()
        return
      }
      if (!gesture || !touchDrag) return
      const touch = Array.from(event.touches).find(touch => touch.identifier === gesture!.identifier)
      if (!touch) {
        finish()
        return
      }
      const next = point(touch)
      const position = { x: gesture.origin.x + next.x - gesture.start.x, y: gesture.origin.y + next.y - gesture.start.y }
      const snapped = snap() ? place(position) : position
      gesture.moved ||= snapped.x !== gesture.origin.x || snapped.y !== gesture.origin.y
      setNodes(node => node.id === touchDrag!.id, 'position', snapped)
    }
    const end = (event: TouchEvent) => {
      if (!claimed) return
      // Document events also include fingers that never belonged to this drag.
      // Once multi-touch has canceled it, any final release can clear ownership.
      if (gesture && !Array.from(event.changedTouches).some(touch => touch.identifier === gesture!.identifier)) return
      if (canvas.contains(event.target as HTMLElement)) consume(event)
      finish()
      if (!event.touches.length) claimed = false
    }
    // End may belong to another finger outside the canvas. Graph replacement
    // may detach the original target so no terminal event can reach us at all.
    createEffect(
      on(
        () => props.graph,
        graph => {
          if (touchDrag && touchDrag.graph !== graph) {
            finish()
            claimed = false
          }
        },
      ),
    )
    onMount(() => {
      canvas.addEventListener('touchstart', start, { capture: true, passive: false })
      canvas.addEventListener('touchmove', move, { capture: true, passive: false })
      canvas.ownerDocument.addEventListener('touchend', end, { capture: true, passive: false })
      canvas.ownerDocument.addEventListener('touchcancel', end, { capture: true, passive: false })
    })
    onCleanup(() => {
      touchDrag = undefined
      canvas.removeEventListener('touchstart', start, true)
      canvas.removeEventListener('touchmove', move, true)
      canvas.ownerDocument.removeEventListener('touchend', end, true)
      canvas.ownerDocument.removeEventListener('touchcancel', end, true)
    })
    return null
  }

  // Flow nodes carry only identity, position and selection; cards read everything else from their view.
  createEffect(
    on(
      () => [graph(), views(), props.trace, spread()] as const,
      ([current, built, trace, moved]) =>
        untrack(() =>
          setNodes(
            reconcile(
              current.nodes.map((node, index) => ({
                id: node.id,
                type: 'card',
                data: {},
                position:
                  touchDrag?.graph === props.graph && touchDrag.id === node.id
                    ? { ...nodes.find(n => n.id === node.id)!.position }
                    : { ...(moved.get(node.id) ?? basePosition(node, index)) },
                domAttributes: { 'aria-label': `${built.get(node.id)?.view.label ?? node.id}${trace ? `. Run: ${traceText(trace.nodes[node.id])}` : ''}` },
                selected: selectedSet().has(node.id),
              })),
              { key: 'id' },
            ),
          ),
        ),
    ),
  )
  createEffect(
    on(
      () => [graph(), catalog(), props.trace] as const,
      ([current, snapshot, trace]) =>
        untrack(() =>
          setEdges(
            reconcile(
              current.edges.flatMap(edge => {
                const view = buildEdgeView(edge, current, snapshot)
                if (!view) return []
                const travelled = trace?.edges.has(edge.id)
                return [{
                  id: view.id,
                  source: view.source,
                  target: view.target,
                  sourceHandle: view.sourceHandle,
                  targetHandle: view.targetHandle,
                  type: view.kind,
                  data: { color: view.color, error: view.error },
                  class: trace ? (travelled ? 'hs-wire-group hs-wire--travelled' : 'hs-wire-group hs-wire--idle') : 'hs-wire-group',
                  ariaLabel: `${view.label}${trace ? (travelled ? ', ran' : ', not taken') : ''}`,
                  zIndex: view.kind === 'exec' ? 1 : 0,
                  selected: selection().edge === view.id,
                }]
              }),
              { key: 'id' },
            ),
          ),
        ),
    ),
  )
  // The library marks selected nodes and wires on these objects; the adapter's selection is the source of truth.
  const pushSelection = (current: Selection) => {
    const chosen = new Set(current.nodes)
    setNodes(produce(list => list.forEach(node => { if (!!node.selected !== chosen.has(node.id)) node.selected = chosen.has(node.id) })))
    setEdges(produce(list => list.forEach(edge => { if (!!edge.selected !== (edge.id === current.edge)) edge.selected = edge.id === current.edge })))
  }
  createEffect(on(selection, pushSelection))

  // ---- Multi-node actions: each is one graph change, so one undo step.
  const targets = (target?: Element) => {
    const id = target?.closest('.solid-flow__node')?.getAttribute('data-id')
    return id && !selectedSet().has(id) ? [id] : selection().nodes
  }
  const deleteSelection = (target: Element) => {
    if (readOnly()) return
    const edgeId = target.closest('.solid-flow__edge')?.getAttribute('data-id')
    if (edgeId) return removeWire(edgeId)
    const ids = targets(target)
    if (ids.length) {
      let next = graph()
      for (const id of ids) next = removeNode(next, id, catalog())
      select([])
      change(next)
      focusCanvas()
      return
    }
    const edge = selection().edge
    if (edge) removeWire(edge)
  }
  const steps = (count: number) => `${count} step${count === 1 ? '' : 's'}`
  const copy = (target: Element) => {
    const data = copySteps(graph(), targets(target))
    if (!data) return
    void writeSteps(data)
    notify(`Copied ${steps(data.nodes.length)}`)
  }
  let lastPaste: { data: StepClipboard; count: number } | undefined
  const insert = (data: StepClipboard, placement: { at: Point } | { offset: Point }, verb = 'Pasted'): string[] => {
    const result = pasteSteps(graph(), data, catalog(), placement, snap() ? GRID : undefined)
    if ('error' in result) {
      notify(`Cannot ${verb === 'Pasted' ? 'paste' : 'duplicate'}: ${result.error}`)
      return []
    }
    // Moved down together, below any card the copies would cover.
    const added = new Set(result.ids)
    const size = typicalSize()
    const dy = roomBelow(result.graph.nodes.filter(node => added.has(node.id)).map(node => ({ x: node.position?.x ?? 0, y: node.position?.y ?? 0, ...size })))
    change(dy ? { ...result.graph, nodes: result.graph.nodes.map(node => (added.has(node.id) ? { ...node, position: { x: node.position?.x ?? 0, y: (node.position?.y ?? 0) + dy } } : node)) } : result.graph)
    selectAdded(result.ids)
    return result.ids
  }
  const paste = async () => {
    if (readOnly()) return
    const data = await readSteps()
    if (!data) return notify('Nothing to paste: copy steps first')
    // At the pointer when it is over the canvas; from the keyboard alone, a little below the originals (further
    // each time the same copy is pasted again).
    if (lastPointer) return void insert(data, { at: toFlow(lastPointer) })
    const count = lastPaste && JSON.stringify(lastPaste.data) === JSON.stringify(data) ? lastPaste.count + 1 : 1
    lastPaste = { data, count }
    insert(data, { offset: { x: 40 * count, y: 40 * count } })
  }
  const duplicateSteps = (ids: string[]) => {
    if (readOnly()) return []
    const data = copySteps(graph(), ids)
    return data ? insert(data, { offset: { x: 40, y: 40 } }, 'Duplicated') : []
  }
  const duplicate = (target: Element) => void duplicateSteps(targets(target))
  const tidy = () => {
    if (readOnly()) return
    const sizes = (id: string) => {
      const measured = flow?.getInternalNode(id)?.measured
      return measured?.width && measured.height ? { width: measured.width, height: measured.height } : undefined
    }
    const next = applyLayout(graph(), tidyLayout(graph(), catalog(), { sizes, grid: GRID }))
    if (next === graph()) return notify('Already tidy')
    change(next)
    requestAnimationFrame(() => void flow?.fitView({ ...fitOptions, duration: motion() }))
  }
  const nudge = (ids: string[], [dx, dy]: [number, number], large: boolean) => {
    const step = (snap() ? GRID : 10) * (large ? 4 : 1)
    const moving = new Set(ids)
    const next = { ...graph(), nodes: graph().nodes.map(node => (moving.has(node.id) ? { ...node, position: { x: (node.position?.x ?? 60) + dx * step, y: (node.position?.y ?? 100) + dy * step } } : node)) }
    change(next)
  }
  props.controls?.({
    duplicate: duplicateSteps,
    placeAtCentre: () => placeNew(toFlow(centre())),
    notify,
    reveal: (id, bottom) => {
      const card = [...canvas.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.getAttribute('data-id') === id)
      if (!card || !flow) return
      const rect = card.getBoundingClientRect(), area = canvas.getBoundingClientRect()
      const top = area.top + 16, limit = Math.min(bottom, area.bottom) - 16
      // Up until its bottom clears the sheet, but never so far that its top leaves the canvas.
      let dy = rect.bottom > limit ? limit - rect.bottom : 0
      if (rect.top + dy < top) dy = top - rect.top
      if (Math.abs(dy) < 1) return
      const viewport = flow.getViewport()
      void flow.setViewport({ ...viewport, y: viewport.y + dy }, { duration: motion() })
    },
  })

  // ---- Marquee: Shift and drag on empty canvas adds the cards it touches to the selection.
  const [marquee, setMarquee] = createSignal<{ start: Point; end: Point; base: string[] }>()
  let suppressClick = false
  const inside = (a: Point, b: Point) => {
    const p = toFlow(a), q = toFlow(b)
    const left = Math.min(p.x, q.x), right = Math.max(p.x, q.x), top = Math.min(p.y, q.y), bottom = Math.max(p.y, q.y)
    return graph().nodes.flatMap(node => {
      const internal = flow?.getInternalNode(node.id)
      const at = internal?.internals.positionAbsolute ?? node.position ?? { x: 0, y: 0 }
      const width = internal?.measured.width ?? 0, height = internal?.measured.height ?? 0
      return at.x <= right && at.x + width >= left && at.y <= bottom && at.y + height >= top ? [node.id] : []
    })
  }
  const marqueeMove = (event: PointerEvent) => {
    const current = marquee()
    if (!current) return
    const end = { x: event.clientX, y: event.clientY }
    setMarquee({ ...current, end })
    setSelection({ nodes: [...new Set([...current.base, ...inside(current.start, end)])] })
  }
  const marqueeEnd = (event: PointerEvent) => {
    const current = marquee()
    canvas.ownerDocument.removeEventListener('pointermove', marqueeMove, true)
    canvas.ownerDocument.removeEventListener('pointerup', marqueeEnd, true)
    canvas.ownerDocument.removeEventListener('pointercancel', marqueeEnd, true)
    setMarquee(undefined)
    if (!current) return
    const end = event.type === 'pointercancel' ? current.end : { x: event.clientX, y: event.clientY }
    select([...new Set([...current.base, ...inside(current.start, end)])])
    // The click that ends the drag on the pane would clear the selection again.
    suppressClick = true
    setTimeout(() => (suppressClick = false))
  }
  const marqueeRect = () => {
    const current = marquee()
    if (!current) return
    const rect = canvas.getBoundingClientRect()
    return {
      left: `${Math.min(current.start.x, current.end.x) - rect.left}px`,
      top: `${Math.min(current.start.y, current.end.y) - rect.top}px`,
      width: `${Math.abs(current.end.x - current.start.x)}px`,
      height: `${Math.abs(current.end.y - current.start.y)}px`,
    }
  }

  // ---- Pointer and touch gestures the library does not have.
  let press: { timer: ReturnType<typeof setTimeout>; x: number; y: number } | undefined
  let pressedAt = 0
  const cancelPress = () => {
    if (press) clearTimeout(press.timer)
    press = undefined
  }
  const pressStart = (event: TouchEvent) => {
    cancelPress()
    if (readOnly() || event.touches.length !== 1 || !emptyPane(event.target)) return
    const touch = event.touches[0]
    press = {
      x: touch.clientX,
      y: touch.clientY,
      timer: setTimeout(() => {
        const at = press!
        press = undefined
        pressedAt = Date.now()
        openQuickAdd({ x: at.x, y: at.y })
      }, LONG_PRESS_MS),
    }
  }
  const pressMove = (event: TouchEvent) => {
    const touch = event.touches[0]
    if (press && (event.touches.length !== 1 || !touch || Math.hypot(touch.clientX - press.x, touch.clientY - press.y) > 10)) cancelPress()
  }
  const pointerDownCapture = (event: PointerEvent) => {
    pointerHeld = true
    presses++
    if (event.button !== 0 || !event.shiftKey || !event.isPrimary || !emptyPane(event.target)) return
    event.stopPropagation()
    event.preventDefault()
    closeQuickAdd()
    const start = { x: event.clientX, y: event.clientY }
    setMarquee({ start, end: start, base: selection().nodes })
    canvas.ownerDocument.addEventListener('pointermove', marqueeMove, true)
    canvas.ownerDocument.addEventListener('pointerup', marqueeEnd, true)
    canvas.ownerDocument.addEventListener('pointercancel', marqueeEnd, true)
  }
  // The marquee's pointer must not also pan the canvas (the library pans on mousedown).
  const mouseDownCapture = (event: MouseEvent) => {
    if (marquee()) event.stopPropagation()
  }
  const clickCapture = (event: MouseEvent) => {
    if (suppressClick && emptyPane(event.target)) {
      suppressClick = false
      event.stopImmediatePropagation()
      return
    }
    edgeClick(event)
  }
  const released = () => {
    pointerHeld = false
  }
  /** Whether the node's code can be opened: a Custom C# node, on a page that has the code editor. */
  const hasCode = (id: string) => {
    const kind = graph().nodes.find(n => n.id === id)?.kind
    return !!props.onEditCode && !!kind && Object.hasOwn(catalog().nodes, kind) && catalog().nodes[kind].dynamicInputs === 'custom'
  }
  const editCode = (id: string) => { if (hasCode(id)) props.onEditCode?.(id) }
  const doubleClick = (event: MouseEvent) => {
    const target = event.target as Element
    if (emptyPane(target)) return openQuickAdd({ x: event.clientX, y: event.clientY })
    // A double-click on a Custom C# card (not in its own editors, pins or buttons) opens its code.
    const id = target.closest?.('.solid-flow__node')?.getAttribute('data-id')
    if (id && hasCode(id) && !target.closest('.hs-card-editor, .solid-flow__handle, input, textarea, select, button, a')) {
      event.preventDefault()
      editCode(id)
    }
  }
  onMount(() => {
    const doc = canvas.ownerDocument
    canvas.addEventListener('click', clickCapture, true)
    canvas.addEventListener('pointerdown', pointerDownCapture, true)
    canvas.addEventListener('mousedown', mouseDownCapture, true)
    canvas.addEventListener('dblclick', doubleClick)
    canvas.addEventListener('touchstart', pressStart, { capture: true, passive: true })
    canvas.addEventListener('touchmove', pressMove, { capture: true, passive: true })
    // XYFlow listens for touchend, but not touchcancel. A later release must not
    // resurrect a canceled output-to-empty-space gesture.
    doc.addEventListener('touchcancel', cancelPending, true)
    for (const type of ['touchend', 'touchcancel']) doc.addEventListener(type, cancelPress, true)
    for (const type of ['pointerup', 'pointercancel']) doc.addEventListener(type, released, true)
    onCleanup(() => {
      cancelPress()
      canvas.removeEventListener('click', clickCapture, true)
      canvas.removeEventListener('pointerdown', pointerDownCapture, true)
      canvas.removeEventListener('mousedown', mouseDownCapture, true)
      canvas.removeEventListener('dblclick', doubleClick)
      canvas.removeEventListener('touchstart', pressStart, true)
      canvas.removeEventListener('touchmove', pressMove, true)
      doc.removeEventListener('touchcancel', cancelPending, true)
      for (const type of ['touchend', 'touchcancel']) doc.removeEventListener(type, cancelPress, true)
      for (const type of ['pointerup', 'pointercancel']) doc.removeEventListener(type, released, true)
      doc.removeEventListener('pointermove', marqueeMove, true)
      doc.removeEventListener('pointerup', marqueeEnd, true)
      doc.removeEventListener('pointercancel', marqueeEnd, true)
    })
  })

  const types = createMemo(() => graphTypes(graph(), catalog()))
  const context: FlowContextValue = {
    readOnly,
    catalog,
    types,
    view: id => views().get(id)?.view,
    graphNode: id => graph().nodes.find(n => n.id === id),
    editable: () => editable,
    trace: () => props.trace,
    pinState: (nodeId, handle, type) => pinStates().get(`${nodeId}|${type}|${handle}`),
    names: nameResolver,
    entities: () => names()?.entities,
    activateHandle,
    connecting: () => !!origin(),
    refusal: (nodeId, handle) => {
      const current = rejected()
      return current?.nodeId === nodeId && current.handle === handle ? current.message : undefined
    },
    removeEdge: removeWire,
    moved: gestureMoved,
    setInput: setNodeInput,
    setConfig: (nodeId, config) => {
      const node = graph().nodes.find(n => n.id === nodeId)
      if (node && !readOnly() && config !== node.config) change(updateNode(graph(), nodeId, { config }))
    },
    locateProblem,
    hasCode,
    editCode,
  }

  const selectedText = () => {
    const root = canvas.getRootNode() as { getSelection?: () => Selection | null }
    return (root.getSelection?.() ?? canvas.ownerDocument.getSelection())?.toString() ?? ''
  }
  const consume = (event: KeyboardEvent) => {
    event.preventDefault()
    event.stopPropagation()
  }
  const keyboard = (event: KeyboardEvent) => {
    const target = event.target as Element
    // Keys typed in a field, a menu, a list or the quick-add palette are theirs.
    if (typing(target)) return
    const command = event.ctrlKey || event.metaKey
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key
    if (event.key === 'Escape') {
      // Escape during a keyboard connection from a pin cancels it, and nothing else (full screen) takes the key.
      if (activeHandle()) event.preventDefault()
      pending = undefined
      setActiveHandle(undefined)
      setDragFrom(undefined)
      setRejected(undefined)
      closeQuickAdd()
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      // Nothing is deleted from a run or saved version on view; the key never reaches Home Assistant either.
      consume(event)
      deleteSelection(target)
      return
    }
    if (!command && !event.altKey && key === 'a') {
      if (readOnly()) return
      consume(event)
      const pin = activeHandle()
      const at = pin ? besidePin(pin) : undefined
      openQuickAdd(lastPointer && !pin ? lastPointer : centre(), pin, at)
      return
    }
    if (command && !event.altKey && !event.shiftKey && (key === 'c' || key === 'v' || key === 'd')) {
      // Text selected on a card is the user's to copy.
      if (key === 'c' && selectedText()) return
      consume(event)
      if (key === 'c') copy(target)
      else if (key === 'v') void paste()
      else duplicate(target)
      return
    }
    // A focused or selected wire: Escape deselects it (and nothing else sees the key).
    if (event.key === 'Escape' && (target.closest('.solid-flow__edge') || (selection().edge && !target.closest('.solid-flow__node')))) {
      consume(event)
      select([])
      return
    }
    if (target.closest('.solid-flow__handle')) return
    const id = target.closest('.solid-flow__node')?.getAttribute('data-id')
    if (!id) return
    if (event.key === 'Enter' || event.key === ' ') {
      consume(event)
      select([id])
      return
    }
    // E on a focused Custom C# card opens its code (Enter selects the step and opens the panel).
    if (!command && !event.altKey && key === 'e' && hasCode(id)) {
      consume(event)
      editCode(id)
      return
    }
    if (event.key === 'Escape') {
      consume(event)
      select([])
      return
    }
    const delta: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
    const direction = delta[event.key]
    if (direction && selectedSet().has(id) && !readOnly()) {
      consume(event)
      nudge(selection().nodes, direction, event.shiftKey)
    }
  }

  return (
    <FlowContext.Provider value={context}>
      <div
        ref={canvas}
        class="hs-flow"
        classList={{ 'hs-flow--readonly': readOnly(), 'hs-flow--traced': !!props.trace, 'hs-flow--connecting': !!origin(), 'hs-flow--marquee': !!marquee() }}
        tabindex="-1"
        aria-label={readOnly() ? 'Automation canvas, read only' : 'Automation canvas'}
        on:pointerdown={event => {
          gestureStart = { x: event.clientX, y: event.clientY }
          setGestureMoved(false)
          // Keys pressed after clicking empty canvas reach the canvas shortcuts.
          if (emptyPane(event.target)) focusCanvas()
        }}
        on:pointermove={event => {
          lastPointer = { x: event.clientX, y: event.clientY }
          if (gestureStart && !gestureMoved() && Math.hypot(event.clientX - gestureStart.x, event.clientY - gestureStart.y) > 3) setGestureMoved(true)
        }}
        on:pointerleave={() => (lastPointer = undefined)}
        onDragOver={event => {
          if (!readOnly() && event.dataTransfer?.types.includes(NODE_DRAG_TYPE)) {
            event.preventDefault()
            event.dataTransfer.dropEffect = 'copy'
          }
        }}
        onDrop={event => {
          event.preventDefault()
          if (readOnly()) return
          const kind = event.dataTransfer?.getData(NODE_DRAG_TYPE) as NodeKind
          if (!palette().some(item => item.kind === kind)) return
          const next = createNodeAt(graph(), kind, placeNew(toFlow({ x: event.clientX, y: event.clientY })), undefined, catalog())
          if (next === graph()) return
          change(next)
          select([next.nodes.at(-1)!.id])
        }}
        on:focusin={(event: FocusEvent) => {
          // A pointer selects on click (Shift-click adds); focus moved by the keyboard selects what it lands on.
          if (pointerHeld) return
          const target = event.target as Element
          const edge = target.closest('.solid-flow__edge')?.getAttribute('data-id')
          if (edge) {
            if (selection().edge !== edge) select([], edge)
            return
          }
          const id = target.closest('.solid-flow__node')?.getAttribute('data-id')
          if (id && !selectedSet().has(id)) select([id])
        }}
        on:keydown={keyboard}
      >
        {/* CSS never escapes the canvas into HA's light DOM. Requires CSS @scope support. */}
        <style>{`@scope (.hs-flow) { ${flowStyles} }`}</style>
        <SolidFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={{ card: NodeCard }}
          edgeTypes={edgeTypes}
          initialViewport={{ x: 10, y: 20, zoom: 0.75 }}
          fitView
          fitViewOptions={fitOptions}
          minZoom={props.trace ? TRACE_MIN_ZOOM : 0.25}
          maxZoom={1.5}
          snapGrid={snap() ? [GRID, GRID] : undefined}
          nodesDraggable={!readOnly()}
          nodesConnectable={!readOnly()}
          disableKeyboardA11y={readOnly()}
          zoomOnDoubleClick={false}
          autoPanOnNodeDrag={false}
          autoPanOnConnect={false}
          clickConnect={false}
          noDragClass="nodrag"
          noPanClass="nopan"
          noWheelClass="nowheel"
          onPaneClick={({ event }) => {
            // Clicking empty canvas closes the palette and clears the selection; it never adds anything.
            closeQuickAdd()
            if (event.button !== 0 || gestureMoved() || suppressPaneClick || Date.now() - pressedAt < 800) return pushSelection(selection())
            select([])
          }}
          onPaneContextMenu={({ event }) => {
            // A run or saved version on view has nothing to add: the browser's own menu stays.
            if (readOnly()) return
            event.preventDefault()
            // A long press already opened it (some browsers then send a context menu too).
            if (Date.now() - pressedAt < 1000) return
            openQuickAdd({ x: event.clientX, y: event.clientY })
          }}
          onMoveStart={() => closeQuickAdd()}
          // Only a person's pan or zoom (it has a source event): a view the canvas moves itself (a run's path fitted on
          // open, a step centred) must not swallow the next click.
          onMove={event => { if (event) { setGestureMoved(true); viewMoved = true } }}
          onConnectStart={(_event, params) => {
            closeQuickAdd()
            setRejected(undefined)
            setActiveHandle(undefined)
            const pin = params.nodeId && params.handleId && params.handleType ? { nodeId: params.nodeId, handle: params.handleId, type: params.handleType } : undefined
            setDragFrom(pin)
            pending = pin
          }}
          onConnectEnd={(event, state) => {
            const pin = pending
            pending = undefined
            setDragFrom(undefined)
            suppressPaneClick = true
            queueMicrotask(() => {
              suppressPaneClick = false
            })
            // Dropped on a pin that refused: say why beside that pin.
            if (!state.isValid && state.toHandle && state.fromHandle && state.toHandle.nodeId !== state.fromHandle.nodeId) {
              const from = state.fromHandle, to = state.toHandle
              const conn = from.type === 'source'
                ? { source: from.nodeId, sourceHandle: from.id, target: to.nodeId, targetHandle: to.id }
                : { source: to.nodeId, sourceHandle: to.id, target: from.nodeId, targetHandle: from.id }
              const message = quickError(conn)
              if (message) reject({ nodeId: conn.target, handle: conn.targetHandle ?? 'in' }, message)
              return
            }
            if (!pin || state.isValid || event.type === 'touchcancel' || readOnly()) return
            const point = 'changedTouches' in event ? event.changedTouches[0] : event
            if (!point) return
            // Dropped on empty canvas: offer the steps that can connect to the pin.
            const root = canvas.getRootNode() as Document | ShadowRoot
            const below = root.elementFromPoint?.(point.clientX, point.clientY) ?? null
            if (emptyPane(below) && palette().some(item => canAddFor(graph(), item.kind, pin, catalog()))) openQuickAdd({ x: point.clientX, y: point.clientY }, pin)
          }}
          deleteKey={null}
          multiSelectionKey={null}
          selectionKey={null}
          panActivationKey={null}
          zoomActivationKey={null}
          onNodeClick={({ node, event }) => {
            if (gestureMoved()) return
            if (event.shiftKey || event.metaKey || event.ctrlKey) toggle(node.id)
            else select([node.id])
          }}
          onNodeDragStart={({ targetNode }) => {
            if (targetNode && !selectedSet().has(targetNode.id)) select([targetNode.id])
          }}
          onNodeDragStop={() => commitGeometry()}
          onEdgeClick={({ edge }) => select([], edge.id)}
          isValidConnection={connection => !readOnly() && !quickError(connection)}
          onConnect={connect}
        >
          <Viewport />
          <Show when={!readOnly()}>
            <TouchNodeDrag />
          </Show>
          <Background variant="dots" gap={GRID} />
          <CanvasToolbar
            readOnly={readOnly()}
            snap={snap()}
            minimap={minimap()}
            onAdd={() => openQuickAdd(centre())}
            onZoomIn={() => void flow?.zoomIn({ duration: motion() })}
            onZoomOut={() => void flow?.zoomOut({ duration: motion() })}
            onFit={() => void flow?.fitView({ ...fitOptions, duration: motion() })}
            onTidy={tidy}
            onSnap={() => setSnap(value => !value)}
            onMinimap={() => setMinimap(value => !value)}
          >
            {props.toolbarExtra}
          </CanvasToolbar>
          <Show when={minimap()}>
            <MiniMap
              class="hs-minimap"
              pannable
              zoomable
              width={168}
              height={112}
              nodeClass={(node: Node) => `hs-minimap-node hs-minimap-node--${views().get(node.id)?.view.identity.key ?? 'code'}`}
            />
          </Show>
        </SolidFlow>
        <Show when={marqueeRect()}>{style => <div class="hs-marquee" style={style()} aria-hidden="true" />}</Show>
        <Show when={activeHandle()}>
          <p class="hs-connection-prompt" role="status">
            Choose a pin to connect to: Tab moves between the pins that fit, A adds a new step, Escape cancels.
          </p>
        </Show>
        <Show when={notice()}>
          {current => (
            <p class="hs-canvas-notice" classList={{ 'hs-canvas-notice--kept': current().keep }} role="status">
              <span>{current().text}</span>
              <Show when={current().keep}>
                <button type="button" class="hs-canvas-notice__close" aria-label="Dismiss" title="Dismiss" onClick={dismissNotice}>
                  <Icon name="close" />
                </button>
              </Show>
            </p>
          )}
        </Show>
        <Show when={!readOnly() && quickAdd()} keyed>
          {request => {
            // Worked out once per opening (and graph change), not per keystroke in the search.
            const allowed = createMemo(() => new Set(palette().filter(item => canAddFor(graph(), item.kind, request.pin, catalog())).map(item => item.kind)))
            return (
              <QuickAdd
                catalog={catalog()}
                {...quickAddOffset(request.screen)}
                title={quickAddTitle(request.pin)}
                allowed={kind => allowed().has(kind)}
                onClose={closeQuickAdd}
                onChoose={create}
              />
            )
          }}
        </Show>
      </div>
    </FlowContext.Provider>
  )
}
