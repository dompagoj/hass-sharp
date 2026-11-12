import { createEffect, on, onCleanup, onMount, untrack, type Component } from 'solid-js'
import { createStore, reconcile } from 'solid-js/store'
import { Background, Controls, Handle, NodeResizer, Position, SolidFlow, type Node, type NodeProps, type Edge } from '@dschz/solid-flow'
import flowStyles from '@dschz/solid-flow/styles?inline'
import { canConnect, connectNodes, updateNode, type VisualGraph } from './graph'

interface Props {
  graph: VisualGraph
  selectedId?: string
  onSelect: (id: string) => void
  onChange: (graph: VisualGraph) => void
}

// The rest of the app depends only on VisualGraph, never on Solid Flow stores/events.
export const FlowAdapter: Component<Props> = props => {
  let canvas!: HTMLDivElement
  let disposed = false
  const [nodes, setNodes] = createStore<Node[]>([])
  const [edges, setEdges] = createStore<Edge[]>([])
  const commitGeometry = () => {
    const current = props.graph
    props.onChange({ ...current, nodes: current.nodes.map(node => {
      const flowNode = nodes.find(n => n.id === node.id)
      return flowNode ? { ...node, position: { ...flowNode.position }, size: {
        width: flowNode.width ?? node.size.width, height: flowNode.height ?? node.size.height,
      } } : node
    }) })
  }
  // XYDrag aborts multi-touch without onDragStop. Capture termination before
  // D3 stops propagation, then snapshot its final store after that handler runs.
  const finishTouch = () => queueMicrotask(() => { if (!disposed) commitGeometry() })
  onMount(() => {
    canvas.addEventListener('touchend', finishTouch, true)
    canvas.addEventListener('touchcancel', finishTouch, true)
  })
  onCleanup(() => {
    disposed = true
    canvas.removeEventListener('touchend', finishTouch, true)
    canvas.removeEventListener('touchcancel', finishTouch, true)
  })
  createEffect(on(() => [props.graph, props.selectedId] as const, ([graph, selectedId]) => untrack(() => {
    setNodes(reconcile(graph.nodes.map(node => ({
      id: node.id, type: 'automation', data: { label: node.label, kind: node.kind, summary: Object.values(node.config)[0] ?? '' },
      position: { ...node.position }, width: node.size.width, height: node.size.height,
      ariaLabel: `${node.label}, ${node.kind}`, selected: node.id === selectedId,
    })), { key: 'id' }))
    setEdges(reconcile(graph.edges.map(edge => ({ ...edge, sourceHandle: 'out', targetHandle: 'in', type: 'smoothstep' })), { key: 'id' }))
  })))
  const AutomationNode = (node: NodeProps) => <>
    <NodeResizer visible={node.selected} minWidth={180} minHeight={100} onResizeEnd={commitGeometry} />
    {node.data.kind !== 'trigger' && <Handle type="target" position={Position.Left} id="in" />}
    <div class="hs-flow-card"><span class="hs-flow-kind">{String(node.data.kind)}</span><strong>{String(node.data.label)}</strong><span>{String(node.data.summary)}</span></div>
    <Handle type="source" position={Position.Right} id="out" />
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
        props.onChange(updateNode(props.graph, id, { position: { x: node.position.x + direction[0] * step, y: node.position.y + direction[1] * step } }))
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
      isValidConnection={connection => canConnect(props.graph, connection.source, connection.target)}
      onConnect={connection => props.onChange(connectNodes(props.graph, connection.source, connection.target))}>
      <Background variant="dots" /><Controls showLock={false} />
    </SolidFlow>
  </div>
}
