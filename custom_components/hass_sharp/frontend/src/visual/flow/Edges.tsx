import { BaseEdge, getBezierPath, type EdgeProps } from '@dschz/solid-flow'

/** What the adapter puts in `edge.data` for a wire. */
export interface WireData extends Record<string, unknown> {
  color: string
  /** The execution error path: red and dashed. */
  error?: boolean
}

const path = (props: EdgeProps, curvature: number) =>
  getBezierPath({
    sourceX: props.sourceX,
    sourceY: props.sourceY,
    targetX: props.targetX,
    targetY: props.targetY,
    sourcePosition: props.sourcePosition,
    targetPosition: props.targetPosition,
    curvature,
  })[0]

const wire = (props: EdgeProps) => (props.data ?? {}) as Partial<WireData>

/**
 * An execution wire: the order steps run in. Thick and neutral, so it reads as the spine of the automation; the
 * error path is red and dashed. Trace classes on the edge group (`hs-wire--travelled`, `hs-wire--idle`) style it.
 */
export function ExecEdge(props: EdgeProps) {
  return (
    <BaseEdge
      path={path(props, 0.32)}
      class={`hs-wire hs-wire--exec${wire(props).error ? ' hs-wire--error' : ''}`}
      style={{ '--hs-wire-color': wire(props).color ?? 'var(--hs-port-execution)' }}
      interactionWidth={16}
    />
  )
}

/** A data wire: a value handed from an output to an input, in the colour of its type. */
export function DataEdge(props: EdgeProps) {
  return (
    <BaseEdge
      path={path(props, 0.25)}
      class="hs-wire hs-wire--data"
      style={{ '--hs-wire-color': wire(props).color ?? 'var(--hs-port-object)' }}
      interactionWidth={14}
    />
  )
}

export const edgeTypes = { exec: ExecEdge, data: DataEdge }
