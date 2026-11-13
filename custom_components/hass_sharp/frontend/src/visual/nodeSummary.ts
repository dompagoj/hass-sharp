import { dataOutputs, inputValue, nodeCatalog, type Graph, type GraphNode, type VisualCatalogSnapshot } from './contract'
import { domainWords, typeLabel } from './typeLabels'

/** Registry names used to say "Hallway" rather than `light.hallway`; every list is optional. */
export interface SummaryNames {
  entities?: readonly { entity_id: string; name: string | null }[]
  areas?: readonly { area_id: string; name: string }[]
  devices?: readonly { id: string; name: string | null }[]
  floors?: readonly { floor_id: string; name: string }[]
  labels?: readonly { label_id: string; name: string }[]
}

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown) => (typeof value === 'string' ? value.trim() : '')
const lookups = new WeakMap<object, Map<string, string>>()
function names<T>(list: readonly T[] | undefined, id: (item: T) => string, name: (item: T) => string | null): Map<string, string> {
  if (!list) return new Map()
  let map = lookups.get(list)
  if (!map) {
    map = new Map()
    for (const item of list) {
      const label = name(item)
      if (label) map.set(id(item), label)
    }
    lookups.set(list, map)
  }
  return map
}
/** The friendly name of an entity, else its object id in words ("light.hallway_lamp" → "Hallway lamp"). */
export function entityName(id: string, registry?: SummaryNames): string {
  const known = names(registry?.entities, e => e.entity_id, e => e.name).get(id)
  if (known) return known
  if (id === 'all') return 'all entities'
  const dot = id.indexOf('.')
  return dot > 0 ? domainWords(id.slice(dot + 1)) : id
}
const list = (items: string[]) => (items.length <= 2 ? items.join(' and ') : `${items[0]} +${items.length - 1}`)
const strings = (value: unknown) =>
  (Array.isArray(value) ? value : value === undefined || value === null ? [] : [value]).filter((v): v is string => typeof v === 'string' && v !== '')

/** "30 s", "5 minutes", "1 h 30 min". Accepts seconds, "HH:MM:SS" and Home Assistant duration objects. */
export function durationText(value: unknown): string | undefined {
  let seconds: number | undefined
  if (typeof value === 'number' && Number.isFinite(value)) seconds = value
  else if (typeof value === 'string' && /^\d+(:\d{1,2}){1,2}(\.\d+)?$/.test(value)) {
    const parts = value.split(':').map(Number)
    seconds = parts.reduce((sum, part) => sum * 60 + part, 0)
  } else if (record(value)) {
    const part = (key: string) => (typeof value[key] === 'number' ? (value[key] as number) : 0)
    seconds = part('days') * 86400 + part('hours') * 3600 + part('minutes') * 60 + part('seconds') + part('milliseconds') / 1000
  }
  if (seconds === undefined || seconds < 0) return
  const plural = (n: number, unit: string) => `${n} ${unit}${n === 1 ? '' : 's'}`
  if (seconds === 0) return '0 seconds'
  if (seconds % 86400 === 0) return plural(seconds / 86400, 'day')
  if (seconds % 3600 === 0) return plural(seconds / 3600, 'hour')
  if (seconds % 60 === 0 && seconds < 3600) return plural(seconds / 60, 'minute')
  if (seconds < 60) return `${Math.round(seconds * 100) / 100} s`
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = Math.round(seconds % 60)
  return [h && `${h} h`, m && `${m} min`, s && `${s} s`].filter(Boolean).join(' ')
}

/** A short display of a stored literal value. */
function valueText(value: unknown, registry?: SummaryNames): string | undefined {
  if (value === undefined) return
  if (value === null) return 'nothing'
  if (typeof value === 'boolean') return value ? 'true' : 'false'
  if (typeof value === 'number') return String(value)
  if (typeof value === 'string') return /^[a-z0-9_]+\.[a-z0-9_]+$/.test(value) ? entityName(value, registry) : `“${value}”`
  if (Array.isArray(value)) return value.length ? `${value.length} item${value.length === 1 ? '' : 's'}` : 'an empty list'
  return 'an object'
}

interface Context {
  node: GraphNode
  catalog: VisualCatalogSnapshot
  registry?: SummaryNames
  graph?: Graph
}
/** What an input currently reads: a literal, a variable or the wired output's label; undefined when not set. */
function operandText(ctx: Context, port: string): string | undefined {
  const edge = ctx.graph?.edges.find(e => e?.kind === 'data' && e.target === ctx.node.id && e.targetPort === port)
  if (edge?.kind === 'data') {
    const source = ctx.graph!.nodes.find(n => n?.id === edge.source)
    const output = source ? dataOutputs(source, ctx.catalog)[edge.sourcePort] : undefined
    return (output?.label ?? edge.sourcePort).toLowerCase()
  }
  const operand = inputValue(ctx.node, port, ctx.catalog)
  if (!record(operand)) return
  if (operand.kind === 'variable') return text(operand.name) || undefined
  if (operand.kind === 'literal') return valueText(operand.value, ctx.registry)
}
const entityOperand = (ctx: Context, port: string) => {
  const operand = inputValue(ctx.node, port, ctx.catalog)
  return record(operand) && operand.kind === 'literal' && typeof operand.value === 'string' ? entityName(operand.value, ctx.registry) : operandText(ctx, port)
}
const operators: Record<string, string> = { eq: '=', ne: '≠', lt: '<', le: '≤', gt: '>', ge: '≥' }

function targetText(ctx: Context): string | undefined {
  const operand = ctx.node.config.target
  if (!record(operand) || operand.kind !== 'literal') return operandText(ctx, 'target')
  const value = typeof operand.value === 'string' ? { entity_id: operand.value } : record(operand.value) ? operand.value : {}
  const registry = ctx.registry
  const parts = [
    ...strings(value.entity_id).map(id => entityName(id, registry)),
    ...strings(value.area_id).map(id => names(registry?.areas, a => a.area_id, a => a.name).get(id) ?? domainWords(id)),
    ...strings(value.device_id).map(id => names(registry?.devices, d => d.id, d => d.name).get(id) ?? 'a device'),
    ...strings(value.floor_id).map(id => names(registry?.floors, f => f.floor_id, f => f.name).get(id) ?? domainWords(id)),
    ...strings(value.label_id).map(id => names(registry?.labels, l => l.label_id, l => l.name).get(id) ?? domainWords(id)),
  ]
  return parts.length ? list(parts) : undefined
}

function triggerText(ctx: Context): string | undefined {
  const config = ctx.node.config
  const type = text(config.type) || text(ctx.catalog.nodes.trigger?.config.type?.default) || 'entity'
  if (type === 'entity') return text(config.entity) ? `When ${entityName(text(config.entity), ctx.registry)} changes` : undefined
  if (type === 'daily') return text(config.time) ? `Every day at ${text(config.time)}` : undefined
  if (type === 'once') return text(config.at) ? `Once at ${text(config.at).replace('T', ' ').replace(/(:\d\d)(:00)?(\.\d+)?(Z|[+-]\d\d:\d\d)?$/, '$1')}` : undefined
  if (type === 'manual') return 'Run manually'
  if (type === 'startup') return 'When Home Assistant starts'
  if (type !== 'ha' || !record(config.trigger)) return
  const t = config.trigger
  const platform = text(t.trigger) || text(t.platform)
  const who = list(strings(t.entity_id).map(id => entityName(id, ctx.registry))) || 'an entity'
  const hold = durationText(t.for)
  const forText = hold ? ` for ${hold}` : ''
  const state = (value: unknown) => strings(value).join(' or ')
  switch (platform) {
    case 'state':
      if (t.to !== undefined && t.to !== null && state(t.to)) return `When ${who} becomes ${state(t.to)}${forText}`
      if (state(t.from)) return `When ${who} stops being ${state(t.from)}${forText}`
      return `When ${who} changes${forText}`
    case 'numeric_state': {
      const above = t.above !== undefined ? String(t.above) : '', below = t.below !== undefined ? String(t.below) : ''
      if (above && below) return `When ${who} is between ${above} and ${below}${forText}`
      if (above) return `When ${who} rises above ${above}${forText}`
      if (below) return `When ${who} drops below ${below}${forText}`
      return `When ${who} changes${forText}`
    }
    case 'time': {
      const at = strings(t.at).map(v => (/^[a-z_]+\.[a-z0-9_]+$/.test(v) ? entityName(v, ctx.registry) : v.replace(/:00$/, '')))
      return at.length ? `At ${list(at)}` : undefined
    }
    case 'time_pattern': {
      for (const [key, unit] of [['seconds', 'second'], ['minutes', 'minute'], ['hours', 'hour']] as const) {
        const every = /^\/(\d+)$/.exec(String(t[key] ?? ''))?.[1]
        if (every) return `Every ${every === '1' ? unit : `${every} ${unit}s`}`
      }
      return 'On a time pattern'
    }
    case 'sun': return `At ${text(t.event) || 'sunset'}${text(t.offset) ? ` (${text(t.offset)})` : ''}`
    case 'homeassistant': return text(t.event) === 'shutdown' ? 'When Home Assistant stops' : 'When Home Assistant starts'
    case 'event': return text(t.event_type) ? `When ${text(t.event_type)} fires` : 'When an event fires'
    case 'mqtt': return text(t.topic) ? `When MQTT ${text(t.topic)} receives a message` : undefined
    case 'webhook': return 'When the webhook is called'
    case 'zone': return `When ${who} ${text(t.event) === 'leave' ? 'leaves' : 'enters'} ${text(t.zone) ? entityName(text(t.zone), ctx.registry) : 'a zone'}`
    case 'template': return 'When a template becomes true'
    default: return platform ? `When ${domainWords(platform).toLowerCase()} triggers` : undefined
  }
}

function kindText(ctx: Context): string | undefined {
  const { node, catalog } = ctx
  const config = node.config
  switch (node.kind) {
    case 'trigger': return triggerText(ctx)
    case 'service': {
      const service = text(config.service)
      if (!service) return
      const target = targetText(ctx)
      return target ? `${service} · ${target}` : service
    }
    case 'condition': {
      const entity = entityOperand(ctx, 'entity')
      return entity && text(config.state) ? `If ${entity} is ${text(config.state)}` : undefined
    }
    case 'if': { const value = operandText(ctx, 'value'); return value ? `If ${value}` : undefined }
    case 'compare': {
      const left = operandText(ctx, 'left') ?? 'left', right = operandText(ctx, 'right') ?? 'right'
      return `If ${left} ${operators[text(config.operator) || 'eq'] ?? '='} ${right}`
    }
    case 'and': case 'or': case 'xor': {
      const left = operandText(ctx, 'left'), right = operandText(ctx, 'right')
      return left && right ? `${left} ${node.kind} ${right}` : undefined
    }
    case 'not': { const value = operandText(ctx, 'value'); return value ? `Not ${value}` : undefined }
    case 'delay': {
      const operand = config.seconds
      const duration = record(operand) && operand.kind === 'literal' ? durationText(operand.value) : undefined
      const other = duration ?? operandText(ctx, 'seconds')
      return other ? `Wait ${duration ?? `for ${other}`}` : undefined
    }
    case 'wait_until': {
      const entity = entityOperand(ctx, 'entity'), right = operandText(ctx, 'right')
      const what = text(config.field) === 'attribute' && text(config.attribute) ? ` ${text(config.attribute)}` : ''
      return entity && right ? `Wait until ${entity}${what} ${operators[text(config.operator) || 'eq'] ?? '='} ${right}` : undefined
    }
    case 'read_entity': { const entity = entityOperand(ctx, 'entity'); return entity ? `Read ${entity}` : undefined }
    case 'entity_field': {
      const entity = operandText(ctx, 'entity') ?? 'entity'
      return `${entity} · ${text(config.field) === 'attribute' ? text(config.attribute) || 'attribute' : 'state'}`
    }
    case 'set_value': { const value = operandText(ctx, 'value'); return value ? `Set to ${value}` : undefined }
    case 'convert': return text(config.type) ? `To ${typeLabel(text(config.type), catalog).toLowerCase()}` : undefined
    case 'log': { const value = operandText(ctx, 'value'); return value ? `Log ${value}` : undefined }
    case 'repeat': { const count = operandText(ctx, 'count'); return count ? `Repeat ${count} times` : undefined }
    case 'foreach': return 'For each item'
    case 'while': return 'Repeat while the condition holds'
    case 'until': return 'Repeat until the condition holds'
    case 'parallel': { const n = Array.isArray(config.branches) ? config.branches.length : 0; return `${n} branch${n === 1 ? '' : 'es'} at once` }
    case 'stop': return 'End the run'
    case 'custom': {
      const line = text(config.code).split('\n').map(l => l.trim()).find(l => l && !l.startsWith('//'))
      return line ? (line.length > 60 ? `${line.slice(0, 59)}…` : line) : undefined
    }
    case 'get_field': return text(config.pointer) ? `Field ${text(config.pointer)}` : undefined
    case 'object_create': { const keys = record(config.fields) ? Object.keys(config.fields) : []; return keys.length ? `Fields ${keys.join(', ')}` : undefined }
    case 'array_create': { const n = Array.isArray(config.items) ? config.items.length : 0; return n ? `${n} item${n === 1 ? '' : 's'}` : undefined }
    case 'condition_result': { const value = operandText(ctx, 'value'); return value ? `Return ${value}` : undefined }
    case 'loop_result': return 'Finish this iteration'
  }
}

/**
 * A one-line, human description of a node for its card and for lists: "light.turn_on · Hallway",
 * "When Bathroom TRV becomes heat for 30 minutes", "Wait 5 minutes", "If brightness > 40". It falls back to the
 * kind's description. `graph`, when given, names the outputs wired into inputs.
 */
export function nodeSummary(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog, entities?: SummaryNames, graph?: Graph): string {
  let summary: string | undefined
  try {
    summary = kindText({ node, catalog, registry: entities, graph })
  } catch {
    summary = undefined // A malformed config still gets the kind's description.
  }
  return summary || (Object.hasOwn(catalog.nodes, node.kind) ? catalog.nodes[node.kind].description : 'Unknown node kind')
}
