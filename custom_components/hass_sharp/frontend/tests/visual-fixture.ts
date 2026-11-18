import { nodeCatalog, words, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import type { GraphOutline, HomeAutomation, VisualDiagnostic, VisualDocument, VisualPreview } from '../src/visual/api'
import type { DefinitionBody, EntityDocument, EntityType, RegisteredEntity } from '../src/entities/model'
import type { VisualGraph } from '../src/visual/graph'

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
 * `!!` in it has an error there.
 */
export function fixtureCustomPreview(nodeId: string, source: string) {
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
 * A class for the fixture's `types`, with the entities its instance reports once loaded (`entities`, fixture only: the
 * real `types` answer lists none, and neither does the fixture's).
 */
export type FixtureEntityType = EntityType & { entities?: { key: string; domain: string; name?: string | null }[] }

/**
 * The in-memory `hass_sharp/entities` backend of {@link visualFixture} (`fixture.entities`), answering as
 * `EntityApi.EntityRequest` and the Python route do, in the `{version, ok, result | error}` envelope.
 *
 * - `documents`: the stored definitions by id, as the store keeps them: secrets in clear, `logic` as `{kind, type, graph}`,
 *   the graph without `definition`, with the run fields an answer has (`automationId` `entity:<id>`, `status`, …). Every
 *   answer masks secrets from it: a graph's `secret` settings, and every setting of a class except those its type in
 *   `types` declares and does not mark secret (all of them when the type is not in `types`, as for a library that does
 *   not compile).
 * - `seed(id, body, fields?)`: stores a definition at revision 1, as `controls.activation` says it runs (off with
 *   `{enabled: false}`); `fields` overrides any member of the stored document (`status`, `liveEntities`, …).
 * - `types`: the classes (`FixtureEntityType`, from the `entityTypes` option), answered by `types` without their
 *   `entities`, whose `generation` is the library generation. A class's `entities` are what its instance reports.
 * - With a host that is not stopping (`running`, `setup` while its start runs, `failed` when its start threw; each after
 *   its entities were made), a definition answers `liveEntities`: a graph's entities, or its class's `entities`, each
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
 * - `controls.classChecks` (off by default): a class definition's class and setting values are checked as the real
 *   preview and save check them; `controls.libraryErrors`: the node libraries do not compile (`definition.library`,
 *   and every class setting answered masked).
 *
 * The document checks are the real ones a test meets most (codes of `DefinitionDocument`/`DefinitionGenerator`): a name
 * of 1–120 characters (`definition.name`) and, for a graph, at least one entity (`definition.entitiesRequired`), each
 * keyed `^[a-z][a-z0-9_]{0,39}$` (`definition.entityKey`) and uniquely (`definition.duplicateEntity`), of a domain of
 * the catalog's `entityDomains` (`definition.entityDomain`), with options as `DefinitionDocument.Options` checks them
 * (`definition.entityOption`: one its domain has, of its type, not an empty list, a number Home Assistant takes, a
 * pattern that is a regular expression, every option the domain needs, a min below the max); with any of them the
 * seeded diagnostics are not answered.
 *
 * Refusals come in the real order, with the real `invalid_request` texts for a field missing (`id is required`) or of
 * the wrong type (`id must be a string`, `expectedRevision must be a whole number`, `enabled must be true or false`).
 * Every operation with an id refuses one outside `^[a-z0-9][a-z0-9_-]{0,79}$` ("Invalid definition ID") after reading
 * its fields, and before `not_found` (`read`, `set_enabled`, `delete` of an unknown id) and `conflict`. A save whose
 * `definition.logic` is `{kind: 'graph', graph: <an object>}` is first previewed, as Python does: `keep` with a value
 * (`invalid_request`), then `validation`, whatever its id and revision. Then, for every save: `id`, `expectedRevision`,
 * the `definition` (missing, not an object, `keep` with a value), `enabled`, the id pattern, the reserved id `new`
 * ("Invalid definition ID: new is reserved"), a stale revision (`conflict`), `enabled` for a saved definition, and a
 * class's `validation`. A setting sent with `keep`, or with `hasValue` and no value, is stored with the value (and
 * secret flag) stored before; any other as sent.
 */
function entitiesFixture(options: { entityTypes?: FixtureEntityType[] }, generation: () => string) {
  const documents = new Map<string, EntityDocument>()
  const registry = new Map<string, RegisteredEntity[]>()
  const published = new Map<string, RegisteredEntity[]>()
  const runs = new Map<string, FixtureRuns>()
  const types = structuredClone(options.entityTypes ?? [])
  const requests: any[] = []
  const controls = {
    diagnostics: [] as VisualDiagnostic[], activation: 'running' as FixtureActivation, activationError: 'Fixture start failed',
    /**
     * Check a class definition as `DefinitionGenerator.Preview` does (off unless a test turns it on): the class is in
     * `types` (`definition.unknownType`), and each setting is one it declares (`definition.unknownSetting`), of its type
     * and range (`definition.settingType`), once (`definition.duplicateSetting`); a kept value is checked as stored.
     */
    classChecks: false,
    /** The node libraries do not compile: every definition is refused (`definition.library`), and a class's settings are all answered masked. */
    libraryErrors: false,
  }
  const ok = (result: unknown) => ({ version: 1, ok: true, result })
  const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
  const empty = (value: unknown) => value === undefined || value === null
  const problem = (code: string, message: string, inputId: string | null = null, portId: string | null = null): VisualDiagnostic =>
    ({ nodeId: null, message, severity: 3, startLine: null, startColumn: null, inputId, portId, code })
  /** The document's problems, as `DefinitionGenerator.Preview` and `DefinitionDocument.Check` word them. */
  const documentProblems = (body: DefinitionBody) => {
    const problems: VisualDiagnostic[] = []
    if (typeof body.name !== 'string' || !body.name.trim() || body.name.length > 120) problems.push(problem('definition.name', 'Give the definition a name of up to 120 characters'))
    if (body.logic?.kind !== 'graph') return problems
    const entities: unknown[] = body.entities ?? []
    if (!entities.length) problems.push(problem('definition.entitiesRequired', 'Add an entity: a definition built from nodes creates the entities it lists, and lists none'))
    const keys = new Set<string>(), repeated = new Set<string>()
    entities.forEach((entity: any, i) => {
      const index = i + 1
      if (!entity || typeof entity !== 'object' || Array.isArray(entity)) return problems.push(problem('definition.entityKey', `Entity ${index} is not an entity: give each entity a key and a domain`))
      const key = entity.key
      if (typeof key !== 'string') return problems.push(problem('definition.entityKey', `Give entity ${index} a key, such as ${index === 1 ? 'power' : `power_${index}`}`))
      if (!/^[a-z][a-z0-9_]{0,39}$/.test(key)) problems.push(problem('definition.entityKey', `Entity key ${key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters`, key))
      if (keys.has(key) && !repeated.has(key)) { repeated.add(key); problems.push(problem('definition.duplicateEntity', `Two entities have the key ${key}; give each a key of its own`, key)) }
      keys.add(key)
      const domains = nodeCatalog.entityDomains ?? {}
      const domain = entity.domain
      if (typeof domain !== 'string') return problems.push(problem('definition.entityDomain', `Choose a domain for entity ${key}: ${words.or(Object.keys(domains))}`, key))
      if (!Object.hasOwn(domains, domain)) return problems.push(problem('definition.entityDomain', `Entity ${key} has the domain ${domain}, which a definition built from nodes cannot create; choose ${words.or(Object.keys(domains))}`, key))
      problems.push(...optionProblems(key, domain, entity.options, typeof entity.name === 'string' && entity.name.trim() ? entity.name : undefined))
    })
    return problems
  }
  /**
   * The problems of an entity's options, in the order and words of `DefinitionDocument.Options`: each about the entity
   * (`inputId`) and the option (`portId`), the option by its label and the entity by its name, else its key.
   */
  const optionProblems = (key: string, domain: string, given: unknown, name: string | undefined): VisualDiagnostic[] => {
    const out: VisualDiagnostic[] = []
    const named = name ?? key
    const row = nodeCatalog.entityDomains![domain], known = row.options ?? {}
    const label = row.label || key, a = `${words.article(label)} ${label.toLowerCase()}`
    const shown = (option: string) => (Object.hasOwn(known, option) && known[option].label) || words.key(option)
    const lower = (text: string) => text.charAt(0).toLowerCase() + text.slice(1)
    const error = (option: string | null, message: string) => out.push(problem('definition.entityOption', message, key, option))
    if (given !== undefined && given !== null && (typeof given !== 'object' || Array.isArray(given))) {
      error(null, `The options of entity ${key} are not a set of named values; give them as an object, such as {"unit": "W"}`)
      return out
    }
    const options = (given ?? {}) as Record<string, unknown>
    const number = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : undefined)
    // What Home Assistant takes of a number option beyond its type (`DefinitionDocument.Limit`).
    const limit = (option: string, value: number) =>
      domain === 'number' && option === 'step' && !(value > 0) ? 'a number above 0'
        : domain === 'text' && (option === 'min' || option === 'max') && value !== Math.floor(value) ? 'a whole number'
        : domain === 'text' && option === 'min' && value < 0 ? '0 or more'
        : domain === 'text' && option === 'max' && value > 255 ? '255 or less, as Home Assistant keeps at most 255 characters' : undefined
    let bounded = false
    for (const [option, value] of Object.entries(options)) {
      if (!Object.hasOwn(known, option)) {
        error(option, Object.keys(known).length === 0 ? `Entity ${key} has the option ${option}, but ${a} takes no options; remove it`
          : `Entity ${key} has no option ${option}; ${a} takes ${words.or(Object.keys(known))}`)
        continue
      }
      if (value === null) continue
      const rule = known[option], allowed = Array.isArray(rule.enum) ? rule.enum.filter((v): v is string => typeof v === 'string') : undefined
      const fitsEnum = (item: unknown) => !allowed || (typeof item === 'string' && allowed.includes(item))
      const fits = rule.type === 'array' ? Array.isArray(value) && value.every(item => typeof item === 'string' && fitsEnum(item))
        : (rule.type === 'string' ? typeof value === 'string' : rule.type === 'boolean' ? typeof value === 'boolean' : rule.type === 'number' ? number(value) !== undefined : false) && fitsEnum(value)
      const phrase = rule.type === 'array' ? 'a list of texts' : allowed ? `one of ${allowed.join(', ')}`
        : rule.type === 'string' ? 'a text' : rule.type === 'boolean' ? 'true or false' : rule.type === 'number' ? 'a number' : rule.type
      const bound = number(value) !== undefined ? limit(option, value as number) : undefined
      const of = `${shown(option)} of ${named}`
      if (!fits) error(option, `${of} takes ${phrase}`)
      else if (Array.isArray(value) && value.length === 0) error(option, `${of} is an empty list; give it at least one text${rule.required === true ? '' : ', or leave it out'}`)
      else if (bound) { error(option, `${of} takes ${bound}, not ${value}`); bounded ||= option === 'min' || option === 'max' }
      else if (option === 'pattern' && rule.type === 'string' && typeof value === 'string')
        // The tail is the regular expression engine's own words, as the server's is .NET's.
        try { new RegExp(value) } catch (failure) { error(option, `${of} is not a regular expression: ${(failure as Error).message}`) }
    }
    for (const [option, rule] of Object.entries(known))
      if (rule.required === true && (options[option] === undefined || options[option] === null)) error(option, `${name ?? `Entity ${key}`} needs ${lower(shown(option))}: ${a} cannot work without it`)
    // A number's and a text's own min and max are 0 and 100 (`SharpDomains.DefaultOptions`).
    if (!bounded && Object.hasOwn(known, 'min') && Object.hasOwn(known, 'max') && (Object.hasOwn(options, 'min') || Object.hasOwn(options, 'max'))) {
      const min = number(options.min) ?? 0, max = number(options.max) ?? 100
      if (!(min < max)) error('min', `${shown('min')} of ${named} is ${min}, which is not below its ${lower(shown('max'))} of ${max}`)
    }
    return out
  }
  /** A class's setting value problems, as `DefinitionGenerator.Settings` words them; `before` gives a kept value. */
  const classProblems = (body: DefinitionBody, before?: EntityDocument): VisualDiagnostic[] => {
    if (controls.libraryErrors) return [problem('definition.library', 'The node libraries have errors; fix them first')]
    if (!controls.classChecks || body.logic?.kind !== 'class') return []
    const name = body.logic.type
    if (typeof name !== 'string' || !name.trim()) return [problem('definition.unknownType', 'No entity class was given: choose the class the definition creates')]
    const type = types.find(t => t.id === name)
    if (!type) return [problem('definition.unknownType', `No entity class ${name} in the node libraries`)]
    const given = (body.settings ?? []).map(s => {
      const kept = s.keep === true || (s.hasValue === true && empty(s.value))
      return { key: s.key, value: kept ? before?.settings.find(old => old.key === s.key)?.value ?? null : s.value ?? null }
    })
    const problems: VisualDiagnostic[] = []
    for (const key of new Set(given.map(s => s.key))) if (!type.settings.some(s => s.key === key)) problems.push(problem('definition.unknownSetting', `${type.title} has no setting ${key}`, key))
    // A number's range as `DefinitionGenerator.Phrase` words it: from … to …, of at least …, or of at most ….
    const range = (s: (typeof type.settings)[number]) =>
      s.minimum !== undefined && s.maximum !== undefined ? ` from ${s.minimum} to ${s.maximum}` : s.minimum !== undefined ? ` of at least ${s.minimum}` : s.maximum !== undefined ? ` of at most ${s.maximum}` : ''
    const phrase = (s: (typeof type.settings)[number]) =>
      s.type === 'integer' ? `a whole number${range(s)}` : s.type === 'number' ? `a number${range(s)}`
        : s.type === 'enum' ? `one of ${s.options.join(', ')}` : s.type === 'boolean' ? 'true or false' : 'a text'
    const within = (s: (typeof type.settings)[number], value: number) => value >= (s.minimum ?? -Infinity) && value <= (s.maximum ?? Infinity)
    const fits = (s: (typeof type.settings)[number], value: unknown) =>
      empty(value) || (s.type === 'integer' ? Number.isInteger(value) && within(s, value as number)
        : s.type === 'number' ? typeof value === 'number' && Number.isFinite(value) && within(s, value)
        : s.type === 'enum' ? typeof value === 'string' && s.options.includes(value)
        : s.type === 'boolean' ? typeof value === 'boolean' : typeof value === 'string')
    for (const { key, value } of given) {
      const setting = type.settings.find(s => s.key === key)
      if (setting && !fits(setting, value)) problems.push(problem('definition.settingType', `${setting.label} takes ${phrase(setting)}`, key))
    }
    for (const key of new Set(given.map(s => s.key).filter((key, i, all) => all.indexOf(key) !== i))) problems.push(problem('definition.duplicateSetting', `${key} is set twice`, key))
    return problems
  }
  const preview = (body: DefinitionBody, before?: EntityDocument) => {
    const problems = [...documentProblems(body), ...classProblems(body, before)]
    const diagnostics = problems.length ? problems : structuredClone(controls.diagnostics)
    const valid = !diagnostics.some(d => d.severity >= 3)
    return { valid, diagnostics, source: valid ? '// HassSharp entity definition (fixture)' : '', sourceMap: [] }
  }
  /** The run fields of a stored definition, as `controls.activation` says it runs when on. */
  const running = (id: string, body: DefinitionBody, enabled: boolean): Partial<EntityDocument> => {
    const activation = enabled ? controls.activation : 'off'
    // Its script loaded; its host, unless it has none or the one it has is stopping (`EntityApi.RunningHost`).
    const loaded = activation !== 'off' && activation !== 'notLoaded'
    const host = activation === 'running' || activation === 'setup' || activation === 'failed' ? activation : undefined
    // A graph's entities, or those its class's instance reports (`FixtureEntityType.entities`).
    const made = body.logic.kind === 'graph' ? body.entities ?? [] : types.find(t => t.id === body.logic.type)?.entities ?? []
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
    const graph = body.logic.kind === 'graph'
    const settings = (body.settings ?? []).map(setting => {
      const old = before?.settings.find(s => s.key === setting.key)
      const kept = setting.keep === true || (setting.hasValue === true && empty(setting.value))
      const value = kept ? old?.value ?? null : setting.value ?? null
      if (!graph) return { key: setting.key, value }
      const secret = setting.secret === true || (kept && old?.secret === true)
      return { key: setting.key, value, ...(empty(setting.label) ? {} : { label: setting.label }), ...(empty(setting.type) ? {} : { type: setting.type }), ...(secret ? { secret } : {}) }
    })
    const d = body.device
    const { definition: _definition, ...logicGraph } = (body.logic.kind === 'graph' ? body.logic.graph : {}) as Record<string, unknown>
    return {
      version: 1, id, revision, name: body.name,
      device: d ? { manufacturer: d.manufacturer ?? null, model: d.model ?? null, swVersion: d.swVersion ?? null, hwVersion: d.hwVersion ?? null, configurationUrl: d.configurationUrl ?? null, suggestedArea: d.suggestedArea ?? null, via: d.via ?? null } : null,
      settings, entities: structuredClone(body.entities ?? null),
      logic: body.logic.kind === 'graph' ? { kind: 'graph', type: null, graph: structuredClone(logicGraph) as unknown as VisualGraph } : { kind: 'class', type: body.logic.type, graph: null },
      source: '// HassSharp entity definition (fixture)', sourceMap: [], enabled, savedUtc: new Date().toISOString(), catalogGeneration: generation(),
      ...running(id, body, enabled), registeredEntities: [],
    } as EntityDocument
  }
  /** The document as an answer gives it: secrets masked, the registry's entries. */
  const answered = (document: EntityDocument) => {
    const type = document.logic.kind === 'class' ? types.find(t => t.id === document.logic.type) : undefined
    // As EntityLifecycle.WithoutSecretValues: a graph's setting is answered only when no setting of its key is secret, a
    // class's only when its type declares it, not secret.
    const masked = (key: string) => document.logic.kind === 'graph' ? document.settings.some(s => s.key === key && s.secret === true)
      : controls.libraryErrors || !type || !type.settings.some(s => s.key === key && !s.secret)
    const settings = document.settings.map(s => (masked(s.key) ? { ...s, value: null, secret: true, hasValue: !empty(s.value) } : s))
    const entries = [...(registry.get(document.id) ?? published.get(document.id) ?? [])].sort((a, b) => (a.uniqueId < b.uniqueId ? -1 : a.uniqueId > b.uniqueId ? 1 : 0))
    return structuredClone({ ...document, settings, registeredEntities: entries })
  }
  const summary = (document: EntityDocument) => { const { source: _source, sourceMap: _sourceMap, ...rest } = answered(document); return rest }
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
  /** The definition's stored document, or `not_found`. */
  const found = (id: string) => documents.get(id) ?? (() => { throw new Refused(fail('not_found', 'Entity definition not found')) })()
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
    const existing = documents.get(id)
    if ((existing?.revision ?? 0) !== expected) return fail('conflict', 'Revision conflict; reload the current document')
    if (existing && enabled !== undefined) refuse('enabled can only be given when saving a new definition; use set_enabled to turn a saved one on or off')
    const checked = preview(definition, existing)
    if (!checked.valid) return fail('validation', checked)
    const document = keep(stored(id, definition, expected + 1, existing?.enabled ?? enabled ?? true, existing))
    return ok({ id, saved: true, document: answered(document), ...started(document), ...(checked.diagnostics.length ? { diagnostics: checked.diagnostics } : {}) })
  }
  const answer = (request: any) => {
    switch (request.operation) {
      case 'types': return ok({ types: types.map(({ entities: _entities, ...type }) => structuredClone(type)), generation: generation() })
      case 'list': {
        const view = optionalText(request, 'view')
        if (view !== undefined && view !== 'home') refuse('Unknown list view; the entity definitions list has the view home')
        return ok(sorted().map(document => view === 'home'
          ? { ...summary(document), recentRuns: [], runsToday: 0, failedLastDay: 0, lastRunUtc: null, ...structuredClone(runs.get(document.id) ?? {}) }
          : summary(document)))
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
      case 'delete': {
        const id = text(request, 'id'), expected = revision(request)
        checkId(id)
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
  return { documents, registry, published, runs, types, requests, controls, seed, callWS }
}

export function visualFixture(
  options: { catalog?: VisualCatalogSnapshot; libraries?: { id: string; name: string; revision: number; source: string }[]; entityTypes?: FixtureEntityType[] } = {},
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
  const entities = entitiesFixture(options, () => controls.generation)
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
      if (request.operation === 'custom_preview') return ok(fixtureCustomPreview(request.nodeId ?? 'custom', request.source))
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
