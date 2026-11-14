import { createMemo, For } from 'solid-js'
import type { GraphOutline } from '../visual/api'
import { isPure } from '../visual/contract'
import { nodeIdentity } from '../visual/icons'

// Sizes in graph units, about those of the cards on the canvas; value nodes are smaller.
const STEP = { w: 260, h: 110 }
const VALUE = { w: 200, h: 64 }
const PAD = 60
// A graph of one or two steps still reads as small, not blown up to the thumbnail's size.
const MIN = { w: 1100, h: 420 }

/**
 * A small picture of a graph from its saved node positions: one rounded rectangle per node in its category colour,
 * the execution wires as thin lines. Decoration only (`aria-hidden`): the card says the same in words.
 */
export function GraphThumbnail(props: { outline: GraphOutline; class?: string }) {
  const layout = createMemo(() => {
    const nodes = props.outline.nodes.map(node => {
      const size = isPure(node.kind) ? VALUE : STEP
      return { x: node.x, y: node.y, w: size.w, h: size.h, color: nodeIdentity(node.kind).color, trigger: node.kind === 'trigger' }
    })
    if (!nodes.length) return { nodes, wires: [], box: `0 0 ${MIN.w} ${MIN.h}` }
    let left = Math.min(...nodes.map(n => n.x)) - PAD, top = Math.min(...nodes.map(n => n.y)) - PAD
    let width = Math.max(...nodes.map(n => n.x + n.w)) + PAD - left, height = Math.max(...nodes.map(n => n.y + n.h)) + PAD - top
    if (width < MIN.w) { left -= (MIN.w - width) / 2; width = MIN.w }
    if (height < MIN.h) { top -= (MIN.h - height) / 2; height = MIN.h }
    const wires = props.outline.edges.flatMap(([from, to]) => {
      const a = nodes[from], b = nodes[to]
      if (!a || !b) return []
      const x1 = a.x + a.w, y1 = a.y + a.h / 2, x2 = b.x, y2 = b.y + b.h / 2
      const bend = Math.max(40, Math.abs(x2 - x1) / 2)
      return [`M${x1} ${y1}C${x1 + bend} ${y1} ${x2 - bend} ${y2} ${x2} ${y2}`]
    })
    return { nodes, wires, box: `${left} ${top} ${width} ${height}` }
  })
  return (
    <svg class={props.class ?? 'hs-thumb'} viewBox={layout().box} preserveAspectRatio="xMidYMid meet" aria-hidden="true">
      <For each={layout().wires}>{d => <path class="hs-thumb__wire" d={d} />}</For>
      <For each={layout().nodes}>
        {node => (
          <rect class="hs-thumb__node" classList={{ 'hs-thumb__node--trigger': node.trigger }} x={node.x} y={node.y} width={node.w} height={node.h} rx="22"
            style={{ '--hs-thumb-color': node.color }} />
        )}
      </For>
    </svg>
  )
}
