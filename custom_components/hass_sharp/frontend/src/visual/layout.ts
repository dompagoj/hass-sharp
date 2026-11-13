import { executionPorts, nodeCatalog, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { outputPort, type VisualGraph } from './graph'

type Point = { x: number; y: number }
export interface LayoutSize { width: number; height: number }
export interface LayoutOptions {
  /** Measured card sizes; unmeasured cards count as `fallback`. */
  sizes?: (id: string) => LayoutSize | undefined
  /** Lay out only these nodes; the others stay where they are. */
  only?: ReadonlySet<string>
  /** Positions are multiples of this (px). */
  grid?: number
  fallback?: LayoutSize
  /** Space between columns and between cards in a column (px). */
  gapX?: number
  gapY?: number
}

const valid = (node: unknown): node is GraphNode =>
  !!node && typeof node === 'object' && typeof (node as GraphNode).id === 'string' && typeof (node as GraphNode).kind === 'string'

/**
 * A layered left-to-right layout ("Tidy"). Execution wires decide the columns: every node sits one column after the
 * furthest step that runs it (longest path). A node with only data wires sits in the column before its first
 * consumer. Within a column the order follows execution (a branch's ports top to bottom), then a few barycentre
 * sweeps reduce crossings. Each card is placed level with the steps that feed it where there is room, and never
 * overlaps another: columns are as wide as their widest card and cards in a column are stacked. The result depends
 * only on the graph and the sizes, so the same input always gives the same positions.
 */
export function tidyLayout(graph: VisualGraph, catalog: VisualCatalogSnapshot = nodeCatalog, options: LayoutOptions = {}): Map<string, Point> {
  const grid = options.grid ?? 20, gapX = options.gapX ?? 100, gapY = options.gapY ?? 40
  const fallback = options.fallback ?? { width: 260, height: 120 }
  const nodes = (Array.isArray(graph.nodes) ? graph.nodes : []).filter(valid).filter((node, i, all) =>
    all.findIndex(other => other.id === node.id) === i && (!options.only || options.only.has(node.id)))
  if (!nodes.length) return new Map()
  const ids = nodes.map(node => node.id)
  const index = new Map(ids.map((id, i) => [id, i]))
  const byId = new Map(nodes.map(node => [node.id, node]))
  const edges = (Array.isArray(graph.edges) ? graph.edges : []).filter(edge => !!edge && index.has(edge.source) && index.has(edge.target) && edge.source !== edge.target)
  // Execution successors in the order of the source's ports (true before false, body before next); data links.
  const portRank = (source: GraphNode, port: string) => {
    const ports = executionPorts(source, catalog)
    const at = ports.indexOf(port)
    return at < 0 ? ports.length : at
  }
  const exec = new Map<string, string[]>(ids.map(id => [id, []]))
  const execIn = new Map<string, string[]>(ids.map(id => [id, []]))
  const ranked = edges
    .filter(edge => edge.kind !== 'data')
    .map((edge, order) => ({ edge, order, rank: portRank(byId.get(edge.source)!, outputPort(byId.get(edge.source)!.kind, edge)) }))
    .sort((a, b) => index.get(a.edge.source)! - index.get(b.edge.source)! || a.rank - b.rank || a.order - b.order)
  for (const { edge } of ranked) {
    exec.get(edge.source)!.push(edge.target)
    execIn.get(edge.target)!.push(edge.source)
  }
  const dataOut = new Map<string, string[]>(ids.map(id => [id, []]))
  const dataIn = new Map<string, string[]>(ids.map(id => [id, []]))
  for (const edge of edges) if (edge.kind === 'data') {
    dataOut.get(edge.source)!.push(edge.target)
    dataIn.get(edge.target)!.push(edge.source)
  }
  const onExec = (id: string) => exec.get(id)!.length > 0 || execIn.get(id)!.length > 0

  // Columns: longest path over execution wires (Kahn's order, ties by graph order). Nodes on a cycle the validator
  // would refuse still get a column: one after their placed predecessors.
  const layer = new Map<string, number>()
  const pending = new Map(ids.map(id => [id, execIn.get(id)!.length]))
  const ready = ids.filter(id => onExec(id) && pending.get(id) === 0)
  while (ready.length) {
    ready.sort((a, b) => index.get(a)! - index.get(b)!)
    const id = ready.shift()!
    if (!layer.has(id)) layer.set(id, 0)
    for (const next of exec.get(id)!) {
      layer.set(next, Math.max(layer.get(next) ?? 0, layer.get(id)! + 1))
      pending.set(next, pending.get(next)! - 1)
      if (pending.get(next) === 0) ready.push(next)
    }
  }
  for (const id of ids) if (onExec(id) && !layer.has(id)) layer.set(id, Math.max(0, ...execIn.get(id)!.map(p => (layer.get(p) ?? -1) + 1)))
  // Data-only nodes: the column before their first consumer that has one (chains resolve over a few passes).
  for (let pass = 0; pass < ids.length; pass++) {
    let changed = false
    for (const id of ids) {
      if (layer.has(id) || onExec(id)) continue
      const consumer = dataOut.get(id)!.find(target => layer.has(target))
      if (consumer !== undefined) { layer.set(id, layer.get(consumer)! - 1); changed = true }
    }
    if (!changed) break
  }
  // Left over: fed only (the column after their producers) or not connected at all (the first column).
  for (const id of ids) if (!layer.has(id)) {
    const producers = dataIn.get(id)!.filter(source => layer.has(source))
    layer.set(id, producers.length ? Math.max(...producers.map(p => layer.get(p)! + 1)) : 0)
  }
  const shift = Math.min(...layer.values())
  for (const id of ids) layer.set(id, layer.get(id)! - shift)

  // Initial order in each column: depth-first along execution from the roots, so a branch's steps stay together.
  const seen = new Map<string, number>()
  const visit = (id: string) => {
    if (seen.has(id)) return
    seen.set(id, seen.size)
    for (const next of exec.get(id)!) visit(next)
  }
  for (const id of ids) if (onExec(id) && execIn.get(id)!.length === 0) visit(id)
  for (const id of ids) if (onExec(id)) visit(id)
  const firstKey = (id: string): number => {
    if (seen.has(id)) return seen.get(id)!
    const consumer = dataOut.get(id)!.find(target => seen.has(target))
    return consumer !== undefined ? seen.get(consumer)! - 0.5 : ids.length + index.get(id)!
  }
  const columns: string[][] = []
  for (const id of ids) (columns[layer.get(id)!] ??= []).push(id)
  for (let i = 0; i < columns.length; i++) columns[i] = (columns[i] ?? []).sort((a, b) => firstKey(a) - firstKey(b) || index.get(a)! - index.get(b)!)

  // Barycentre sweeps: order each column by the mean rank of its neighbours in earlier (then later) columns.
  const neighbours = (id: string, before: boolean) => {
    const list = before ? [...execIn.get(id)!, ...dataIn.get(id)!] : [...exec.get(id)!, ...dataOut.get(id)!]
    return list.filter(other => (before ? layer.get(other)! < layer.get(id)! : layer.get(other)! > layer.get(id)!))
  }
  const rank = new Map<string, number>()
  const rerank = () => columns.forEach(column => column.forEach((id, i) => rank.set(id, column.length > 1 ? i / (column.length - 1) : 0.5)))
  rerank()
  for (let sweep = 0; sweep < 4; sweep++) {
    const before = sweep % 2 === 0
    const order = before ? columns.map((_, i) => i) : columns.map((_, i) => columns.length - 1 - i)
    for (const c of order) {
      const column = columns[c]
      const key = new Map(column.map(id => {
        const near = neighbours(id, before)
        return [id, near.length ? near.reduce((sum, other) => sum + rank.get(other)!, 0) / near.length : rank.get(id)!] as const
      }))
      const position = new Map(column.map((id, i) => [id, i]))
      column.sort((a, b) => key.get(a)! - key.get(b)! || position.get(a)! - position.get(b)!)
      column.forEach((id, i) => rank.set(id, column.length > 1 ? i / (column.length - 1) : 0.5))
    }
  }

  // Coordinates from the top-left of the nodes being laid out.
  const size = (id: string) => {
    const measured = options.sizes?.(id)
    return measured && measured.width > 0 && measured.height > 0 ? measured : fallback
  }
  const up = (value: number) => Math.ceil(value / grid) * grid
  const origin = nodes.reduce<Point>((min, node) => ({
    x: Math.min(min.x, finite(node.position?.x, 60)), y: Math.min(min.y, finite(node.position?.y, 100)),
  }), { x: Infinity, y: Infinity })
  const start = { x: up(origin.x), y: up(origin.y) }
  const positions = new Map<string, Point>()
  let x = start.x
  for (const column of columns) {
    let bottom = -Infinity
    for (const id of column) {
      const { height } = size(id)
      // Level with the centre of the cards that feed it, when nothing above is in the way.
      const feeders = neighbours(id, true).filter(other => positions.has(other))
      const want = feeders.length
        ? feeders.reduce((sum, other) => sum + positions.get(other)!.y + size(other).height / 2, 0) / feeders.length - height / 2
        : start.y
      const y = up(Math.max(want, start.y, bottom === -Infinity ? start.y : bottom + gapY))
      positions.set(id, { x, y })
      bottom = y + height
    }
    x = up(x + Math.max(...column.map(id => size(id).width)) + gapX)
  }
  return positions
}

const finite = (value: unknown, fallback: number) => (typeof value === 'number' && Number.isFinite(value) ? value : fallback)

/** The graph with these positions; the same object when nothing moved. */
export function applyLayout(graph: VisualGraph, positions: ReadonlyMap<string, Point>): VisualGraph {
  let moved = false
  const nodes = graph.nodes.map(node => {
    const next = valid(node) ? positions.get(node.id) : undefined
    if (!next || (node.position?.x === next.x && node.position?.y === next.y)) return node
    moved = true
    return { ...node, position: { ...next } }
  })
  return moved ? { ...graph, nodes } : graph
}
