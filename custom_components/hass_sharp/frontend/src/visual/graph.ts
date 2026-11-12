import { createSignal } from 'solid-js'

// Domain data only. Library objects, JSX, selection and measured sizes never enter JSON.
export type NodeKind = 'trigger' | 'condition' | 'service' | 'delay' | 'custom'
export interface GraphNode {
  id: string
  kind: NodeKind
  label: string
  position: { x: number; y: number }
  size: { width: number; height: number }
  config: Record<string, string>
}
export interface GraphEdge { id: string; source: string; target: string }
export interface VisualGraph { version: 1; name: string; nodes: GraphNode[]; edges: GraphEdge[] }

export const palette: { kind: NodeKind; label: string; description: string; config: Record<string, string> }[] = [
  { kind: 'trigger', label: 'Entity changes', description: 'Start with a state change', config: { entity: 'binary_sensor.motion' } },
  { kind: 'condition', label: 'State condition', description: 'Check an entity’s state', config: { entity: 'binary_sensor.motion', state: 'on' } },
  { kind: 'service', label: 'Call service', description: 'Ask Home Assistant to act', config: { service: 'light.turn_on', entity: 'light.hall' } },
  { kind: 'delay', label: 'Wait', description: 'Pause before the next step', config: { seconds: '60' } },
  { kind: 'custom', label: 'Custom C#', description: 'Keep a code section in the graph', config: { code: '// Add your C# here' } },
]

export function addNode(graph: VisualGraph, kind: NodeKind, id: string = crypto.randomUUID()): VisualGraph {
  const item = palette.find(item => item.kind === kind)!
  const offset = graph.nodes.length * 35
  return { ...graph, nodes: [...graph.nodes, { id, kind, label: item.label, config: { ...item.config }, position: { x: 60 + offset, y: 240 + offset }, size: { width: 220, height: 112 } }] }
}
export function createGraph(name: string): VisualGraph {
  let graph: VisualGraph = { version: 1, name, nodes: [], edges: [] }
  for (const [i, kind] of (['trigger', 'condition', 'service'] as const).entries()) {
    const id = `${kind}-1`
    graph = addNode(graph, kind, id)
    graph = updateNode(graph, id, { position: { x: 60 + i * 300, y: 100 } })
  }
  return connectNodes(connectNodes(graph, 'trigger-1', 'condition-1'), 'condition-1', 'service-1')
}
export function updateNode(graph: VisualGraph, id: string, patch: Partial<Omit<GraphNode, 'id' | 'kind'>>): VisualGraph {
  return { ...graph, nodes: graph.nodes.map(node => node.id === id ? { ...node, ...patch } : node) }
}
export function removeNode(graph: VisualGraph, id: string): VisualGraph {
  return { ...graph, nodes: graph.nodes.filter(node => node.id !== id), edges: graph.edges.filter(edge => edge.source !== id && edge.target !== id) }
}
export function canConnect(graph: VisualGraph, source: string, target: string): boolean {
  if (source === target || !graph.nodes.some(n => n.id === source) || !graph.nodes.some(n => n.id === target)) return false
  if (graph.nodes.find(n => n.id === target)?.kind === 'trigger' || graph.edges.some(e => e.source === source && e.target === target)) return false
  const visited = new Set<string>()
  const reachesSource = (id: string): boolean => {
    if (id === source) return true
    if (visited.has(id)) return false
    visited.add(id)
    return graph.edges.filter(e => e.source === id).some(e => reachesSource(e.target))
  }
  return !reachesSource(target)
}
export function connectNodes(graph: VisualGraph, source: string, target: string): VisualGraph {
  if (!canConnect(graph, source, target)) return graph
  return { ...graph, edges: [...graph.edges, { id: `${source}->${target}`, source, target }] }
}
export const serializeGraph = (graph: VisualGraph) => JSON.stringify(graph, null, 2)

export function createGraphHistory(initial: VisualGraph) {
  const [graph, setGraph] = createSignal(initial)
  const [past, setPast] = createSignal<VisualGraph[]>([])
  const [future, setFuture] = createSignal<VisualGraph[]>([])
  return {
    graph,
    canUndo: () => past().length > 0,
    canRedo: () => future().length > 0,
    commit(next: VisualGraph) {
      if (serializeGraph(next) === serializeGraph(graph())) return
      setPast([...past().slice(-99), graph()]); setFuture([]); setGraph(next)
    },
    undo() {
      if (!past().length) return
      setFuture([graph(), ...future()]); setGraph(past().at(-1)!); setPast(past().slice(0, -1))
    },
    redo() {
      if (!future().length) return
      setPast([...past(), graph()]); setGraph(future()[0]); setFuture(future().slice(1))
    },
  }
}
