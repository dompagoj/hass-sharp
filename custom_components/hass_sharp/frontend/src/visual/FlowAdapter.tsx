import { createEffect, For, on, onCleanup, onMount, untrack, type Component } from 'solid-js'
import { createStore, reconcile } from 'solid-js/store'
import { Background, Controls, Handle, NodeResizer, Position, SolidFlow, useSolidFlow, type Node, type NodeProps, type Edge } from '@dschz/solid-flow'
import flowStyles from '@dschz/solid-flow/styles?inline'
import type { VisualDiagnostic } from './api'
import { canConnect, connectNodes, outputPort, outputPorts, portLabels, updateNode, type EdgePort, type VisualGraph } from './graph'

interface Props {
  graph: VisualGraph
  selectedId?: string
  diagnostics?: VisualDiagnostic[]
  onSelect: (id: string) => void
  onChange: (graph: VisualGraph) => void
}

// The rest of the app depends only on VisualGraph, never on Solid Flow stores/events.
export const FlowAdapter: Component<Props> = props => {
  let canvas!: HTMLDivElement
  let touchDrag: { id: string; graph: VisualGraph } | undefined
  const [nodes, setNodes] = createStore<Node[]>([])
  const [edges, setEdges] = createStore<Edge[]>([])
  // Source handle ids are backend edge ports; canConnect rejects any id the node does not offer.
  const port = (handle?: string | null) => (handle ?? undefined) as EdgePort | undefined
  const commitGeometry = () => {
    const current = props.graph
    props.onChange({ ...current, nodes: current.nodes.map(node => {
      const flowNode = nodes.find(n => n.id === node.id)
      return flowNode ? { ...node, position: { ...flowNode.position }, size: {
        width: flowNode.width ?? node.size?.width ?? 220, height: flowNode.height ?? node.size?.height ?? 112,
      } } : node
    }) })
  }
  // Solid Flow 0.2.6 rebinds D3 touch handlers on every node update. Own only
  // single-touch node-body movement here; mouse, handles and resize stay native.
  const TouchNodeDrag = () => {
    const flow = useSolidFlow()
    let gesture: { identifier: number; origin: { x: number; y: number }; start: { x: number; y: number }; moved: boolean } | undefined
    let claimed = false
    const point = (touch: Touch) => flow.screenToFlowPosition({ x: touch.clientX, y: touch.clientY }, { snapToGrid: false })
    const consume = (event: TouchEvent) => { event.stopPropagation(); event.preventDefault() }
    const finish = () => {
      const current = gesture
      gesture = undefined
      const sameGraph = touchDrag?.graph === props.graph
      touchDrag = undefined
      if (current?.moved && sameGraph) commitGeometry()
    }
    const start = (event: TouchEvent) => {
      if (claimed) { consume(event); if (event.touches.length > 1) finish(); return }
      const target = event.target as Element
      if (event.touches.length !== 1 || target.closest('.solid-flow__handle,.solid-flow__resize-control,.nodrag,input,textarea,select,button,a,[contenteditable="true"]')) return
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
      if (event.touches.length !== 1 || touchDrag?.graph !== props.graph) { finish(); return }
      if (!gesture || !touchDrag) return
      const touch = Array.from(event.touches).find(touch => touch.identifier === gesture!.identifier)
      if (!touch) { finish(); return }
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
    createEffect(on(() => props.graph, graph => {
      if (touchDrag && touchDrag.graph !== graph) { finish(); claimed = false }
    }))
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
  createEffect(on(() => [props.graph, props.selectedId, props.diagnostics] as const, ([graph, selectedId]) => untrack(() => {
    setNodes(reconcile(graph.nodes.map((node, index) => ({
      id: node.id, type: 'automation', data: { label: node.label ?? node.kind, kind: node.kind, summary: String(Object.values(node.config)[0] ?? ''), errors: props.diagnostics?.filter(d => d.nodeId === node.id && d.severity === 3).length ?? 0, ports: outputPorts(node.kind) },
      position: touchDrag?.graph === graph && touchDrag.id === node.id ? { ...nodes.find(n => n.id === node.id)!.position } : node.position ? { ...node.position } : { x: 60 + index * 300, y: 100 }, width: node.size?.width ?? 220, height: node.size?.height ?? 112,
      ariaLabel: `${node.label ?? node.kind}, ${node.kind}`, selected: node.id === selectedId,
    })), { key: 'id' }))
    setEdges(reconcile(graph.edges.map(edge => {
      const handle = outputPort(graph.nodes.find(n => n.id === edge.source)?.kind ?? 'service', edge)
      return { id: edge.id, source: edge.source, target: edge.target, sourceHandle: handle, targetHandle: 'in', type: 'smoothstep', label: handle === 'next' ? undefined : portLabels[handle] }
    }), { key: 'id' }))
  })))
  const AutomationNode = (node: NodeProps) => <>
    <NodeResizer visible={node.selected} minWidth={180} minHeight={100} onResizeEnd={commitGeometry} />
    {node.data.kind !== 'trigger' && <Handle type="target" position={Position.Left} id="in" />}
    <div class="hs-flow-card" classList={{ 'hs-flow-card--error': !!node.data.errors }}><span class="hs-flow-kind">{String(node.data.kind)}</span><strong>{String(node.data.label)}</strong><span>{String(node.data.summary)}</span>{!!node.data.errors && <span class="hs-error">{String(node.data.errors)} error(s)</span>}</div>
    <For each={node.data.ports as EdgePort[]}>{(id, index) => {
      const top = () => `${(index() + 1) * 100 / ((node.data.ports as EdgePort[]).length + 1)}%`
      return <>
        <Handle type="source" position={Position.Right} id={id} class={`hs-port hs-port--${id}`} style={{ top: top() }} title={portLabels[id]} />
        {(node.data.ports as EdgePort[]).length > 1 && <span class="hs-port-label" style={{ top: top() }} aria-hidden="true">{portLabels[id]}</span>}
      </>
    }}</For>
  </>
  return <div ref={canvas} class="hs-flow" aria-label="Automation canvas"
    onFocusIn={event => { const id = (event.target as Element).closest('.solid-flow__node')?.getAttribute('data-id'); if (id) props.onSelect(id) }}
    on:keydown={event => {
      const id = (event.target as Element).closest('.solid-flow__node')?.getAttribute('data-id')
      if (!id) return
      if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); props.onSelect(id); return }
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); props.onSelect(''); return }
      const delta: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
      const direction = delta[event.key]
      const node = props.graph.nodes.find(n => n.id === id)
      if (direction && node && props.selectedId === id) {
        event.preventDefault(); event.stopPropagation()
        const step = event.shiftKey ? 40 : 10
        props.onChange(updateNode(props.graph, id, { position: { x: (node.position?.x ?? 60) + direction[0] * step, y: (node.position?.y ?? 100) + direction[1] * step } }))
      }
    }}>
    {/* CSS never escapes the canvas into HA's light DOM. Requires CSS @scope support. */}
    <style>{`@scope (.hs-flow) { ${flowStyles} }`}</style>
    <SolidFlow nodes={nodes} edges={edges} nodeTypes={{ automation: AutomationNode }}
      initialViewport={{ x: 10, y: 20, zoom: 0.75 }} minZoom={0.25} maxZoom={1.5}
      autoPanOnNodeDrag={false}
      deleteKey={null} multiSelectionKey={null} selectionKey={null} panActivationKey={null} zoomActivationKey={null}
      onNodeClick={({ node }) => props.onSelect(node.id)}
      onNodeDragStart={({ targetNode }) => { if (targetNode) props.onSelect(targetNode.id) }}
      onNodeDragStop={commitGeometry}
      isValidConnection={connection => canConnect(props.graph, connection.source, connection.target, port(connection.sourceHandle))}
      onConnect={connection => props.onChange(connectNodes(props.graph, connection.source, connection.target, port(connection.sourceHandle)))}>
      <TouchNodeDrag /><Background variant="dots" /><Controls showLock={false} />
    </SolidFlow>
  </div>
}
