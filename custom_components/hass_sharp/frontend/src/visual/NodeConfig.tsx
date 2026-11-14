import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Menu, type MenuItem } from '../components/Menu'
import { ServiceFields } from './ServiceFields'
import { retypeTriggerNode, TriggerAdvanced, TriggerConfig } from './TriggerConfig'
import { Glyph } from './Glyph'
import { Errors, focusRow, InputRow, type RowContext } from './InputRow'
import { ArrayItems, Branches, Declarations, ObjectFields, type StructureContext } from './StructureEditors'
import { buildNodeView } from './flow/flowModel'
import { constantTypes, retypeConstant } from './constant'
import { RemoveWire } from './RemoveWire'
import { graphTypes } from './connectionIndex'
import { customSignature, words, type ConfigSpec, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { updateNode, type VisualGraph } from './graph'
import { icons } from './icons'
import { typeLabel } from './typeLabels'
import { DraftField } from './widgets/common'
import { stateField, WidgetControl } from './widgets'
import type { VisualDiagnostic } from './api'

type Options = { coalesce?: string }
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const sameList = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((v, i) => v === b[i])
const operators: Record<string, string> = { eq: 'is equal to', ne: 'is not equal to', lt: 'is less than', le: 'is at most', gt: 'is greater than', ge: 'is at least' }
/** Config keys edited by a kind's own section, or not at all (`values` of a loop result are its inputs). */
const ownConfig = new Set(['carry', 'exports', 'branches'])
const kindConfig: Record<string, string[]> = { service: ['service', 'fields', 'returnResponse'], custom: ['source', 'signature', 'arguments'], object_create: ['fields'], array_create: ['items'], loop_result: ['values'] }

export interface NodeConfigProps {
  node: GraphNode
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics?: VisualDiagnostic[]
  /** A new config for this node; edits of one drag or one typed name share a `coalesce` key (one undo step). */
  onChange(config: GraphNode['config'], options?: Options): void
  onGraphChange(graph: VisualGraph, options?: Options): void
  /** Select another node on the canvas (a connected input's source). */
  onSelect?(id?: string): void
  onDuplicate?(): void
  onDelete?(): void
  /** Opens the code editor of a Custom C# node. */
  onEditCode?(): void
  /** Says what a change also did (the wires a new trigger type removed), on the canvas. */
  onNotice?(text: string): void
}

/**
 * The inspector of one node, top to bottom: header (icon, editable title, kind, "⋯" menu, description), its
 * problems, the kind's main section (service, trigger, code or settings), its inputs, its outputs and a collapsed
 * Advanced section. Values are edited with the same `InputEditor` as the node card. Nothing here connects or
 * disconnects anything: a connected input is a read-only "From …" pill that selects its source.
 *
 * Every part reads the node through `props`, so the panel (and the focus, open sections and drags in it) survives
 * edits of the node; the inspector replaces it only when another node is selected.
 */
export function NodeConfig(props: NodeConfigProps) {
  let root!: HTMLDivElement
  let title!: HTMLInputElement
  const descriptionId = createUniqueId()
  const spec = createMemo(() => (Object.hasOwn(props.catalog.nodes, props.node.kind) ? props.catalog.nodes[props.node.kind] : undefined))
  const view = createMemo(() => buildNodeView(props.node, { graph: props.graph, catalog: props.catalog, diagnostics: props.diagnostics ?? [] }))
  const inputs = createMemo(() => new Map(view().rows.flatMap(row => (row.input ? [[row.input.port, row.input] as const] : []))))
  const ports = createMemo(() => [...inputs().keys()], undefined, { equals: sameList })
  const outputs = createMemo(() => view().rows.flatMap(row => (row.output?.kind === 'data' ? [row.output] : [])))
  const outputIds = createMemo(() => outputs().map(pin => pin.id), undefined, { equals: sameList })
  const problems = createMemo(() => (props.diagnostics ?? []).filter(d => d.nodeId === props.node.id && d.severity >= 2))
  const errorsFor = (key: string) => problems().filter(d => d.severity === 3 && (d.inputId === key || d.portId === key)).map(d => d.message)
  const kind = () => props.node.kind
  const kindLabel = () => spec()?.label ?? words.key(kind())
  /** A Custom C# node's pins, title and description, as its code gives them. */
  const signature = () => customSignature(props.node, props.catalog)
  /** What the card shows when the node has no label of its own. */
  const unnamed = () => signature()?.title || kindLabel()
  const [status, setStatus] = createSignal('')
  const [more, setMore] = createSignal(false)
  const focus = (port: string) => queueMicrotask(() => focusRow(root, port))
  const set = (key: string, value: unknown, options?: Options) => {
    const next = { ...props.node.config }
    if (value === undefined) delete next[key]
    else next[key] = value
    props.onChange(next, options)
  }
  const copy = (text: string, done: string) => {
    void Promise.resolve(navigator.clipboard?.writeText?.(text)).then(() => setStatus(done), () => setStatus('Copying is not allowed here'))
  }

  // The rows' type chips and editors know the types the graph's Custom C# nodes declare.
  const types = createMemo(() => graphTypes(props.graph, props.catalog))
  const rows: RowContext = {
    node: () => props.node,
    catalog: types,
    input: port => inputs().get(port),
    targetEntityIds: () => view().targetEntityIds,
    onChange: (config, options) => props.onChange(config, options),
    onSelect: id => props.onSelect?.(id),
    focus,
    // A removed row with no neighbour: the service's "+ Add field", else the title.
    fallback: () => root.querySelector<HTMLElement>('[data-input-id="fields"] input') ?? title,
  }
  const structure: StructureContext = {
    node: () => props.node,
    graph: () => props.graph,
    catalog: () => props.catalog,
    set: (key, value) => set(key, value),
    errorsFor,
    focus,
  }
  // Messages about the service, its response or its fields as a whole, which have no input row.
  const serviceMessages = (key: string) => problems().filter(d => d.inputId === key).map(d => d.message)

  /** A setting of the node that is not an input (an operator, a type, a level, a state). */
  const ConfigField = (field: { name: string; rule: ConfigSpec }) => {
    const id = createUniqueId(), hintId = `${id}-hint`
    const hint = () => (field.rule.description ? hintId : undefined)
    const label = () => (field.rule.label && field.rule.label !== field.name ? field.rule.label : words.key(field.name))
    const value = () => props.node.config[field.name]
    const errors = () => errorsFor(field.name)
    const types = () => field.rule.enumSource === 'types' || field.name === 'type' || field.name === 'itemType'
    // A Constant offers only the types a value can be typed in for; a Convert or a loop any type.
    const options = () => (field.rule.enumSource === 'types' ? (kind() === 'constant' && field.name === 'type' ? constantTypes(props.catalog) : Object.keys(props.catalog.types)) : (field.rule.enum ?? []).map(String))
    const optionLabel = (option: string) => (types() ? typeLabel(option, props.catalog) : field.name === 'operator' ? operators[option] ?? option : words.key(option))
    const number = () => field.rule.type === 'integer' || field.rule.type === 'number'
    const entityIds = () => { const entity = props.node.config.entity; return record(entity) && entity.kind === 'literal' && typeof entity.value === 'string' ? [entity.value] : [] }
    return (
      <div class="hs-insp-field" data-input-id={field.name} aria-invalid={errors().length > 0 || undefined}>
        <label class="hs-insp-field__label" for={id}>{label()}</label>
        <Show when={field.rule.enum || field.rule.enumSource} fallback={
          <Show when={field.rule.type === 'boolean'} fallback={
            <Show when={kind() === 'condition' && field.name === 'state'} fallback={
              <DraftField id={id} label={label()} text={value() === undefined ? '' : String(value())} placeholder="Not set" describedBy={hint()}
                inputmode={number() ? 'decimal' : undefined} invalid={errors().length > 0}
                commit={text => {
                  if (!number()) { set(field.name, text === '' && !field.rule.required ? undefined : text); return }
                  if (text.trim() === '') { set(field.name, undefined); return }
                  const parsed = Number(text)
                  if (!Number.isFinite(parsed) || (field.rule.type === 'integer' && !Number.isInteger(parsed))) return field.rule.type === 'integer' ? 'Enter a whole number.' : 'Enter a number.'
                  set(field.name, parsed)
                }} />
            }>
              <WidgetControl widget={stateField} value={typeof value() === 'string' ? (value() as string) : undefined} id={id} label={label()}
                describedBy={hint()} invalid={errors().length > 0} disabled={false} compact={false} required={!!field.rule.required}
                context={{ targetEntityIds: entityIds() }} commit={v => set(field.name, typeof v === 'string' ? v : undefined)} />
            </Show>
          }>
            <span class="hs-widget-row">
              <input id={id} type="checkbox" role="switch" class="hs-switch" aria-describedby={hint()} checked={value() === true} onChange={e => set(field.name, e.currentTarget.checked)} />
              <span class="hs-muted">{value() === undefined ? 'Not set' : value() ? 'Yes' : 'No'}</span>
            </span>
          </Show>
        }>
          <select id={id} class="hs-input" aria-describedby={hint()} aria-invalid={errors().length > 0 || undefined}
            onChange={e => {
              const next = e.currentTarget.value === '' ? undefined : e.currentTarget.value
              // A Constant's value follows its Type in the same change: kept while it fits, else cleared.
              if (kind() === 'constant' && field.name === 'type') { const config = retypeConstant(props.node.config, next, rows.catalog()); if (config !== props.node.config) props.onChange(config) }
              else set(field.name, next)
            }}>
            <Show when={value() === undefined || !options().includes(String(value()))}>
              <option value="" selected>{value() === undefined ? 'Not set' : types() ? typeLabel(String(value()), props.catalog) : String(value())}</option>
            </Show>
            <For each={options()}>{option => <option value={option} selected={String(value()) === option}>{optionLabel(option)}</option>}</For>
          </select>
        </Show>
        <Show when={field.rule.description}><small id={hintId} class="hs-field-hint">{field.rule.description}</small></Show>
        <Errors list={errors()} />
      </div>
    )
  }
  const configFields = createMemo(() => {
    // A trigger's whole configuration is TriggerConfig's.
    if (kind() === 'trigger') return []
    const own = new Set([...ownConfig, ...(kindConfig[kind()] ?? [])])
    return Object.entries(spec()?.config ?? {}).filter(([name]) => {
      if (own.has(name) || (spec()?.argumentStorage === 'arguments' && name === 'arguments')) return false
      // An attribute name matters only while the node reads an attribute.
      return !(name === 'attribute' && spec()?.config.field && props.node.config.field !== 'attribute')
    }).map(([name]) => name)
  }, undefined, { equals: sameList })

  const menu = (): MenuItem[] => [
    ...(props.onDuplicate ? [{ label: 'Duplicate', onSelect: () => props.onDuplicate?.() }] : []),
    { label: 'Copy node ID', onSelect: () => copy(props.node.id, 'Node ID copied') },
    ...(props.onDelete ? [{ label: 'Delete', onSelect: () => props.onDelete?.(), danger: true }] : []),
  ]
  const description = () => signature()?.description || (spec()?.description ?? 'This kind of step is not available. Its settings are kept.')
  const stored = () => JSON.stringify({ kind: props.node.kind, ...(props.node.label ? { label: props.node.label } : {}), config: props.node.config }, null, 2)
  const loop = () => !!spec()?.config.carry

  return (
    <div class="hs-insp-node" ref={root} data-node-id={props.node.id} style={{ '--hs-cat': view().identity.color }}>
      <header class="hs-insp-head" classList={{ 'hs-insp-head--trigger': kind() === 'trigger' }}>
        <span class="hs-insp-head__icon" title={view().identity.label}><Glyph paths={view().identity.icon} /></span>
        <div class="hs-insp-head__titles">
          <input ref={title} class="hs-insp-title" aria-label="Node label" aria-describedby={descriptionId} value={props.node.label ?? ''} placeholder={unnamed()}
            onInput={e => props.onGraphChange(updateNode(props.graph, props.node.id, { label: e.currentTarget.value || undefined }), { coalesce: `label:${props.node.id}` })} />
          <small class="hs-insp-kind">{kindLabel() === view().identity.label ? kindLabel() : `${kindLabel()} · ${view().identity.label}`}</small>
        </div>
        <Menu label="Node actions" items={menu()} />
      </header>
      <div class="hs-insp-desc-row">
        <p id={descriptionId} class="hs-insp-desc" classList={{ 'hs-insp-desc--open': more() }}>{description()}</p>
        <Show when={description().length > 110}>
          <button type="button" class="hs-insp-more" aria-expanded={more()} onClick={() => setMore(!more())}>{more() ? 'Less' : 'More'}</button>
        </Show>
      </div>
      <p class="hs-visually-hidden" role="status">{status()}</p>

      <Show when={problems().length}>
        <ul class="hs-insp-problems" aria-label="Problems">
          <For each={problems()}>
            {problem => {
              const port = problem.inputId ?? problem.portId ?? undefined
              const body = () => <><Glyph paths={problem.severity === 3 ? icons.problem : icons.warning} /><span>{problem.message}</span></>
              return (
                <li class="hs-insp-problem" classList={{ 'hs-insp-problem--warning': problem.severity !== 3 }}>
                  <Show when={port} fallback={<span class="hs-insp-problem__body">{body()}</span>}>
                    <button type="button" class="hs-insp-problem__body" title="Go to the field" onClick={() => focusRow(root, port!)}>{body()}</button>
                  </Show>
                  <RemoveWire graph={props.graph} diagnostic={problem} catalog={props.catalog} class="hs-insp-link hs-insp-problem__action"
                    onRemove={next => props.onGraphChange(next)} focusAfter={() => title} />
                </li>
              )
            }}
          </For>
        </ul>
      </Show>

      <div class="hs-insp-main">
        <Show when={kind() === 'service'}>
          <ServiceFields node={props.node} graph={props.graph} catalog={props.catalog} onChange={props.onChange} focus={focus} messages={serviceMessages}
            row={(port, extra) => <InputRow port={port} ctx={rows} {...extra} />} />
        </Show>
        <Show when={kind() === 'trigger'}>
          <TriggerConfig node={props.node} onChange={props.onChange} diagnostics={props.diagnostics}
            onRetype={(config, label) => {
              const result = retypeTriggerNode(props.graph, props.node.id, config, label, types())
              props.onGraphChange(result.graph)
              if (result.notice) props.onNotice?.(result.notice)
            }} />
        </Show>
        <Show when={kind() === 'custom'}>
          <div class="hs-insp-field hs-insp-code" data-input-id="source" aria-invalid={errorsFor('source').length > 0 || undefined}>
            <Show when={props.onEditCode}>
              <button type="button" class="hs-button hs-button--primary hs-insp-code__edit" onClick={() => props.onEditCode?.()}>
                <Glyph paths={icons.code} />Edit code
              </button>
            </Show>
            <small class="hs-field-hint">Its inputs and outputs come from the code.</small>
            <Errors list={errorsFor('source')} />
          </div>
        </Show>
        <For each={configFields()}>{name => <ConfigField name={name} rule={spec()!.config[name]} />}</For>
        <Show when={kind() === 'parallel'}><Branches ctx={structure} /></Show>
        <Show when={kind() === 'object_create'}><ObjectFields ctx={structure} /></Show>
        <Show when={kind() === 'array_create'}><ArrayItems ctx={structure} /></Show>
        <Show when={kind() !== 'service' && kind() !== 'trigger' && ports().length}>
          <section class="hs-insp-section" aria-label="Inputs">
            <h3 class="hs-insp-heading">Inputs</h3>
            <For each={ports()}>{port => <InputRow port={port} ctx={rows} />}</For>
          </section>
        </Show>
        <Show when={loop()}>
          <Declarations ctx={structure} key="carry" title="Kept between iterations" hint="Each iteration starts from the value the previous one finished with." />
          <Declarations ctx={structure} key="exports" title="Available after the loop" hint="Set inside the loop and read after it finishes." />
        </Show>
      </div>

      <Show when={outputIds().length}>
        <section class="hs-insp-section" aria-label="Outputs">
          <h3 class="hs-insp-heading">Outputs</h3>
          <ul class="hs-insp-outputs">
            <For each={outputIds()}>
              {pinId => {
                const pin = () => outputs().find(p => p.id === pinId)!
                const descId = createUniqueId()
                return (
                  <Show when={outputs().some(p => p.id === pinId)}>
                    <li class="hs-insp-output" aria-label={pin().name} aria-describedby={pin().description ? descId : undefined} style={{ '--hs-chip-color': pin().color }}>
                      <span class="hs-type-chip__dot" classList={{ 'hs-type-chip__dot--list': pin().list, 'hs-type-chip__dot--filled': pin().connected }} aria-hidden="true" />
                      <span class="hs-insp-output__name">{pin().label}</span>
                      <span class="hs-insp-output__type">{pin().typeName}</span>
                      <Show when={pin().description}><small id={descId} class="hs-insp-output__desc">{pin().description}</small></Show>
                    </li>
                  </Show>
                )
              }}
            </For>
          </ul>
        </section>
      </Show>

      <details class="hs-insp-advanced">
        <summary>Advanced</summary>
        <div class="hs-insp-advanced__body">
          <Show when={kind() === 'trigger'}><TriggerAdvanced node={props.node} onChange={props.onChange} /></Show>
          <div class="hs-insp-field">
            <div class="hs-insp-field__row">
              <span class="hs-insp-field__label">Stored data</span>
              <button type="button" class="hs-button hs-button--ghost hs-insp-copy" onClick={() => copy(stored(), 'Stored data copied')}>Copy</button>
            </div>
            <code class="hs-insp-json" tabindex="0" role="region" aria-label="Stored data">{stored()}</code>
          </div>
        </div>
      </details>
    </div>
  )
}
