import { createEffect, createMemo, createSignal, on, onCleanup, onMount, Show, untrack, useContext, type Component } from 'solid-js'
import { createStore, reconcile } from 'solid-js/store'
import { QueryClientContext } from '@tanstack/solid-query'
import { Background, Controls, SolidFlow, useSolidFlow, type Edge, type Node } from '@dschz/solid-flow'
import flowStyles from '@dschz/solid-flow/styles?inline'
import { NodeChooser } from '../NodeChooser'
import type { VisualDiagnostic } from '../api'
import { HassCtx } from '../../context'
import { useRegistry } from '../../registry'
import { connectData, nodeCatalog, setInput as writeInput, type Operand, type VisualCatalogSnapshot } from '../contract'
import { canConnect, canCreateNode, catalogPalette, connectNodes, createNodeAt, dataConnectionError, removeEdge, updateNode, type EdgePort, type NodeKind, type PendingConnection, type VisualGraph } from '../graph'
import { connectionIndex } from '../connectionIndex'
import type { SummaryNames } from '../nodeSummary'
import { registryNames, type NameResolver } from '../widgets'
import type { Registry } from '../../registry'
import { edgeTypes } from './Edges'
import { NodeCard } from './NodeCard'
import {
  buildEdgeView,
  buildNodeView,
  dataPortOf,
  FlowContext,
  graphVariables,
  renderableGraph,
  traceText,
  viewKey,
  type FlowContextValue,
  type FlowTrace,
  type NodeView,
  type PinState,
  type ViewContext,
} from './flowModel'

export interface FlowAdapterProps {
  graph: VisualGraph
  catalog?: VisualCatalogSnapshot
  /** A run or a saved version on view: nothing moves, connects, deletes or edits. */
  readOnly?: boolean
  selectedId?: string
  onSelect(id?: string): void
  onChange?(graph: VisualGraph, options?: { coalesce?: string }): void
  diagnostics?: VisualDiagnostic[]
  addStepRequest?: number
  /** Each new value fits the view to the graph. */
  fitViewKey?: unknown
  trace?: FlowTrace
}

type Point = { x: number; y: number }
type Connection = { source: string; target: string; sourceHandle?: string | null; targetHandle?: string | null }
type HandleRef = { nodeId: string; handle: string; type: 'source' | 'target' }

/** Registry names for summaries; only inside the app's providers (a bare adapter in a test has none). */
function useNames(enabled: boolean): () => Registry | undefined {
  if (!enabled) return () => undefined
  const registry = useRegistry()
  // Reading `data` before the query settles would suspend the editor; names are only an improvement.
  return () => (registry.isSuccess ? registry.data : undefined)
}

/**
 * The canvas. The rest of the app depends only on `VisualGraph`, never on Solid Flow stores or events: cards read
 * their view from a per-node cache keyed by node content, so a drag or a hover never re-derives ports or summaries.
 */
export const FlowAdapter: Component<FlowAdapterProps> = props => {
  let canvas!: HTMLDivElement
  const catalog = () => props.catalog ?? nodeCatalog
  const readOnly = () => !!props.readOnly
  const graph = createMemo(() => renderableGraph(props.graph))
  const palette = () => catalogPalette(catalog())
  const editable = !!useContext(QueryClientContext) && !!useContext(HassCtx)
  const names = useNames(editable)
  // Value chips name registry items the way the inline editors do (one resolver per registry snapshot).
  const nameResolver = createMemo<NameResolver>(() => registryNames(names()))
  const change = (next: VisualGraph, options?: { coalesce?: string }) => {
    if (readOnly() || !props.onChange || next === graph()) return
    props.onChange(next, options)
  }

  const [rejected, setRejected] = createSignal<{ nodeId: string; handle: string; message: string }>()
  const [activeHandle, setActiveHandle] = createSignal<HandleRef>()
  const [dragFrom, setDragFrom] = createSignal<HandleRef>()
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
  const variables = createMemo(() => graphVariables(graph(), catalog()))

  // Connections. Hover asks the connection index only; a drop runs the full check once, inside connectData (D12).
  const quickError = (connection: Connection): string | undefined => {
    const current = graph()
    const source = current.nodes.find(n => n.id === connection.source),
      target = current.nodes.find(n => n.id === connection.target)
    if (!source || !target) return 'Connection endpoint missing'
    const output = dataPortOf(connection.sourceHandle),
      input = dataPortOf(connection.targetHandle)
    if (output !== undefined && input !== undefined) return dataConnectionError(current, source.id, output, target.id, input, catalog(), { quick: true })
    if (output !== undefined || input !== undefined) return 'Execution and data ports cannot connect'
    if (connection.targetHandle !== 'in' || !canConnect(current, source.id, target.id, connection.sourceHandle ?? undefined, catalog()))
      return 'Execution connection is unavailable or creates a cycle'
  }
  const reject = (connection: Connection, message: string) => {
    const handle = connection.targetHandle ?? 'in'
    setRejected(current =>
      current?.nodeId === connection.target && current.handle === handle && current.message === message ? current : { nodeId: connection.target, handle, message },
    )
  }
  const connect = (connection: Connection) => {
    if (readOnly()) return
    const message = quickError(connection)
    if (message) return reject(connection, message)
    const output = dataPortOf(connection.sourceHandle),
      input = dataPortOf(connection.targetHandle)
    let next: VisualGraph
    try {
      next =
        output !== undefined && input !== undefined
          ? connectData(graph(), connection.source, output, connection.target, input, catalog())
          : connectNodes(graph(), connection.source, connection.target, connection.sourceHandle ?? undefined, catalog())
    } catch (error) {
      return reject(connection, error instanceof Error ? error.message : String(error))
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
  // While a wire is dragged (or a pin is picked from the keyboard), pins it could end on glow; others dim.
  const origin = () => dragFrom() ?? activeHandle()
  const pinStates = createMemo(() => {
    const from = origin()
    const states = new Map<string, PinState>()
    if (!from) return states
    // Keys carry the side: inputs and outputs share handle ids (`data:value` on both sides of Set value).
    states.set(`${from.nodeId}|${from.type}|${from.handle}`, 'origin')
    const side = from.type === 'source' ? 'target' : 'source'
    const data = dataPortOf(from.handle) !== undefined
    for (const [id, entry] of views()) {
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
    props.onSelect(nodeId)
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
    closeChooser()
    suppressPaneClick = true
    change(removeEdge(graph(), id, catalog()))
    queueMicrotask(() => {
      suppressPaneClick = false
    })
  }
  onMount(() => canvas.addEventListener('click', edgeClick, true))
  onCleanup(() => canvas.removeEventListener('click', edgeClick, true))

  const [chooser, setChooser] = createSignal<{ screen: Point; position: Point; pending?: PendingConnection }>()
  let toFlow = (point: Point) => point
  let pending: PendingConnection | undefined
  const cancelPending = () => {
    pending = undefined
    setDragFrom(undefined)
  }
  // XYFlow listens for touchend, but not touchcancel. A later release must not
  // resurrect a canceled output-to-empty-space gesture.
  onMount(() => canvas.ownerDocument.addEventListener('touchcancel', cancelPending, true))
  onCleanup(() => canvas.ownerDocument.removeEventListener('touchcancel', cancelPending, true))
  let gestureStart: Point | undefined
  const [gestureMoved, setGestureMoved] = createSignal(false)
  let suppressPaneClick = false
  const closeChooser = () => setChooser(undefined)
  const openChooser = (screen: Point, connection?: PendingConnection) => {
    if (readOnly()) return
    setChooser({ screen, position: toFlow(screen), pending: connection })
  }
  const create = (kind: NodeKind, position: Point, connection?: PendingConnection) => {
    closeChooser()
    if (readOnly()) return
    const next = createNodeAt(graph(), kind, position, connection, catalog())
    if (next !== graph()) {
      change(next)
      props.onSelect(next.nodes.at(-1)!.id)
    }
  }
  const Viewport = () => {
    const flow = useSolidFlow()
    toFlow = point => flow.screenToFlowPosition(point, { snapToGrid: false })
    createEffect(
      on(
        () => props.addStepRequest,
        request => {
          if (!request) return
          const rect = canvas.getBoundingClientRect()
          openChooser({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 })
        },
      ),
    )
    createEffect(
      on(
        () => props.fitViewKey,
        key => {
          if (key === undefined) return
          requestAnimationFrame(() => void flow.fitView({ padding: 0.15, maxZoom: 1 }))
        },
      ),
    )
    createEffect(
      on(
        () => props.trace?.focusNodeId,
        id => {
          if (!id) return
          // Centre once the card has been measured (its size is unknown on the first frames), then leave the
          // viewport to the user: this runs again only when focusNodeId changes.
          let frames = 0, frame = 0
          const centre = () => {
            const node = flow.getInternalNode(id)
            const width = node?.measured.width, height = node?.measured.height
            if (!node || !width || !height) {
              if (++frames < 60) frame = requestAnimationFrame(centre)
              return
            }
            const { x, y } = node.internals.positionAbsolute
            void flow.setCenter(x + width / 2, y + height / 2, { zoom: Math.max(flow.getZoom(), 0.8), duration: 200 })
          }
          frame = requestAnimationFrame(centre)
          onCleanup(() => cancelAnimationFrame(frame))
        },
      ),
    )
    return null
  }
  const emptyPane = (element: Element | null) => !!element && canvas.contains(element) && element.classList.contains('solid-flow__pane')
  const chooserOffset = () => {
    const rect = canvas.getBoundingClientRect()
    const point = chooser()!.screen
    return {
      x: Math.max(8, Math.min(point.x - rect.left, rect.width - 292)),
      y: Math.max(8, Math.min(point.y - rect.top, rect.height - 340)),
    }
  }

  let touchDrag: { id: string; graph: VisualGraph } | undefined
  const [nodes, setNodes] = createStore<Node[]>([])
  const [edges, setEdges] = createStore<Edge[]>([])
  const commitGeometry = () => {
    if (readOnly()) return
    const current = props.graph
    const moved = current.nodes.map(node => {
      if (!node || typeof node !== 'object') return node
      const flowNode = nodes.find(n => n.id === node.id)
      if (!flowNode) return node
      const same = node.position?.x === flowNode.position.x && node.position?.y === flowNode.position.y
      return same ? node : { ...node, position: { ...flowNode.position } }
    })
    if (moved.some((node, i) => node !== current.nodes[i])) props.onChange?.({ ...current, nodes: moved })
  }
  // Solid Flow 0.2.6 rebinds D3 touch handlers on every node update. Own only
  // single-touch node-body movement here; mouse and handles stay native.
  const TouchNodeDrag = () => {
    const flow = useSolidFlow()
    let gesture: { identifier: number; origin: Point; start: Point; moved: boolean } | undefined
    let claimed = false
    const point = (touch: Touch) => flow.screenToFlowPosition({ x: touch.clientX, y: touch.clientY }, { snapToGrid: false })
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
      props.onSelect(node.id)
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
      gesture.moved ||= position.x !== gesture.origin.x || position.y !== gesture.origin.y
      setNodes(node => node.id === touchDrag!.id, 'position', position)
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
      () => [graph(), props.selectedId, views(), props.trace] as const,
      ([current, selectedId, built, trace]) =>
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
                    : node.position
                      ? { ...node.position }
                      : { x: 60 + index * 300, y: 100 },
                domAttributes: { 'aria-label': `${built.get(node.id)?.view.label ?? node.id}${trace ? `. Run: ${traceText(trace.nodes[node.id])}` : ''}` },
                selected: node.id === selectedId,
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
                }]
              }),
              { key: 'id' },
            ),
          ),
        ),
    ),
  )

  const context: FlowContextValue = {
    readOnly,
    catalog,
    view: id => views().get(id)?.view,
    graphNode: id => graph().nodes.find(n => n.id === id),
    variables,
    editable: () => editable,
    trace: () => props.trace,
    pinState: (nodeId, handle, type) => pinStates().get(`${nodeId}|${type}|${handle}`),
    names: nameResolver,
    activateHandle,
    moved: gestureMoved,
    setInput: setNodeInput,
    locateProblem,
  }

  const keyboard = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      pending = undefined
      setActiveHandle(undefined)
      setDragFrom(undefined)
      closeChooser()
    }
    const target = event.target as Element
    if (readOnly() && (event.key === 'Delete' || event.key === 'Backspace')) {
      // Nothing is deleted from a run or saved version on view.
      event.preventDefault()
      event.stopPropagation()
      return
    }
    if (target.closest('.solid-flow__handle')) return
    const id = target.closest('.solid-flow__node')?.getAttribute('data-id')
    if (!id) return
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      event.stopPropagation()
      props.onSelect(id)
      return
    }
    if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      props.onSelect(undefined)
      return
    }
    const delta: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
    const direction = delta[event.key]
    const node = graph().nodes.find(n => n.id === id)
    if (direction && node && props.selectedId === id && !readOnly()) {
      event.preventDefault()
      event.stopPropagation()
      const step = event.shiftKey ? 40 : 10
      change(updateNode(graph(), id, { position: { x: (node.position?.x ?? 60) + direction[0] * step, y: (node.position?.y ?? 100) + direction[1] * step } }))
    }
  }

  return (
    <FlowContext.Provider value={context}>
      <div
        ref={canvas}
        class="hs-flow"
        classList={{ 'hs-flow--readonly': readOnly(), 'hs-flow--traced': !!props.trace, 'hs-flow--connecting': !!origin() }}
        aria-label={readOnly() ? 'Automation canvas, read only' : 'Automation canvas'}
        on:pointerdown={event => {
          gestureStart = { x: event.clientX, y: event.clientY }
          setGestureMoved(false)
        }}
        on:pointermove={event => {
          if (gestureStart && !gestureMoved() && Math.hypot(event.clientX - gestureStart.x, event.clientY - gestureStart.y) > 3) setGestureMoved(true)
        }}
        onDragOver={event => {
          if (!readOnly() && event.dataTransfer?.types.includes('application/x-hass-sharp-node')) {
            event.preventDefault()
            event.dataTransfer.dropEffect = 'copy'
          }
        }}
        onDrop={event => {
          event.preventDefault()
          const kind = event.dataTransfer?.getData('application/x-hass-sharp-node') as NodeKind
          if (palette().some(item => item.kind === kind)) create(kind, toFlow({ x: event.clientX, y: event.clientY }))
        }}
        onFocusIn={event => {
          const id = (event.target as Element).closest('.solid-flow__node')?.getAttribute('data-id')
          if (id && id !== props.selectedId) props.onSelect(id)
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
          minZoom={0.25}
          maxZoom={1.5}
          nodesDraggable={!readOnly()}
          nodesConnectable={!readOnly()}
          disableKeyboardA11y={readOnly()}
          autoPanOnNodeDrag={false}
          autoPanOnConnect={false}
          clickConnect={false}
          noDragClass="nodrag"
          noPanClass="nopan"
          noWheelClass="nowheel"
          onPaneClick={({ event }) => {
            if (event.button === 0 && !gestureMoved() && !suppressPaneClick) openChooser({ x: event.clientX, y: event.clientY })
          }}
          onMoveStart={closeChooser}
          onMove={() => setGestureMoved(true)}
          onConnectStart={(_event, params) => {
            closeChooser()
            setRejected(undefined)
            setActiveHandle(undefined)
            if (params.nodeId && params.handleId && params.handleType) setDragFrom({ nodeId: params.nodeId, handle: params.handleId, type: params.handleType })
            pending =
              params.handleType === 'source' && params.nodeId && params.handleId && !params.handleId.startsWith('data:')
                ? { source: params.nodeId, port: params.handleId as EdgePort }
                : undefined
          }}
          onConnectEnd={(event, state) => {
            const connection = pending
            pending = undefined
            setDragFrom(undefined)
            suppressPaneClick = true
            queueMicrotask(() => {
              suppressPaneClick = false
            })
            // Dropped on a pin that refused: say why on that pin.
            if (!state.isValid && state.toHandle && state.fromHandle && state.toHandle.nodeId !== state.fromHandle.nodeId) {
              const from = state.fromHandle, to = state.toHandle
              const conn = from.type === 'source'
                ? { source: from.nodeId, sourceHandle: from.id, target: to.nodeId, targetHandle: to.id }
                : { source: to.nodeId, sourceHandle: to.id, target: from.nodeId, targetHandle: from.id }
              const message = quickError(conn)
              if (message) reject(conn, message)
              return
            }
            if (!connection || state.isValid || event.type === 'touchcancel') return
            const point = 'changedTouches' in event ? event.changedTouches[0] : event
            if (!point) return
            const root = canvas.getRootNode() as Document | ShadowRoot
            const below = root.elementFromPoint?.(point.clientX, point.clientY)
            if (emptyPane(below) && palette().some(item => canCreateNode(graph(), item.kind, connection, catalog()))) {
              openChooser({ x: point.clientX, y: point.clientY }, connection)
            }
          }}
          deleteKey={null}
          multiSelectionKey={null}
          selectionKey={null}
          panActivationKey={null}
          zoomActivationKey={null}
          onNodeClick={({ node }) => props.onSelect(node.id)}
          onNodeDragStart={({ targetNode }) => {
            if (targetNode) props.onSelect(targetNode.id)
          }}
          onNodeDragStop={() => commitGeometry()}
          isValidConnection={connection => !readOnly() && !quickError(connection)}
          onConnect={connect}
        >
          <Viewport />
          <Show when={!readOnly()}>
            <TouchNodeDrag />
          </Show>
          <Background variant="dots" />
          <Controls showLock={false} />
        </SolidFlow>
        <Show when={activeHandle()}>
          <p class="hs-connection-prompt" role="status">
            Select a destination port. Escape cancels.
          </p>
        </Show>
        <Show when={rejected()}>
          <p class="hs-connection-error hs-error" role="alert">
            {rejected()!.message}
          </p>
        </Show>
        <Show when={!readOnly() && chooser()} keyed>
          {request => (
            <NodeChooser
              catalog={catalog()}
              {...chooserOffset()}
              pending={request.pending}
              allowed={kind => canCreateNode(graph(), kind, request.pending, catalog())}
              onClose={closeChooser}
              onChoose={kind => create(kind, request.position, request.pending)}
            />
          )}
        </Show>
      </div>
    </FlowContext.Provider>
  )
}
