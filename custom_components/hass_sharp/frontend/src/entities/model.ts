import { addGraphNode, declaredEntities, entityKey, slug, trimSeparators, type Graph, type GraphDefinition, type JsonValue } from '../visual/contract'
import type { HomeAutomation, SourceRange } from '../visual/api'

/**
 * An entity a graph definition describes. `key` is permanent once saved (`^[a-z][a-z0-9_]{0,39}$`); the backend keeps
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
  [member: string]: unknown
}
/**
 * A device a graph definition's Device node declares, as a read answer lists it (`devices`): the node's config, without a
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
 * A setting: a graph's own (with `label`, `type` and `secret`), or a value for a class's setting (key and value only).
 * An answer masks a secret as `value: null`, `secret: true` and `hasValue` (whether a value is stored); `hasValue` is
 * never changed by typing. A request sends `keep: true` and no value to leave the stored value as it is.
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
/**
 * The device this definition's device is connected through: another definition's (`{definition}`), or a Home Assistant
 * device (`{deviceId}`). An answer also carries the other member as null.
 */
export interface DefinitionVia {
  definition?: string | null
  deviceId?: string | null
}
/** What Home Assistant's device page shows; each null for none (or the class's own). */
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
/** Where the behaviour comes from; an answer also carries the other kind's member as null. */
export type DefinitionLogic = { kind: 'graph'; graph: Graph; type?: null } | { kind: 'class'; type: string; graph?: null }
/** A definition as the user writes it: the `definition` of `preview` and `save`. */
export interface DefinitionBody {
  name: string
  device?: DefinitionDevice | null
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
/** A Trigger step of a saved graph definition, as an automation's are answered (`runnable_triggers`). */
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
  /** 3; a class definition saved by an earlier build is 1 or 2 (a graph one of those is unreadable). */
  version: 1 | 2 | 3
  id: string
  revision: number
  device: DefinitionDevice | null
  settings: DefinitionSetting[]
  /**
   * For a graph, the entities its Entity nodes declare, each with the device it is on, for reading only; none for a class
   * (its code makes them).
   */
  entities: DefinitionEntity[] | null
  /** For a graph, the devices its Device nodes declare, in node order, for reading only; none for a class. */
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
  /** A graph's Trigger steps, in the graph's order; none for a class. Python adds it to every list, read and save answer. */
  triggers?: DefinitionTrigger[]
}
/** A `list` entry: the document without its generated source. */
export type DefinitionSummary = Omit<EntityDocument, 'source' | 'sourceMap'>
/** An entry of the entities page's list (`list` with `view: 'home'`): a summary and how it has been running. */
export type HomeDefinition = DefinitionSummary & Pick<HomeAutomation, 'recentRuns' | 'runsToday' | 'failedLastDay' | 'lastRunUtc'>
/** A setting a class declares, as the `types` answer gives it. */
export interface EntityTypeSetting {
  key: string
  label: string
  description: string | null
  type: 'string' | 'boolean' | 'integer' | 'number' | 'enum'
  secret: boolean
  /** An `enum` setting's member names, in declaration order; empty for any other. */
  options: string[]
  minimum?: number
  maximum?: number
}
/** A device or entity class a node library declares, which a class definition creates (`types`). */
export interface EntityType {
  /** `library:<source id>:<namespace-qualified name>`. */
  id: string
  sourceId: string
  typeExpression: string
  kind: 'device' | 'entity'
  domain: string | null
  title: string
  description: string | null
  settings: EntityTypeSetting[]
}

/**
 * The definition an editor graph carries in `graph.definition`: every member of its settings and its device. The
 * validator reads the settings' keys and types, and ignores the rest. Its entities are its Entity nodes.
 */
export interface EditorDefinition {
  settings: DefinitionSetting[]
  device: DefinitionDevice | null
}

/** What an entity's key looks like (`^[a-z][a-z0-9_]{0,39}$`). A setting's key only has to be non-blank and unique (ruling R15). */
export const entityKeyPattern = /^[a-z][a-z0-9_]{0,39}$/
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const editorDefinition = (graph: Graph) => graph.definition as unknown as EditorDefinition

/**
 * The graph the visual editor edits for a graph definition: its logic graph named after the definition, with the whole
 * definition under `definition`, so the editor's dirty tracking, undo, drafts and conflicts cover all of it. Class
 * definitions are edited by a form, from the body itself. The entities and devices a read answer lists are its Entity
 * and Device nodes' (edited on the nodes), so they are not carried.
 */
export function editorGraph(body: DefinitionBody): Graph {
  if (body.logic.kind !== 'graph') throw new Error('A class definition has no editor graph; edit its body')
  const definition: EditorDefinition = {
    settings: structuredClone(body.settings ?? []),
    device: structuredClone(body.device ?? null),
  }
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
 * `devices`: they are the graph's Device nodes (the server ignores a list of them).
 */
export function definitionBody(graph: Graph): DefinitionBody {
  if (!record(graph.definition)) throw new Error('This graph has no definition: it is not an entity definition’s')
  const { definition, ...logic } = graph
  const { settings, device } = definition as unknown as EditorDefinition
  return {
    name: graph.name,
    device: structuredClone(device ?? null),
    settings: (settings ?? []).map(setting => settingToSend(structuredClone(setting))),
    logic: { kind: 'graph', graph: structuredClone(logic) },
  }
}

/** The graph a browser draft keeps: every secret setting without its value (`hasValue` as stored). Never changes its argument. */
export function draftGraph(graph: Graph): Graph {
  if (!record(graph.definition)) return { ...graph }
  const definition = editorDefinition(graph)
  const settings = (definition.settings ?? []).map(setting => (setting.secret ? { ...setting, value: null } : setting))
  return { ...graph, definition: { ...definition, settings } as unknown as GraphDefinition }
}

export { entityKey }

/**
 * A new graph definition: no device, no settings, and a graph whose one step is an Entity node, a binary sensor named
 * after the definition (no name when that is blank) and keyed by that name.
 */
export function newGraphDefinition(name: string): DefinitionBody {
  // The shape `createGraph` starts an automation with; its one step a new Entity node.
  const graph = addGraphNode({ version: 5, name, nodes: [], edges: [] }, 'entity', 'entity-1')
  const [node] = graph.nodes
  const { name: _typeLabel, ...config } = node.config
  const starter = { ...node, config: { ...config, key: entityKey(name), domain: 'binary_sensor', ...(name.trim() ? { name } : {}) } }
  return { name, device: null, settings: [], logic: { kind: 'graph', graph: { ...graph, nodes: [starter] } } }
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
 * `entity` is a Home Assistant entity id.
 */
export const definitionEntityKinds: readonly string[] = ['set_entity_state', 'set_entity_available', 'show_frame']

/**
 * Renames the entity the Entity node `nodeId` declares, and every step whose `entity` names it
 * ({@link definitionEntityKinds}). The same graph when that node does not declare `from`, `to` is not an entity key, or
 * another entity already has it. The steps stay with `from` while another node keeps it (two nodes declared the same key).
 */
export function renameEntityKey(graph: Graph, from: string, to: string, nodeId: string): Graph {
  if (!record(graph.definition) || from === to || !entityKeyPattern.test(to)) return graph
  const declared = declaredEntities(graph)
  const keys = declared.map(d => d.entity.key)
  if (!declared.some(d => d.nodeId === nodeId && (d.entity.key ?? '') === from) || keys.includes(to)) return graph
  const follow = from !== '' && keys.filter(key => key === from).length === 1
  return {
    ...graph,
    nodes: graph.nodes.map(node => node.id === nodeId ? { ...node, config: { ...node.config, key: to } }
      : follow && definitionEntityKinds.includes(node.kind) && node.config?.entity === from ? { ...node, config: { ...node.config, entity: to } } : node),
  }
}
