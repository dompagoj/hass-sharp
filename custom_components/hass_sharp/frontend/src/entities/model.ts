import { addGraphNode, classSignature, declaredEntities, entityKey, entityKeyHolders, hasCode, isClassNode, nodeCatalog, slug, trimSeparators, type ClassSetting, type Graph, type GraphDefinition, type JsonValue, type VisualCatalogSnapshot } from '../visual/contract'
import type { HomeAutomation, SourceRange } from '../visual/api'

/**
 * An entity a definition describes. `key` is permanent once saved (`^[a-z][a-z0-9_]{0,39}$`); the backend keeps
 * members it does not know.
 */
export interface DefinitionEntity {
  key: string
  domain: string
  name?: string | null
  icon?: string | null
  deviceClass?: string | null
  entityCategory?: 'config' | 'diagnostic' | null
  /** Absent means true. */
  enabledByDefault?: boolean
  /** Only the options the domain lists (`entityDomains`). */
  options?: Record<string, unknown>
  /** In a read answer: the key of the Device node it is on, null for none. Derived from the graph, never sent. */
  device?: string | null
  /**
   * In a read answer: true for a C# entity's, which holds only its key, its class's domain, a null name (its class names
   * it) and its device; set by its node's kind, never read from a request.
   */
  csharp?: true
  [member: string]: unknown
}
/**
 * A device a definition's Device node declares, as a read answer lists it (`devices`): the node's config, without a
 * text left blank or a Part of of nothing. Derived from the graph, for reading only; a request's is ignored.
 */
export interface DerivedDevice {
  key: string
  name?: string
  manufacturer?: string
  model?: string
  swVersion?: string
  hwVersion?: string
  configurationUrl?: string
  suggestedArea?: string
  /** Part of: a Home Assistant device (`{deviceId}`) or another Device node of this definition (`{device}`, its key). */
  via?: { deviceId: string } | { device: string }
  [member: string]: unknown
}
/**
 * A setting of a definition, with its `label`, `type` and `secret`. An answer masks a secret as `value: null`, `secret:
 * true` and `hasValue` (whether a value is stored); `hasValue` is never changed by typing. A request sends `keep: true`
 * and no value to leave the stored value as it is.
 */
export interface DefinitionSetting {
  key: string
  value?: JsonValue
  label?: string | null
  /** A graph setting's type: `string`, `number` or `boolean`. */
  type?: string | null
  secret?: boolean
  hasValue?: boolean
  keep?: boolean
}
/** A Home Assistant device a device is part of (`{deviceId}`). */
interface DefinitionVia {
  deviceId?: string | null
}
/** What Home Assistant's device page shows of a device; each null for none. */
export interface DefinitionDevice {
  manufacturer?: string | null
  model?: string | null
  swVersion?: string | null
  hwVersion?: string | null
  configurationUrl?: string | null
  suggestedArea?: string | null
  /** Part of: the device it is connected through, if any. */
  via?: DefinitionVia | null
}
/** Where the behaviour comes from: a node graph. */
export type DefinitionLogic = { kind: 'graph'; graph: Graph }
/**
 * A definition as the user writes it: the `definition` of `preview` and `save`. It has no `device`: its devices are its
 * Device nodes, and the server refuses a request whose device is not null.
 */
export interface DefinitionBody {
  name: string
  settings?: DefinitionSetting[]
  logic: DefinitionLogic
}
/** An entity of a running definition, with the entity id Home Assistant gave it. */
export interface LiveEntity {
  uniqueId: string
  key: string
  domain: string
  name: string | null
  entityId: string | null
}
/** An entity of the definition in Home Assistant's entity registry, which has it while the definition is off or failed too. */
export interface RegisteredEntity {
  uniqueId: string
  entityId: string
  domain: string
  name: string | null
  disabled: boolean
}
/** A Trigger step of a saved definition, as an automation's are answered (`runnable_triggers`). */
export interface DefinitionTrigger {
  triggerId: string
  method: string
  /** Its "Can be started with Run now". */
  manual: boolean
  /** Whether `run` runs it now: it can be started with Run now, and the definition runs its saved revision and is running. */
  runnable: boolean
}
/** A saved definition as `read` and `save` answer it (`list` without `source` and `sourceMap`). */
export interface EntityDocument extends DefinitionBody {
  /** 3; one an earlier development build saved is unreadable. */
  version: 3
  id: string
  revision: number
  settings: DefinitionSetting[]
  /**
   * The entities its Entity nodes declare, then its C# entities', each with the device it is on, for reading only. The
   * entities a C# device's code adds are not listed: they are in `liveEntities` and `registeredEntities`, under the
   * unique id `<id>:<device key>:<entity key>`.
   */
  entities: DefinitionEntity[] | null
  /** The devices its Device nodes declare, in node order, then its C# devices', for reading only. */
  devices?: DerivedDevice[]
  enabled: boolean
  source: string
  sourceMap: SourceRange[]
  savedUtc?: string | null
  catalogGeneration?: string | null
  /** Its run history's key (`run_history`). */
  automationId: string
  active: boolean
  currentRevisionActive: boolean
  activationError: string | null
  status: 'running' | 'setup' | 'failed' | 'off'
  liveEntities: LiveEntity[]
  /** Sorted by unique id. */
  registeredEntities: RegisteredEntity[]
  /** Its Trigger steps, in the graph's order. Python adds it to every list, read and save answer. */
  triggers?: DefinitionTrigger[]
}
/**
 * A `list` entry: the document without its generated source, and with how many devices it makes in Home Assistant (its
 * Device nodes and C# devices) in place of the list of them.
 */
export type DefinitionSummary = Omit<EntityDocument, 'source' | 'sourceMap' | 'devices'> & { devices: number }
/** An entry of the entities page's list (`list` with `view: 'home'`): a summary and how it has been running. */
export type HomeDefinition = DefinitionSummary & Pick<HomeAutomation, 'recentRuns' | 'runsToday' | 'failedLastDay' | 'lastRunUtc'>
/**
 * The range a number setting of a class's signature declares, as its hint says it: "At least 1.", "At most 60." or
 * "Between 1 and 3600."; none without one. An `int`'s own bounds, given for a side the setting does not declare, are
 * no range.
 */
export function rangeHint(setting: Pick<ClassSetting, 'type' | 'minimum' | 'maximum'>): string | undefined {
  if (setting.type !== 'integer' && setting.type !== 'number') return
  const whole = setting.type === 'integer'
  const minimum = setting.minimum !== undefined && !(whole && setting.minimum <= -2147483648) ? setting.minimum : undefined
  const maximum = setting.maximum !== undefined && !(whole && setting.maximum >= 2147483647) ? setting.maximum : undefined
  if (minimum !== undefined && maximum !== undefined) return `Between ${minimum} and ${maximum}.`
  if (minimum !== undefined) return `At least ${minimum}.`
  if (maximum !== undefined) return `At most ${maximum}.`
}
/** How many of a definition's entities its card lists before "and N more"; a C# device's card on the canvas lists as many of its own. */
export const shownEntities = 6
/** An entity of a definition as its card lists it; `device`, the key of the C# device whose code added it, else none. */
export interface CardEntity { key: string; name: string; domain: string; entityId: string | null; disabled: boolean; device?: string }
const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })
/**
 * What follows a definition's id in one of its entities' unique ids: `<key>` for an entity it declares (an Entity node's
 * or a C# entity's), `<device key>:<key>` for one a C# device's code added. Keys hold no colon, so the tail is
 * read whole: never as a key split at its first colon alone, which would take `plug:relay` for the entity `plug`. A
 * unique id without the definition's prefix is no C# device's entity: it is its own key, whole.
 */
function uniqueIdTail(definitionId: string, uniqueId: string): { key: string; device?: string } {
  const prefix = `${definitionId}:`
  if (!uniqueId.startsWith(prefix)) return { key: uniqueId }
  const tail = uniqueId.slice(prefix.length)
  const parts = tail.split(':')
  return parts.length === 2 && parts[0] && parts[1] ? { device: parts[0], key: parts[1] } : { key: tail }
}
/**
 * A definition's entities: those it registered while running (or failed), else Home Assistant's entity registry's,
 * which keeps them while it is off or not loaded; a C# device's entities have neither before it has run. Whether Home
 * Assistant has one disabled always comes from the registry. Each is named by its name (its own, else the one the
 * definition gives it: an Entity node's, else an entity class's title), else its key, else its entity id; a device
 * class's entity by its own name, else its key. The entities the definition declares come first, then each device
 * class's, in the order of its nodes (one whose node is gone last); in each, enabled ones come first, each group by
 * name. A class node is a C# entity or C# device, or a node library's entity or device class, by `catalog`: a
 * library's is known only to the live catalog, and without it is listed as a node that is gone.
 */
export function cardEntities(item: Pick<HomeDefinition, 'id' | 'entities' | 'liveEntities' | 'registeredEntities'> & { logic?: HomeDefinition['logic'] }, catalog: VisualCatalogSnapshot = nodeCatalog): CardEntity[] {
  const graphNodes = Array.isArray(item.logic?.graph?.nodes) ? item.logic.graph.nodes : []
  const specOf = (node: (typeof graphNodes)[number]) => (typeof node?.kind === 'string' && Object.hasOwn(catalog.nodes, node.kind) ? catalog.nodes[node.kind] : undefined)
  // The class nodes (C# entities and C# devices, a library's entity and device classes) by the catalog, which knows a
  // library's only when it is the live one: without it, a library's class is not known here.
  const classNodes = graphNodes.filter(node => isClassNode(specOf(node)))
  const keyOf = (node: (typeof graphNodes)[number]) => (typeof node.config?.key === 'string' ? node.config.key : undefined)
  // An entity class's stored name is null (its class names it): its class's title, as its node shows it (a C# entity's
  // signature's, a library class's entry's).
  const titles = new Map(classNodes.filter(node => specOf(node)!.declares === 'entity').map(node => {
    const spec = specOf(node)!
    const title = ((hasCode(spec) ? node.config?.signature : spec.classSignature) as { title?: unknown } | undefined)?.title
    return [keyOf(node), typeof title === 'string' ? title : undefined] as const
  }))
  const declared = new Map((item.entities ?? []).map(entity => [entity.key, entity.name || (entity.csharp === true ? titles.get(entity.key) : undefined)]))
  const deviceOrder = classNodes.filter(node => specOf(node)!.declares === 'device').map(keyOf).filter((key): key is string => key !== undefined)
  const registry = new Map(item.registeredEntities.map(entry => [entry.uniqueId, entry]))
  const entity = (uniqueId: string, name: string | null | undefined, domain: string, entityId: string | null, disabled: boolean): CardEntity => {
    const { key, device } = uniqueIdTail(item.id, uniqueId)
    return { key, name: name || (device === undefined ? declared.get(key) : undefined) || key || entityId || '', domain, entityId, disabled, ...(device === undefined ? {} : { device }) }
  }
  const listed = item.liveEntities.length
    ? item.liveEntities.map(e => entity(e.uniqueId, e.name, e.domain, e.entityId, registry.get(e.uniqueId)?.disabled === true))
    : item.registeredEntities.map(e => entity(e.uniqueId, e.name, e.domain, e.entityId, e.disabled))
  const group = (e: CardEntity) => (e.device === undefined ? -1 : deviceOrder.includes(e.device) ? deviceOrder.indexOf(e.device) : deviceOrder.length)
  return listed.sort((a, b) => group(a) - group(b) || (group(a) === deviceOrder.length ? collator.compare(a.device!, b.device!) : 0)
    || Number(a.disabled) - Number(b.disabled) || collator.compare(a.name, b.name))
}
/**
 * The entities each C# device's code added, by the device's key, as the definition's card lists them ({@link
 * cardEntities}): only those whose unique id is `<definition id>:<device key>:<key>`, each by its name (else its key)
 * and entity id. None for a device Home Assistant has none of yet.
 */
export function deviceEntities(item: Parameters<typeof cardEntities>[0]): Record<string, { name: string; entityId: string | null }[]> {
  // Only this definition's own unique ids: one of another definition's is never taken for a device's. As the card reads
  // them: the running entities, else (nothing runs) the registry's; the registry also says which are disabled, listed last.
  const prefix = `${item.id}:`
  const own = <T extends { uniqueId: string }>(list: readonly T[]) => list.filter(entry => entry.uniqueId.startsWith(prefix))
  const live = own(item.liveEntities)
  const listed = cardEntities({ ...item, liveEntities: live, registeredEntities: live.length || !item.liveEntities.length ? own(item.registeredEntities) : [] })
  const byDevice: Record<string, { name: string; entityId: string | null }[]> = {}
  for (const entity of listed) if (entity.device !== undefined) (byDevice[entity.device] ??= []).push({ name: entity.name, entityId: entity.entityId })
  return byDevice
}
/**
 * The definition an editor graph carries in `graph.definition`: every member of its settings. The validator reads the
 * settings' keys and types, and ignores the rest. Its entities are its Entity nodes, and its devices its Device nodes.
 */
export interface EditorDefinition {
  settings: DefinitionSetting[]
}

/** What an entity's key looks like (`^[a-z][a-z0-9_]{0,39}$`). A setting's key only has to be non-blank and unique (ruling R15). */
export const entityKeyPattern = /^[a-z][a-z0-9_]{0,39}$/
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const editorDefinition = (graph: Graph) => graph.definition as unknown as EditorDefinition

/**
 * The graph the visual editor edits for a definition: its logic graph named after the definition, with the whole
 * definition under `definition`, so the editor's dirty tracking, undo, drafts and conflicts cover all of it. The
 * entities and devices a read answer lists are its Entity and Device nodes' (edited on the nodes), so they are not
 * carried.
 */
export function editorGraph(body: DefinitionBody): Graph {
  const definition: EditorDefinition = { settings: structuredClone(body.settings ?? []) }
  return { ...structuredClone(body.logic.graph), name: body.name, definition: definition as unknown as GraphDefinition }
}

/**
 * Whether the setting is masked as the API answers it and nobody changed it since: no value here (null or none) and
 * one stored (`hasValue`). A save keeps the stored value of such a setting, as `EntityDefinitionStore`'s `Kept` does.
 */
export const maskedSetting = (setting: DefinitionSetting | undefined): boolean =>
  !!setting && setting.hasValue === true && (setting.value === null || setting.value === undefined)

/**
 * A setting as a request sends it: a masked value nobody changed ({@link maskedSetting}) as `keep`, every other
 * without `hasValue` and `keep`.
 */
const settingToSend = (setting: DefinitionSetting): DefinitionSetting => {
  const { hasValue: _hasValue, keep: _keep, ...sent } = setting
  if (maskedSetting(setting)) {
    const { value: _value, ...described } = sent
    return { ...described, keep: true }
  }
  return sent
}

/**
 * The body an editor graph stands for, for `preview` and `save`: the name from the graph, the logic graph without its
 * definition. It has no `entities`: they are the graph's Entity nodes, and the server refuses a list of them. Nor has it
 * a `device` or `devices`: its devices are the graph's Device nodes (the server refuses a device, and ignores a list).
 */
export function definitionBody(graph: Graph): DefinitionBody {
  if (!record(graph.definition)) throw new Error('This graph has no definition: it is not an entity definition’s')
  const { definition, ...logic } = graph
  const { settings } = definition as unknown as EditorDefinition
  return {
    name: graph.name,
    settings: (settings ?? []).map(setting => settingToSend(structuredClone(setting))),
    logic: { kind: 'graph', graph: structuredClone(logic) },
  }
}

/**
 * The graph a browser draft keeps: every secret setting without its value (`hasValue` as stored), and of the definition
 * nothing else (not a device an earlier build's draft kept); every class node (a C# node, a node library's class)
 * without a value typed into a secret setting pin (one a hand-made or pasted graph may hold: the validators refuse it,
 * and a Setting gives it). Its settings are read from `catalog`, the live one: a library class's are its entry's, which
 * the built-in catalog does not have. Never changes its argument.
 */
export function draftGraph(graph: Graph, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  const nodes = Array.isArray(graph.nodes) ? graph.nodes.map(node => withoutTypedSecrets(node, catalog)) : graph.nodes
  if (!record(graph.definition)) return { ...graph, nodes }
  const definition = editorDefinition(graph)
  const settings = (definition.settings ?? []).map(setting => (setting.secret ? { ...setting, value: null } : setting))
  return { ...graph, nodes, definition: { settings } as unknown as GraphDefinition }
}
/** A class node without the values typed into its secret settings' pins, by its class's signature in `catalog`; any other node as it is. */
function withoutTypedSecrets(node: Graph['nodes'][number], catalog: VisualCatalogSnapshot): Graph['nodes'][number] {
  const secrets = new Set(classSignature(node, catalog)?.settings.filter(setting => setting.secret).map(setting => setting.key))
  const config = node.config as Record<string, unknown> | undefined
  const typed = record(config?.arguments) ? Object.entries(config.arguments) : []
  const secret = ([key, value]: [string, unknown]) => secrets.has(key) && record(value) && value.kind === 'literal'
  if (!typed.some(secret)) return node
  return { ...node, config: { ...config, arguments: Object.fromEntries(typed.filter(entry => !secret(entry))) } }
}

export { entityKey }

/**
 * A new definition: no settings, and a graph whose one step is an Entity node, a binary sensor named after the
 * definition (no name when that is blank) and keyed by that name. It has no device: its devices are Device nodes.
 */
export function newGraphDefinition(name: string): DefinitionBody {
  // The shape `createGraph` starts an automation with; its one step a new Entity node.
  const graph = addGraphNode({ version: 5, name, nodes: [], edges: [] }, 'entity', 'entity-1')
  const [node] = graph.nodes
  const { name: _typeLabel, ...config } = node.config
  const starter = { ...node, config: { ...config, key: entityKey(name), domain: 'binary_sensor', ...(name.trim() ? { name } : {}) } }
  return { name, settings: [], logic: { kind: 'graph', graph: { ...graph, nodes: [starter] } } }
}

/**
 * A definition id from a name, fixed at its first save: `Any motion` → `any_motion`, then `any_motion_2`, `_3`… past
 * those `taken`; at most 80 characters, `entity` when the name gives none. Never `new`, the address of a definition not
 * saved yet (`/entities/new`, ruling R17): `New` → `new_2`.
 */
export function definitionId(name: string, taken: string[]): string {
  const base = trimSeparators(slug(name).slice(0, 80)) || 'entity'
  const used = new Set([...taken, 'new'])
  if (!used.has(base)) return base
  for (let n = 2; ; n++) {
    const suffix = `_${n}`
    const id = trimSeparators(base.slice(0, 80 - suffix.length)) + suffix
    if (!used.has(id)) return id
  }
}

/**
 * The steps whose `entity` is one of the definition's entities by its key (catalog `documents: ['entity']`); a trigger's
 * `entity`, and an Entity's (`ha_entity`), is a Home Assistant entity id.
 */
export const definitionEntityKinds: readonly string[] = ['set_entity_state', 'set_entity_available']

/**
 * Renames the entity the Entity node or entity class (a C# entity, a library's entity class) `nodeId` declares, and
 * every step whose `entity` names an Entity node's ({@link definitionEntityKinds}; no step names an entity class's). The
 * same graph when that node does not declare `from`, `to` is not an entity key, or another entity already has it. The
 * steps stay with `from` while another node keeps it (two nodes declared the same key). What each node declares is read
 * from `catalog`.
 */
export function renameEntityKey(graph: Graph, from: string, to: string, nodeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  if (!record(graph.definition) || from === to || !entityKeyPattern.test(to)) return graph
  const declared = declaredEntities(graph, catalog)
  // An entity class's key is an entity key too: none is given another's.
  const keys = entityKeyHolders(graph, catalog).map(d => d.key)
  const hub = declared.some(d => d.nodeId === nodeId && (d.entity.key ?? '') === from)
  const spec = (kind: string) => (Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined)
  const csharp = graph.nodes.some(node => node.id === nodeId && isClassNode(spec(node.kind)) && spec(node.kind)!.declares === 'entity' && (record(node.config) ? node.config.key ?? '' : '') === from)
  if ((!hub && !csharp) || keys.includes(to)) return graph
  const follow = hub && from !== '' && keys.filter(key => key === from).length === 1
  return {
    ...graph,
    nodes: graph.nodes.map(node => node.id === nodeId ? { ...node, config: { ...node.config, key: to } }
      : follow && definitionEntityKinds.includes(node.kind) && node.config?.entity === from ? { ...node, config: { ...node.config, entity: to } } : node),
  }
}
