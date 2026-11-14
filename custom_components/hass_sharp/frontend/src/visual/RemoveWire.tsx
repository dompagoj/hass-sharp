import { Show } from 'solid-js'
import type { VisualCatalogSnapshot } from './contract'
import { danglingWire, removeWireLabel, withoutWire } from './danglingWire'
import type { VisualGraph } from './graph'

/**
 * "Remove connection" on a problem about a value wire whose pin is missing (which the canvas cannot draw, so it has no
 * "Delete wire" of its own). It removes exactly that wire, as one change (one undo step); nothing when the problem is
 * about anything else. Focus then goes to `focusAfter`, since the problem and its button go with the wire.
 */
export function RemoveWire(props: {
  graph: VisualGraph
  diagnostic: { edgeId?: string }
  catalog: VisualCatalogSnapshot
  class: string
  onRemove(graph: VisualGraph): void
  focusAfter?(): HTMLElement | null | undefined
}) {
  const edge = () => danglingWire(props.graph, props.diagnostic)
  return (
    <Show when={edge()}>
      {wire => (
        <button type="button" class={props.class} aria-label={removeWireLabel(props.graph, wire(), props.catalog)}
          onClick={() => {
            props.onRemove(withoutWire(props.graph, wire().id))
            queueMicrotask(() => props.focusAfter?.()?.focus())
          }}>
          Remove connection
        </button>
      )}
    </Show>
  )
}
