import { createUniqueId, createEffect, createSignal, Show, For, on, onCleanup, onMount, untrack, type Component } from 'solid-js'
import { createStore, reconcile } from 'solid-js/store'
import {
  Background,
  Controls,
  Handle,
  NodeResizer,
  Position,
  SolidFlow,
  useSolidFlow,
  useUpdateNodeInternals,
  type Node,
  type NodeProps,
  type Edge,
} from '@dschz/solid-flow'
import flowStyles from '@dschz/solid-flow/styles?inline'
import { NodeChooser } from './NodeChooser'
import type { VisualDiagnostic } from './api'
import {
  canConnect,
  canCreateNode,
  createNodeAt,
  catalogPalette,
  connectNodes,
  outputPort,
  nodeSummary,
  nodeLabel,
  portLabel,
  typeColor,
  typeLabel,
  nodeInputs,
  inputType,
  dataConnectionError,
  removeEdge,
  updateNode,
  type EdgePort,
  type NodeKind,
  type PendingConnection,
  type VisualGraph,
} from './graph'
import { nodeCatalog, executionPorts, dataOutputs, connectData, type VisualCatalogSnapshot } from './contract'

interface Props {
  catalog?: VisualCatalogSnapshot
  addStepRequest?: number
  graph: VisualGraph
  selectedId?: string
  diagnostics?: VisualDiagnostic[]
  onSelect: (id: string) => void
  onChange: (graph: VisualGraph) => void
}

// The rest of the app depends only on VisualGraph, never on Solid Flow stores/events.
export const FlowAdapter: Component<Props> = props => {
  let canvas!: HTMLDivElement
  const catalog = () => props.catalog ?? nodeCatalog
  const palette = () => catalogPalette(catalog())
  const [rejected, setRejected] = createSignal<{ nodeId: string; inputId: string; message: string }>()
  const [activeHandle, setActiveHandle] = createSignal<{ nodeId: string; handle: string; type: 'source' | 'target' }>()
  createEffect(
    on(
      () => [props.graph, catalog()] as const,
      () => {
        setRejected(undefined)
        setActiveHandle(undefined)
      },
      { defer: true },
    ),
  )
  const dataPort = (handle?: string | null) => (handle?.startsWith('data:') ? handle.slice(5) : undefined)
  type Connection = { source: string; target: string; sourceHandle?: string | null; targetHandle?: string | null }
  const connectionError = (connection: Connection): string | undefined => {
    const source = props.graph.nodes.find(n => n.id === connection.source),
      target = props.graph.nodes.find(n => n.id === connection.target)
    if (!source || !target) return 'Connection endpoint missing'
    const output = dataPort(connection.sourceHandle),
      input = dataPort(connection.targetHandle)
    if (output !== undefined && input !== undefined) {
      return dataConnectionError(props.graph, source.id, output, target.id, input, catalog())
    }
    if (output !== undefined || input !== undefined) return 'Execution and data ports cannot connect'
    if (connection.targetHandle !== 'in' || !canConnect(props.graph, source.id, target.id, connection.sourceHandle ?? undefined, catalog()))
      return 'Execution connection is unavailable or creates a cycle'
  }
  const validate = (connection: Connection) => {
    const message = connectionError(connection)
    if (message) {
      const inputId = dataPort(connection.targetHandle) ?? connection.targetHandle ?? 'in'
      setRejected(current =>
        current?.nodeId === connection.target && current.inputId === inputId && current.message === message
          ? current
          : { nodeId: connection.target, inputId, message },
      )
    }
    return !message
  }
  const connect = (connection: Connection) => {
    if (!validate(connection)) return
    const output = dataPort(connection.sourceHandle),
      input = dataPort(connection.targetHandle)
    const next =
      output !== undefined && input !== undefined
        ? connectData(props.graph, connection.source, output, connection.target, input, catalog())
        : connectNodes(props.graph, connection.source, connection.target, connection.sourceHandle ?? undefined, catalog())
    setRejected(undefined)
    setActiveHandle(undefined)
    props.onChange(next)
  }
  const activateHandle = (nodeId: string, handle: string, type: 'source' | 'target') => {
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
  const edgeClick = (event: MouseEvent) => {
    const edge = (event.target as Element).closest('.solid-flow__edge')
    if (!edge || !event.altKey || event.button !== 0) return
    const id = edge.getAttribute('data-id')
    if (!id) return
    event.preventDefault()
    event.stopImmediatePropagation()
    pending = undefined
    setActiveHandle(undefined)
    closeChooser()
    suppressPaneClick = true
    props.onChange(removeEdge(props.graph, id, catalog()))
    queueMicrotask(() => {
      suppressPaneClick = false
    })
  }
  onMount(() => canvas.addEventListener('click', edgeClick, true))
  onCleanup(() => canvas.removeEventListener('click', edgeClick, true))
  type Point = { x: number; y: number }
  const [chooser, setChooser] = createSignal<{ screen: Point; position: Point; pending?: PendingConnection }>()
  let toFlow = (point: Point) => point
  let pending: PendingConnection | undefined
  const cancelPending = () => {
    pending = undefined
  }
  // XYFlow listens for touchend, but not touchcancel. A later release must not
  // resurrect a canceled output-to-empty-space gesture.
  onMount(() => canvas.ownerDocument.addEventListener('touchcancel', cancelPending, true))
  onCleanup(() => canvas.ownerDocument.removeEventListener('touchcancel', cancelPending, true))
  let gestureStart: Point | undefined
  let gestureMoved = false
  let suppressPaneClick = false
  const closeChooser = () => setChooser(undefined)
  const openChooser = (screen: Point, connection?: PendingConnection) => {
    setChooser({ screen, position: toFlow(screen), pending: connection })
  }
  const create = (kind: NodeKind, position: Point, connection?: PendingConnection) => {
    const next = createNodeAt(props.graph, kind, position, connection, catalog())
    closeChooser()
    if (next !== props.graph) {
      props.onChange(next)
      props.onSelect(next.nodes.at(-1)!.id)
    }
  }
  const Coordinates = () => {
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
  // Source handle ids are backend edge ports; canConnect rejects any id the node does not offer.
  const commitGeometry = (resizedId?: string) => {
    const current = props.graph
    props.onChange({
      ...current,
      nodes: current.nodes.map(node => {
        const flowNode = nodes.find(n => n.id === node.id)
        return flowNode
          ? {
              ...node,
              position: { ...flowNode.position },
              ...(resizedId === node.id
                ? { size: { width: flowNode.width ?? node.size?.width ?? 220, height: flowNode.height ?? node.size?.height ?? 112 } }
                : {}),
            }
          : node
      }),
    })
  }
  // Solid Flow 0.2.6 rebinds D3 touch handlers on every node update. Own only
  // single-touch node-body movement here; mouse, handles and resize stay native.
  const TouchNodeDrag = () => {
    const flow = useSolidFlow()
    let gesture: { identifier: number; origin: { x: number; y: number }; start: { x: number; y: number }; moved: boolean } | undefined
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
  createEffect(
    on(
      () => [props.graph, props.selectedId, props.diagnostics, catalog(), rejected()] as const,
      ([graph, selectedId]) =>
        untrack(() => {
          setNodes(
            reconcile(
              graph.nodes.map((node, index) => {
                const exec = executionPorts(node, catalog()),
                  inputs = Object.entries(nodeInputs(graph, node, catalog())),
                  outputs = Object.entries(dataOutputs(node, catalog()))
                const errors = props.diagnostics?.filter(d => d.nodeId === node.id && d.severity === 3) ?? []
                const rejection = rejected()
                if (rejection?.nodeId === node.id) errors.push({ ...rejection, severity: 3, startLine: null, startColumn: null })
                const rows = inputs.length + (node.kind === 'trigger' ? 0 : 1) + exec.length + outputs.length
                const inputTypes = Object.fromEntries(inputs.map(([id, input]) => [id, inputType(graph, node, id, input, catalog())]))
                const labels = [
                  ...inputs.map(
                    ([id, input]) =>
                      `${input.label ?? id} · ${inputTypes[id] ? typeLabel(inputTypes[id], catalog()) : 'Any registered type'}`,
                  ),
                  ...outputs.map(([id, output]) => `${output.label ?? id} · ${typeLabel(output.type, catalog())}`),
                ]
                const naturalWidth = Math.max(220, ...labels.map(label => label.length * 5.5 + 28))
                return {
                  id: node.id,
                  type: 'automation',
                  data: {
                    label: nodeLabel(node, catalog()),
                    category: catalog().nodes[node.kind]?.category ?? node.kind,
                    kind: node.kind,
                    summary: nodeSummary(node, catalog()),
                    errors,
                    ports: exec,
                    inputs,
                    inputTypes,
                    outputs,
                  },
                  position:
                    touchDrag?.graph === graph && touchDrag.id === node.id
                      ? { ...nodes.find(n => n.id === node.id)!.position }
                      : node.position
                        ? { ...node.position }
                        : { x: 60 + index * 300, y: 100 },
                  width: Math.max(node.size?.width ?? 220, naturalWidth),
                  height: Math.max(node.size?.height ?? 112, 60 + rows * 28),
                  domAttributes: {
                    'aria-label': `${nodeLabel(node, catalog())}${errors.length ? '. ' + errors.map(error => error.message).join('; ') : ''}`,
                  },
                  selected: node.id === selectedId,
                }
              }),
              { key: 'id' },
            ),
          )
          setEdges(
            reconcile(
              graph.edges.map(edge => {
                const source = graph.nodes.find(n => n.id === edge.source),
                  target = graph.nodes.find(n => n.id === edge.target)
                const output = edge.kind === 'data' && source ? dataOutputs(source, catalog())[edge.sourcePort] : undefined
                const handle = outputPort(source?.kind ?? 'service', edge),
                  color = edge.kind === 'data' ? typeColor(output?.type ?? 'object', catalog()) : edge.port === 'error' ? 'var(--hs-port-error)' : 'var(--hs-port-execution)'
                return {
                  id: edge.id,
                  source: edge.source,
                  target: edge.target,
                  sourceHandle: edge.kind === 'data' ? `data:${edge.sourcePort}` : handle,
                  targetHandle: edge.kind === 'data' ? `data:${edge.targetPort}` : 'in',
                  type: 'smoothstep',
                  style: { stroke: color },
                  label:
                    edge.kind === 'data'
                      ? `${output?.label ?? edge.sourcePort} · ${typeLabel(output?.type ?? 'object', catalog())}`
                      : portLabel(handle),
                  ariaLabel: `${edge.kind === 'data' ? 'Data' : 'Execution'} edge from ${nodeLabel(source, catalog(), edge.source)} to ${nodeLabel(target, catalog(), edge.target)}`,
                }
              }),
              { key: 'id' },
            ),
          )
        }),
    ),
  )
  const AutomationNode = (node: NodeProps) => {
    const refresh = useUpdateNodeInternals()
    const stableId = node.id
    const tooltipId = createUniqueId()
    let alive = true
    onCleanup(() => {
      alive = false
    })
    const inputs = () => node.data.inputs as [string, import('./contract').InputSpec][]
    const outputs = () => node.data.outputs as [string, import('./contract').OutputSpec][]
    const ports = () => node.data.ports as string[]
    const errors = () => node.data.errors as VisualDiagnostic[]
    const errorFor = (id: string) => errors().filter(d => d.inputId === id || d.portId === id)
    createEffect(() => {
      JSON.stringify([inputs(), outputs(), ports()])
      queueMicrotask(() => {
        if (alive) refresh(stableId)
      })
    })
    const handle = (id: string, type: 'source' | 'target', row: number, label: string, color: string, errorId = id) => (
      <>
        <Handle
          type={type}
          position={type === 'source' ? Position.Right : Position.Left}
          id={id}
          tabIndex={0}
          class="hs-port"
          style={{ top: `${52 + row * 28}px`, background: color, '--hs-value-color': color }}
          title={
            errorFor(errorId)
              .map(d => d.message)
              .join('; ') || label
          }
          aria-label={label}
          aria-invalid={errorFor(errorId).length > 0 || undefined}
          aria-describedby={errorFor(errorId).length ? tooltipId : undefined}
          onClick={event => {
            event.stopPropagation()
            if (!gestureMoved) activateHandle(node.id, id, type)
          }}
          onKeyDown={event => {
            if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault()
              event.stopPropagation()
              activateHandle(node.id, id, type)
            }
          }}
        />
        <span class={`hs-port-label hs-port-label--${type}`} style={{ top: `${52 + row * 28}px`, color }}>
          {label.replace(/ (input|output)$/, '')}
        </span>
      </>
    )
    return (
      <>
        <NodeResizer
          visible={node.selected}
          minWidth={180}
          minHeight={Math.max(
            100,
            60 + (inputs().length + (node.data.kind === 'trigger' ? 0 : 1) + ports().length + outputs().length) * 28,
          )}
          onResizeEnd={() => commitGeometry(node.id)}
        />
        <div
          class="hs-flow-card"
          classList={{ 'hs-flow-card--error': errors().length > 0 }}
          aria-describedby={errors().length ? tooltipId : undefined}
        >
          <strong>{String(node.data.label)}</strong>
          <small class="hs-flow-kind">{String(node.data.category)}</small>
        </div>
        <Show when={node.data.kind !== 'trigger'}>{handle('in', 'target', 0, 'Execution input', 'var(--hs-port-execution)')}</Show>
        <For each={inputs()}>
          {([id, input], index) =>
            handle(
              `data:${id}`,
              'target',
              index() + (node.data.kind === 'trigger' ? 0 : 1),
              `${input.label ?? id} · ${(node.data.inputTypes as Record<string, string | undefined>)[id] ? typeLabel((node.data.inputTypes as Record<string, string>)[id], catalog()) : 'Any registered type'} input`,
              typeColor((node.data.inputTypes as Record<string, string>)[id] ?? 'object', catalog()),
              id,
            )
          }
        </For>
        <For each={ports()}>
          {(id, index) =>
            handle(
              id,
              'source',
              index() + inputs().length + (node.data.kind === 'trigger' ? 0 : 1),
              `${portLabel(id)} output`,
              id === 'error' ? 'var(--hs-port-error)' : 'var(--hs-port-execution)',
            )
          }
        </For>
        <For each={outputs()}>
          {([id, output], index) =>
            handle(
              `data:${id}`,
              'source',
              index() + ports().length + inputs().length + (node.data.kind === 'trigger' ? 0 : 1),
              `${output.label ?? id} · ${typeLabel(output.type, catalog())} output`,
              typeColor(output.type, catalog()),
              id,
            )
          }
        </For>
        <Show when={errors().length}>
          <div role="tooltip" id={tooltipId} class="hs-node-errors">
            <For each={errors()}>
              {error => (
                <p>
                  {error.inputId ? `${error.inputId}: ` : ''}
                  {error.message}
                </p>
              )}
            </For>
          </div>
        </Show>
      </>
    )
  }
  return (
    <div
      ref={canvas}
      class="hs-flow"
      aria-label="Automation canvas"
      on:pointerdown={event => {
        gestureStart = { x: event.clientX, y: event.clientY }
        gestureMoved = false
      }}
      on:pointermove={event => {
        if (gestureStart && Math.hypot(event.clientX - gestureStart.x, event.clientY - gestureStart.y) > 3) gestureMoved = true
      }}
      onDragOver={event => {
        if (event.dataTransfer?.types.includes('application/x-hass-sharp-node')) {
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
        if (id) props.onSelect(id)
      }}
      on:keydown={event => {
        if (event.key === 'Escape') {
          pending = undefined
          setActiveHandle(undefined)
          closeChooser()
        }
        if ((event.target as Element).closest('.solid-flow__handle')) return
        const id = (event.target as Element).closest('.solid-flow__node')?.getAttribute('data-id')
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
          props.onSelect('')
          return
        }
        const delta: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
        const direction = delta[event.key]
        const node = props.graph.nodes.find(n => n.id === id)
        if (direction && node && props.selectedId === id) {
          event.preventDefault()
          event.stopPropagation()
          const step = event.shiftKey ? 40 : 10
          props.onChange(
            updateNode(props.graph, id, {
              position: { x: (node.position?.x ?? 60) + direction[0] * step, y: (node.position?.y ?? 100) + direction[1] * step },
            }),
          )
        }
      }}
    >
      {/* CSS never escapes the canvas into HA's light DOM. Requires CSS @scope support. */}
      <style>{`@scope (.hs-flow) { ${flowStyles} }`}</style>
      <SolidFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={{ automation: AutomationNode }}
        initialViewport={{ x: 10, y: 20, zoom: 0.75 }}
        minZoom={0.25}
        maxZoom={1.5}
        autoPanOnNodeDrag={false}
        autoPanOnConnect={false}
        clickConnect={false}
        onPaneClick={({ event }) => {
          if (event.button === 0 && !gestureMoved && !suppressPaneClick) openChooser({ x: event.clientX, y: event.clientY })
        }}
        onMoveStart={closeChooser}
        onMove={() => {
          gestureMoved = true
        }}
        onConnectStart={(_event, params) => {
          closeChooser()
          setRejected(undefined)
          pending =
            params.handleType === 'source' && params.nodeId && params.handleId && !params.handleId.startsWith('data:')
              ? { source: params.nodeId, port: params.handleId as EdgePort }
              : undefined
        }}
        onConnectEnd={(event, state) => {
          const connection = pending
          pending = undefined
          suppressPaneClick = true
          queueMicrotask(() => {
            suppressPaneClick = false
          })
          if (!connection || state.isValid || event.type === 'touchcancel') return
          const point = 'changedTouches' in event ? event.changedTouches[0] : event
          if (!point) return
          const root = canvas.getRootNode() as Document | ShadowRoot
          const below = root.elementFromPoint?.(point.clientX, point.clientY)
          if (emptyPane(below) && palette().some(item => canCreateNode(props.graph, item.kind, connection, catalog()))) {
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
        isValidConnection={validate}
        onConnect={connect}
      >
        <Coordinates />
        <TouchNodeDrag />
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
      <Show when={chooser()} keyed>
        {request => (
          <NodeChooser
            catalog={catalog()}
            {...chooserOffset()}
            pending={request.pending}
            allowed={kind => canCreateNode(props.graph, kind, request.pending, catalog())}
            onClose={closeChooser}
            onChoose={kind => create(kind, request.position, request.pending)}
          />
        )}
      </Show>
    </div>
  )
}
