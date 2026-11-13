import { createMemo, Show } from 'solid-js'
import { BaseEdge, EdgeLabelRenderer, getBezierPath, useViewport, type EdgeProps } from '@dschz/solid-flow'
import { ToolIcon, toolIcons } from './CanvasToolbar'
import { useFlow } from './flowModel'

/** What the adapter puts in `edge.data` for a wire. */
export interface WireData extends Record<string, unknown> {
  color: string
  /** The execution error path: red and dashed. */
  error?: boolean
}

const geometry = (props: EdgeProps, curvature: number) =>
  getBezierPath({
    sourceX: props.sourceX,
    sourceY: props.sourceY,
    targetX: props.targetX,
    targetY: props.targetY,
    sourcePosition: props.sourcePosition,
    targetPosition: props.targetPosition,
    curvature,
  })

const wire = (props: EdgeProps) => (props.data ?? {}) as Partial<WireData>

/**
 * A selected wire shows a "Delete wire" button at its middle, for touch and for anyone who does not use Delete. It is
 * scaled against the zoom, so it keeps its on-screen size (and its 32 px touch target) however far the canvas zooms.
 */
function DeleteWire(props: { edge: EdgeProps; x: number; y: number }) {
  const flow = useFlow()
  const viewport = useViewport()
  const zoom = () => viewport().zoom || 1
  return (
    <Show when={props.edge.selected && !flow.readOnly()}>
      <EdgeLabelRenderer>
        <button
          type="button"
          class="hs-wire-delete nodrag nopan"
          style={{ transform: `translate(${props.x}px, ${props.y}px) translate(-50%, -50%) scale(${1 / zoom()})` }}
          aria-label="Delete wire"
          data-tip="Delete wire (Delete)"
          on:pointerdown={(event: PointerEvent) => event.stopPropagation()}
          on:click={(event: MouseEvent) => {
            event.stopPropagation()
            flow.removeEdge(props.edge.id)
          }}
        >
          <ToolIcon paths={toolIcons.remove} />
        </button>
      </EdgeLabelRenderer>
    </Show>
  )
}

/**
 * An execution wire: the order steps run in. Thick and neutral, so it reads as the spine of the automation; the
 * error path is red and dashed. Trace classes on the edge group (`hs-wire--travelled`, `hs-wire--idle`) style it.
 */
export function ExecEdge(props: EdgeProps) {
  // One path per change of the ends, read by the wire and the button alike.
  const path = createMemo(() => geometry(props, 0.32))
  return (
    <>
      <BaseEdge
        path={path()[0]}
        class={`hs-wire hs-wire--exec${wire(props).error ? ' hs-wire--error' : ''}`}
        style={{ '--hs-wire-color': wire(props).color ?? 'var(--hs-port-execution)' }}
        interactionWidth={16}
      />
      <DeleteWire edge={props} x={path()[1]} y={path()[2]} />
    </>
  )
}

/** A data wire: a value handed from an output to an input, in the colour of its type. */
export function DataEdge(props: EdgeProps) {
  const path = createMemo(() => geometry(props, 0.25))
  return (
    <>
      <BaseEdge
        path={path()[0]}
        class="hs-wire hs-wire--data"
        style={{ '--hs-wire-color': wire(props).color ?? 'var(--hs-port-object)' }}
        interactionWidth={14}
      />
      <DeleteWire edge={props} x={path()[1]} y={path()[2]} />
    </>
  )
}

export const edgeTypes = { exec: ExecEdge, data: DataEdge }
