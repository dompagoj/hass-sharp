import { addGraphNode, declaredEntities, listedEntities, nodeCatalog, withGraphTypes, words, type Graph, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import type { GraphOutline, HomeAutomation, VisualDiagnostic, VisualDocument, VisualPreview } from '../src/visual/api'
import type { DefinitionBody, DefinitionEntity, DerivedDevice, EntityDocument, RegisteredEntity } from '../src/entities/model'
import { updateNode, type VisualGraph } from '../src/visual/graph'

/** A value typed in on a step's input. */
export const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
/**
 * An entity definition's editor graph with no steps and no settings, as `editorGraph` gives one: its entities are the
 * Entity nodes added to it.
 */
export const empty = (name = 'Nodes'): VisualGraph =>
  ({ version: 5, name, nodes: [], edges: [], definition: { settings: [] } as never })
/**
 * The graph with a `kind` step `id` added as the editor adds one (an Entity node named by its type with a free key, a
 * step prefilled with the definition's only entity), then `config` merged over its config and, when given, placed at
 * `position`. An Entity node is `add(graph, 'entity', id, { key, domain, name })`, its state values under `arguments`.
 */
export const add = (graph: Graph, kind: string, id: string, config: Record<string, unknown> = {}, position?: { x: number; y: number }) => {
  const added = addGraphNode(graph, kind, id, nodeCatalog)
  return updateNode(added, id, { config: { ...added.nodes.at(-1)!.config, ...config }, ...(position ? { position } : {}) })
}

/** The server's outline of a graph (VisualApi.Outline): node kinds, positions and labels, trigger ids and configs, execution edges. */
export function outlineOf(graph: VisualGraph): GraphOutline {
  const index = new Map(graph.nodes.map((node, i) => [node.id, i]))
  return {
    name: graph.name,
    nodes: graph.nodes.map(node => ({
      kind: node.kind, x: Math.round(node.position?.x ?? 0), y: Math.round(node.position?.y ?? 0),
      ...(node.label ? { label: node.label } : {}),
      ...(node.kind === 'trigger' ? { id: node.id, config: structuredClone(node.config) } : {}),
    })),
    edges: graph.edges.flatMap(edge => (edge.kind !== 'data' && index.has(edge.source) && index.has(edge.target) ? [[index.get(edge.source)!, index.get(edge.target)!] as [number, number]] : [])),
  }
}
const clrTypes: Record<string, string> = { string: 'global::System.String', int: 'global::System.Int32', double: 'global::System.Double', bool: 'global::System.Boolean', 'string?': 'global::System.String?', 'double?': 'global::System.Double?' }
const inWords = (name: string) => { const words = name.replace(/_/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase().trim(); return words.charAt(0).toUpperCase() + words.slice(1) }
/**
 * A fixture's `custom_preview`: no compiler, only enough to try the code editor. The one `[Node("Title")]` method's
 * parameters (`string`, `int`, `double`, `bool`; `NodeContext` and `CancellationToken` are supplied by the run) and
 * its return value (one value, a named tuple, or `void`/`Task`) give the pins; `Pure = true` a value node. Code with
 * `!!` in it has an error there. For a C# entity or C# device (`kind`), the code's one public class gives the class
 * signature instead: see {@link fixtureClassPreview}.
 */
export function fixtureCustomPreview(nodeId: string, source: string, kind = 'custom') {
  if (kind === 'csharp_entity' || kind === 'csharp_device') return fixtureClassPreview(nodeId, source, kind)
  const bad = source.indexOf('!!')
  if (bad >= 0) {
    const lines = source.slice(0, bad).split('\n')
    const line = lines.length, column = lines.at(-1)!.length + 1
    return { valid: false, signature: null, diagnostics: [{ sourceId: nodeId, startLine: line, startColumn: column, endLine: line, endColumn: column + 2, severity: 3, message: "Unexpected '!!' (fixture)" }] }
  }
  const method = /\[Node\("([^"]*)"([^\]]*)\)\]\s*public\s+static\s+(?:async\s+)?([\w<>?,\s()]+?)\s+(\w+)\s*\(([^)]*)\)/.exec(source)
  if (!method) return { valid: false, signature: null, diagnostics: [{ sourceId: nodeId, startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, severity: 3, message: 'Mark exactly one public static method with [Node] (fixture)' }] }
  const [, title, options, returns, , parameters] = method
  const summary = /<summary>([\s\S]*?)<\/summary>/.exec(source.slice(0, method.index))?.[1].trim() ?? ''
  const inputs: Record<string, unknown> = {}
  for (const parameter of parameters.split(',').map(p => p.trim()).filter(Boolean)) {
    const [, type, name, fallback] = /^(?:\[Port\("[^"]*"\)\]\s*)?([\w?]+)\s+(\w+)(?:\s*=\s*(.+))?$/.exec(parameter) ?? []
    if (!type || type === 'NodeContext' || type === 'CancellationToken') continue
    const typeId = clrTypes[type] ?? clrTypes.string
    inputs[name] = { types: [typeId], required: fallback === undefined && !type.endsWith('?'), label: /\[Port\("([^"]*)"\)\]/.exec(parameter)?.[1] ?? inWords(name), description: '', nullable: type.endsWith('?'), hasDefaultValue: fallback !== undefined }
  }
  const outputs: Record<string, unknown> = {}
  const result = returns.replace(/^(?:Task|ValueTask)<(.+)>$/, '$1').trim()
  const tuple = /^\((.+)\)$/.exec(result)
  if (tuple) for (const element of tuple[1].split(',')) {
    const [type, name] = element.trim().split(/\s+/)
    outputs[name.charAt(0).toLowerCase() + name.slice(1)] = { type: clrTypes[type] ?? clrTypes.string, label: inWords(name), description: '' }
  } else if (!['void', 'Task', 'ValueTask'].includes(result)) outputs.result = { type: clrTypes[result] ?? clrTypes.string, label: 'Result', description: '' }
  return { valid: true, diagnostics: [], signature: { title, description: summary, pure: /Pure\s*=\s*true/.test(options), inputs, outputs, types: {} } }
}
const settingTypes: Record<string, string> = { string: 'string', bool: 'boolean', int: 'integer', long: 'integer', double: 'number' }
/**
 * The fixture's `custom_preview` of a C# entity's or C# device's class: no compiler. The one `public class Name : Base`
 * is titled by its name in words and described by the `<summary>` before it; a C# entity's domain is its base's name
 * after `Sharp` in snake case (`SharpBinarySensor` is `binary_sensor`), a C# device's null. Each
 * `[Setting("Label"…)] public T Name` is a setting keyed `Name` (`string`, `bool`, `int`, `long`, `double`, else an enum
 * of the `enum T { … }` in the code), secret with `Secret = true`. Like the real check it does not refuse a setting
 * named `device`. Code with `!!` in it has an error there.
 */
export function fixtureClassPreview(nodeId: string, source: string, kind: 'csharp_entity' | 'csharp_device') {
  const bad = source.indexOf('!!')
  const at = (offset: number) => { const lines = source.slice(0, offset).split('\n'); return { line: lines.length, column: lines.at(-1)!.length + 1 } }
  if (bad >= 0) {
    const { line, column } = at(bad)
    return { valid: false, signature: null, diagnostics: [{ sourceId: nodeId, startLine: line, startColumn: column, endLine: line, endColumn: column + 2, severity: 3, message: "Unexpected '!!' (fixture)" }] }
  }
  const classes = [...source.matchAll(/public\s+class\s+(\w+)\s*:\s*(\w+)/g)]
  if (classes.length !== 1) {
    const message = kind === 'csharp_entity' ? 'Write one public class that derives from an entity class, such as SharpSwitch: it becomes this entity' : 'Write one public class that derives from SharpDevice: it becomes this device'
    return { valid: false, signature: null, diagnostics: [{ sourceId: nodeId, startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, severity: 3, message }] }
  }
  const [declaration, name, base] = classes[0]
  const summary = /<summary>([\s\S]*?)<\/summary>\s*$/.exec(source.slice(0, classes[0].index).trimEnd())?.[1].trim() ?? ''
  const domain = kind === 'csharp_entity' ? base.replace(/^Sharp/, '').replace(/([a-z0-9])([A-Z])/g, '$1_$2').toLowerCase() : null
  const body = source.slice(classes[0].index! + declaration.length)
  const settings = [...body.matchAll(/\[Setting\("([^"]*)"([^\]]*)\)\]\s*public\s+([\w?]+)\s+(\w+)/g)].map(([, label, options, type, key]) => {
    const scalar = settingTypes[type.replace(/\?$/, '')]
    const members = scalar ? undefined : new RegExp(`enum\\s+${type.replace(/\?$/, '')}\\s*\\{([^}]*)\\}`).exec(source)?.[1].split(',').map(m => m.trim()).filter(Boolean)
    return { key, label, description: '', type: scalar ?? 'enum', secret: /Secret\s*=\s*true/.test(options), ...(members ? { options: members } : {}) }
  })
  return { valid: true, diagnostics: [], signature: { title: inWords(name), description: summary, domain, settings } }
}

/** The fixture's `EntityStateSnapshot`, as the server rebuilds a HassSharp type from its metadata. */
export const fixtureSnapshotDefinition = `namespace HassSharp;

/// <summary>A detached Home Assistant entity state: what a step reads, kept as it was (fixture).</summary>
public sealed record EntityStateSnapshot
{
    public EntityStateSnapshot(string EntityId, string State, JsonObject Attributes, DateTimeOffset LastChanged, DateTimeOffset LastUpdated);
    public string EntityId { get; init; }
    public string State { get; init; }
    public JsonObject Attributes { get; init; }
    public DateTimeOffset LastChanged { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public double? NumericState { get; }
}`

/**
 * A fixture's `type_definition`: the declaration of the type's last name (with its doc comments) in the code of the
 * Custom C# node the request names, or else in a saved library; `EntityStateSnapshot` from HassSharp. No compiler: a
 * declaration ends at its first `;` or at the brace that closes it.
 */
export function fixtureTypeDefinition(request: { type: string; nodeId?: string; source?: string }, libraries: Iterable<{ id: string; name: string; source: string }>) {
  const id = request.type.replace(/\?$/, '')
  if (id === 'global::HassSharp.EntityStateSnapshot') return { name: 'EntityStateSnapshot', kind: 'metadata', text: fixtureSnapshotDefinition, origin: { assembly: 'HassSharp' } }
  const name = id.replace(/^global::/, '').replace(/^NodeLib\.(?:Custom_[^.]+\.)?/, '')
  if (!/^[A-Za-z_]\w*$/.test(name)) return undefined
  const find = (source: string) => {
    const header = new RegExp(`^([ \\t]*(?:\\/\\/.*\\n[ \\t]*)*)(?:(?:public|internal|sealed|static|abstract|partial|readonly)\\s+)*(?:record\\s+struct|record|class|struct|interface|enum)\\s+${name}\\b`, 'm').exec(source)
    if (!header) return
    let end = header.index + header[0].length, depth = 0
    for (; end < source.length; end++) {
      const c = source[end]
      if (c === ';' && depth === 0) { end++; break }
      if (c === '{') depth++
      if (c === '}' && --depth === 0) { end++; break }
    }
    return { text: source.slice(header.index, end), line: source.slice(0, header.index).split('\n').length }
  }
  if (request.nodeId !== undefined) {
    const found = find(request.source ?? '')
    return found && { name, kind: 'source', text: found.text, origin: { nodeId: request.nodeId }, line: found.line }
  }
  for (const library of libraries) {
    const found = find(library.source)
    if (found) return { name, kind: 'source', text: found.text, origin: { library: library.id, file: library.name }, line: found.line }
  }
}

/** How a fixture automation has been running, for the automations page; nothing when it never ran. */
export type FixtureRuns = Partial<Pick<HomeAutomation, 'recentRuns' | 'runsToday' | 'failedLastDay' | 'lastRunUtc' | 'problemCount'>>

/**
 * How a fixture definition runs once loaded (`controls.activation`): its host's status (`DefinitionHost.Status`:
 * `running`, `setup`, `failed`, `stopped`), or no host as its script did not load (`notLoaded`).
 */
export type FixtureActivation = 'running' | 'setup' | 'failed' | 'stopped' | 'notLoaded'

/**
 * The in-memory `hass_sharp/entities` backend of {@link visualFixture} (`fixture.entities`), answering as
 * `EntityApi.EntityRequest` and the Python route do, in the `{version, ok, result | error}` envelope.
 *
 * - `documents`: the stored definitions by id, as the store keeps them: secrets in clear, `logic` as `{kind, type, graph}`,
 *   the graph without `definition`, at `version` 3, its `entities` derived as `EntityDefinitionStore.Save` stores them
 *   (those its Entity nodes declare), with the run fields an answer has (`automationId` `entity:<id>`, `status`, …).
 *   Every answer masks its `secret` settings.
 * - `seed(id, body, fields?)`: stores a definition at revision 1, as `controls.activation` says it runs (off with
 *   `{enabled: false}`); `fields` overrides any member of the stored document (`status`, `liveEntities`, …).
 * - With a host that is not stopping (`running`, `setup` while its start runs, `failed` when its start threw; each after
 *   its entities were made), a definition answers `liveEntities`: its entities, each
 *   `{uniqueId: '<id>:<key>', key, domain, name, entityId: '<domain>.<id>_<key>'}`. Each such load also records them as
 *   Home Assistant's registry would (`published`, replaced on the next load), each `disabled` as the entry of its unique
 *   id seeded in `registry` (else recorded before) is, as Home Assistant keeps an entry's `disabled_by`; they are
 *   answered as `registeredEntities` while the definition is off or not loaded too, until it is deleted.
 * - `registry`: registry entries a test seeds by definition id; seeded for an id, they are its `registeredEntities`
 *   instead of the recorded ones.
 * - `runs`: the run summary of the home list (`recentRuns`, `runsToday`, `failedLastDay`, `lastRunUtc`) by definition id.
 * - `requests`: every `request` received, in order (`fixture.calls` keeps the whole messages, as for every route).
 * - `controls.diagnostics`: what `preview` answers for a definition with no document problems: valid unless one is an
 *   error (severity 3), when a save is refused with `validation`; a valid save answers its warnings as `diagnostics`.
 * - `controls.activation`: how a saved, seeded or turned-on definition runs, its status as `EntityApi.EntityJson` maps
 *   its host's: `running`; `setup` (loaded, its start still running: `status: 'setup'`, `active: true`, no error);
 *   `failed` (loaded, its start threw: `status: 'failed'`, `active: true`, `activationError`); `stopped` (loaded, its
 *   host stopping, which counts as none: `status: 'off'`, `active: true`, no live entities, no error); `notLoaded`
 *   (no host: `status: 'failed'` as it could not be prepared, `active: false`, `activationError`; `status: 'off'` when
 *   that error is the backend's `Not active`, as for no recorded reason). `controls.activationError` is the text of
 *   `failed` and `notLoaded`. A definition off by the user is `off`, `active: false`, `activationError: 'Not active'` in
 *   documents and `null` in the save and `set_enabled` answers.
 *
 * The document checks are the real ones a test meets most (codes of `DefinitionDocument`/`DefinitionGenerator`): a name
 * of 1–120 characters (`definition.name`), no list of entities (`definition.entitiesNotAllowed`: at all, even empty or
 * null), no device (`definition.deviceNotAllowed`: one that is not null) and at least one Entity node
 * (`definition.entitiesRequired`),
 * each with options as `DefinitionDocument.Options` checks them (`definition.entityOption`, on the node: one its domain
 * has, of its type, not an empty list, a number Home Assistant takes and within its rule's range, a pattern that is a
 * regular expression, every option the domain needs, a min below the max); with any of them the seeded diagnostics are
 * not answered.
 *
 * Every definition `list`, `read` and `save` answer carries `triggers`, as Python's `definition_triggers` gives them: a
 * graph's Trigger steps `{triggerId, method, manual, runnable}`, `runnable` only while its saved revision runs and is
 * `running` and the step can be started with Run now. `run` (`id`, `triggerId`) answers `{version: 1, runId:
 * 'manual-entity-run'}`, recorded in `runs` of `controls`, or refuses as `EntityApi.DispatchDefinition` does, each
 * `invalid_request` in its words: a definition that is off, one whose saved revision is not running, a step that is not
 * there, one that is not a Trigger, one that cannot be started with Run now.
 *
 * Refusals come in the real order, with the real `invalid_request` texts for a field missing (`id is required`) or of
 * the wrong type (`id must be a string`, `expectedRevision must be a whole number`, `enabled must be true or false`).
 * Every operation with an id refuses one outside `^[a-z0-9][a-z0-9_-]{0,79}$` ("Invalid definition ID") after reading
 * its fields, and before `not_found` (`read`, `set_enabled`, `delete` of an unknown id) and `conflict`. A save whose
 * `definition.logic` is `{kind: 'graph', graph: <an object>}` is first previewed, as Python does: `keep` with a value
 * (`invalid_request`), then `validation`, whatever its id and revision. Then, for every save: `id`, `expectedRevision`,
 * the `definition` (missing, not an object, `keep` with a value), `enabled`, the id pattern, the reserved id `new`
 * ("Invalid definition ID: new is reserved"), a stale revision (`conflict`), and `enabled` for a saved definition. A
 * setting sent with `keep`, or with `hasValue` and no value, is stored with the value (and secret flag) stored before;
 * any other as sent.
 */
function entitiesFixture(generation: () => string) {
  const documents = new Map<string, EntityDocument>()
  /** The definition files that cannot be read, by id, with why (`Read`'s message): listed apart, deleted at revision 0. */
  const unreadable = new Map<string, string>()
  const registry = new Map<string, RegisteredEntity[]>()
  const published = new Map<string, RegisteredEntity[]>()
  const runs = new Map<string, FixtureRuns>()
  const requests: any[] = []
  const controls = {
    diagnostics: [] as VisualDiagnostic[], activation: 'running' as FixtureActivation, activationError: 'Fixture start failed',
    /** The Trigger steps `run` started, in order. */
    runs: [] as { id: string; triggerId: string }[],
  }
  const ok = (result: unknown) => ({ version: 1, ok: true, result })
  const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
  const empty = (value: unknown) => value === undefined || value === null
  const problem = (code: string, message: string, inputId: string | null = null, portId: string | null = null): VisualDiagnostic =>
    ({ nodeId: null, message, severity: 3, startLine: null, startColumn: null, inputId, portId, code })
  /**
   * A definition's entities as the store keeps and answers them, derived by the editor's own `listedEntities` (which
   * mirrors `DefinitionGraph.ListedEntities`): those its Entity nodes declare, then each C# entity's.
   */
  const derivedEntities = (body: DefinitionBody): DefinitionEntity[] =>
    listedEntities({ ...body.logic.graph, definition: { settings: [] } }, nodeCatalog) as DefinitionEntity[]
  /** A definition's devices, as `EntityDefinitionStore.Save` stores them: those its Device nodes and C# devices declare. */
  const derivedDevices = (body: DefinitionBody): DerivedDevice[] => {
    return structuredClone(withGraphTypes(nodeCatalog, { ...body.logic.graph, definition: { settings: [] } }).definition?.devices ?? []) as DerivedDevice[]
  }
  /**
   * The document's problems, as `DefinitionGenerator.Preview` and `DefinitionDocument.Check` word them: a request that
   * lists entities, a request with a device, a graph with no Entity node, and each Entity node's options, whose problems
   * are on it, about `option:<option>`. A request that lists entities, or else gives a device, gets that refusal alone
   * (beside its name's): nothing else of it is checked.
   */
  const documentProblems = (body: DefinitionBody) => {
    const problems: VisualDiagnostic[] = []
    if (typeof body.name !== 'string' || !body.name.trim() || body.name.length > 120) problems.push(problem('definition.name', 'Give the definition a name of up to 120 characters'))
    // As EntityApi reads it: an entities key given as null is an empty list.
    const listed = Object.hasOwn(body, 'entities') ? (body as { entities?: unknown }).entities ?? [] : undefined
    if (body.logic?.kind !== 'graph') return problems
    if (listed !== undefined) return [...problems, problem('definition.entitiesNotAllowed', 'Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each', 'entities')]
    if (!empty(body.device)) return [...problems, problem('definition.deviceNotAllowed', "Devices are Device nodes in the graph now: leave device out, add a Device node and connect its Entities pin to each entity's Device pin", 'device')]
    const nodes = declaredEntities(body.logic.graph)
    // A graph whose nodes are not a list is the validator's to tell (graph.shape), as the server has it.
    if (!nodes.length && Array.isArray((body.logic.graph as { nodes?: unknown } | undefined)?.nodes)) problems.push(problem('definition.entitiesRequired', 'Add an Entity node: an entity definition creates the entities on its canvas, and has none'))
    for (const { nodeId, entity } of nodes) {
      const domain = entity.domain, options = entity.options
      if (typeof domain !== 'string' || !Object.hasOwn(nodeCatalog.entityDomains ?? {}, domain) || (options !== undefined && (options === null || typeof options !== 'object' || Array.isArray(options)))) continue
      for (const found of optionProblems(typeof entity.key === 'string' ? entity.key : '', domain, options, typeof entity.name === 'string' && entity.name.trim() ? entity.name : undefined))
        problems.push({ ...found, nodeId, inputId: `option:${found.portId}`, portId: null })
    }
    return problems
  }
  /**
   * The problems of an Entity node's options (an object, or none), in the order and words of `DefinitionDocument.Options`:
   * each about the entity (`inputId`) and the option (`portId`), which the caller puts on the node, the option by its
   * label and the entity by its name, else its key.
   */
  const optionProblems = (key: string, domain: string, given: unknown, name: string | undefined): VisualDiagnostic[] => {
    const out: VisualDiagnostic[] = []
    // As Words.EntityName names it: its name, else its key, each trimmed, else Entity; a sentence starts with Entity and its key.
    const trimmed = (text: string) => text.replace(/^\s+|\s+$/g, '')
    const entity = trimmed(key) ? `Entity ${trimmed(key)}` : 'Entity'
    const named = name !== undefined ? trimmed(name) : trimmed(key) || 'Entity'
    const row = nodeCatalog.entityDomains![domain], known = row.options ?? {}
    const label = row.label || key, a = `${words.article(label)} ${label.toLowerCase()}`
    const shown = (option: string) => (Object.hasOwn(known, option) && known[option].label) || words.key(option)
    const lower = (text: string) => text.charAt(0).toLowerCase() + text.slice(1)
    const error = (option: string, message: string) => out.push(problem('definition.entityOption', message, key, option))
    const options = (given ?? {}) as Record<string, unknown>
    const number = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : undefined)
    // What Home Assistant takes of a number option beyond its type (`DefinitionDocument.Limit`).
    const limit = (option: string, value: number) =>
      domain === 'number' && option === 'step' && !(value > 0) ? 'a number above 0'
        : domain === 'text' && (option === 'min' || option === 'max') && value !== Math.floor(value) ? 'a whole number'
        : domain === 'text' && option === 'min' && value < 0 ? '0 or more'
        : domain === 'text' && option === 'max' && value > 255 ? '255 or less, as Home Assistant keeps at most 255 characters' : undefined
    // A number outside the range its rule gives, such as a camera's seconds between frames (`DefinitionDocument.Range`).
    const range = (rule: { min?: number; max?: number }, value: number) =>
      typeof rule.min === 'number' && typeof rule.max === 'number' && (value < rule.min || value > rule.max) ? `a number from ${rule.min} to ${rule.max}` : undefined
    let bounded = false
    for (const [option, value] of Object.entries(options)) {
      if (!Object.hasOwn(known, option)) {
        error(option, Object.keys(known).length === 0 ? `${entity} has the option ${option}, but ${a} takes no options; remove it`
          : `${entity} has no option ${option}; ${a} takes ${words.or(Object.keys(known))}`)
        continue
      }
      if (value === null) continue
      const rule = known[option], allowed = Array.isArray(rule.enum) ? rule.enum.filter((v): v is string => typeof v === 'string') : undefined
      const fitsEnum = (item: unknown) => !allowed || (typeof item === 'string' && allowed.includes(item))
      const fits = rule.type === 'array' ? Array.isArray(value) && value.every(item => typeof item === 'string' && fitsEnum(item))
        : (rule.type === 'string' ? typeof value === 'string' : rule.type === 'boolean' ? typeof value === 'boolean' : rule.type === 'number' ? number(value) !== undefined : false) && fitsEnum(value)
      const phrase = rule.type === 'array' ? 'a list of texts' : allowed ? `one of ${allowed.join(', ')}`
        : rule.type === 'string' ? 'a text' : rule.type === 'boolean' ? 'true or false' : rule.type === 'number' ? 'a number' : rule.type
      const bound = number(value) !== undefined ? limit(option, value as number) ?? range(rule, value as number) : undefined
      const of = `${shown(option)} of ${named}`
      if (!fits) error(option, `${of} takes ${phrase}`)
      else if (Array.isArray(value) && value.length === 0) error(option, `${of} is an empty list; give it at least one text${rule.required === true ? '' : ', or leave it out'}`)
      else if (bound) { error(option, `${of} takes ${bound}, not ${value}`); bounded ||= option === 'min' || option === 'max' }
      else if (option === 'pattern' && rule.type === 'string' && typeof value === 'string')
        // The tail is the regular expression engine's own words, as the server's is .NET's.
        try { new RegExp(value) } catch (failure) { error(option, `${of} is not a regular expression: ${(failure as Error).message}`) }
    }
    for (const [option, rule] of Object.entries(known))
      if (rule.required === true && (options[option] === undefined || options[option] === null)) error(option, `${name !== undefined ? named : entity} needs ${lower(shown(option))}: ${a} cannot work without it`)
    // A number's and a text's own min and max are 0 and 100 (`SharpDomains.DefaultOptions`).
    if (!bounded && Object.hasOwn(known, 'min') && Object.hasOwn(known, 'max') && (Object.hasOwn(options, 'min') || Object.hasOwn(options, 'max'))) {
      const min = number(options.min) ?? 0, max = number(options.max) ?? 100
      if (!(min < max)) error('min', `${shown('min')} of ${named} is ${min}, which is not below its ${lower(shown('max'))} of ${max}`)
    }
    return out
  }
  const preview = (body: DefinitionBody) => {
    const problems = documentProblems(body)
    // A problem on an Entity node is the graph's: reported with the graph's own, as the server does.
    const diagnostics = problems.some(p => p.nodeId === null) ? problems : [...problems, ...structuredClone(controls.diagnostics)]
    const valid = !diagnostics.some(d => d.severity >= 3)
    return { valid, diagnostics, source: valid ? '// HassSharp entity definition (fixture)' : '', sourceMap: [] }
  }
  /** The run fields of a stored definition, as `controls.activation` says it runs when on. */
  const running = (id: string, body: DefinitionBody, enabled: boolean): Partial<EntityDocument> => {
    const activation = enabled ? controls.activation : 'off'
    // Its script loaded; its host, unless it has none or the one it has is stopping (`EntityApi.RunningHost`).
    const loaded = activation !== 'off' && activation !== 'notLoaded'
    const host = activation === 'running' || activation === 'setup' || activation === 'failed' ? activation : undefined
    // Its entities, listed and declared by its Entity nodes.
    const made = derivedEntities(body)
    return {
      automationId: `entity:${id}`, active: loaded, currentRevisionActive: loaded,
      activationError: activation === 'off' ? 'Not active' : activation === 'notLoaded' || host === 'failed' ? controls.activationError : null,
      // On with no host: failed when it could not be prepared (a load error is recorded), else off (`EntityApi.EntityJson`).
      status: host ?? (activation === 'notLoaded' && controls.activationError !== 'Not active' ? 'failed' : 'off'),
      liveEntities: host ? made.map(e => ({ uniqueId: `${id}:${e.key}`, key: e.key, domain: e.domain, name: (e.name as string | null | undefined) ?? null, entityId: `${e.domain}.${id}_${e.key}` })) : [],
    }
  }
  /** Keeps the document; a loaded one's entities as the registry has them from then on, disabled as their entries were. */
  const keep = (document: EntityDocument) => {
    documents.set(document.id, document)
    // Only one that has a host made its entities: not one off, stopping, or failed for want of loading.
    if (document.status !== 'off' && !(document.status === 'failed' && !document.active)) {
      const before = registry.get(document.id) ?? published.get(document.id) ?? []
      published.set(document.id, document.liveEntities.map(e => ({
        uniqueId: e.uniqueId, entityId: e.entityId ?? `${e.domain}.${document.id}_${e.key}`, domain: e.domain, name: e.name,
        disabled: before.find(entry => entry.uniqueId === e.uniqueId)?.disabled ?? false,
      })))
    }
    return document
  }
  /** What a save or `set_enabled` answers of a definition's start: off by the user has no error. */
  const started = (document: EntityDocument) => ({ active: document.currentRevisionActive, activationError: document.enabled ? document.activationError : null })
  /** The document as the store keeps it, from a request's body and the definition stored before. */
  const stored = (id: string, body: DefinitionBody, revision: number, enabled: boolean, before?: EntityDocument): EntityDocument => {
    const settings = (body.settings ?? []).map(setting => {
      const old = before?.settings.find(s => s.key === setting.key)
      const kept = setting.keep === true || (setting.hasValue === true && empty(setting.value))
      const value = kept ? old?.value ?? null : setting.value ?? null
      const secret = setting.secret === true || (kept && old?.secret === true)
      return { key: setting.key, value, ...(empty(setting.label) ? {} : { label: setting.label }), ...(empty(setting.type) ? {} : { type: setting.type }), ...(secret ? { secret } : {}) }
    })
    // A device is refused before this: the one stored is null.
    const { definition: _definition, ...logicGraph } = body.logic.graph as unknown as Record<string, unknown>
    return {
      version: 3, id, revision, name: body.name, device: null,
      settings, entities: derivedEntities(body), devices: derivedDevices(body),
      logic: { kind: 'graph', type: null, graph: structuredClone(logicGraph) as unknown as VisualGraph },
      source: '// HassSharp entity definition (fixture)', sourceMap: [], enabled, savedUtc: new Date().toISOString(), catalogGeneration: generation(),
      ...running(id, body, enabled), registeredEntities: [],
    } as EntityDocument
  }
  /** Its Trigger steps as `definition_triggers` answers them. */
  const triggersOf = (document: EntityDocument) => {
    const running = document.currentRevisionActive && document.status === 'running'
    return document.logic.graph.nodes.filter(node => node.kind === 'trigger').map(node => {
      const manual = node.config?.manual !== false
      const method = 'Run_' + [...new TextEncoder().encode(node.id)].map(b => b.toString(16).padStart(2, '0')).join('').toUpperCase()
      return { triggerId: node.id, method, manual, runnable: running && manual }
    })
  }
  /** The document as an answer gives it: secrets masked, the registry's entries, its Trigger steps. */
  const answered = (document: EntityDocument) => {
    // As EntityLifecycle.WithoutSecretValues: a setting is answered only when no setting of its key is secret.
    const masked = (key: string) => document.settings.some(s => s.key === key && s.secret === true)
    const settings = document.settings.map(s => (masked(s.key) ? { ...s, value: null, secret: true, hasValue: !empty(s.value) } : s))
    const entries = [...(registry.get(document.id) ?? published.get(document.id) ?? [])].sort((a, b) => (a.uniqueId < b.uniqueId ? -1 : a.uniqueId > b.uniqueId ? 1 : 0))
    return structuredClone({ ...document, settings, registeredEntities: entries, triggers: triggersOf(document) })
  }
  /**
   * The document as the list answers it: without its source, and its devices counted instead (as `EntityApi` lists
   * them): its Device nodes' and C# devices'.
   */
  const summary = (document: EntityDocument) => {
    const { source: _source, sourceMap: _sourceMap, device: _device, devices, ...rest } = answered(document)
    return { ...rest, devices: devices?.length ?? 0 }
  }
  const sorted = () => [...documents.values()].sort((a, b) => (a.id < b.id ? -1 : 1))
  /** A refusal, thrown by the field readers and answered by `callWS`. */
  class Refused { constructor(readonly answer: unknown) {} }
  const refuse = (message: string): never => { throw new Refused(fail('invalid_request', message)) }
  const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
  // As ApiRequest's readers: JSON null is a field not given.
  const text = (request: any, key: string): string => empty(request[key]) ? refuse(`${key} is required`) : typeof request[key] === 'string' ? request[key] : refuse(`${key} must be a string`)
  const optionalText = (request: any, key: string): string | undefined => empty(request[key]) ? undefined : typeof request[key] === 'string' ? request[key] : refuse(`${key} must be a string`)
  const whole = (request: any, key: string, hint: string): number => empty(request[key]) ? refuse(`${key} is required: ${hint}`) : Number.isInteger(request[key]) ? request[key] : refuse(`${key} must be a whole number`)
  const optionalFlag = (request: any, key: string): boolean | undefined => empty(request[key]) ? undefined : typeof request[key] === 'boolean' ? request[key] : refuse(`${key} must be true or false`)
  const flag = (request: any, key: string): boolean => optionalFlag(request, key) ?? refuse(`${key} is required`)
  const revision = (request: any) => whole(request, 'expectedRevision', 'the revision this replaces, 0 for a new definition')
  /** The request's `definition` as `EntityApi.DefinitionBody` reads it. */
  const definitionOf = (request: any): DefinitionBody => {
    const definition = request.definition
    if (empty(definition)) refuse('definition is required')
    if (!object(definition)) refuse('definition must be an object')
    const both = (definition as unknown as DefinitionBody).settings?.find(s => s.keep === true && !empty(s.value))
    if (both) refuse(`Setting ${both.key} has both a value and keep; give the value, or keep to leave the saved one as it is`)
    return definition as unknown as DefinitionBody
  }
  const checkId = (id: string) => { if (!/^[a-z0-9][a-z0-9_-]{0,79}$/.test(id)) refuse('Invalid definition ID') }
  /** The definition's stored document, or `not_found`; a file that cannot be read (`unreadable`) answers why, as `Read` throws it. */
  const found = (id: string) => {
    if (unreadable.has(id)) refuse(unreadable.get(id)!)
    return documents.get(id) ?? (() => { throw new Refused(fail('not_found', 'Entity definition not found')) })()
  }
  const save = (request: any) => {
    // Python previews a graph (an object, as `definition_graph` reads it) before .NET reads the save's other fields.
    const logic = object(request.definition) ? request.definition.logic : undefined
    if (object(logic) && logic.kind === 'graph' && object(logic.graph)) {
      const checked = preview(definitionOf(request))
      if (!checked.valid) return fail('validation', checked)
    }
    const id = text(request, 'id'), expected = revision(request), definition = definitionOf(request), enabled = optionalFlag(request, 'enabled')
    checkId(id)
    // As EntityDefinitionStore.Save: /entities/new is the new-definition page, so no definition is saved as new.
    if (id === 'new') refuse('Invalid definition ID: new is reserved')
    // As EntityDefinitionStore.Save, which reads the file it replaces: one that cannot be read is refused with why.
    if (unreadable.has(id)) refuse(unreadable.get(id)!)
    const existing = documents.get(id)
    if ((existing?.revision ?? 0) !== expected) return fail('conflict', 'Revision conflict; reload the current document')
    if (existing && enabled !== undefined) refuse('enabled can only be given when saving a new definition; use set_enabled to turn a saved one on or off')
    const checked = preview(definition)
    if (!checked.valid) return fail('validation', checked)
    const document = keep(stored(id, definition, expected + 1, existing?.enabled ?? enabled ?? true, existing))
    return ok({ id, saved: true, document: answered(document), ...started(document), ...(checked.diagnostics.length ? { diagnostics: checked.diagnostics } : {}) })
  }
  const answer = (request: any) => {
    switch (request.operation) {
      case 'list': {
        const view = optionalText(request, 'view')
        if (view !== undefined && view !== 'home') refuse('Unknown list view; the entity definitions list has the view home')
        // Beside the result, as EntityApi answers it: each file that cannot be read, with why.
        return { ...ok(sorted().map(document => view === 'home'
          ? { ...summary(document), recentRuns: [], runsToday: 0, failedLastDay: 0, lastRunUtc: null, ...structuredClone(runs.get(document.id) ?? {}) }
          : summary(document))), unreadable: [...unreadable].sort(([a], [b]) => (a < b ? -1 : 1)).map(([id, reason]) => ({ id, reason, deletable: /^[a-z0-9][a-z0-9_-]{0,79}$/.test(id) })) }
      }
      case 'preview': return ok(preview(definitionOf(request)))
      case 'save': return save(request)
      case 'read': { const id = text(request, 'id'); checkId(id); return ok(answered(found(id))) }
      case 'set_enabled': {
        const id = text(request, 'id'), enabled = flag(request, 'enabled')
        checkId(id)
        const existing = found(id)
        const document = keep({ ...existing, enabled, ...running(id, existing, enabled) })
        return ok({ id, enabled: document.enabled, ...started(document) })
      }
      case 'run': {
        // As EntityApi: the id, the stored definition and the step's id, then whether it runs and the step it names.
        const id = text(request, 'id')
        checkId(id)
        const document = found(id), triggerId = text(request, 'triggerId')
        if (!document.enabled) refuse('The entity definition is off')
        if (!document.currentRevisionActive || document.status !== 'running')
          refuse(`The saved revision of this entity definition is not running: ${document.activationError && document.activationError !== 'Not active' ? document.activationError : 'it has not started'}`)
        const node = document.logic.graph!.nodes.find(n => n.id === triggerId) ?? refuse('Trigger not found')
        if (node.kind !== 'trigger') refuse(`Only a Trigger step can be run by hand; ${node.label?.trim() || nodeCatalog.nodes[node.kind]?.label || node.kind} is not one`)
        if (node.config?.manual === false) refuse('Trigger does not permit manual execution')
        controls.runs.push({ id, triggerId })
        return ok({ version: 1, runId: 'manual-entity-run' })
      }
      case 'delete': {
        const id = text(request, 'id'), expected = revision(request)
        checkId(id)
        // A file that cannot be read is deleted at revision 0 only, with its entities.
        if (unreadable.has(id)) {
          if (expected !== 0) return fail('conflict', 'Revision conflict; reload the current document')
          unreadable.delete(id)
          registry.delete(id)
          published.delete(id)
          return ok({ deleted: true })
        }
        if (found(id).revision !== expected) return fail('conflict', 'Revision conflict; reload the current document')
        documents.delete(id)
        registry.delete(id)
        published.delete(id)
        return ok({ deleted: true })
      }
      default: return refuse('Unknown entity operation')
    }
  }
  const callWS = async (message: any) => {
    const request = message.request
    requests.push(structuredClone(request))
    try {
      return answer(request)
    } catch (error) {
      if (error instanceof Refused) return error.answer
      throw error
    }
  }
  const seed = (id: string, body: DefinitionBody, fields: Partial<EntityDocument> = {}) => {
    return keep({ ...stored(id, body, 1, fields.enabled ?? true), ...structuredClone(fields) })
  }
  return { documents, unreadable, registry, published, runs, requests, controls, seed, callWS }
}

export function visualFixture(
  options: { catalog?: VisualCatalogSnapshot; libraries?: { id: string; name: string; revision: number; source: string }[] } = {},
) {
  const documents = new Map<string, VisualDocument>()
  const runs = new Map<string, FixtureRuns>()
  const calls: any[] = []
  const libraries = new Map((options.libraries ?? []).map(d => [d.id, { version: 1, ...d }]))
  let libraryRevision = 0
  const controls = {
    conflict: false,
    active: true,
    previewError: false,
    invalid: false,
    deleteConflict: false,
    preview: undefined as VisualPreview | undefined,
    catalog: options.catalog ?? nodeCatalog,
    generation: 'saved-fixture',
    /** The C4 services section; the catalog reports its generation. Undefined: no services op (an older backend). */
    services: undefined as ServicesSection | undefined,
    servicesError: false,
    /** `set_enabled` fails with this error code. */
    setEnabledError: undefined as string | undefined,
    /** The automations page's list fails. */
    homeError: false,
    /** `set_enabled` waits for this before the server takes the switch. */
    holdEnabled: undefined as Promise<unknown> | undefined,
    /** A save is taken at once; its reply waits for this. */
    holdSaveReply: undefined as Promise<unknown> | undefined,
    /** Types whose `type_definition` fails as if renamed since. */
    missingTypes: new Set<string>(),
    /** `type_definition` waits for this before it answers. */
    holdTypeDefinition: undefined as Promise<unknown> | undefined,
  }
  const entities = entitiesFixture(() => controls.generation)
  const makePreview = (_graph: VisualGraph): VisualPreview => ({
    valid: !controls.invalid,
    source: 'class VisualAutomation\n{\n  // generated on server\n}',
    sourceMap: [{ nodeId: 'trigger-1', startLine: 1, endLine: 4 }],
    diagnostics: controls.invalid ? [{ nodeId: 'trigger-1', message: 'Invalid entity', severity: 3, startLine: 3, startColumn: 2 }] : [],
  })
  const callWS = async (message: any) => {
    calls.push(structuredClone(message))
    if (message.type === 'hass_sharp/entities') return entities.callWS(message)
    if (message.type === 'hass_sharp/node_library') {
      const request = message.request
      if (request.operation === 'catalog')
        return {
          version: 1,
          ok: true,
          result: { catalog: controls.catalog, generation: controls.generation, ...(controls.services ? { servicesGeneration: controls.services.servicesGeneration } : {}), saved: true, valid: true, diagnostics: [] },
        }
      if (request.operation === 'services') {
        if (controls.servicesError || !controls.services) throw new Error('Services offline')
        return { version: 1, ok: true, result: structuredClone(controls.services) }
      }
      if (request.operation === 'list')
        return { version: 1, ok: true, result: { documents: [...libraries.values()], generation: controls.generation } }
      if (request.operation === 'read') {
        const document = libraries.get(request.id)
        return document
          ? { version: 1, ok: true, result: document }
          : { version: 1, ok: false, error: { code: 'not_found', detail: 'Library missing' } }
      }
      const ok = (result: unknown) => ({ version: 1, ok: true, result })
      const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
      if (request.operation === 'save' || request.operation === 'delete') {
        const existing = libraries.get(request.id)
        if (controls.conflict || (existing?.revision ?? 0) !== request.expectedRevision) return fail('conflict', 'Library changed elsewhere')
        if (controls.invalid) return fail('validation', { diagnostics: [{ sourceId: request.id, startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, message: 'Fixture library validation error', severity: 3 }], automations: [] })
        if (request.operation === 'delete' && !existing) return fail('not_found', 'Library missing')
        const document = request.operation === 'save' ? { version: 1, id: request.id, name: request.name, source: request.source, revision: request.expectedRevision + 1 } : null
        if (document) libraries.set(document.id, document); else libraries.delete(request.id)
        controls.generation = `saved-fixture-${++libraryRevision}`
        const automations = [...documents.values()].map(prior => {
          const updated = { ...prior, revision: prior.revision + 1, catalogGeneration: controls.generation, currentRevisionActive: controls.active, activeRevision: controls.active ? prior.revision + 1 : prior.activeRevision }
          documents.set(prior.id, updated)
          return { id: prior.id, saved: true, document: updated, active: controls.active, activationError: controls.active ? null : 'Fixture activation failed' }
        })
        return ok({ saved: true, deleted: !document, document, generation: controls.generation, catalog: controls.catalog, automations })
      }
      if (request.operation === 'type_definition') {
        await controls.holdTypeDefinition
        const found = controls.missingTypes.has(request.type) ? undefined : fixtureTypeDefinition(request, libraries.values())
        return found ? ok(found) : fail('not_found', `There is no type ${request.type.replace(/global::/g, '')}; it may have been renamed or removed`)
      }
      if (request.generation && request.generation !== controls.generation) return fail('conflict', 'Saved library generation changed')
      if (controls.previewError) throw new Error('Library fixture offline')
      if (request.operation === 'custom_preview') return ok(fixtureCustomPreview(request.nodeId ?? 'custom', request.source, request.kind ?? 'custom'))
      // A Custom C# node's code (`custom: true`): replies name the node; its own code is the `$custom` document.
      const custom = request.custom === true
      const sources = new Map([...libraries.values()].map(d => [d.id, d.source]))
      for (const other of request.sources ?? []) sources.set(other.id, other.source)
      sources.set(custom ? '$custom' : request.id, request.source)
      const identity = JSON.stringify([...sources].sort()) + controls.generation
      let hash = 2166136261
      for (let i = 0; i < identity.length; i++) hash = Math.imul(hash ^ identity.charCodeAt(i), 16777619)
      const wrap = (data: unknown) => ok({ id: custom ? request.nodeId ?? 'custom' : request.id, workspaceVersion: request.workspaceVersion, generation: controls.generation, sourceGeneration: `fixture-source-${hash >>> 0}`, saved: false, data })
      const range = (source: string, start: number, length: number) => {
        const position = (offset: number) => { const lines = source.slice(0, offset).split('\n'); return { line: lines.length, column: lines.at(-1)!.length + 1 } }
        const a = position(start), b = position(start + length)
        return { start, length, startLine: a.line, startColumn: a.column, endLine: b.line, endColumn: b.column }
      }
      if (request.operation === 'preview') {
        const diagnostics = [...sources].flatMap(([id, source]) => {
          const start = source.indexOf('MissingName')
          return start < 0 ? [] : [{ sourceId: id, ...range(source, start, 11), severity: 3, message: "The name 'MissingName' does not exist in the current context (fixture)" }]
        })
        return wrap({ valid: !diagnostics.length, diagnostics, catalog: controls.catalog })
      }
      const before = request.source.slice(0, request.position ?? 0)
      const word = /[A-Za-z_][A-Za-z_0-9]*$/.exec(before)?.[0] ?? ''
      if (request.operation === 'completion') {
        const members = before.includes('entity.') || before.includes('state.') ? ['State', 'Attributes', 'EntityId', 'LastChanged', 'LastUpdated', 'LastReported'] : ['State', 'Describe', 'Make', 'Reading', 'EntityStateSnapshot']
        // Every member the first letter can match: the editor narrows the list itself as the word grows.
        return wrap({ items: members.filter(name => !word || name.toLowerCase().startsWith(word[0].toLowerCase())).map(name => ({ displayText: name, displayTextPrefix: '', displayTextSuffix: '', filterText: name, tags: ['Property'], textEdit: { start: before.length - word.length, length: word.length, newText: name }, additionalTextEdits: [] })), incomplete: false })
      }
      if (request.operation === 'hover') return wrap({ text: 'EntityStateSnapshot.State : string\nThe detached Home Assistant entity state (fixture).', range: range(request.source, before.length - word.length, Math.max(1, word.length)) })
      if (request.operation === 'signature') {
        const argumentsText = before.slice(before.lastIndexOf('(') + 1)
        const argument = argumentsText.split(',').at(-1) ?? ''
        const activeParameter = /count\s*:/.test(argument) ? 1 : /label\s*:/.test(argument) ? 0 : argumentsText.split(',').length - 1
        return wrap({ signatures: [{ label: 'Reading Factory.Make(string? label, int count = 1)', documentation: '<summary>Create a reading (fixture).</summary>', parameters: ['string? label', 'int count = 1'] }], activeSignature: 0, activeParameter })
      }
      if (request.operation === 'definition') {
        const target = [...sources].find(([, source]) => source.includes('Reading Describe'))
        const start = target?.[1].indexOf('Describe') ?? -1
        return wrap(target && start >= 0 ? [{ id: target[0], range: range(target[1], start, 'Describe'.length) }] : [])
      }
      if (request.operation === 'format') return wrap(request.source.replace(/\s*=>\s*/g, ' => '))
      throw new Error(`Unsupported library fixture operation ${request.operation}`)
    }
    if (message.type === 'get_services') return { light: { turn_on: { name: 'Turn on', description: 'Turn on a light' } } }
    if (message.type === 'hass_sharp/run_history') return { version: 2, offset: message.offset ?? 0, limit: message.limit ?? 20, runs: [] }
    if (message.type === 'hass_sharp/run_now') return { version: 1, runId: 'manual-visual-run' }
    if (message.type === 'hass_sharp/get_entities') return { entities: [] }
    const req = message.request
    let result: unknown
    const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
    if (req.operation === 'preview') {
      if (controls.previewError) throw new Error('Preview offline')
      result = controls.preview ?? makePreview(req.graph)
    // Like the backend, list omits the generated source; read returns it.
    } else if (req.operation === 'list' && req.view === 'home') {
      if (controls.homeError) throw new Error('Automations offline')
      result = [...documents.values()].map((document): HomeAutomation => ({
        id: document.id, revision: document.revision, automationId: document.automationId, enabled: document.enabled ?? true,
        active: document.active, currentRevisionActive: document.currentRevisionActive ?? true, activationError: document.activationError ?? null,
        savedUtc: document.savedUtc ?? null, catalogGeneration: document.catalogGeneration, outline: outlineOf(document.graph),
        recentRuns: [], runsToday: 0, failedLastDay: 0, lastRunUtc: null, problemCount: null, ...structuredClone(runs.get(document.id) ?? {}),
      }))
    } else if (req.operation === 'list') result = [...documents.values()].map(({ source: _source, sourceMap: _sourceMap, savedUtc: _saved, ...summary }) => ({ enabled: true, ...summary }))
    else if (req.operation === 'set_enabled') {
      if (controls.holdEnabled) await controls.holdEnabled
      if (controls.setEnabledError) return fail(controls.setEnabledError, 'Fixture refused the switch')
      const document = documents.get(req.id)
      if (!document) return fail('not_found', 'Document missing')
      documents.set(req.id, { ...document, enabled: req.enabled })
      result = { id: req.id, enabled: req.enabled }
    }
    else if (req.operation === 'read') {
      if (!documents.has(req.id)) return fail('not_found', 'Document missing')
      result = documents.get(req.id)
    } else if (req.operation === 'save') {
      if (controls.conflict || (documents.get(req.id)?.revision ?? 0) !== req.expectedRevision) return fail('conflict', 'Changed elsewhere')
      // Like the backend: enabled only for a new automation.
      if (documents.has(req.id) && req.enabled !== undefined) return fail('invalid_request', 'enabled can only be given when saving a new automation; use set_enabled to turn a saved one on or off')
      const preview = makePreview(req.graph)
      if (!preview.valid) return fail('validation', preview)
      const document: VisualDocument = {
        version: 1,
        id: req.id,
        revision: req.expectedRevision + 1,
        graph: structuredClone(req.graph),
        source: preview.source,
        sourceMap: preview.sourceMap,
        currentRevisionActive: controls.active,
        active: controls.active,
        catalogGeneration: 'saved-fixture',
        activeRevision: controls.active ? req.expectedRevision + 1 : null,
        automationId: `visual:${req.id}`,
        enabled: documents.get(req.id)?.enabled ?? req.enabled ?? true,
        savedUtc: new Date().toISOString(),
      }
      documents.set(req.id, document)
      result = { document: structuredClone(document), active: controls.active, activationError: controls.active ? null : 'Compilation failed during activation' }
      if (controls.holdSaveReply) await controls.holdSaveReply
    } else if (req.operation === 'delete') {
      if (controls.deleteConflict || documents.get(req.id)?.revision !== req.expectedRevision) return fail('conflict', 'Changed elsewhere')
      documents.delete(req.id)
      result = { deleted: true }
    } else throw new Error(`Unexpected request ${JSON.stringify(message)}`)
    return { version: 1, ok: true, result }
  }
  return {
    documents,
    runs,
    libraries,
    calls,
    controls,
    /** The in-memory entity definitions backend (`entitiesFixture`). */
    entities,
    callWS,
    connection: {
      subscribeMessage: async (callback: (value: unknown) => void) => {
        callback({
          'update.became_available': { fields: {}, target: { entity: { domain: 'update' } } },
          'battery.started_charging': { fields: {}, target: { entity: { domain: 'sensor' } } },
          'temperature.changed': { fields: {}, target: { entity: { domain: 'sensor' } } },
        })
        return () => {}
      },
    },
  }
}
