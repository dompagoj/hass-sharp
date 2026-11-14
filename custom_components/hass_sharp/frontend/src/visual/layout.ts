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

/** A port whose steps run inside the step (a loop's body or condition, a parallel branch). */
const scopePort = (node: GraphNode, port: string, catalog: VisualCatalogSnapshot) =>
  (Object.hasOwn(catalog.nodes, node.kind) && !!catalog.nodes[node.kind].scopePorts?.includes(port)) || (node.kind === 'parallel' && port.startsWith('branch:'))

/**
 * A layered left-to-right layout ("Tidy"). Each connected flow (steps joined by execution or value wires) is a
 * horizontal band of its own; bands are stacked top to bottom in the order of their first trigger's current
 * position, and steps connected to nothing share a last band, in a row.
 *
 * Within a band, execution wires decide the columns: every node sits one column after the furthest step that runs it
 * (longest path), and what runs after a loop or a parallel step sits right of every step inside it. A node with only
 * data wires (a value node) sits in the column before its first reader: the one in the leftmost column. Within a column the order follows execution (a branch's
 * ports top to bottom), then a few barycentre sweeps reduce crossings. Each card is placed level with the steps that
 * feed it where there is room, and never overlaps another: columns are as wide as their widest card and cards in a
 * column are stacked. The result depends only on the graph, the sizes and the order of the bands' triggers, so the
 * same input always gives the same positions, and tidying a tidy graph changes nothing.
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
    .map((edge, order) => ({ edge, order, port: outputPort(byId.get(edge.source)!.kind, edge) }))
    .map(entry => ({ ...entry, rank: portRank(byId.get(entry.edge.source)!, entry.port) }))
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
  // Column order: execution wires, plus "after everything inside" for what runs after a loop or parallel step.
  const after = new Map<string, string[]>(ids.map(id => [id, [...exec.get(id)!]]))
  const afterIn = new Map<string, string[]>(ids.map(id => [id, [...execIn.get(id)!]]))
  for (const node of nodes) {
    const inner = ranked.filter(({ edge, port }) => edge.source === node.id && scopePort(node, port, catalog)).map(({ edge }) => edge.target)
    if (!inner.length) continue
    const scope = new Set<string>()
    const walk = (id: string) => {
      if (id === node.id || scope.has(id)) return
      scope.add(id)
      for (const next of exec.get(id)!) walk(next)
    }
    inner.forEach(walk)
    const outside = ranked.filter(({ edge, port }) => edge.source === node.id && !scopePort(node, port, catalog)).map(({ edge }) => edge.target)
    for (const member of scope) for (const target of outside) if (!scope.has(target)) {
      after.get(member)!.push(target)
      afterIn.get(target)!.push(member)
    }
  }

  // Bands: connected flows, by execution and value wires together.
  const parent = new Map(ids.map(id => [id, id]))
  const find = (id: string): string => (parent.get(id) === id ? id : (parent.set(id, find(parent.get(id)!)), parent.get(id)!))
  for (const edge of edges) parent.set(find(edge.source), find(edge.target))
  const groups = new Map<string, string[]>()
  for (const id of ids) groups.set(find(id), [...(groups.get(find(id)) ?? []), id])
  const at = (id: string) => ({ x: finite(byId.get(id)!.position?.x, 0), y: finite(byId.get(id)!.position?.y, 0) })
  const before = (a: string, b: string) => at(a).y - at(b).y || at(a).x - at(b).x || index.get(a)! - index.get(b)!
  const lead = (members: string[]) => {
    const triggers = members.filter(id => byId.get(id)!.kind === 'trigger')
    return [...(triggers.length ? triggers : members)].sort(before)[0]
  }
  const connected = [...groups.values()].filter(members => members.length > 1)
  const hasTrigger = (members: string[]) => members.some(id => byId.get(id)!.kind === 'trigger')
  connected.sort((a, b) => Number(hasTrigger(b)) - Number(hasTrigger(a)) || before(lead(a), lead(b)))
  const loose = [...groups.values()].filter(members => members.length === 1).map(([id]) => id).sort((a, b) => index.get(a)! - index.get(b)!)

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

  /** Lays out one connected flow with its top at `top`; returns its bottom edge. */
  const band = (members: string[], top: number): number => {
    // Columns: longest path (Kahn's order, ties by graph order). Nodes on a cycle the validator would refuse still
    // get a column: one after their placed predecessors.
    const layer = new Map<string, number>()
    const pending = new Map(members.map(id => [id, afterIn.get(id)!.length]))
    const ready = members.filter(id => onExec(id) && pending.get(id) === 0)
    while (ready.length) {
      ready.sort((a, b) => index.get(a)! - index.get(b)!)
      const id = ready.shift()!
      if (!layer.has(id)) layer.set(id, 0)
      for (const next of after.get(id)!) {
        layer.set(next, Math.max(layer.get(next) ?? 0, layer.get(id)! + 1))
        pending.set(next, pending.get(next)! - 1)
        if (pending.get(next) === 0) ready.push(next)
      }
    }
    for (const id of members) if (onExec(id) && !layer.has(id)) layer.set(id, Math.max(0, ...afterIn.get(id)!.map(p => (layer.get(p) ?? -1) + 1)))
    // Data-only nodes: the column before their first reader, the leftmost one that has a column. Chains settle over a
    // few passes: a node is moved left again when a reader placed later sits further left.
    for (let pass = 0; pass < members.length; pass++) {
      let changed = false
      for (const id of members) {
        if (onExec(id)) continue
        const readers = dataOut.get(id)!.filter(target => layer.has(target))
        if (!readers.length) continue
        const column = Math.min(...readers.map(target => layer.get(target)!)) - 1
        if (layer.get(id) !== column) { layer.set(id, column); changed = true }
      }
      if (!changed) break
    }
    // Left over: fed only (the column after their producers) or not connected at all (the first column).
    for (const id of members) if (!layer.has(id)) {
      const producers = dataIn.get(id)!.filter(source => layer.has(source))
      layer.set(id, producers.length ? Math.max(...producers.map(p => layer.get(p)! + 1)) : 0)
    }
    const shift = Math.min(...members.map(id => layer.get(id)!))
    for (const id of members) layer.set(id, layer.get(id)! - shift)

    // Initial order in each column: depth-first along execution from the roots, so a branch's steps stay together.
    const seen = new Map<string, number>()
    const visit = (id: string) => {
      if (seen.has(id)) return
      seen.set(id, seen.size)
      for (const next of exec.get(id)!) visit(next)
    }
    for (const id of members) if (onExec(id) && execIn.get(id)!.length === 0) visit(id)
    for (const id of members) if (onExec(id)) visit(id)
    const firstKey = (id: string): number => {
      if (seen.has(id)) return seen.get(id)!
      const consumer = dataOut.get(id)!.find(target => seen.has(target))
      return consumer !== undefined ? seen.get(consumer)! - 0.5 : members.length + index.get(id)!
    }
    const columns: string[][] = []
    for (const id of members) (columns[layer.get(id)!] ??= []).push(id)
    for (let i = 0; i < columns.length; i++) columns[i] = (columns[i] ?? []).sort((a, b) => firstKey(a) - firstKey(b) || index.get(a)! - index.get(b)!)

    // Barycentre sweeps: order each column by the mean rank of its neighbours in earlier (then later) columns.
    const neighbours = (id: string, earlier: boolean) => {
      const list = earlier ? [...execIn.get(id)!, ...dataIn.get(id)!] : [...exec.get(id)!, ...dataOut.get(id)!]
      return list.filter(other => (earlier ? layer.get(other)! < layer.get(id)! : layer.get(other)! > layer.get(id)!))
    }
    const rank = new Map<string, number>()
    columns.forEach(column => column.forEach((id, i) => rank.set(id, column.length > 1 ? i / (column.length - 1) : 0.5)))
    for (let sweep = 0; sweep < 4; sweep++) {
      const earlier = sweep % 2 === 0
      const order = earlier ? columns.map((_, i) => i) : columns.map((_, i) => columns.length - 1 - i)
      for (const c of order) {
        const column = columns[c]
        const key = new Map(column.map(id => {
          const near = neighbours(id, earlier)
          return [id, near.length ? near.reduce((sum, other) => sum + rank.get(other)!, 0) / near.length : rank.get(id)!] as const
        }))
        const position = new Map(column.map((id, i) => [id, i]))
        column.sort((a, b) => key.get(a)! - key.get(b)! || position.get(a)! - position.get(b)!)
        column.forEach((id, i) => rank.set(id, column.length > 1 ? i / (column.length - 1) : 0.5))
      }
    }

    let x = start.x, bandBottom = top
    for (const column of columns) {
      let bottom = -Infinity
      for (const id of column) {
        const { height } = size(id)
        // Level with the centre of the cards that feed it, when nothing above is in the way.
        const feeders = neighbours(id, true).filter(other => positions.has(other))
        const want = feeders.length
          ? feeders.reduce((sum, other) => sum + positions.get(other)!.y + size(other).height / 2, 0) / feeders.length - height / 2
          : top
        const y = up(Math.max(want, top, bottom === -Infinity ? top : bottom + gapY))
        positions.set(id, { x, y })
        bottom = y + height
        bandBottom = Math.max(bandBottom, bottom)
      }
      x = up(x + Math.max(...column.map(id => size(id).width)) + gapX)
    }
    return bandBottom
  }

  let top = start.y
  for (const members of connected) top = up(band(members, top) + gapY * 2)
  // Steps connected to nothing: one row, in graph order.
  let x = start.x
  for (const id of loose) {
    positions.set(id, { x, y: top })
    x = up(x + size(id).width + gapX / 2)
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

export interface LayoutBox extends Point, LayoutSize { id: string }

/**
 * Where cards go so that none overlaps another, for a view that cannot be edited (the trace view): stored positions
 * are kept except that a card overlapping one above it (one that has grown taller since the graph was laid out, by a
 * run's values or a newer card design) moves down to `gap` below it; what it then overlaps moves down after it. Cards
 * that only sit close are left where they are.
 * Only the cards that move are in the result, so a graph without overlaps gets an empty map. The graph is unchanged.
 */
export function separateCards(boxes: readonly LayoutBox[], gap = 24): Map<string, Point> {
  const order = [...boxes].sort((a, b) => a.y - b.y || a.x - b.x || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  const placed: LayoutBox[] = [], moved = new Map<string, Point>()
  const across = (a: LayoutBox, b: LayoutBox) => a.x < b.x + b.width && b.x < a.x + a.width
  for (const box of order) {
    let y = box.y
    // Below every placed card it would overlap, until it overlaps none (each pass moves it further down).
    for (let again = true; again; ) {
      again = false
      for (const other of placed) {
        if (across(box, other) && y < other.y + other.height && other.y < y + box.height) { y = other.y + other.height + gap; again = true }
      }
    }
    if (y !== box.y) moved.set(box.id, { x: box.x, y })
    placed.push({ ...box, y })
  }
  return moved
}
