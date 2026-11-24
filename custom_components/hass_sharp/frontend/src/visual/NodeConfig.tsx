import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Dynamic } from 'solid-js/web'
import { Menu, type MenuItem } from '../components/Menu'
import { ServiceFields } from './ServiceFields'
import { retypeTriggerNode, TriggerAdvanced, TriggerConfig } from './TriggerConfig'
import { Glyph } from './Glyph'
import { Errors, focusRow, InputRow, type RowContext } from './InputRow'
import { CSharpTypeText } from './typeDefinition'
import { csharpType } from './csharpType'
import { ArrayItems, Branches, Declarations, ObjectFields, type StructureContext } from './StructureEditors'
import { buildNodeView } from './flow/flowModel'
import { constantTypeLabels, constantTypes, retypeConstant } from './constant'
import { RemoveWire } from './RemoveWire'
import { chooseProperty, PropertyPicker } from './selectProperty'
import { graphTypes } from './connectionIndex'
import { entityStateContext } from './entityStateContext'
import { classSignature, customSignature, executionPorts, graphDocument, hasCode, isClassNode, listedTypes, trackHistoryDefault, words, type ConfigSpec, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { stepDescription, updateNode, type VisualGraph } from './graph'
import { icons } from './icons'
import { typeLabel } from './typeLabels'
import { describedBy, DraftField } from './widgets/common'
import { numberMessage, parseNumber, useDecimalSeparator } from './widgets/number'
import { stateField, WidgetControl } from './widgets'
import type { VisualDiagnostic } from './api'
import type { SavedEntities } from './adapter'
import { stateValueRow, stepInputRows, stepPicker, type StepPickerContext } from '../entities/NodePickers'
import { FieldErrors } from '../entities/formFields'

type Options = { coalesce?: string }
const sameList = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((v, i) => v === b[i])
const operators: Record<string, string> = { eq: 'is equal to', ne: 'is not equal to', lt: 'is less than', le: 'is at most', gt: 'is greater than', ge: 'is at least' }
/** Config keys edited by a kind's own section, or not at all (`values` of a loop result are its inputs). */
const ownConfig = new Set(['carry', 'exports', 'branches'])
const kindConfig: Record<string, string[]> = {
  service: ['service', 'fields', 'returnResponse'], custom: ['source', 'signature', 'arguments'], select_property: ['property'], object_create: ['fields'], array_create: ['items'], loop_result: ['values'],
}
const own = <T,>(map: Record<string, T>, key: string) => (Object.hasOwn(map, key) ? map[key] : undefined)
/** What the stored data shows in place of a value typed into a secret setting. */
const hiddenSecret = 'A typed secret, not shown'

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
  /** The saved document's entities as Home Assistant has them (an Entity node's saved key and entity id). */
  savedEntities?: SavedEntities
  /** The saved node libraries' names, by id: what a library class node's panel calls its library. */
  libraries?: Readonly<Record<string, string>>
  /** Opens a node library, by its id: a library class node's library's name links to it. */
  onOpenLibrary?(id: string): void
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
  let title!: HTMLElement
  const descriptionId = createUniqueId(), nameErrorsId = `${descriptionId}-name-errors`
  const spec = createMemo(() => (Object.hasOwn(props.catalog.nodes, props.node.kind) ? props.catalog.nodes[props.node.kind] : undefined))
  const view = createMemo(() => buildNodeView(props.node, { graph: props.graph, catalog: props.catalog, diagnostics: props.diagnostics ?? [] }))
  // The card's rows: an Entity node's Device pin, which carries no value, is its header's (`headerInput`) and is wired
  // from its section's Device select, so it is not among them.
  const inputs = createMemo(() => new Map(view().rows.flatMap(row => (row.input ? [[row.input.port, row.input] as const] : []))))
  const ports = createMemo(() => [...inputs().keys()], undefined, { equals: sameList })
  const outputs = createMemo(() => view().rows.flatMap(row => (row.output?.kind === 'data' ? [row.output] : [])))
  const outputIds = createMemo(() => outputs().map(pin => pin.id), undefined, { equals: sameList })
  const problems = createMemo(() => (props.diagnostics ?? []).filter(d => d.nodeId === props.node.id && d.severity >= 2))
  const errorsFor = (key: string) => problems().filter(d => d.severity === 3 && (d.inputId === key || d.portId === key)).map(d => d.message)
  const kind = () => props.node.kind
  /** What the node declares (an entity, a device): it is named by it, so its header edits that name, not a label of the node. */
  const declares = () => spec()?.declares
  /** An entity class (a C# entity's, a library's) names its entity: its header shows the class's title (`nodeTitle`), with nothing to type in. */
  const codeNamed = () => declares() === 'entity' && isClassNode(spec())
  /** The section of an entity definition's step (its entity, its setting; a class node's key, device or Part of), if it has one. */
  const picker = () => stepPicker(kind(), spec())
  /** The node library a class node without code comes from, by its id; undefined for any other node. */
  const library = () => (isClassNode(spec()) && !hasCode(spec()) ? spec()!.sourceId ?? '' : undefined)
  /** That library's name, when the saved libraries' names have it: never its id, which says nothing to the person. */
  const libraryName = () => { const id = library(), names = props.libraries; return id && names && Object.hasOwn(names, id) && names[id] ? names[id] : undefined }
  const nameLabel = () => (declares() === 'device' ? 'Device name' : declares() ? 'Entity name' : 'Node label')
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
  /** The header's text: the declared entity's or device's name (left out when cleared, never null), else the node's label. */
  const retitle = (value: string) => {
    if (!declares()) return props.onGraphChange(updateNode(props.graph, props.node.id, { label: value || undefined }), { coalesce: `label:${props.node.id}` })
    // An Entity or Device node's own label is never shown: the name replaces it.
    const { label: _label, ...node } = props.node
    const config = { ...node.config }
    if (value) config.name = value
    else delete config.name
    props.onGraphChange({ ...props.graph, nodes: props.graph.nodes.map(n => (n.id === node.id ? { ...node, config } : n)) }, { coalesce: `name:${props.node.id}` })
  }
  /**
   * A declared name left (or Enter pressed) is kept trimmed, in the same undo step as its typing: spaces are typed as
   * they come, and a name of only spaces is no name.
   */
  const commitTitle = () => {
    if (!declares()) return
    const name = typeof props.node.config.name === 'string' ? props.node.config.name : undefined
    if (name !== undefined && name !== name.trim()) retitle(name.trim())
  }
  const copy = (text: string, done: string) => {
    void Promise.resolve(navigator.clipboard?.writeText?.(text)).then(() => setStatus(done), () => setStatus('Copying is not allowed here'))
  }

  // The rows' type chips and editors know the types the graph's Custom C# nodes declare.
  const types = createMemo(() => graphTypes(props.graph, props.catalog))
  const decimal = useDecimalSeparator()
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
  const pickers: StepPickerContext = {
    node: () => props.node,
    graph: () => props.graph,
    catalog: types,
    errorsFor,
    onChange: config => props.onChange(config),
    onGraphChange: graph => props.onGraphChange(graph),
    onNotice: text => props.onNotice?.(text),
    saved: () => props.savedEntities,
  }
  /** How the row of an input is edited when its kind says so (a Has state's state offers its entity's states). */
  const rowFor = (port: string) => {
    const row = own(stepInputRows, kind())
    if (!row || !Object.hasOwn(row, port)) {
      // A Set state's value, as its entity's type has it: ready to fill, set to true by Set value, or said to be kept when left empty.
      const state = createMemo(() => stateValueRow(props.node, port, types(), props.graph))
      return { get ready() { return state()?.ready }, get description() { return state()?.description }, get setValue() { return state()?.setValue } }
    }
    const edit = createMemo(() => row[port](props.node, props.graph, types()))
    return { get widget() { return edit().widget }, get context() { return edit().context } }
  }
  // Messages about the service, its response or its fields as a whole, which have no input row.
  const serviceMessages = (key: string) => problems().filter(d => d.inputId === key).map(d => d.message)

  /** A setting of the node that is not an input (an operator, a type, a level, a state). */
  const ConfigField = (field: { name: string; rule: ConfigSpec }) => {
    const id = createUniqueId(), hintId = `${id}-hint`
    const hint = () => (field.rule.description ? hintId : undefined)
    const label = () => (field.rule.label && field.rule.label !== field.name ? field.rule.label : words.key(field.name))
    // Run history tracking that is not set is what the document does by default (a trigger of an entity definition is not recorded).
    const value = () => field.name === 'trackHistory' ? props.node.config.trackHistory ?? trackHistoryDefault(kind(), graphDocument(props.graph), props.catalog) : props.node.config[field.name]
    const errors = () => errorsFor(field.name)
    const types = () => field.rule.enumSource === 'types' || field.name === 'type' || field.name === 'itemType'
    // A Constant offers only the types a value can be typed in for; a Convert or a loop any listed type.
    const options = () => (field.rule.enumSource === 'types' ? (kind() === 'constant' && field.name === 'type' ? constantTypes(props.catalog) : listedTypes(props.catalog)) : (field.rule.enum ?? []).map(String))
    // A Constant's Type list names no two entries alike ("Number (int)" beside Number).
    const constantLabels = createMemo(() => (kind() === 'constant' && field.name === 'type' ? constantTypeLabels(props.catalog, typeof value() === 'string' ? (value() as string) : undefined) : undefined))
    // A choice the catalog names (enumLabels, beside enum) reads as it says ("JSON object"), any other in words.
    const listed = (option: string) => { const label = field.rule.enumLabels?.[(field.rule.enum ?? []).map(String).indexOf(option)]; return typeof label === 'string' && label ? label : undefined }
    const optionLabel = (option: string) => (types() ? constantLabels()?.get(option) ?? typeLabel(option, props.catalog) : field.name === 'operator' ? operators[option] ?? option : listed(option) ?? words.key(option))
    const number = () => field.rule.type === 'integer' || field.rule.type === 'number'
    const stateContext = createMemo(() => kind() === 'condition' && field.name === 'state' ? entityStateContext(props.graph, props.node, 'entity', props.catalog) : undefined)
    const homeEntity = () => kind() === 'ha_entity' && field.name === 'entity'
    return (
      <div class="hs-insp-field" data-input-id={field.name} aria-invalid={errors().length > 0 || undefined}>
        <label class="hs-insp-field__label" for={id}>{label()}</label>
        <Show when={field.rule.enum || field.rule.enumSource} fallback={
          <Show when={field.rule.type === 'boolean'} fallback={
            <Show when={kind() === 'condition' && field.name === 'state'} fallback={
              <Show when={!homeEntity()} fallback={
                // An Entity's entity: any of Home Assistant's, chosen from them all or typed in as an id.
                <WidgetControl widget={{ kind: 'entity', multiple: false }} value={typeof value() === 'string' && value() !== '' ? (value() as string) : undefined} id={id} label={label()}
                  describedBy={hint()} invalid={errors().length > 0} disabled={false} compact={false} required={!!field.rule.required}
                  commit={v => set(field.name, typeof v === 'string' ? v : '')} />
              }>
                <DraftField id={id} label={label()} text={value() === undefined ? '' : String(value())} placeholder="Not set" describedBy={hint()}
                  inputmode={number() ? 'decimal' : undefined} invalid={errors().length > 0}
                  commit={text => {
                    if (!number()) { set(field.name, text === '' && !field.rule.required ? undefined : text); return }
                    if (text.trim() === '') { set(field.name, undefined); return }
                    const parsed = parseNumber(text, decimal()) ?? Number.NaN
                    const message = field.rule.type === 'integer' ? 'Enter a whole number.' : 'Enter a number.'
                    if (!Number.isFinite(parsed)) return numberMessage(text, decimal(), message)
                    if (field.rule.type === 'integer' && !Number.isInteger(parsed)) return message
                    set(field.name, parsed)
                  }} />
              </Show>
            }>
              <WidgetControl widget={{ ...stateField, fallbackStateOptions: stateContext()?.fallbackStateOptions }} value={typeof value() === 'string' ? (value() as string) : undefined} id={id} label={label()}
                describedBy={hint()} invalid={errors().length > 0} disabled={false} compact={false} required={!!field.rule.required}
                context={{ targetEntityIds: stateContext()?.entityIds }} commit={v => set(field.name, typeof v === 'string' ? v : undefined)} />
            </Show>
          }>
            <span class="hs-widget-row">
              <input id={id} type="checkbox" role="switch" class="hs-switch" aria-describedby={hint()} checked={value() === true} onChange={e => set(field.name, e.currentTarget.checked)} />
              <span class="hs-muted">{value() === undefined ? 'Not set' : value() ? 'True' : 'False'}</span>
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
              <option value="" selected>{value() === undefined ? 'Not set' : types() ? optionLabel(String(value())) : String(value())}</option>
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
    // An entity definition's steps choose its entities, their commands and its settings with pickers of their own.
    const edited = new Set([...ownConfig, ...(own(kindConfig, kind()) ?? []), ...(picker()?.keys ?? [])])
    return Object.entries(spec()?.config ?? {}).filter(([name]) => {
      if (edited.has(name) || (spec()?.argumentStorage === 'arguments' && name === 'arguments')) return false
      // An attribute name matters only while the node reads an attribute.
      return !(name === 'attribute' && spec()?.config.field && props.node.config.field !== 'attribute')
    }).map(([name]) => name)
  }, undefined, { equals: sameList })

  const menu = (): MenuItem[] => [
    ...(props.onDuplicate ? [{ label: 'Duplicate', onSelect: () => props.onDuplicate?.() }] : []),
    { label: 'Copy node ID', onSelect: () => copy(props.node.id, 'Node ID copied') },
    ...(props.onDelete ? [{ label: 'Delete', onSelect: () => props.onDelete?.(), danger: true }] : []),
  ]
  const inputsHeading = () => (isClassNode(spec()) ? 'Settings' : 'Inputs')
  /** Edit code, for a node that holds code, and what its code gives it. */
  const code = () => (
    <div class="hs-insp-field hs-insp-code" data-input-id="source" aria-invalid={errorsFor('source').length > 0 || undefined}>
      <Show when={props.onEditCode}>
        <button type="button" class="hs-button hs-button--primary hs-insp-code__edit" onClick={() => props.onEditCode?.()}>
          <Glyph paths={icons.code} />Edit code
        </button>
      </Show>
      <small class="hs-field-hint">{isClassNode(spec()) ? 'Its settings come from the code.' : 'Its inputs and outputs come from the code.'}</small>
      <Errors list={errorsFor('source')} />
    </div>
  )
  const description = () => signature()?.description
    || (spec() ? stepDescription(kind(), spec()!.description, graphDocument(props.graph)) : 'This kind of step is not available. Its settings are kept.')
  /** The config as stored, but for a value typed into a class's secret setting (a hand-made graph), which is never shown. */
  const storedConfig = () => {
    const secrets = new Set((classSignature(props.node, props.catalog)?.settings ?? []).filter(s => s.secret).map(s => s.key))
    const values = props.node.config.arguments
    if (!secrets.size || !values || typeof values !== 'object' || Array.isArray(values)) return props.node.config
    const shown = Object.fromEntries(Object.entries(values).map(([key, value]) =>
      [key, secrets.has(key) && (value as { kind?: unknown } | null)?.kind === 'literal' ? hiddenSecret : value]))
    return { ...props.node.config, arguments: shown }
  }
  const stored = () => JSON.stringify({ kind: props.node.kind, ...(props.node.label ? { label: props.node.label } : {}), config: storedConfig() }, null, 2)
  const loop = () => !!spec()?.config.carry

  return (
    <div class="hs-insp-node" ref={root} data-node-id={props.node.id} style={{ '--hs-cat': view().identity.color }}>
      <header class="hs-insp-head" classList={{ 'hs-insp-head--trigger': kind() === 'trigger' }}>
        <span class="hs-insp-head__icon" title={view().identity.label}><Glyph paths={view().identity.icon} /></span>
        <div class="hs-insp-head__titles">
          <Show when={!codeNamed()} fallback={<h2 ref={el => (title = el)} class="hs-insp-title" tabindex="-1" aria-describedby={descriptionId}>{view().title}</h2>}>
            <input ref={el => (title = el)} class="hs-insp-title" aria-label={nameLabel()}
              aria-describedby={describedBy(descriptionId, declares() && errorsFor('name').length > 0 && nameErrorsId)}
              data-input-id={declares() ? 'name' : undefined} aria-invalid={(declares() && errorsFor('name').length > 0) || undefined}
              value={declares() ? (typeof props.node.config.name === 'string' ? props.node.config.name : '') : props.node.label ?? ''}
              placeholder={declares() ? 'Not set' : unnamed()} onInput={e => retitle(e.currentTarget.value)}
              onBlur={commitTitle} onKeyDown={e => { if (e.key === 'Enter') commitTitle() }} />
          </Show>
          <small class="hs-insp-kind">{kindLabel() === view().identity.label ? kindLabel() : `${kindLabel()} · ${view().identity.label}`}</small>
        </div>
        <Menu label="Node actions" items={menu()} />
      </header>
      <Show when={declares()}><FieldErrors id={nameErrorsId} list={errorsFor('name')} /></Show>
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
              // A problem on an execution pin (a command of an Entity node) has no field to go to.
              const port = problem.inputId ?? (problem.portId && !executionPorts(props.node, types()).includes(problem.portId) ? problem.portId : undefined)
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
          <TriggerConfig node={props.node} onChange={props.onChange} diagnostics={props.diagnostics} document={graphDocument(props.graph)}
            onRetype={(config, label) => {
              const result = retypeTriggerNode(props.graph, props.node.id, config, label, types())
              props.onGraphChange(result.graph)
              if (result.notice) props.onNotice?.(result.notice)
            }} />
        </Show>
        {/* A Custom C# step's code comes first; a C# entity's or C# device's after its key and device, right above its
            settings. A library's class has no code: in its place, the node library it comes from. */}
        <Show when={hasCode(spec()) && !isClassNode(spec())}>{code()}</Show>
        <Show when={picker()}>{picker => <Dynamic component={picker().Section} ctx={pickers} />}</Show>
        <Show when={hasCode(spec()) && isClassNode(spec())}>{code()}</Show>
        <Show when={library() !== undefined}>
          <div class="hs-insp-field">
            <small class="hs-field-hint hs-insp-library">
              <Show when={libraryName()} fallback="From a node library.">{name => <>
                From the node library{' '}
                <Show when={props.onOpenLibrary} fallback={name()}>
                  <button type="button" class="hs-insp-link" onClick={() => props.onOpenLibrary?.(library()!)}>{name()}</button>
                </Show>.
              </>}</Show>
            </small>
          </div>
        </Show>
        <For each={configFields()}>{name => <ConfigField name={name} rule={spec()!.config[name]} />}</For>
        <Show when={kind() === 'parallel'}><Branches ctx={structure} /></Show>
        <Show when={kind() === 'object_create'}><ObjectFields ctx={structure} /></Show>
        <Show when={kind() === 'array_create'}><ArrayItems ctx={structure} /></Show>
        <Show when={kind() !== 'service' && kind() !== 'trigger' && ports().length}>
          {/* A C# entity's or C# device's inputs are its class's settings, as its code editor calls them. */}
          <section class="hs-insp-section" aria-label={inputsHeading()}>
            <h3 class="hs-insp-heading">{inputsHeading()}</h3>
            <For each={ports()}>{port => <InputRow port={port} ctx={rows} {...rowFor(port)} />}</For>
          </section>
        </Show>
        <Show when={kind() === 'select_property'}>
          {(() => {
            const id = createUniqueId(), hintId = `${id}-hint`
            return (
              <div class="hs-insp-field" data-input-id="property" aria-invalid={errorsFor('property').length > 0 || undefined}>
                <label class="hs-insp-field__label" for={id}>{spec()?.config.property?.label ?? 'Property'}</label>
                <PropertyPicker id={id} node={props.node} graph={props.graph} catalog={props.catalog} errors={errorsFor('property')} describedBy={hintId}
                  onChoose={name => {
                    const result = chooseProperty(props.graph, props.node.id, name, props.catalog)
                    props.onGraphChange(result.graph)
                    if (result.notice) props.onNotice?.(result.notice)
                  }} />
                <small id={hintId} class="hs-field-hint">{spec()?.config.property?.description}</small>
                <Errors list={errorsFor('property')} />
              </div>
            )
          })()}
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
                const csharp = () => (pin().typeId ? csharpType(pin().typeId!, types()) : undefined)
                const csharpIds = () => (pin().typeId ? [pin().typeId!] : [])
                // The canvas pin's name, with the C# type after the friendly one: "Output Result, True/False, C# type bool, connected".
                const name = () => [`Output ${pin().spoken ?? pin().label}`, pin().typeName, csharp() && `C# type ${csharp()}`, pin().connected ? 'connected' : 'not connected'].filter(Boolean).join(', ')
                return (
                  <Show when={outputs().some(p => p.id === pinId)}>
                    <li class="hs-insp-output" aria-label={name()} aria-describedby={pin().description ? descId : undefined} style={{ '--hs-chip-color': pin().color }}>
                      <span class="hs-type-chip__dot" classList={{ 'hs-type-chip__dot--list': pin().list, 'hs-type-chip__dot--filled': pin().connected }} aria-hidden="true" />
                      <span class="hs-insp-output__name">{pin().label}</span>
                      <span class="hs-insp-output__type" title={csharp() ? `Type: ${pin().typeName} · C#: ${csharp()}` : undefined}>
                        <span class="hs-insp-output__friendly">{pin().typeName}</span>
                        <CSharpTypeText types={csharpIds()} catalog={types()} />
                      </span>
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
