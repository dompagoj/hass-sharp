import { createMemo, createUniqueId, Show, type Component, type JSX } from 'solid-js'
import { dataInputs, dataOutputs, definitionEntities, definitionEntity, definitionSettings, entityChoices, entityCommandLabel, entityCommands, entityTypeLabel, isAssignable, withEntityCommand, words, type GraphNode, type VisualCatalogSnapshot } from '../visual/contract'
import { graphTypes } from '../visual/connectionIndex'
import { entityStateContext } from '../visual/entityStateContext'
import { removeEdge, updateNode, type VisualGraph } from '../visual/graph'
import { Errors, type FieldRow } from '../visual/InputRow'
import { typeLabel } from '../visual/typeLabels'
import { stateField } from '../visual/widgets'
import { SearchSelect, type SearchOption } from '../visual/widgets/SearchSelect'
import { definitionEntityKinds } from './model'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown) => (typeof value === 'string' ? value : '')
const nodeSpec = (kind: string, catalog: VisualCatalogSnapshot) => (Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined)

/** A definition's entity as people read it: its name, else its key. */
const entityName = (entity: Record<string, unknown>) => text(entity.name) || text(entity.key)

/**
 * The graph with the step's entity set to `key`, as one change. A When commanded's command follows the entity (ruling
 * R12). A State or Set state takes its inputs from the entity's type: the values and wires of inputs the new type does
 * not have, or whose new type does not take them, are removed and named in the notice, as applying a Custom C# node's
 * code does. A wire that already went nowhere stays, for the validator to name.
 */
export function chooseEntity(graph: VisualGraph, nodeId: string, key: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || node.config.entity === key) return { graph }
  // The definition is the graph's and does not change here, so one lifted catalog serves before and after.
  const types = graphTypes(graph, catalog)
  const config = withEntityCommand(node.kind, { ...node.config, entity: key }, types)
  const fitted = refit(graph, node, config, types, types)
  return { graph: fitted.graph, notice: refitNotice(fitted.removed, fitted.cleared) }
}

/**
 * The graph with the definition's entity `key` of the type `domain`, as one change: the options its new type does not
 * take go, and every step naming it is fitted to the new type as {@link chooseEntity} fits a step to a new entity, with
 * the same notice.
 */
export function retypeEntity(graph: VisualGraph, key: string, domain: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; notice?: string } {
  const definition = record(graph.definition) ? (graph.definition as unknown as { entities: Record<string, unknown>[] }) : undefined
  const entity = definition?.entities.find(e => e.key === key)
  if (!definition || !entity || entity.domain === domain) return { graph }
  const spec = catalog.entityDomains && Object.hasOwn(catalog.entityDomains, domain) ? catalog.entityDomains[domain] : undefined
  const { options, ...rest } = entity
  const kept = record(options) ? Object.fromEntries(Object.entries(options).filter(([option]) => !!spec && Object.hasOwn(spec.options, option))) : {}
  const retyped = { ...rest, domain, ...(Object.keys(kept).length ? { options: kept } : {}) }
  let next: VisualGraph = { ...graph, definition: { ...definition, entities: definition.entities.map(e => (e === entity ? retyped : e)) } as unknown as VisualGraph['definition'] }
  const before = graphTypes(graph, catalog), after = graphTypes(next, catalog)
  const removed: string[] = [], cleared: string[] = []
  for (const node of graph.nodes) {
    if (!definitionEntityKinds.includes(node.kind) || node.config.entity !== key) continue
    const fitted = refit(next, node, withEntityCommand(node.kind, { ...node.config }, after), before, after)
    next = fitted.graph
    removed.push(...fitted.removed)
    cleared.push(...fitted.cleared)
  }
  return { graph: next, notice: refitNotice(removed, cleared) }
}

/**
 * `graph` with `node` given `config`, its entity's state inputs as `after` types them where `before` typed them: a
 * literal value or a wire into an input the step no longer has, or one that no longer takes it, is removed (named in
 * `cleared` and `removed`).
 */
function refit(graph: VisualGraph, node: GraphNode, given: GraphNode['config'], before: VisualCatalogSnapshot, after: VisualCatalogSnapshot) {
  const config = { ...given }
  const spec = nodeSpec(node.kind, after)
  const removed: string[] = [], cleared: string[] = []
  if (spec?.dynamicInputs !== 'entity') return { graph: updateNode(graph, node.id, { config }), removed, cleared }
  const was = dataInputs(node, undefined, before), now = dataInputs({ ...node, config }, undefined, after)
  const fits = (type: unknown, accepted: string[]) => typeof type === 'string' && accepted.some(t => isAssignable(type, t, after))
  const label = (port: string) => words.portLabel(was[port], port)

  // Values: a state input the new type also has keeps a value of a type it takes.
  for (const port of Object.keys(was)) {
    if (Object.hasOwn(spec.config, port) || Object.hasOwn(spec.inputs, port) || !Object.hasOwn(config, port)) continue
    const value = config[port], input = Object.hasOwn(now, port) ? now[port] : undefined
    const literal = record(value) && value.kind === 'literal'
    if (input && (!literal || fits(value.type, input.types))) continue
    delete config[port]
    if (literal) cleared.push(label(port))
  }
  let next = updateNode(graph, node.id, { config })

  // Wires into an input the step had: removed when the input is gone or no longer takes the value.
  for (const edge of graph.edges) {
    if (edge.kind !== 'data' || edge.target !== node.id || !Object.hasOwn(was, edge.targetPort)) continue
    const source = graph.nodes.find(n => n.id === edge.source)
    const output = source ? dataOutputs(source, after)[edge.sourcePort] : undefined
    const input = Object.hasOwn(now, edge.targetPort) ? now[edge.targetPort] : undefined
    if (input && (!output || fits(output.type, input.types))) continue
    next = removeEdge(next, edge.id, after)
    removed.push(label(edge.targetPort))
  }
  return { graph: next, removed, cleared }
}

/** What fitting steps removed, in one notice; none when nothing was. */
function refitNotice(removed: string[], cleared: string[]): string | undefined {
  const list = (names: string[]) => [...new Set(names)].join(', ')
  const notes = [
    ...(removed.length ? [`Removed ${removed.length} ${removed.length === 1 ? 'connection that no longer fits' : 'connections that no longer fit'}: ${list(removed)}`] : []),
    ...(cleared.length ? [`Cleared ${cleared.length === 1 ? 'a value that no longer fits' : `${cleared.length} values that no longer fit`}: ${list(cleared)}`] : []),
  ]
  return notes.length ? `${notes.join('. ')}.` : undefined
}

/** What a step's picker section reads and writes, through accessors so it survives edits of the step. */
export interface StepPickerContext {
  node(): GraphNode
  graph(): VisualGraph
  /** The catalog with the graph's definition lifted into it (`graphTypes`). */
  catalog(): VisualCatalogSnapshot
  errorsFor(key: string): string[]
  onChange(config: GraphNode['config']): void
  onGraphChange(graph: VisualGraph): void
  onNotice?(text: string): void
}

/** One labelled picker of a step's config, with its hint and problems, as the inspector's other settings are laid out. */
function PickerField(props: {
  name: string
  label: string
  options: SearchOption[]
  value: string
  placeholder: string
  hint?: string
  invalid?: boolean
  disabled?: boolean
  errors: string[]
  onSelect(value: string): void
}): JSX.Element {
  const id = createUniqueId(), hintId = `${id}-hint`
  const invalid = () => props.errors.length > 0 || !!props.invalid
  return (
    <div class="hs-insp-field" data-input-id={props.name} aria-invalid={invalid() || undefined}>
      <label class="hs-insp-field__label" for={id}>{props.label}</label>
      <SearchSelect id={id} label={props.label} options={props.options} value={props.value || undefined} placeholder={props.placeholder}
        describedBy={props.hint ? hintId : undefined} invalid={invalid()} disabled={props.disabled}
        onSelect={value => { if (value !== props.value) props.onSelect(value) }} />
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
      <Errors list={props.errors} />
    </div>
  )
}

const configLabel = (ctx: StepPickerContext, key: string, fallback: string) => text(nodeSpec(ctx.node().kind, ctx.catalog())?.config[key]?.label) || fallback
const configHint = (ctx: StepPickerContext, key: string) => text(nodeSpec(ctx.node().kind, ctx.catalog())?.config[key]?.description) || undefined

/**
 * The entity of a State, When commanded, Set state or Set available: the definition's entities by name, with their
 * type (a When commanded's only those that take commands). A key the definition does not have stays chosen, marked
 * missing, so the validator's problem about it can be acted on.
 */
function EntityPicker(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const current = () => text(ctx.node().config.entity)
  const choices = createMemo(() => entityChoices(ctx.catalog(), ctx.node().kind))
  const option = (entity: Record<string, unknown>, detail?: string): SearchOption =>
    ({ value: text(entity.key), label: entityName(entity), detail: detail ?? entityTypeLabel(ctx.catalog(), text(entity.domain)) })
  const known = () => definitionEntity(ctx.catalog(), current())
  const listed = () => choices().some(e => e.key === current())
  const missing = () => current() !== '' && !known()
  const options = createMemo((): SearchOption[] => [
    ...(missing() ? [{ value: current(), label: `Missing entity: ${current()}` }] : []),
    // An entity the definition has that this step cannot name (one that takes no commands) is still shown as chosen.
    ...(current() !== '' && known() && !listed() ? [option(known()!, `${entityTypeLabel(ctx.catalog(), text(known()!.domain))} · takes no commands`)] : []),
    ...choices().map(e => option(e)),
  ])
  const hint = () => !definitionEntities(ctx.catalog()).length ? 'This definition has no entities yet.'
    : !choices().length ? 'None of this definition\'s entities takes commands.' : configHint(ctx, 'entity')
  return (
    <PickerField name="entity" label={configLabel(ctx, 'entity', 'Entity')} options={options()} value={current()} placeholder="Choose an entity"
      hint={hint()} invalid={missing()} disabled={!options().length} errors={ctx.errorsFor('entity')}
      onSelect={key => {
        const result = chooseEntity(ctx.graph(), ctx.node().id, key, ctx.catalog())
        ctx.onGraphChange(result.graph)
        if (result.notice) ctx.onNotice?.(result.notice)
      }} />
  )
}

/** A When commanded's command: the commands of its entity's type, by label; disabled until an entity of a known type is chosen. */
function CommandPicker(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const entity = () => text(ctx.node().config.entity)
  const current = () => text(ctx.node().config.command)
  const commands = createMemo(() => entityCommands(ctx.catalog(), entity()))
  const label = (command: string) => entityCommandLabel(ctx.catalog(), text(definitionEntity(ctx.catalog(), entity())?.domain), command)
  const options = createMemo((): SearchOption[] => [
    ...(current() !== '' && commands().length && !commands().includes(current()) ? [{ value: current(), label: `Missing command: ${current()}` }] : []),
    ...commands().map(command => ({ value: command, label: label(command) })),
  ])
  const ready = () => commands().length > 0
  return (
    <PickerField name="command" label={configLabel(ctx, 'command', 'Command')} options={options()} value={current()}
      placeholder={ready() ? 'Choose a command' : current() || 'Not set'} hint={ready() ? configHint(ctx, 'command') : 'Choose an entity first.'}
      invalid={ready() && current() !== '' && !commands().includes(current())} disabled={!ready()} errors={ctx.errorsFor('command')}
      onSelect={command => ctx.onChange({ ...ctx.node().config, command })} />
  )
}

/** A Setting's setting: the definition's settings by label, with their type. Never a value: a setting may be a secret. */
function SettingPicker(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const current = () => text(ctx.node().config.key)
  const settings = createMemo(() => definitionSettings(ctx.catalog()))
  const missing = () => current() !== '' && !settings().some(s => s.key === current())
  const options = createMemo((): SearchOption[] => [
    ...(missing() ? [{ value: current(), label: `Missing setting: ${current()}` }] : []),
    ...settings().map(s => ({ value: text(s.key), label: text(s.label) || text(s.key), ...(text(s.type) ? { detail: typeLabel(text(s.type), ctx.catalog()) } : {}) })),
  ])
  return (
    <PickerField name="key" label={configLabel(ctx, 'key', 'Setting')} options={options()} value={current()} placeholder="Choose a setting"
      hint={settings().length ? configHint(ctx, 'key') : 'This definition has no settings yet.'} invalid={missing()} disabled={!options().length}
      errors={ctx.errorsFor('key')} onSelect={key => ctx.onChange({ ...ctx.node().config, key })} />
  )
}

const EntitySection: Component<{ ctx: StepPickerContext }> = props => <EntityPicker ctx={props.ctx} />
const CommandSection: Component<{ ctx: StepPickerContext }> = props => <><EntityPicker ctx={props.ctx} /><CommandPicker ctx={props.ctx} /></>
const SettingSection: Component<{ ctx: StepPickerContext }> = props => <SettingPicker ctx={props.ctx} />

/**
 * The pickers of an entity definition's steps, by kind: the config keys each section edits (which the inspector's
 * generic settings leave out) and the section.
 */
export const stepPickers: Record<string, { keys: string[]; Section: Component<{ ctx: StepPickerContext }> }> = {
  entity_state: { keys: ['entity'], Section: EntitySection },
  entity_command: { keys: ['entity', 'command'], Section: CommandSection },
  set_entity_state: { keys: ['entity'], Section: EntitySection },
  set_entity_available: { keys: ['entity'], Section: EntitySection },
  setting: { keys: ['key'], Section: SettingSection },
}

/**
 * How the inspector edits some inputs of these steps, by kind and port: a Has state's state offers the states of the
 * Home Assistant entity it checks, as a State condition's state does.
 */
export const stepInputRows: Record<string, Record<string, (node: GraphNode, graph: VisualGraph, catalog: VisualCatalogSnapshot) => Pick<FieldRow, 'widget' | 'context'>>> = {
  entity_is: {
    state: (node, graph, catalog) => {
      const states = entityStateContext(graph, node, 'entity', catalog)
      return { widget: { ...stateField, fallbackStateOptions: states.fallbackStateOptions }, context: { targetEntityIds: states.entityIds } }
    },
  },
}
