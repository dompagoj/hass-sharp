import { createMemo, For, Show } from 'solid-js'
import { Glyph } from './Glyph'
import { TypeChip } from './InputRow'
import { graphDocument, type VisualCatalogSnapshot } from './contract'
import { buildNodeView, type PinView } from './flow/flowModel'
import { createNodeAt, stepDescription, type VisualGraph } from './graph'
import { nodeIdentity } from './icons'

export interface KindPreviewProps {
  kind: string
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  onClose(): void
  /** A pressed in the details: the canvas's quick-add, as A on the canvas opens it. */
  onQuickAdd?(): void
}

/**
 * What a kind of step is, read only, for the step library: its header as {@link NodeConfig} has it, its whole
 * description for this graph's document, how to add one and the pins a new step of it would have. Nothing here adds
 * a step; A opens quick-add and Escape closes the details. A kind the catalog does not have renders nothing.
 */
export function KindPreview(props: KindPreviewProps) {
  const spec = () => (Object.hasOwn(props.catalog.nodes, props.kind) ? props.catalog.nodes[props.kind] : undefined)
  const identity = () => nodeIdentity(props.kind, props.catalog)
  // The pins come from a step of the kind as this graph would add it; none when the graph cannot take one.
  const view = createMemo(() => {
    // Refused, createNodeAt returns the same graph, whose last node is some other step: its pins must not show here.
    const next = createNodeAt(props.graph, props.kind, { x: 0, y: 0 }, undefined, props.catalog)
    return next === props.graph ? undefined : buildNodeView(next.nodes.at(-1)!, { graph: next, catalog: props.catalog, diagnostics: [] })
  })
  const inputs = () => view()?.rows.flatMap(row => (row.input ? [row.input.pin] : [])) ?? []
  const outputs = () => {
    const current = view()
    if (!current) return []
    return [...(current.next ? [current.next] : []), ...current.rows.flatMap(row => (row.output ? [row.output] : [])), ...(current.footer ? [current.footer] : [])]
  }
  const pin = (current: PinView) => (
    <li class="hs-insp-output">
      <span class="hs-insp-output__name">{current.label}</span>
      <Show when={current.kind === 'data'}><TypeChip text={current.typeName ?? 'Any value'} color={current.color} list={current.list} /></Show>
      <Show when={current.description}><small class="hs-insp-output__desc">{current.description}</small></Show>
    </li>
  )
  const pins = (label: string, list: PinView[]) => (
    <Show when={list.length}>
      <section class="hs-insp-section">
        <h3 class="hs-insp-heading">{label}</h3>
        <ul class="hs-insp-outputs" aria-label={label}><For each={list}>{pin}</For></ul>
      </section>
    </Show>
  )

  const keydown = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.preventDefault()
      props.onClose()
    } else if (!event.ctrlKey && !event.metaKey && !event.altKey && event.key.toLowerCase() === 'a' && props.onQuickAdd) {
      event.preventDefault()
      props.onQuickAdd()
    }
  }

  return (
    <Show when={spec()}>
      {current => (
        <section class="hs-insp-preview" aria-label="Step details" style={{ '--hs-cat': identity().color }} onKeyDown={keydown}>
          <header class="hs-insp-head">
            <span class="hs-insp-head__icon" title={identity().label}><Glyph paths={identity().icon} /></span>
            <div class="hs-insp-head__titles">
              <h2 class="hs-insp-title">{current().label}</h2>
              <small class="hs-insp-kind">{current().label === identity().label ? current().label : `${current().label} · ${identity().label}`}</small>
            </div>
            <button type="button" class="hs-button hs-button--ghost hs-insp-close" aria-label="Close details" onClick={() => props.onClose()}>
              <Glyph paths={['M6 6l12 12', 'M18 6 6 18']} />
            </button>
          </header>
          <p class="hs-insp-desc hs-insp-desc--open">{stepDescription(props.kind, current().description, graphDocument(props.graph))}</p>
          <p class="hs-muted hs-insp-hint">Drag onto the canvas, or use + on the canvas (A).</p>
          {pins('Inputs', inputs())}
          {pins('Outputs', outputs())}
        </section>
      )}
    </Show>
  )
}
