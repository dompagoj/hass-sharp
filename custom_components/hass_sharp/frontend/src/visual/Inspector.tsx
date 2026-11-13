import { createEffect, createMemo, createSignal, For, on, onCleanup, onMount, Show, type JSX } from 'solid-js'
import { NodeConfig } from './NodeConfig'
import { TypeChip } from './InputRow'
import { Glyph } from './Glyph'
import type { VisualDiagnostic } from './api'
import { copySteps, pasteSteps } from './clipboard'
import { dataOutputs, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { execLabel, nodeTitle } from './flow/flowModel'
import { catalogPalette, nodeInputs, outputPort, portLabel, removeNode, updateNode, type VisualGraph } from './graph'
import { icons, nodeIdentity } from './icons'
import { applyLayout, tidyLayout } from './layout'
import { nodeSummary, serviceFieldLabel } from './nodeSummary'
import { typeColor, typeIsList, typeLabel } from './typeLabels'

type Options = { coalesce?: string }
/** The last run of the automation, when the page knows it. */
export interface InspectorLastRun { status: string; when?: string; ok?: boolean }

export interface InspectorProps {
  /** A palette node the user clicked for its description; shown instead of the selection. */
  description?: ReturnType<typeof catalogPalette>[number]
  /** The one selected node (the canvas' primary selection). */
  selected?: GraphNode
  /** Every selected node; two or more show the selection view. */
  selectedIds?: string[]
  /** A selected wire. */
  selectedEdgeId?: string
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics: VisualDiagnostic[]
  commit(graph: VisualGraph, options?: Options): void
  /** Select a node (or nothing) on the canvas. */
  onSelect?(id?: string): void
  /** Shows the problems list; without it the page's diagnostics list is focused. */
  onShowProblems?(): void
  lastRun?: InspectorLastRun
  /** Below 900px the inspector is a bottom sheet: shown while `open` (default: while something is selected). */
  open?: boolean
  onClose?(): void
}

/**
 * The right-hand panel. In order of precedence: a palette node's description, several selected nodes, the
 * selected node ({@link NodeConfig}), a selected wire (read only) or, with nothing selected, an overview of the
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
  createEffect(on(() => [props.selected?.id, props.selectedEdgeId, ids().join(' '), props.description?.kind], () => setDismissed(false), { defer: true }))
  const hasContent = () => !!(props.description || props.selected || many() || edge())
  const open = () => (props.open ?? hasContent()) && !dismissed()
  const workspace = (): ParentNode => aside.closest('.hs-visual-workspace') ?? aside.parentElement ?? document
  const card = (id: string | undefined) =>
    id ? [...workspace().querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.getAttribute('data-id') === id) : undefined
  const canvas = () => workspace().querySelector<HTMLElement>('.hs-flow')
  /** Focuses what `find` returns once the update that shows it has been rendered (it may take a frame). */
  const focusSoon = (find: () => HTMLElement | null | undefined) => {
    let tries = 0
    const attempt = () => {
      const target = find()
      if (target?.isConnected) { target.focus(); target.scrollIntoView?.({ block: 'nearest' }); return }
      if (++tries < 3) setTimeout(attempt, 0)
    }
    queueMicrotask(attempt)
  }
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
  // Selecting a step again (click, or Enter or Space on its card) brings a dismissed sheet back.
  onMount(() => {
    const scope = aside.closest('.hs-visual-workspace')
    if (!scope) return
    const reopen = (event: Event) => {
      if (!dismissed() || !(event.target instanceof Element) || !event.target.closest('.solid-flow__node')) return
      if (event instanceof KeyboardEvent && event.key !== 'Enter' && event.key !== ' ') return
      setDismissed(false)
    }
    scope.addEventListener('click', reopen, true)
    scope.addEventListener('keydown', reopen, true)
    onCleanup(() => { scope.removeEventListener('click', reopen, true); scope.removeEventListener('keydown', reopen, true) })
  })

  const duplicate = (selection: string[]) => {
    const data = copySteps(props.graph, selection)
    if (!data) return
    const result = pasteSteps(props.graph, data, props.catalog, { offset: { x: 40, y: 40 } }, 20)
    if ('error' in result) return setStatus(result.error)
    props.commit(result.graph)
    setStatus(result.ids.length === 1 ? 'Duplicated' : `Duplicated ${result.ids.length} steps`)
    if (selection.length === 1 && result.ids.length === 1) selectAndFocus(result.ids[0])
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
  const showProblems = () => {
    if (props.onShowProblems) return props.onShowProblems()
    const list = (aside.closest('.hs-visual-editor, .hs-visual') ?? document).querySelector<HTMLElement>('.hs-diagnostics')
    const first = list?.querySelector<HTMLElement>('button')
    first?.scrollIntoView?.({ block: 'nearest' })
    first?.focus()
  }

  return (
    <aside ref={aside} class="hs-inspector" aria-label="Node inspector" data-open={open() ? 'true' : 'false'}>
      <div class="hs-insp-sheet-bar">
        <SheetGrip onClose={close} />
        <button type="button" class="hs-button hs-button--ghost hs-insp-close" aria-label="Close panel" onClick={close}>
          <Glyph paths={['M6 6l12 12', 'M18 6 6 18']} />
        </button>
      </div>
      <p class="hs-visually-hidden" role="status">{status()}</p>
      <Show when={props.description} fallback={
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
                  onDuplicate={() => duplicate([id])} onDelete={() => remove([id])} />
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
      }>
        {item => (
          <section class="hs-insp-palette" aria-label={item().label}>
            <h2 class="hs-insp-many__count">{item().label}</h2>
            <p class="hs-insp-desc hs-insp-desc--open">{item().description}</p>
            <p class="hs-insp-meta">
              Inputs: {Object.entries(item().inputs).map(([id, input]) => input.label ?? id).join(', ') || 'None'}
            </p>
            <p class="hs-insp-meta">Outputs: {item().execution.map(portLabel).join(', ')}</p>
            <p class="hs-muted">Drag this step onto the canvas, or use Add step.</p>
          </section>
        )}
      </Show>
    </aside>
  )

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
        <h2 class="hs-insp-heading">Starts when</h2>
        <Show when={triggers().length} fallback={<p class="hs-muted">This automation has no trigger yet.</p>}>
          <ul class="hs-insp-triggers">
            <For each={triggers()}>
              {node => (
                <li>
                  <button type="button" class="hs-insp-trigger-line" onClick={() => selectAndFocus(node.id)}>
                    <span class="hs-insp-trigger-line__icon" aria-hidden="true"><Glyph paths={icons.trigger} /></span>
                    <span>{nodeSummary(node, props.catalog, undefined, props.graph)}</span>
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
          {id => <p class="hs-insp-meta">Carries <TypeChip text={typeLabel(id(), props.catalog)} color={typeColor(id(), props.catalog)} list={typeIsList(id(), props.catalog)} /></p>}
        </Show>
        <p class="hs-muted hs-insp-hint">To remove this wire, press Delete on the canvas or drag its end away.</p>
      </section>
    )
  }
}

/** The bottom sheet's handle: tap closes it, so does a downward drag of more than 80px. */
function SheetGrip(props: { onClose(): void }) {
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
