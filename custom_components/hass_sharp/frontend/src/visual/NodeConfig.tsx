import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Menu, type MenuItem } from '../components/Menu'
import { InputEditor } from './InputEditor'
import { ServiceFields, type FieldRow } from './ServiceFields'
import { Glyph, TriggerAdvanced, TriggerConfig } from './TriggerConfig'
import { buildNodeView, execLabel, graphVariables, type InputView } from './flow/flowModel'
import { inputValue, resolveTypeSpec, setInput, words, type ConfigSpec, type GraphNode, type InputSpec, type Operand, type VisualCatalogSnapshot } from './contract'
import { updateNode, type VisualGraph } from './graph'
import { icons } from './icons'
import { typeColor, typeIsList, typeLabel } from './typeLabels'
import { DraftField } from './widgets/common'
import { WidgetControl } from './widgets'
import type { VisualDiagnostic } from './api'

export { Glyph } from './TriggerConfig'

type Options = { coalesce?: string }
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const sameList = (a: readonly string[], b: readonly string[]) => a.length === b.length && a.every((v, i) => v === b[i])
const operators: Record<string, string> = { eq: 'is equal to', ne: 'is not equal to', lt: 'is less than', le: 'is at most', gt: 'is greater than', ge: 'is at least' }
/** Config keys edited by a kind's own section, or not at all (`values` of a loop result are its inputs). */
const ownConfig = new Set(['output', 'carry', 'exports', 'branches'])
const kindConfig: Record<string, string[]> = { service: ['service', 'fields', 'returnResponse'], custom: ['code'], object_create: ['fields'], array_create: ['items'], loop_result: ['values'] }
const closeIcon = ['M6 6l12 12', 'M18 6 6 18']

/** The focusable control of an inspector row: the one its label names, else its first field, else any button. */
export function focusRow(root: ParentNode | undefined, port: string) {
  const row = root && [...root.querySelectorAll<HTMLElement>('[data-input-id]')].find(el => el.dataset.inputId === port)
  if (!row) return false
  const labelled = row.querySelector<HTMLLabelElement>('label[for]')
  const target = (labelled?.htmlFor ? row.querySelector<HTMLElement>(`[id="${labelled.htmlFor}"]`) : null)
    ?? row.querySelector<HTMLElement>('input:not([type="hidden"]),select,textarea')
    ?? row.querySelector<HTMLElement>('button:not([aria-haspopup="menu"])')
    ?? row
  target.focus()
  target.scrollIntoView?.({ block: 'nearest' })
  return true
}

/** The friendly type of an input: its resolved type, else its declared types; never an unresolvable id. */
export function inputTypeChip(view: InputView | undefined, spec: InputSpec | undefined, catalog: VisualCatalogSnapshot) {
  const id = view?.pin.typeId
  if (id && resolveTypeSpec(id, catalog)) return { text: typeLabel(id, catalog), color: typeColor(id, catalog), list: typeIsList(id, catalog) }
  const types = (spec?.types ?? []).filter(t => resolveTypeSpec(t, catalog))
  const names = [...new Set(types.map(t => typeLabel(t, catalog)))]
  if (names.length === 1) return { text: names[0], color: typeColor(types[0], catalog), list: typeIsList(types[0], catalog) }
  return { text: names.length && names.length <= 3 ? names.join(' or ') : 'Any value', color: 'var(--hs-muted)', list: false }
}

export const TypeChip = (props: { text: string; color: string; list?: boolean }) => (
  <span class="hs-type-chip" style={{ '--hs-chip-color': props.color }} title={`Type: ${props.text}`}>
    <span class="hs-type-chip__dot" classList={{ 'hs-type-chip__dot--list': !!props.list }} aria-hidden="true" />
    {props.text}
  </span>
)

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
}

/**
 * The inspector of one node, top to bottom: header (icon, editable title, kind, "⋯" menu, description), its
 * problems, the kind's main section (service, trigger, code or settings), its inputs, its outputs and a collapsed
 * Advanced section. Values are edited with the same `InputEditor` as the node card. Nothing here connects or
 * disconnects anything: a connected input is a read-only "From …" pill that selects its source.
 */
export function NodeConfig(props: NodeConfigProps) {
  let root!: HTMLDivElement
  const descriptionId = createUniqueId()
  const spec = createMemo(() => (Object.hasOwn(props.catalog.nodes, props.node.kind) ? props.catalog.nodes[props.node.kind] : undefined))
  const view = createMemo(() => buildNodeView(props.node, { graph: props.graph, catalog: props.catalog, diagnostics: props.diagnostics ?? [] }))
  const inputs = createMemo(() => new Map(view().rows.flatMap(row => (row.input ? [[row.input.port, row.input] as const] : []))))
  const ports = createMemo(() => [...inputs().keys()], undefined, { equals: sameList })
  const outputs = createMemo(() => view().rows.flatMap(row => (row.output?.kind === 'data' ? [row.output] : [])))
  const outputIds = createMemo(() => outputs().map(pin => pin.id), undefined, { equals: sameList })
  const variables = createMemo(() => graphVariables(props.graph, props.catalog).filter(v => v.name !== props.node.config.output))
  const problems = createMemo(() => (props.diagnostics ?? []).filter(d => d.nodeId === props.node.id && d.severity >= 2))
  const errorsFor = (key: string) => problems().filter(d => d.severity === 3 && (d.inputId === key || d.portId === key)).map(d => d.message)
  const kind = () => props.node.kind
  const kindLabel = () => spec()?.label ?? words.key(kind())
  const [status, setStatus] = createSignal('')
  const [more, setMore] = createSignal(false)
  const focus = (port: string) => queueMicrotask(() => focusRow(root, port))
  const set = (key: string, value: unknown, options?: Options) => {
    const next = { ...props.node.config }
    if (value === undefined) delete next[key]
    else next[key] = value
    props.onChange(next, options)
  }
  const connected = (port: string) => props.graph.edges.some(e => e.kind === 'data' && e.target === props.node.id && e.targetPort === port)
  const copy = (text: string, done: string) => {
    void Promise.resolve(navigator.clipboard?.writeText?.(text)).then(() => setStatus(done), () => setStatus('Copying is not allowed here'))
  }
  const Errors = (errors: { list: string[] }) => (
    <Show when={errors.list.length}><ul class="hs-input-editor__errors"><For each={errors.list}>{m => <li>{m}</li>}</For></ul></Show>
  )

  /** One input: label and type chip, then its editor, a "From …" pill when connected, or "Not set". */
  const Row = (row: { port: string } & FieldRow) => {
    const input = () => inputs().get(row.port)
    const inputSpec = () => row.spec ?? input()?.spec
    const label = () => row.label ?? input()?.pin.label ?? words.portLabel(undefined, row.port)
    const operand = () => row.value ?? (inputValue(props.node, row.port, props.catalog) as Operand | undefined)
    const wired = () => input()?.mode === 'connected'
    const [setting, setSetting] = createSignal(false)
    // A service's target and the fields added on purpose always show their editor.
    const muted = () => !wired() && !setting() && input()?.mode === 'muted' && !(kind() === 'service' && (row.port === 'target' || row.port.startsWith('field:')))
    const errors = () => input()?.errors ?? []
    const chip = () => inputTypeChip(input(), inputSpec(), props.catalog)
    const editSpec = (): InputSpec | undefined => { const s = inputSpec(); return s && row.description !== undefined ? { ...s, description: row.description } : s }
    // A wire-only type that accepts null (a nullable C# type) can still be given "no value" without a wire.
    const nullable = () => input()?.mode === 'wire' && !!inputSpec()?.types.length && inputSpec()!.types.every(t => resolveTypeSpec(t, props.catalog)?.nullable === true)
    const isNull = () => { const o = operand(); return o?.kind === 'literal' && o.value === null }
    const change = (next: Operand, options?: Options) => {
      if (row.onChange) return row.onChange(next, options)
      const node = structuredClone(props.node)
      if (row.port.startsWith('field:') && !record(node.config.fields)) node.config.fields = {}
      setInput(node, row.port, next, props.catalog)
      props.onChange(node.config, options)
    }
    return (
      <div class="hs-insp-input" data-input-id={row.port} aria-invalid={errors().length > 0 || undefined}>
        <Show when={!wired() && !muted() && editSpec()} fallback={<span class="hs-insp-input__label">{label()}</span>}>
          {current => (
            <InputEditor variant="panel" input={current()} value={operand()} label={label()} catalog={props.catalog} variables={variables()}
              context={{ targetEntityIds: view().targetEntityIds }} errors={errors()} onChange={change} />
          )}
        </Show>
        <TypeChip {...chip()} />
        <Show when={row.onRemove}>
          {remove => (
            <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={row.removeLabel ?? `Remove ${label()}`}
              title={row.removeBlocked ?? row.removeLabel} disabled={!!row.removeBlocked} onClick={() => remove()()}>
              <Glyph paths={closeIcon} />
            </button>
          )}
        </Show>
        <Show when={wired() && input()}>
          {current => (
            <>
              <button type="button" class="hs-insp-pill" style={{ '--hs-chip-color': chip().color }}
                aria-label={`${label()}, ${chip().text}, from ${current().source}. Select it on the canvas`}
                title={`Select ${current().source?.split(' · ')[0]} on the canvas`} onClick={() => props.onSelect?.(current().sourceNode)}>
                <span class="hs-type-chip__dot hs-type-chip__dot--filled" aria-hidden="true" />
                <span>From {current().source}</span>
              </button>
              <Errors list={errors()} />
            </>
          )}
        </Show>
        <Show when={!wired() && nullable()}>
          <label class="hs-insp-toggle hs-insp-null">
            <input type="checkbox" role="switch" class="hs-switch" checked={isNull()}
              onChange={e => change(e.currentTarget.checked ? { kind: 'literal', type: inputSpec()!.types[0], value: null } : { kind: 'unset' })} />
            <span><strong>No value</strong><small class="hs-field-hint">Pass null instead of connecting a value.</small></span>
          </label>
        </Show>
        <Show when={muted()}>
          <div class="hs-insp-unset">
            <span class="hs-muted">Not set</span>
            <button type="button" class="hs-button hs-button--ghost" aria-label={`Set ${label()}`}
              onClick={() => { setSetting(true); focus(row.port) }}>Set value</button>
          </div>
          <Show when={inputSpec()?.description}><small class="hs-field-hint">{inputSpec()!.description}</small></Show>
          <Errors list={errors()} />
        </Show>
      </div>
    )
  }

  /** A setting of the node that is not an input (an operator, a type, a level, a state). */
  const ConfigField = (field: { name: string; rule: ConfigSpec }) => {
    const id = createUniqueId(), hintId = `${id}-hint`
    const label = () => (field.rule.label && field.rule.label !== field.name ? field.rule.label : words.key(field.name))
    const value = () => props.node.config[field.name]
    const errors = () => errorsFor(field.name)
    const types = () => field.rule.enumSource === 'types' || field.name === 'type' || field.name === 'itemType'
    const options = () => (field.rule.enumSource === 'types' ? Object.keys(props.catalog.types) : (field.rule.enum ?? []).map(String))
    const optionLabel = (option: string) => (types() ? typeLabel(option, props.catalog) : field.name === 'operator' ? operators[option] ?? option : words.key(option))
    const number = () => field.rule.type === 'integer' || field.rule.type === 'number'
    const entityIds = () => { const entity = props.node.config.entity; return record(entity) && entity.kind === 'literal' && typeof entity.value === 'string' ? [entity.value] : [] }
    return (
      <div class="hs-insp-field" data-input-id={field.name} aria-invalid={errors().length > 0 || undefined}>
        <label class="hs-insp-field__label" for={id}>{label()}</label>
        <Show when={field.rule.enum || field.rule.enumSource} fallback={
          <Show when={field.rule.type === 'boolean'} fallback={
            <Show when={kind() === 'condition' && field.name === 'state'} fallback={
              <DraftField id={id} label={label()} text={value() === undefined ? '' : String(value())} placeholder="Not set" describedBy={hintId}
                inputmode={number() ? 'decimal' : undefined} invalid={errors().length > 0}
                commit={text => {
                  if (!number()) { set(field.name, text === '' && !field.rule.required ? undefined : text); return }
                  if (text.trim() === '') { set(field.name, undefined); return }
                  const parsed = Number(text)
                  if (!Number.isFinite(parsed) || (field.rule.type === 'integer' && !Number.isInteger(parsed))) return field.rule.type === 'integer' ? 'Enter a whole number.' : 'Enter a number.'
                  set(field.name, parsed)
                }} />
            }>
              <WidgetControl widget={{ kind: 'state', multiple: false }} value={typeof value() === 'string' ? (value() as string) : undefined} id={id} label={label()}
                describedBy={hintId} invalid={errors().length > 0} disabled={false} compact={false} required={!!field.rule.required}
                context={{ targetEntityIds: entityIds() }} commit={v => set(field.name, typeof v === 'string' ? v : undefined)} />
            </Show>
          }>
            <span class="hs-widget-row">
              <input id={id} type="checkbox" role="switch" class="hs-switch" aria-describedby={hintId} checked={value() === true} onChange={e => set(field.name, e.currentTarget.checked)} />
              <span class="hs-muted">{value() === undefined ? 'Not set' : value() ? 'Yes' : 'No'}</span>
            </span>
          </Show>
        }>
          <select id={id} class="hs-input" aria-describedby={hintId} aria-invalid={errors().length > 0 || undefined}
            onChange={e => set(field.name, e.currentTarget.value === '' ? undefined : e.currentTarget.value)}>
            <Show when={value() === undefined || !options().includes(String(value()))}>
              <option value="" selected>{value() === undefined ? 'Not set' : String(value())}</option>
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
    ...(props.onDuplicate ? [{ label: 'Duplicate', onSelect: props.onDuplicate }] : []),
    { label: 'Copy node ID', onSelect: () => copy(props.node.id, 'Node ID copied') },
    ...(props.onDelete ? [{ label: 'Delete', onSelect: props.onDelete, danger: true }] : []),
  ]
  const description = () => spec()?.description ?? 'This kind of step is not available. Its settings are kept.'
  const stored = () => JSON.stringify({ kind: props.node.kind, ...(props.node.label ? { label: props.node.label } : {}), config: props.node.config }, null, 2)
  const loop = () => !!spec()?.config.carry

  return (
    <div class="hs-insp-node" ref={root} style={{ '--hs-cat': view().identity.color }}>
      <header class="hs-insp-head" classList={{ 'hs-insp-head--trigger': kind() === 'trigger' }}>
        <span class="hs-insp-head__icon" title={view().identity.label}><Glyph paths={view().identity.icon} /></span>
        <div class="hs-insp-head__titles">
          <input class="hs-insp-title" aria-label="Node label" aria-describedby={descriptionId} value={props.node.label ?? ''} placeholder={kindLabel()}
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
                </li>
              )
            }}
          </For>
        </ul>
      </Show>

      <div class="hs-insp-main">
        <Show when={kind() === 'service'}>
          <ServiceFields node={props.node} graph={props.graph} catalog={props.catalog} onChange={props.onChange} focus={focus}
            row={(port, extra) => <Row port={port} {...extra} />} />
        </Show>
        <Show when={kind() === 'trigger'}>
          <TriggerConfig node={props.node} onChange={props.onChange} diagnostics={props.diagnostics} />
        </Show>
        <Show when={kind() === 'custom'}>
          <div class="hs-insp-field" data-input-id="code" aria-invalid={errorsFor('code').length > 0 || undefined}>
            <label class="hs-insp-field__label" for={`${descriptionId}-code`}>C# code</label>
            <DraftField id={`${descriptionId}-code`} label="C# code" multiline shortcutHint class="hs-code-input hs-insp-code" text={typeof props.node.config.code === 'string' ? props.node.config.code : ''}
              invalid={errorsFor('code').length > 0} commit={text => { set('code', text); return undefined }} />
            <Errors list={errorsFor('code')} />
          </div>
        </Show>
        <For each={configFields()}>{name => <ConfigField name={name} rule={spec()!.config[name]} />}</For>
        <Show when={kind() === 'parallel'}><Branches /></Show>
        <Show when={kind() === 'object_create'}><ObjectFields /></Show>
        <Show when={kind() === 'array_create'}><ArrayItems /></Show>
        <Show when={kind() !== 'service' && kind() !== 'trigger' && ports().length}>
          <section class="hs-insp-section" aria-label="Inputs">
            <h3 class="hs-insp-heading">Inputs</h3>
            <For each={ports()}>{port => <Row port={port} />}</For>
          </section>
        </Show>
        <Show when={loop()}>
          <Declarations key="carry" title="Kept between iterations" hint="Each iteration starts from the value the previous one finished with." />
          <Declarations key="exports" title="Available after the loop" hint="Set inside the loop and read after it finishes." />
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
                    <li class="hs-insp-output" tabindex="0" aria-label={pin().name} aria-describedby={pin().description ? descId : undefined} style={{ '--hs-chip-color': pin().color }}>
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
          <Show when={spec()?.config.output}>
            <div class="hs-insp-field" data-input-id="output" aria-invalid={errorsFor('output').length > 0 || undefined}>
              <label class="hs-insp-field__label" for={`${descriptionId}-output`}>Expose to C# as</label>
              <DraftField id={`${descriptionId}-output`} label="Expose to C# as" text={typeof props.node.config.output === 'string' ? props.node.config.output : ''}
                placeholder="Not exposed" describedBy={`${descriptionId}-output-hint`} invalid={errorsFor('output').length > 0}
                commit={text => { set('output', text.trim() || undefined); return undefined }} />
              <small id={`${descriptionId}-output-hint`} class="hs-field-hint">A name Custom C# steps can read this value by. Leave empty to keep it to the canvas.</small>
              <Errors list={errorsFor('output')} />
            </div>
          </Show>
          <Show when={kind() === 'trigger'}><TriggerAdvanced node={props.node} onChange={props.onChange} /></Show>
          <Show when={kind() === 'custom'}><SharedCode /></Show>
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

  /** "Shared C# for this automation": the graph's usings and helper members, only on Custom C# nodes. */
  function SharedCode() {
    const id = createUniqueId()
    const setGraph = (key: 'imports' | 'members', text: string) => {
      const next: VisualGraph = { ...props.graph }
      if (text.trim()) next[key] = text
      else delete next[key]
      props.onGraphChange(next)
    }
    return (
      <fieldset class="hs-insp-group">
        <legend class="hs-insp-field__label">Shared C# for this automation</legend>
        <For each={[['imports', 'Usings', 'using lines for every Custom C# step, for example using System.Globalization;'], ['members', 'Helper members', 'Fields, properties and methods every Custom C# step of this automation can use.']] as const}>
          {([key, label, hint]) => (
            <div class="hs-insp-field">
              <label class="hs-insp-field__label" for={`${id}-${key}`}>{label}</label>
              <DraftField id={`${id}-${key}`} label={label} multiline shortcutHint class="hs-code-input" text={props.graph[key] ?? ''} describedBy={`${id}-${key}-hint`}
                commit={text => { setGraph(key, text); return undefined }} />
              <small id={`${id}-${key}-hint`} class="hs-field-hint">{hint}</small>
            </div>
          )}
        </For>
      </fieldset>
    )
  }

  /** The branches of a Parallel step; one that has steps attached is removed on the canvas first. */
  function Branches() {
    const branches = () => (Array.isArray(props.node.config.branches) ? props.node.config.branches.filter((b): b is string => typeof b === 'string') : [])
    const used = (id: string) => props.graph.edges.some(e => e.kind !== 'data' && e.source === props.node.id && e.port === `branch:${id}`)
    const add = () => {
      const taken = new Set(branches())
      const id = [...'abcdefghijklmnopqrstuvwxyz'].find(letter => !taken.has(letter)) ?? `branch_${branches().length + 1}`
      set('branches', [...branches(), id])
    }
    return (
      <div class="hs-insp-field" data-input-id="branches" aria-invalid={errorsFor('branches').length > 0 || undefined}>
        <span class="hs-insp-field__label">Branches</span>
        <ul class="hs-insp-list">
          <For each={branches()}>
            {id => {
              const name = () => execLabel(props.node, `branch:${id}`, props.catalog)
              return (
                <li>
                  <span>{name()}</span>
                  <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove ${name()}`} disabled={used(id) || branches().length <= 1}
                    title={used(id) ? 'Remove its steps on the canvas first' : undefined} onClick={() => set('branches', branches().filter(b => b !== id))}>
                    <Glyph paths={closeIcon} />
                  </button>
                </li>
              )
            }}
          </For>
        </ul>
        <button type="button" class="hs-button hs-button--secondary hs-insp-add-button" onClick={add}>+ Add branch</button>
        <small class="hs-field-hint">Branches run at the same time; Done runs once all of them have finished.</small>
        <Errors list={errorsFor('branches')} />
      </div>
    )
  }

  /** The field names of a Create object step; each is an input below. */
  function ObjectFields() {
    const fields = () => (record(props.node.config.fields) ? props.node.config.fields : {})
    return (
      <div class="hs-insp-field" data-input-id="fields">
        <span class="hs-insp-field__label">Fields</span>
        <ul class="hs-insp-list">
          <For each={Object.keys(fields())}>
            {key => (
              <li>
                <code>{key}</code>
                <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove field ${key}`} disabled={connected(`field:${key}`)}
                  title={connected(`field:${key}`) ? 'Remove its connection on the canvas first' : undefined}
                  onClick={() => { const next = { ...fields() }; delete next[key]; set('fields', next) }}>
                  <Glyph paths={closeIcon} />
                </button>
              </li>
            )}
          </For>
        </ul>
        <DraftField label="Add a field named" placeholder="+ Add field…" text="" clearOnCommit
          commit={text => {
            const name = text.trim()
            if (!name) return
            if (Object.hasOwn(fields(), name)) return 'There is already a field with this name.'
            set('fields', { ...fields(), [name]: { kind: 'unset' } })
            focus(`field:${name}`)
          }} />
      </div>
    )
  }

  /** The items of a Create array step; each is an input below. */
  function ArrayItems() {
    const items = () => (Array.isArray(props.node.config.items) ? props.node.config.items : [])
    const lastConnected = () => items().length > 0 && connected(`item:${items().length - 1}`)
    return (
      <div class="hs-insp-field" data-input-id="items">
        <span class="hs-insp-field__label">Items</span>
        <div class="hs-insp-field__row">
          <span class="hs-muted">{items().length === 1 ? '1 item' : `${items().length} items`}</span>
          <button type="button" class="hs-button hs-button--secondary hs-insp-add-button" onClick={() => { set('items', [...items(), { kind: 'unset' }]); focus(`item:${items().length}`) }}>+ Add item</button>
          <button type="button" class="hs-button hs-button--ghost" disabled={!items().length || lastConnected()} title={lastConnected() ? 'Remove its connection on the canvas first' : undefined}
            onClick={() => set('items', items().slice(0, -1))}>Remove last</button>
        </div>
      </div>
    )
  }

  /** Values a loop keeps between iterations or makes available after it, each with its type and starting value. */
  function Declarations(declared: { key: 'carry' | 'exports'; title: string; hint: string }) {
    const list = () => (Array.isArray(props.node.config[declared.key]) ? (props.node.config[declared.key] as unknown[]).filter(record) : [])
    const prefix = declared.key === 'carry' ? 'carry' : 'export'
    const used = (name: string) => props.graph.edges.some(e => e.kind === 'data' && ((e.source === props.node.id && e.sourcePort === `${prefix}:${name}`) || e.targetPort === `${prefix}:${name}`))
    const literalTypes = () => Object.keys(props.catalog.types).filter(t => resolveTypeSpec(t, props.catalog)?.literalKind)
    const [name, setName] = createSignal('')
    const [type, setType] = createSignal('number')
    const [error, setError] = createSignal('')
    const update = (index: number, patch: Record<string, unknown>) =>
      set(declared.key, list().map((item, i) => (i === index ? { ...item, ...patch } : item)))
    const add = () => {
      const value = name().trim()
      if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(value)) return setError('Use letters, digits and underscores, not starting with a digit.')
      if (list().some(item => item.name === value)) return setError('That name is already used.')
      setError('')
      setName('')
      set(declared.key, [...list(), { name: value, type: type(), initial: { kind: 'unset' } }])
    }
    return (
      <section class="hs-insp-section" aria-label={declared.title} data-input-id={declared.key}>
        <h3 class="hs-insp-heading">{declared.title}</h3>
        <small class="hs-field-hint">{declared.hint}</small>
        <For each={list()}>
          {(item, index) => {
            const itemName = String(item.name ?? '')
            const itemType = typeof item.type === 'string' ? item.type : 'object'
            return (
              <div class="hs-insp-declaration">
                <div class="hs-insp-field__row">
                  <code>{itemName}</code>
                  <TypeChip text={typeLabel(itemType, props.catalog)} color={typeColor(itemType, props.catalog)} />
                  <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove ${itemName}`} disabled={used(itemName)}
                    title={used(itemName) ? 'Remove its connections on the canvas first' : undefined}
                    onClick={() => set(declared.key, list().filter((_, i) => i !== index()))}>
                    <Glyph paths={closeIcon} />
                  </button>
                </div>
                <InputEditor variant="panel" label={`${itemName} starts at`} catalog={props.catalog}
                  input={{ types: [itemType], required: true, description: 'Its value before the first iteration.' }}
                  value={record(item.initial) ? (item.initial as Operand) : undefined} onChange={next => update(index(), { initial: next })} />
              </div>
            )
          }}
        </For>
        <div class="hs-insp-declare">
          <input class="hs-input" aria-label={`New ${declared.title.toLowerCase()} name`} placeholder="Name" value={name()} onInput={e => setName(e.currentTarget.value)}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add() } }} />
          <select class="hs-input" aria-label={`New ${declared.title.toLowerCase()} type`} onChange={e => setType(e.currentTarget.value)}>
            <For each={literalTypes()}>{t => <option value={t} selected={t === type()}>{typeLabel(t, props.catalog)}</option>}</For>
          </select>
          <button type="button" class="hs-button hs-button--secondary" onClick={add}>Add</button>
        </div>
        <Show when={error()}><small class="hs-error" role="alert">{error()}</small></Show>
      </section>
    )
  }
}
