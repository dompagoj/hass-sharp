import { createMemo, createUniqueId, For, Show, type JSX } from 'solid-js'
import { JsonTree, type JsonLeaf } from '../../components/JsonTree'
import { TriggerSummary } from '../../components/TriggerSummary'
import { Glyph } from '../Glyph'
import { SheetBar, SheetPeek } from '../Inspector'
import { TypeChip } from '../InputRow'
import { isPure, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../contract'
import { durationLabel, execLabel, nodeTitle, traceStatusLabel, traceText } from '../flow/flowModel'
import { icons, nodeIdentity } from '../icons'
import { focusSoon } from '../soon'
import { typeColor, typeIsList, typeLabel } from '../typeLabels'
import type { AutomationRun, TraceEvent, TraceValue } from '../../types'
import { isCut, isPlaceholder, isTree, opaque, valueText, type OptionText, type TraceModel } from './traceModel'
import { inputLabel, inputOption, inputOrder, inputSource, outputLabel, secretName } from './traceWords'
import { statusIcon } from './Timeline'

export const runTime = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleString() : '—')
export const runDuration = (ms: number | null | undefined) => (ms === null || ms === undefined ? '—' : durationLabel(ms))

export interface TraceInspectorProps {
  run: AutomationRun
  graph?: Graph
  catalog: VisualCatalogSnapshot
  /** Absent when the run has no trace. */
  model?: TraceModel
  selectedNodeId?: string
  /** The selected run of the selected step. */
  event?: TraceEvent
  /** The iteration or branch the canvas shows; `""` the whole run. */
  frame: string
  /** Selects a step on the canvas (a "From …" source). */
  onSelectNode(id: string): void
  /** Shows another run of the selected step (the iteration or branch chooser). */
  onSelectEvent(seq: number): void
  /** Back to the whole run (the chooser's first option). */
  onSelectWholeRun(): void
  /** A side panel beside the canvas; otherwise a sheet (below 900px) or a floating panel (full screen). */
  docked: boolean
  open: boolean
  onOpen(): void
  onClose(): void
}

/**
 * The trace view's right-hand panel, where the editor's inspector sits (same width, same sheet below 900px): the
 * selected step's recorded inputs with where each came from, its outputs, timing and error, and a chooser for its
 * other runs (loop iterations, parallel branches). With nothing selected it describes the run.
 */
export function TraceInspector(props: TraceInspectorProps) {
  let aside!: HTMLElement
  const node = createMemo(() => (props.selectedNodeId ? props.graph?.nodes.find(n => n.id === props.selectedNodeId) : undefined))
  const open = () => props.docked || props.open
  const peek = () => !props.docked && !props.open
  const workspace = (): ParentNode => aside.closest('.hs-visual-workspace') ?? document
  const card = (id: string | undefined) => (id ? [...workspace().querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.dataset.id === id) : undefined)
  const close = () => {
    const id = props.selectedNodeId
    props.onClose()
    // Back to where the selection is: its card, else the canvas; never the page.
    focusSoon(() => card(id) ?? workspace().querySelector<HTMLElement>('.hs-flow'), aside)
  }
  /** A source pill selects its step; the panel shows it and keeps focus on its title. */
  const selectSource = (id: string) => {
    props.onSelectNode(id)
    focusSoon(() => aside.querySelector<HTMLElement>(`.hs-trace-node[data-node-id="${CSS.escape(id)}"] .hs-trace-node__title`), aside)
  }
  return (
    <aside ref={aside} class="hs-inspector hs-trace-panel" aria-label="Step values" data-open={open() ? 'true' : 'false'} data-peek={peek() ? 'true' : undefined}>
      <Show when={peek()}>
        <PeekBar />
      </Show>
      <SheetBar onClose={close} />
      <Show when={node()?.id} keyed fallback={<RunOverview run={props.run} model={props.model} />}>
        {_id => <StepValues node={node()!} graph={props.graph!} catalog={props.catalog} model={props.model} event={props.event} frame={props.frame}
          onSelectSource={selectSource} onSelectEvent={props.onSelectEvent} onSelectWholeRun={props.onSelectWholeRun} />}
      </Show>
    </aside>
  )

  /** The sheet's first stage: the selected step (or the run) in one line; tapping it opens the panel. */
  function PeekBar() {
    const identity = () => { const current = node(); return current ? nodeIdentity(current.kind, props.catalog) : undefined }
    const title = () => (node() ? nodeTitle(node(), props.catalog) : 'This run')
    const line = () => {
      const current = node()
      if (!current) return [props.run.status, runDuration(props.run.durationMs)].filter(t => t !== '—').join(' · ')
      if (!props.model) return ''
      return traceText(props.event ? { status: props.event.status, runs: props.model.runs(current.id), ms: props.event.ms } : undefined, isPure(current, props.catalog))
    }
    return (
      <SheetPeek title={title()} line={line()} icon={identity()?.icon ?? icons.flow} color={identity()?.color} verb="Show" label={`Show values of ${title()}`} onOpen={() => props.onOpen()} />
    )
  }
}

/** With nothing selected: what started the run, when, how it ended, and the trigger's data. */
function RunOverview(props: { run: AutomationRun; model?: TraceModel }) {
  const failed = () => props.run.status === 'Failed' || props.run.status === 'Interrupted'
  return (
    <section class="hs-insp-overview hs-trace-overview" aria-label="This run">
      <h2 class="hs-insp-heading">This run</h2>
      <dl class="hs-run-fields">
        <div><dt>Status</dt><dd>{props.run.status}</dd></div>
        <div><dt>Started</dt><dd>{runTime(props.run.startUtc ?? props.run.receivedUtc)}</dd></div>
        <div><dt>Ended</dt><dd>{runTime(props.run.endUtc)}</dd></div>
        <div><dt>Duration</dt><dd>{runDuration(props.run.durationMs)}</dd></div>
        <Show when={props.model}>{model => <div><dt>Steps</dt><dd>{model().events.length.toLocaleString()} recorded</dd></div>}</Show>
      </dl>
      <Show when={props.run.error}>
        {error => (
          <div class="hs-trace-section">
            <h3 class="hs-insp-heading">{props.run.status === 'Cancelled' ? 'Cancel reason' : failed() ? 'Error' : 'Reason'}</h3>
            <pre class="hs-run-error" classList={{ 'hs-run-error--quiet': !failed() }}>{error()}</pre>
          </div>
        )}
      </Show>
      <div class="hs-trace-section">
        <h3 class="hs-insp-heading">Started by</h3>
        <TriggerSummary trigger={props.run.trigger} />
      </div>
      <Show when={props.model && Object.keys(props.model.trace.trigger ?? {}).length}>
        <div class="hs-trace-section">
          <h3 class="hs-insp-heading">Trigger data</h3>
          <JsonTree value={props.model!.trace.trigger} label="Trigger data" leaf={traceLeaf(false)} />
        </div>
      </Show>
      <p class="hs-muted hs-insp-hint">{props.model ? 'Select a step on the canvas or in the timeline to see its values.' : 'No step-by-step record was kept for this run.'}</p>
    </section>
  )
}

/** One step: its status and timing, which run is shown, its error, then every recorded input and output. */
function StepValues(props: {
  node: GraphNode
  graph: Graph
  catalog: VisualCatalogSnapshot
  model?: TraceModel
  event?: TraceEvent
  frame: string
  onSelectSource(id: string): void
  onSelectEvent(seq: number): void
  onSelectWholeRun(): void
}) {
  const chooserId = createUniqueId()
  const identity = () => nodeIdentity(props.node.kind, props.catalog)
  // A kind this catalog does not know is still a step; its title (its own label) says which.
  const kindLabel = () => (Object.hasOwn(props.catalog.nodes, props.node.kind) ? props.catalog.nodes[props.node.kind].label : 'Step')
  const runs = () => props.model?.eventsOf(props.node.id) ?? []
  const whole = () => props.frame === ''
  // Another run or the whole run can be chosen: more than one run, or one iteration or branch is on view.
  const chooser = () => runs().length > 1 || (!whole() && runs().length > 0)
  const frameLabel = () => props.model?.frameLabel(props.frame) ?? ''
  // A value node is worked out each time a step reads it; one no step read was not used.
  const pure = () => isPure(props.node, props.catalog)
  // Values that were too large to record take their wires' highlight with them.
  const readsValues = () => props.graph.edges.some(e => e.kind === 'data' && e.target === props.node.id)
  const count = () => props.model?.runs(props.node.id) ?? 0
  const inputs = () => props.event?.inputs ?? {}
  const outputs = () => props.event?.outputs ?? {}
  const choice = (event: TraceEvent, index: number) => {
    const frame = props.model!.frameLabel(event.frame) || `Run ${index + 1} of ${runs().length}`
    return event.status === 'ok' ? frame : `${frame} · ${traceStatusLabel[event.status]}`
  }
  const path = () => {
    const port = props.event?.port
    if (!port || props.event?.status === 'error') return undefined
    return port === 'next' && execLabel(props.node, port, props.catalog) === 'Next' ? 'Went on to the next step' : `Took the “${execLabel(props.node, port, props.catalog)}” path`
  }
  return (
    <section class="hs-insp-node hs-trace-node" data-node-id={props.node.id} aria-label={`Values of ${nodeTitle(props.node, props.catalog)}`} style={{ '--hs-cat': identity().color }}>
      <header class="hs-insp-head">
        <span class="hs-insp-head__icon" aria-hidden="true"><Glyph paths={identity().icon} /></span>
        <div class="hs-insp-head__titles">
          <h2 class="hs-trace-node__title" tabindex="-1">{nodeTitle(props.node, props.catalog)}</h2>
          <span class="hs-insp-kind">{kindLabel()}</span>
        </div>
      </header>
      <Show when={chooser()}>
        <div class="hs-insp-field">
          <label class="hs-insp-field__label" for={chooserId}>Which run</label>
          <select id={chooserId} class="hs-input" onChange={e => (e.currentTarget.value === 'whole' ? props.onSelectWholeRun() : props.onSelectEvent(Number(e.currentTarget.value)))}>
            <option value="whole" selected={whole()}>Whole run</option>
            {/* The iteration or branch on view, when this step did not run in it: the control says so. */}
            <Show when={!whole() && !props.event}>
              <option value="none" selected disabled>{`${frameLabel()} (${pure() ? 'not used' : 'did not run'})`}</option>
            </Show>
            <For each={runs()}>{(run, index) => <option value={String(run.seq)} selected={!whole() && run.seq === props.event?.seq}>{choice(run, index())}</option>}</For>
          </select>
          <Show when={whole() && props.event && runs().length > 1}>
            <span class="hs-insp-meta">Showing its last run{props.model!.frameLabel(props.event!.frame) ? `: ${props.model!.frameLabel(props.event!.frame)}` : ''}.</span>
          </Show>
        </div>
      </Show>
      <Show when={props.model && props.event} fallback={
        <p class="hs-insp-note">
          {!props.model ? 'No values were recorded for this run.'
            : pure() ? (runs().length && !whole() ? `Not used in ${frameLabel()}: no step read this value there.` : 'Not used: no step read this value.')
              : runs().length && !whole() ? `This step did not run in ${frameLabel()}.` : 'This step did not run.'}
        </p>
      }>
        {_shown => {
          const event = () => props.event!
          return (
            <>
              <p class={`hs-trace-node__status hs-trace-status--${event().status}`}>
                <Glyph paths={statusIcon[event().status]} />
                <span>
                  <strong>{traceStatusLabel[event().status]}</strong>
                  {' · '}
                  {event().ms === undefined ? 'still running' : `took ${durationLabel(event().ms!)}`}
                  {` · ${durationLabel(event().t) || '0 ms'} into the run`}
                  <Show when={count() > 1}> · {pure() ? 'worked out' : 'ran'} {count().toLocaleString()} times</Show>
                </span>
              </p>
              <Show when={event().error}>
                {error => (
                  <div class="hs-trace-error" classList={{ 'hs-trace-error--recovered': event().status === 'error' }} role="note">
                    <strong>{event().status === 'error' ? 'Failed, then went on through “On error”' : 'Failed here'}</strong>
                    <p>{error().message}</p>
                  </div>
                )}
              </Show>
              <Show when={event().trunc}>
                <p class="hs-insp-note hs-insp-note--warning">
                  {readsValues() ? 'Some values were too large to record, so their wires are not highlighted.' : 'Some values were too large to record.'}
                </p>
              </Show>
              <Show when={path()}>{text => <p class="hs-insp-meta">{text()}</p>}</Show>
              <PortList
                title="Inputs"
                empty="No inputs were recorded."
                ports={inputOrder(props.graph, props.node, inputs(), props.catalog)}
                value={port => inputs()[port]}
                label={port => inputLabel(props.graph, props.node, port, props.catalog)}
                option={port => inputOption(props.graph, props.node, port, props.catalog)}
                source={(_port, value) => (
                  <Show when={value.src} fallback={<span class="hs-trace-port__source">{inputSource(props.graph, value, props.catalog)}</span>}>
                    {src => (
                      <button type="button" class="hs-insp-pill hs-trace-port__from" style={{ '--hs-chip-color': typeColor(value.type, props.catalog) }}
                        title="Select the step it came from" onClick={() => props.onSelectSource(src().node)}>
                        <span>{inputSource(props.graph, value, props.catalog)}</span>
                      </button>
                    )}
                  </Show>
                )}
                catalog={props.catalog}
              />
              <PortList
                title="Outputs"
                empty="No outputs were recorded."
                ports={Object.keys(outputs())}
                value={port => outputs()[port]}
                label={port => outputLabel(props.node, port, props.catalog)}
                catalog={props.catalog}
              />
            </>
          )
        }}
      </Show>
    </section>
  )
}

function PortList(props: {
  title: string
  empty: string
  ports: string[]
  value(port: string): TraceValue
  label(port: string): string
  /** A choice's option labels, for an input that offers options. */
  option?(port: string): OptionText | undefined
  source?(port: string, value: TraceValue): JSX.Element
  catalog: VisualCatalogSnapshot
}) {
  return (
    <div class="hs-trace-section">
      <h3 class="hs-insp-heading">{props.title}</h3>
      <Show when={props.ports.length} fallback={<p class="hs-muted hs-insp-hint">{props.empty}</p>}>
        <ul class="hs-trace-ports">
          <For each={props.ports}>
            {port => {
              const value = () => props.value(port)
              return (
                <li class="hs-trace-port" data-port={port}>
                  <div class="hs-trace-port__head">
                    <span class="hs-trace-port__name">{props.label(port)}</span>
                    <TypeChip text={typeLabel(value().type, props.catalog)} color={typeColor(value().type, props.catalog)} list={typeIsList(value().type, props.catalog)} />
                  </div>
                  {props.source?.(port, value())}
                  <ValueView value={value()} label={props.label(port)} option={props.option?.(port)} />
                </li>
              )
            }}
          </For>
        </ul>
      </Show>
    </div>
  )
}

/**
 * Every value tree on the trace page (step values, the trigger data). What the recorder writes inside a value in
 * place of one it did not keep, shown in words: `"***"` under a name the
 * secret rule matches (the same text under any other name is real), and, in a value marked cut, a C# type name for
 * one it could not read. A `null` member is an empty value ("Not set"); in a value marked cut it may also be one the
 * recorder stopped at, which it cannot tell apart, so it says both.
 */
const traceLeaf = (cut: boolean | undefined): JsonLeaf => (name, v) => {
  if (v === null) return <span class="hs-trace-value--muted hs-trace-leaf">{cut ? 'Not set, or not recorded' : 'Not set'}</span>
  if (v === '***' && secretName(name)) return <span class="hs-trace-value--muted hs-trace-leaf" title="Not recorded because its name looks like a secret.">Hidden</span>
  if (cut && opaque(v)) return <span class="hs-trace-value--muted hs-trace-leaf">Could not be recorded</span>
  return undefined
}

/** A recorded value: a tree for objects and lists, words for anything else; never a placeholder as if it were data. */
function ValueView(props: { value: TraceValue; label: string; option?: OptionText }) {
  return (
    <div class="hs-trace-port__value">
      <Show when={isTree(props.value)} fallback={
        <span class="hs-trace-value" classList={{ 'hs-trace-value--muted': isPlaceholder(props.value) }}>
          {valueText(props.value, props.option)}
        </span>
      }>
        <span class="hs-trace-value hs-trace-value--muted">{valueText(props.value)}</span>
        <JsonTree value={props.value.v} label={`${props.label} value`} leaf={traceLeaf(props.value.trunc)} />
      </Show>
      <Show when={props.value.redacted}><span class="hs-trace-port__note">Not recorded because its name looks like a secret.</span></Show>
      <Show when={isCut(props.value)}><span class="hs-trace-port__note">Too large: only part of it was recorded.</span></Show>
    </div>
  )
}
