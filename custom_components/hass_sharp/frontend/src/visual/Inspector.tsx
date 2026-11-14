import { createEffect, createMemo, createSignal, For, on, Show, type JSX } from 'solid-js'
import { NodeConfig } from './NodeConfig'
import { TypeChip } from './InputRow'
import { Glyph } from './Glyph'
import type { VisualDiagnostic } from './api'
import { copySteps, pasteSteps } from './clipboard'
import { graphTypes } from './connectionIndex'
import { dataOutputs, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { execLabel, nodeTitle } from './flow/flowModel'
import { nodeInputs, outputPort, removeNode, updateNode, type VisualGraph } from './graph'
import { focusSoon as focusWhenShown } from './soon'
import { icons, nodeIdentity } from './icons'
import { applyLayout, tidyLayout } from './layout'
import { nodeSummary, serviceFieldLabel, type SummaryNames } from './nodeSummary'
import { typeColor, typeIsList, typeLabel } from './typeLabels'

type Options = { coalesce?: string }
/** The last run of the automation, when the page knows it. */
export interface InspectorLastRun { status: string; when?: string; ok?: boolean }

export interface InspectorProps {
  /** The one selected node (the canvas' primary selection). */
  selected?: GraphNode
  /** Every selected node; two or more show the selection view. */
  selectedIds?: string[]
  /** A selected wire. */
  selectedEdgeId?: string
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  /** Registry names, so summaries name entities as the cards do. */
  names?: SummaryNames
  diagnostics: VisualDiagnostic[]
  commit(graph: VisualGraph, options?: Options): void
  /** Select a node (or nothing) on the canvas. */
  onSelect?(id?: string): void
  /** Shows the problems list (the overview's problem count links to it). */
  onShowProblems?(): void
  lastRun?: InspectorLastRun
  /** False when the saved automation is turned off (the overview says so); undefined for a draft. */
  enabled?: boolean
  /**
   * Docked: a side panel beside the canvas, always shown, with the overview when nothing is selected. Otherwise
   * (below 900px, or floating over a full-screen canvas) it is a sheet in two stages: a peek bar naming the
   * selection, and the panel; `open` is then the page's choice of the panel (default: while something is selected,
   * until closed).
   */
  docked?: boolean
  open?: boolean
  onClose?(): void
  /** The peek bar was tapped: the page opens the panel. */
  onOpen?(): void
  /** Duplicates steps with the canvas's own placement and returns the copies' ids (else the panel places them). */
  onDuplicate?(ids: string[]): string[]
  /** Opens the code editor of a Custom C# node. */
  onEditCode?(id: string): void
  /** Says on the canvas what a change made here also did (the wires a new trigger type removed). */
  onNotice?(text: string): void
}

/**
 * The right-hand panel. In order of precedence: several selected nodes, the selected node ({@link NodeConfig}), a selected wire (read only) or, with nothing selected, an overview of the
 * automation (its triggers, problem count and last run).
 */
export function Inspector(props: InspectorProps) {
  let aside!: HTMLElement
  const ids = () => (props.selectedIds ?? []).filter(id => props.graph.nodes.some(n => n.id === id))
  const many = () => ids().length > 1
  const edge = createMemo(() => (props.selectedEdgeId ? props.graph.edges.find(e => e.id === props.selectedEdgeId) : undefined))
  const [status, setStatus] = createSignal('')
  const [dismissed, setDismissed] = createSignal(false)
  // A new selection brings the sheet back.
  createEffect(on(() => [props.selected?.id, props.selectedEdgeId, ids().join(' ')], () => setDismissed(false), { defer: true }))
  const hasContent = () => !!(props.selected || many() || edge())
  // Docked, the panel is always there (the overview when nothing is selected).
  const open = () => !!props.docked || (props.open === undefined ? hasContent() && !dismissed() : props.open && hasContent())
  const peek = () => !props.docked && hasContent() && !open()
  const workspace = (): ParentNode => aside.closest('.hs-visual-workspace') ?? aside.parentElement ?? document
  const card = (id: string | undefined) =>
    id ? [...workspace().querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.getAttribute('data-id') === id) : undefined
  const canvas = () => workspace().querySelector<HTMLElement>('.hs-flow')
  /** Focuses what `find` returns once it has been rendered, unless the user has put focus somewhere else meanwhile. */
  const focusSoon = (find: () => HTMLElement | null | undefined) => focusWhenShown(find, aside)
  const title = (id: string) => () =>
    [...aside.querySelectorAll<HTMLElement>('.hs-insp-node')].find(el => el.dataset.nodeId === id)?.querySelector<HTMLElement>('input[aria-label="Node label"]')
  /** Selects a step and moves focus to its title in the panel, so focus never falls to the page. */
  const selectAndFocus = (id?: string) => {
    props.onSelect?.(id)
    if (id) focusSoon(title(id))
    else focusSoon(canvas)
  }
  const close = () => {
    const id = props.selected?.id
    setDismissed(true)
    props.onClose?.()
    // Back to where the selection is: its card on the canvas, else the canvas.
    focusSoon(() => card(id) ?? canvas())
  }

  const duplicate = (selection: string[]) => {
    let ids: string[]
    if (props.onDuplicate) {
      // The canvas places the copies, the same way as its own Duplicate (and says why when it cannot).
      ids = props.onDuplicate(selection)
      if (!ids.length) return
    } else {
      const data = copySteps(props.graph, selection)
      if (!data) return
      const result = pasteSteps(props.graph, data, props.catalog, { offset: { x: 40, y: 40 } }, 20)
      if ('error' in result) return setStatus(result.error)
      props.commit(result.graph)
      ids = result.ids
    }
    setStatus(ids.length === 1 ? 'Duplicated' : `Duplicated ${ids.length} steps`)
    if (selection.length === 1 && ids.length === 1) selectAndFocus(ids[0])
  }
  const remove = (selection: string[]) => {
    const next = selection.reduce((graph, id) => removeNode(graph, id, props.catalog), props.graph)
    props.commit(next)
    // The panel that held focus goes away with the steps; the canvas keeps it.
    selectAndFocus(undefined)
  }
  const tidy = (selection: string[]) => {
    const canvas = workspace()
    const sizes = (id: string) => {
      const card = [...canvas.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.getAttribute('data-id') === id)
      return card?.offsetWidth && card.offsetHeight ? { width: card.offsetWidth, height: card.offsetHeight } : undefined
    }
    const next = applyLayout(props.graph, tidyLayout(props.graph, props.catalog, { only: new Set(selection), grid: 20, sizes }))
    if (next === props.graph) return setStatus('Already tidy')
    props.commit(next)
    setStatus('Tidied')
  }
  const showProblems = () => props.onShowProblems?.()

  return (
    <aside ref={aside} class="hs-inspector" aria-label="Node inspector" data-open={open() ? 'true' : 'false'} data-peek={peek() ? 'true' : undefined}>
      <Show when={peek()}>
        <PeekBar />
      </Show>
      <SheetBar onClose={close} />
      <p class="hs-visually-hidden" role="status">{status()}</p>
      <Show when={many()} fallback={
        <Show when={props.selected?.id} keyed fallback={
          <Show when={edge()} fallback={<Overview />}>{current => <WireView edge={current()} />}</Show>
        }>
          {id => {
            // Keyed on the id, not the node: an edit of the step gives a new node object, and the panel (its
            // focus, open sections, drags and subscriptions) must survive it. Everything reads the latest node.
            let last = props.selected!
            const node = createMemo(() => { const current = props.selected; if (current?.id === id) last = current; return last })
            return (
              <NodeConfig node={node()} graph={props.graph} catalog={props.catalog} diagnostics={props.diagnostics}
                onChange={(config, options) => props.commit(updateNode(props.graph, id, { config }), options)}
                onGraphChange={props.commit} onSelect={selectAndFocus}
                onDuplicate={() => duplicate([id])} onDelete={() => remove([id])}
                onEditCode={props.onEditCode ? () => props.onEditCode?.(id) : undefined} onNotice={props.onNotice} />
            )
          }}
        </Show>
      }>
        <section class="hs-insp-many" aria-label="Selection">
          <h2 class="hs-insp-many__count">{ids().length} steps selected</h2>
          <div class="hs-insp-actions">
            <button type="button" class="hs-button hs-button--secondary" onClick={() => duplicate(ids())}>Duplicate</button>
            <button type="button" class="hs-button hs-button--secondary" onClick={() => tidy(ids())}>Tidy selection</button>
            <button type="button" class="hs-button hs-button--danger" onClick={() => remove(ids())}>Delete</button>
          </div>
          <ul class="hs-insp-list">
            <For each={ids()}>
              {id => {
                const node = () => props.graph.nodes.find(n => n.id === id)
                return (
                  <li>
                    <button type="button" class="hs-insp-link" onClick={() => selectAndFocus(id)}>{nodeTitle(node(), props.catalog)}</button>
                  </li>
                )
              }}
            </For>
          </ul>
        </section>
      </Show>
    </aside>
  )

  /**
   * The sheet's first stage: what is selected in one line and an "Edit" affordance; tapping it opens the panel. Only
   * shown (CSS) where the inspector is a sheet or floats; the side panel never collapses to it.
   */
  function PeekBar() {
    const node = () => (many() ? undefined : props.selected)
    const identity = () => { const current = node(); return current ? nodeIdentity(current.kind, props.catalog) : undefined }
    const title = () => {
      if (many()) return `${ids().length} steps selected`
      const current = node()
      if (current) return nodeTitle(current, props.catalog)
      return edge()?.kind === 'data' ? 'Value wire' : 'Flow wire'
    }
    const line = () => {
      const current = node(), wire = edge()
      if (current) return nodeSummary(current, props.catalog, props.names, props.graph)
      if (!many() && wire) {
        const end = (id: string) => nodeTitle(props.graph.nodes.find(n => n.id === id), props.catalog, 'Missing step')
        return `From ${end(wire.source)} to ${end(wire.target)}`
      }
      return ''
    }
    const expand = () => {
      if (props.onOpen) return props.onOpen()
      setDismissed(false)
    }
    return (
      <SheetPeek title={title()} line={line()} icon={identity()?.icon ?? (many() ? icons.flow : icons.data)} color={identity()?.color} verb="Edit" onOpen={expand} />
    )
  }

  /** With nothing selected: the automation's triggers (each selects itself), problems and last run. */
  function Overview() {
    const triggers = () => props.graph.nodes.filter(n => n.kind === 'trigger')
    const problems = () => props.diagnostics.filter(d => d.severity >= 2)
    const errors = () => problems().filter(d => d.severity === 3).length
    const count = () => {
      const n = problems().length, e = errors(), w = n - e
      return [e && `${e} ${e === 1 ? 'problem' : 'problems'}`, w && `${w} ${w === 1 ? 'warning' : 'warnings'}`].filter(Boolean).join(' and ')
    }
    return (
      <section class="hs-insp-overview" aria-label="Automation overview">
        <Show when={props.enabled === false}>
          <p class="hs-insp-off" role="note"><strong>Off:</strong> nothing starts this automation by itself. Run now still works.</p>
        </Show>
        <h2 class="hs-insp-heading">Starts when</h2>
        <Show when={triggers().length} fallback={<p class="hs-muted">This automation has no trigger yet.</p>}>
          <ul class="hs-insp-triggers">
            <For each={triggers()}>
              {node => (
                <li>
                  <button type="button" class="hs-insp-trigger-line" onClick={() => selectAndFocus(node.id)}>
                    <span class="hs-insp-trigger-line__icon" aria-hidden="true"><Glyph paths={icons.trigger} /></span>
                    <span>{nodeSummary(node, props.catalog, props.names, props.graph)}</span>
                  </button>
                </li>
              )}
            </For>
          </ul>
        </Show>
        <Show when={problems().length} fallback={<p class="hs-insp-meta">No problems found.</p>}>
          <button type="button" class="hs-insp-problem-count" classList={{ 'hs-insp-problem-count--warning': !errors() }} onClick={showProblems}>
            <Glyph paths={errors() ? icons.problem : icons.warning} />
            <span>{count()}</span>
          </button>
        </Show>
        <Show when={props.lastRun}>
          {run => (
            <p class="hs-insp-meta">
              Last run: <span classList={{ 'hs-error': run().ok === false }}>{run().status}</span>
              <Show when={run().when}> · {run().when}</Show>
            </p>
          )}
        </Show>
        <p class="hs-muted hs-insp-hint">Select a step to edit it.</p>
      </section>
    )
  }

  /** A selected wire: what it connects and the type it carries. Wires are changed on the canvas only. */
  function WireView(view: { edge: VisualGraph['edges'][number] }) {
    const source = () => props.graph.nodes.find(n => n.id === view.edge.source)
    const target = () => props.graph.nodes.find(n => n.id === view.edge.target)
    const data = () => (view.edge.kind === 'data' ? view.edge : undefined)
    const outputLabel = () => {
      const wire = data(), node = source()
      if (wire) return node ? (dataOutputs(node, props.catalog)[wire.sourcePort]?.label ?? wire.sourcePort) : wire.sourcePort
      return node ? execLabel(node, outputPort(node.kind, view.edge), props.catalog) : ''
    }
    const inputLabel = () => {
      const wire = data(), node = target()
      if (!wire || !node) return undefined
      if (node.kind === 'service' && wire.targetPort.startsWith('field:')) {
        return serviceFieldLabel(typeof node.config.service === 'string' ? node.config.service : '', wire.targetPort.slice(6), props.catalog.services)
      }
      return nodeInputs(props.graph, node, props.catalog)[wire.targetPort]?.label ?? wire.targetPort
    }
    const type = () => { const wire = data(), node = source(); return wire && node ? dataOutputs(node, props.catalog)[wire.sourcePort]?.type : undefined }
    const end = (node: GraphNode | undefined, port: string | undefined): JSX.Element => (
      <button type="button" class="hs-insp-wire__end" style={{ '--hs-cat': node ? nodeIdentity(node.kind, props.catalog).color : undefined }}
        disabled={!node} onClick={() => node && selectAndFocus(node.id)}>
        <span class="hs-insp-wire__node">{nodeTitle(node, props.catalog, 'Missing step')}</span>
        <Show when={port}><span class="hs-insp-wire__port">{port}</span></Show>
      </button>
    )
    return (
      <section class="hs-insp-wire" aria-label={data() ? 'Value wire' : 'Flow wire'}>
        <h2 class="hs-insp-heading">{data() ? 'Value wire' : 'Flow wire'}</h2>
        <div class="hs-insp-wire__path">
          {end(source(), outputLabel())}
          <span class="hs-insp-wire__arrow" aria-label="to">→</span>
          {end(target(), inputLabel())}
        </div>
        <Show when={type()}>
          {id => {
            const types = () => graphTypes(props.graph, props.catalog)
            return <p class="hs-insp-meta">Carries <TypeChip text={typeLabel(id(), types())} color={typeColor(id(), types())} list={typeIsList(id(), types())} /></p>
          }}
        </Show>
        <p class="hs-muted hs-insp-hint">To remove this wire, press Delete on the canvas or drag its end away.</p>
      </section>
    )
  }
}

/** The bottom sheet's handle: tap closes it, so does a downward drag of more than 80px. */
export function SheetGrip(props: { onClose(): void }) {
  let start: number | undefined
  return (
    <button type="button" class="hs-insp-grip" aria-hidden="true" tabindex="-1"
      onPointerDown={e => { start = e.clientY; e.currentTarget.setPointerCapture?.(e.pointerId) }}
      onPointerUp={e => { const moved = start === undefined ? 0 : e.clientY - start; start = undefined; if (moved > 80 || Math.abs(moved) < 6) props.onClose() }}
      onPointerCancel={() => { start = undefined }}>
      <span aria-hidden="true" />
    </button>
  )
}

/** The sheet's top bar (shown where the panel is a sheet or floats): its handle and the close button. */
export function SheetBar(props: { onClose(): void }) {
  return (
    <div class="hs-insp-sheet-bar">
      <SheetGrip onClose={props.onClose} />
      <button type="button" class="hs-button hs-button--ghost hs-insp-close" aria-label="Close panel" onClick={() => props.onClose()}>
        <Glyph paths={['M6 6l12 12', 'M18 6 6 18']} />
      </button>
    </div>
  )
}

/**
 * The sheet's first stage: what is selected in one line and what tapping it does (`verb`, "Edit" or "Show"); tapping
 * it opens the panel. Only shown (CSS) where the panel is a sheet or floats.
 */
export function SheetPeek(props: { title: string; line?: string; icon: readonly string[]; color?: string; verb: string; label?: string; onOpen(): void }) {
  return (
    <button type="button" class="hs-insp-peek" aria-expanded="false" aria-label={props.label ?? `${props.verb} ${props.title}`} title={props.line || undefined}
      style={{ '--hs-cat': props.color }} onClick={() => props.onOpen()}>
      <span class="hs-insp-peek__icon" aria-hidden="true">
        <Glyph paths={props.icon} />
      </span>
      <span class="hs-insp-peek__text">
        <strong>{props.title}</strong>
        <Show when={props.line}><small>{props.line}</small></Show>
      </span>
      <span class="hs-insp-peek__edit" aria-hidden="true">{props.verb}</span>
    </button>
  )
}
