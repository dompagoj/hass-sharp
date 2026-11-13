import { Show } from 'solid-js'
import { Button } from '../components/Controls'
import { NodeConfig } from './NodeConfig'
import type { VisualDiagnostic } from './api'
import type { GraphNode, VisualCatalogSnapshot } from './contract'
import { catalogPalette, portLabel, removeNode, updateNode, type VisualGraph } from './graph'

/** The right-hand panel: what a palette node does, or the inputs and outputs of the selected node. */
export function Inspector(props: {
  /** A palette node the user clicked for its description; shown instead of the selection. */
  description?: ReturnType<typeof catalogPalette>[number]
  selected?: GraphNode
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics: VisualDiagnostic[]
  commit(graph: VisualGraph, options?: { coalesce?: string }): void
}) {
  return (
    <aside class="hs-inspector" aria-label="Node inspector">
      <Show when={props.description}>
        {item => (
          <div class="hs-stack">
            <h2>{item().label}</h2>
            <p>{item().description}</p>
            <p>
              Inputs:{' '}
              {Object.entries(item().inputs)
                .map(([id, input]) => input.label ?? id)
                .join(', ') || 'None'}
            </p>
            <p>Outputs: {item().execution.map(portLabel).join(', ')}</p>
            <p class="hs-muted">Drag this node onto the canvas, or use Add step.</p>
          </div>
        )}
      </Show>
      <Show when={!props.description}>
        <Show when={props.selected} fallback={<p class="hs-muted">Select a node to edit its inputs and outputs.</p>}>
          {node => (
            <>
              <NodeConfig
                node={node()}
                graph={props.graph}
                catalog={props.catalog}
                diagnostics={props.diagnostics}
                onChange={config => props.commit(updateNode(props.graph, node().id, { config }))}
                onGraphChange={props.commit}
              />
              <Button variant="danger" onClick={() => props.commit(removeNode(props.graph, node().id, props.catalog))}>
                Delete step
              </Button>
            </>
          )}
        </Show>
      </Show>
    </aside>
  )
}
