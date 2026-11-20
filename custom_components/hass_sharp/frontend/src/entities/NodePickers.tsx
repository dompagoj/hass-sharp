import { createMemo, createUniqueId, Show, type Component, type JSX } from 'solid-js'
import { cameraKinds, definitionEntities, definitionEntity, definitionSettings, entityChoices, entityDomain, entityCommandLabel, entityCommands, entityTypeLabel, firstStateValue, booleanStateValue, withEntityCommand, withFirstStateValue, type GraphNode, type Operand, type VisualCatalogSnapshot } from '../visual/contract'
import { graphTypes } from '../visual/connectionIndex'
import { entityStateContext } from '../visual/entityStateContext'
import type { VisualGraph } from '../visual/graph'
import { Errors, type FieldRow } from '../visual/InputRow'
import { typeLabel } from '../visual/typeLabels'
import { stateField } from '../visual/widgets'
import { SearchSelect, type SearchOption } from '../visual/widgets/SearchSelect'
import type { SavedEntities } from '../visual/adapter'
import { EntityNodeSection } from './EntityNodeSection'
import { refit, refitNotice } from './refit'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown) => (typeof value === 'string' ? value : '')
const nodeSpec = (kind: string, catalog: VisualCatalogSnapshot) => (Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined)

/** The steps that name an entity for its state values. */
const stateKinds: readonly string[] = ['entity_state', 'set_entity_state']
/** Whether the catalog's row of a type has state values (a button has none); true for a type it does not have. */
function hasState(catalog: VisualCatalogSnapshot, domain: string): boolean {
  const domains = catalog.entityDomains
  const row = domains && Object.hasOwn(domains, domain) ? domains[domain] : undefined
  return !row || (record(row.state) && Object.keys(row.state).length > 0)
}

/** A definition's entity as people read it: its name, else its key. */
const entityName = (entity: Record<string, unknown>) => text(entity.name) || text(entity.key)

/**
 * Whether a State's or Set state's step sets (or reads) a value of its entity: a value given in its config, or a wire
 * into it. Its entity and its own settings are not values.
 */
export function setsStateValues(graph: VisualGraph, node: GraphNode, catalog: VisualCatalogSnapshot): boolean {
  const spec = nodeSpec(node.kind, catalog)
  const given = Object.entries(node.config).some(([port, value]) => port !== 'entity' && !Object.hasOwn(spec?.config ?? {}, port)
    && !Object.hasOwn(spec?.inputs ?? {}, port) && !(record(value) && value.kind === 'unset'))
  return given || graph.edges.some(edge => edge.kind === 'data' && edge.target === node.id)
}

/**
 * The graph with the step's entity set to `key`, as one change. A When commanded's command follows the entity (ruling
 * R12); a Set state that set no value yet sets its new entity's first value to true when that is true or false (ruling
 * R13). A State or Set state takes its inputs from the entity's type: the values and wires of inputs the new type does
 * not have, or whose new type does not take them, are removed and named in the notice, as applying a Custom C# node's
 * code does; so are a When commanded's wires out of an output its new command does not have. A wire that already went
 * nowhere stays, for the validator to name.
 */
export function chooseEntity(graph: VisualGraph, nodeId: string, key: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || node.config.entity === key) return { graph }
  // The definition is the graph's and does not change here, so one lifted catalog serves before and after.
  const types = graphTypes(graph, catalog)
  // A Set state that set no value yet sets its new entity's On (or Closed) to true in the same change (ruling R13).
  const config = withFirstStateValue(node.kind, withEntityCommand(node.kind, { ...node.config, entity: key }, types), types, setsStateValues(graph, node, types))
  const fitted = refit(graph, node, config, types, types)
  return { graph: fitted.graph, notice: refitNotice(fitted) }
}

/**
 * The graph with the When commanded `nodeId` handling `command`, as one change: the wires out of an output the new
 * command does not have (Turn off gives no brightness) are removed and named in the notice.
 */
export function chooseCommand(graph: VisualGraph, nodeId: string, command: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || node.config.command === command) return { graph }
  const types = graphTypes(graph, catalog)
  const fitted = refit(graph, node, { ...node.config, command }, types, types)
  return { graph: fitted.graph, notice: refitNotice(fitted) }
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
  /** The saved document's entities as Home Assistant has them; undefined before the first save. */
  saved?: () => SavedEntities | undefined
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
 * The entity of a State, When commanded, Set state or Set available, and the camera of a Show frame or When a frame is
 * needed: the definition's entities by name, with their type (a When commanded's only those that take commands, a
 * camera step's only its cameras). A key the definition does not have stays chosen, marked missing, so the validator's
 * problem about it can be acted on.
 */
function EntityPicker(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const current = () => text(ctx.node().config.entity)
  const camera = () => cameraKinds.includes(ctx.node().kind)
  const choices = createMemo(() => entityChoices(ctx.catalog(), ctx.node().kind))
  // A State or Set state cannot name an entity whose type has no state (a button): offered, and marked so.
  const stateless = (entity: Record<string, unknown>) => stateKinds.includes(ctx.node().kind) && !hasState(ctx.catalog(), text(entity.domain))
  const option = (entity: Record<string, unknown>, detail?: string): SearchOption => {
    const type = entityTypeLabel(ctx.catalog(), text(entity.domain))
    return { value: text(entity.key), label: entityName(entity), detail: detail ?? (stateless(entity) ? `${type} · has no state` : type) }
  }
  const known = () => definitionEntity(ctx.catalog(), current())
  const listed = () => choices().some(e => e.key === current())
  const missing = () => current() !== '' && !known()
  const options = createMemo((): SearchOption[] => [
    ...(missing() ? [{ value: current(), label: `Missing entity: ${current()}` }] : []),
    // An entity the definition has that this step cannot name (one that takes no commands, or is not a camera) is still
    // shown as chosen.
    ...(current() !== '' && known() && !listed() ? [option(known()!, `${entityTypeLabel(ctx.catalog(), text(known()!.domain))} · ${camera() ? 'not a camera' : 'takes no commands'}`)] : []),
    ...choices().map(e => option(e)),
  ])
  const hint = () => camera() && !choices().length ? 'Add a camera to this definition first.'
    : !definitionEntities(ctx.catalog()).length ? "None of this definition's steps is an Entity node. Add an Entity node from the step library first."
    : !choices().length ? "None of this definition's entities takes commands. Give one a type that does, such as a switch, on its Entity node." : configHint(ctx, 'entity')
  return (
    <PickerField name="entity" label={configLabel(ctx, 'entity', 'Entity')} options={options()} value={current()} placeholder={camera() ? 'Choose a camera' : 'Choose an entity'}
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
      placeholder={ready() ? 'Choose a command' : current() || 'Not set'} hint={ready() ? configHint(ctx, 'command') : entityChoices(ctx.catalog(), ctx.node().kind).length ? 'Choose an entity first.' : 'Give an entity a type that takes commands first.'}
      invalid={ready() && current() !== '' && !commands().includes(current())} disabled={!ready()} errors={ctx.errorsFor('command')}
      onSelect={command => {
        const result = chooseCommand(ctx.graph(), ctx.node().id, command, ctx.catalog())
        ctx.onGraphChange(result.graph)
        if (result.notice) ctx.onNotice?.(result.notice)
      }} />
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
  camera_frame: { keys: ['entity'], Section: EntitySection },
  show_frame: { keys: ['entity'], Section: EntitySection },
  setting: { keys: ['key'], Section: SettingSection },
  // An Entity node: its whole entity but its name (the header's) and its state values (its inputs).
  entity: { keys: ['key', 'domain', 'name', 'icon', 'deviceClass', 'entityCategory', 'enabledByDefault', 'options', 'history', 'arguments'], Section: EntityNodeSection },
}

/** Under a Set state's value, when its type has more than one: what leaving it empty does. */
const keptHint = 'Leave empty to keep it as it is.'
/** Under a State's value its type does not need. */
const optionalHint = 'Optional.'
/** What "Set value" gives a value that is true or false: the switch then drawn is what is saved. */
const setTrue: Operand = { kind: 'literal', type: 'boolean', value: true }

/**
 * How the inspector shows a state value of a State or Set state, by its entity's row; undefined for any other input.
 * What is drawn is what is saved (ruling R13):
 * - A Set state that sets no value yet shows the editor of the value it is for ({@link firstStateValue}) while it has
 *   none (`ready`), when that is a number, a text or an option, where an empty field reads as empty. A value that is
 *   true or false is never drawn as a switch while it has no value: it shows "Not set", and "Set value" sets it to true
 *   (`setValue`). Every value of a type with more than one says that left empty it is kept as it is.
 * - A State's values its row marks optional say they are optional.
 */
export function stateValueRow(node: GraphNode, port: string, catalog: VisualCatalogSnapshot, graph: VisualGraph):
  { ready?: boolean; description?: string; setValue?: Operand } | undefined {
  if (!stateKinds.includes(node.kind)) return
  const state = entityDomain(catalog, text(node.config.entity))?.state
  if (!record(state) || !Object.hasOwn(state, port)) return
  const value = state[port], toggle = booleanStateValue(value)
  const set = toggle ? { setValue: setTrue } : {}
  if (node.kind === 'entity_state') return { ...set, ...(value.required === false ? { description: optionalHint } : {}) }
  const ready = !toggle && port === firstStateValue(state) && !setsStateValues(graph, node, catalog)
  return { ...set, ...(ready ? { ready } : {}), ...(Object.keys(state).length > 1 ? { description: keptHint } : {}) }
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
