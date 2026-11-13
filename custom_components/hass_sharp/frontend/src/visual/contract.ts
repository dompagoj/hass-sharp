import rawCatalog from '../../../visual_node_catalog.json'
import { domainWords, typeLabel } from './typeLabels'

/** The catalog's graphVersion is the visual graph format. The document/API envelope remains version 1. */
export type DataType = string
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type Operand = { kind: 'literal'; type: DataType; value: JsonValue } | { kind: 'variable'; name: string } | { kind: 'unset' }
export interface GraphNode { id: string; kind: string; config: Record<string, unknown>; label?: string; position?: { x: number; y: number } }
export interface ExecutionEdge { id: string; kind?: 'execution'; source: string; target: string; port?: string }
export interface DataEdge { id: string; kind: 'data'; source: string; target: string; sourcePort: string; targetPort: string }
export type GraphEdge = ExecutionEdge | DataEdge
export interface Graph { version: 5; name: string; nodes: GraphNode[]; edges: GraphEdge[]; imports?: string; members?: string }
export interface GraphDiagnostic { nodeId: string | null; message: string; severity: number; inputId?: string; portId?: string }
export interface ConfigSpec { label?: string; description?: string; type: string; required?: boolean; default?: unknown; enum?: unknown[]; enumSource?: 'types'; min?: number; max?: number; pattern?: string; format?: 'date-time-offset'; minItems?: number; maxItems?: number }
/** Constraints (enum, pattern, minItems, maxItems, min, max, integer) apply to literal operands only; wires are constrained by type. */
export interface InputSpec {
  nullable?: boolean; hasDefaultValue?: boolean; label?: string; types: string[]; required: boolean; description: string; default?: unknown; min?: number; max?: number; integer?: boolean
  enum?: JsonValue[]; enumLabels?: string[]; pattern?: string; minItems?: number; maxItems?: number; step?: number; unit?: string
  selector?: Record<string, unknown>; advanced?: boolean; section?: string | null; example?: JsonValue
}
/** `when` names a boolean config key; the output exists only while it is true. */
export interface OutputSpec { label?: string; type: string; description: string; visibility?: string; pointer?: string; refineFrom?: string; when?: string }
export interface NodeSpec { sourceId?: string; argumentStorage?: 'arguments'; label: string; category: string; description: string; minGraphVersion: number; config: Record<string, ConfigSpec>; inputs: Record<string, InputSpec>; outputs: Record<string, OutputSpec>; execution: string[]; scopePorts?: string[]; requiredExecution?: string[]; dynamicExecution?: string; dynamicInputs?: string; dynamicOutputs?: string; comparison?: { leftInput?: string; typeConfig?: string; rightInput: string; operatorConfig: string; orderingOperators: string[]; orderingType: string } }
export interface TypeSpec { clrType?: string; elementType?: string; description: string; label?: string; category?: string; nullable?: boolean; literalKind?: string | null; assignableTo?: string[]; refines?: string; pattern?: string; min?: number; max?: number; character?: boolean; enumValues?: JsonValue[] }
export interface ParametricTypeSpec { param: string; valuePattern: string; label: string }
/** Contract C4: one normalised Home Assistant service field. */
export interface ServiceFieldSchema {
  label: string; description: string; types: string[]; required: boolean; advanced: boolean; section: string | null; order: number
  min: number | null; max: number | null; integer: boolean; step: number | null; unit: string | null; enum: JsonValue[] | null; enumLabels: string[] | null
  pattern: string | null; minItems: number | null; maxItems: number | null; default: JsonValue; example: JsonValue; selector: Record<string, unknown> | null; filter: Record<string, unknown> | null
}
export interface ServiceTargetFilter { domain: string[]; deviceClass: string[]; supportedFeatures: number[] }
export interface ServiceSchema {
  domain: string; service: string; name: string; description: string
  target: { entity: ServiceTargetFilter[]; device: Record<string, unknown>[] } | null
  response: 'none' | 'optional' | 'only'; fields: Record<string, ServiceFieldSchema>
}
/** Contract C4: the services section, keyed by `domain.service`. Never part of the catalog root. */
export interface ServicesSection { servicesGeneration: string; items: Record<string, ServiceSchema> }
export interface VisualCatalogSnapshot { services?: ServicesSection; version: number; graphVersion: number; limits: { nodes: number; edges: number; nesting: number; executionSteps: number; loopIterations: number }; types: Record<string, TypeSpec>; parametricTypes?: Record<string, ParametricTypeSpec>; nodes: Record<string, NodeSpec>; triggerOutputs?: Record<string, Record<string, OutputSpec>>; structures: { loopDeclaration: { name: { pattern: string } }; branchId: { pattern: string } } }
/** Pass the server-produced catalog explicitly for each document; graph metadata is never type authority. */
export const nodeCatalog = rawCatalog as unknown as VisualCatalogSnapshot
const nodeSpec = (kind: unknown, catalog: VisualCatalogSnapshot = nodeCatalog) => typeof kind === 'string' && Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const declarations = (node: GraphNode, key: 'carry' | 'exports') => (Array.isArray(node.config[key]) ? node.config[key] as unknown[] : []).filter(record)
const text = (value: unknown, fallback = '') => typeof value === 'string' ? value : fallback
const configuredType = (type: string, node: GraphNode) => type.startsWith('$') ? text(node.config[type.slice(1)]) : type
export const effectiveTriggerMethod = (node: GraphNode) => 'Run_' + Array.from(new TextEncoder().encode(node.id), byte => byte.toString(16).padStart(2, '0').toUpperCase()).join('')

// The C1 type rule, mirrored by HassSharp/Visual/VisualTypes.cs. Refinement is decided before
// root assignability: a refined type is never reached from its base implicitly.
// Library ids contain colons (global::HassSharp.EntityStateSnapshot); only this exact shape is parametric.
const parametricId = /^([a-z_]+):([a-z0-9_]+)$/
const matches = (value: string, pattern: string) => new RegExp(pattern).test(value)
/** An exact catalog type, or a type synthesized from `parametricTypes`; undefined when unknown. */
export function resolveTypeSpec(id: string, catalog: VisualCatalogSnapshot = nodeCatalog): TypeSpec | undefined {
  if (Object.hasOwn(catalog.types, id)) return catalog.types[id]
  const match = parametricId.exec(id)
  if (!match) return
  const [, name, param] = match
  const family = catalog.parametricTypes && Object.hasOwn(catalog.parametricTypes, name) ? catalog.parametricTypes[name] : undefined
  const root = Object.hasOwn(catalog.types, name) ? catalog.types[name] : undefined
  if (!family || !root || !matches(param, family.param)) return
  const label = family.label.replaceAll('{Param}', param[0].toUpperCase() + param.slice(1)).replaceAll('{param}', param)
  const resolved: TypeSpec = { refines: name, pattern: family.valuePattern.replaceAll('{param}', param), label, description: label }
  for (const key of ['clrType', 'literalKind', 'category', 'nullable'] as const) if (root[key] !== undefined) Object.assign(resolved, { [key]: root[key] })
  return resolved
}
/** The type, then each type it refines, ending at its root; empty when any link is unknown or cyclic. */
export function typeChain(id: string, catalog: VisualCatalogSnapshot = nodeCatalog): string[] {
  const chain: string[] = []
  for (let current: string | undefined = id; current !== undefined;) {
    const spec = resolveTypeSpec(current, catalog)
    if (chain.includes(current) || !spec) return []
    chain.push(current)
    current = spec.refines
  }
  return chain
}
export const rootType = (id: string, catalog: VisualCatalogSnapshot = nodeCatalog) => typeChain(id, catalog).at(-1) ?? id
export const refinesType = (id: string, ancestor: string, catalog: VisualCatalogSnapshot = nodeCatalog) => typeChain(id, catalog).includes(ancestor)
export function isAssignable(source: string, target: string, catalog: VisualCatalogSnapshot = nodeCatalog): boolean {
  const chain = typeChain(source, catalog)
  if (!chain.length || !typeChain(target, catalog).length) return false
  if (source === target) return true
  if (resolveTypeSpec(target, catalog)!.refines !== undefined) return chain.includes(target)
  const root = chain.at(-1)!
  return root === target || (catalog.types[root].assignableTo ?? []).includes(target)
}
export function executionPorts(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): string[] {
  const spec = nodeSpec(node.kind, catalog)
  return spec ? [...(spec.dynamicExecution === 'branches' && Array.isArray(node.config.branches) ? node.config.branches.filter((id): id is string => typeof id === 'string').map(id => `branch:${id}`) : []), ...spec.execution] : []
}
export function dataOutputs(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, OutputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.outputs ?? {}).map(([key, value]) => [key, { ...value, type: configuredType(value.type, node) }]))) as Record<string, OutputSpec>
  if (spec?.dynamicOutputs === 'loop') for (const key of ['carry', 'exports'] as const) for (const item of declarations(node, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { type: item.type, label: item.name, description: `${key} value ${item.name}`, visibility: key === 'carry' ? 'loop' : 'outside' }
  }
  if (spec?.dynamicOutputs === 'trigger') Object.assign(ports, triggerOutputs(node, catalog))
  // An output with `when` exists only while that boolean config is true (D3, D9).
  for (const [key, port] of Object.entries(ports)) if (port.when && node.config[port.when] !== true) delete ports[key]
  return ports
}
/** The C4 entry of the node's configured service, or undefined without a services section or for an unknown service. */
export function serviceSchema(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): ServiceSchema | undefined {
  const items = catalog.services?.items, service = node.config.service
  return items && typeof service === 'string' && Object.hasOwn(items, service) ? items[service] : undefined
}
/** Entity domains the service's target accepts; empty when any entity filter, or the target itself, is unrestricted by domain. */
export function serviceTargetDomains(schema: ServiceSchema): string[] {
  const filters = (Array.isArray(schema.target?.entity) ? schema.target.entity : []).filter(record)
  const domains = filters.map(f => (Array.isArray(f.domain) ? f.domain : []).filter((d): d is string => typeof d === 'string' && d !== ''))
  return !domains.length || domains.some(d => !d.length) ? [] : [...new Set(domains.flat())]
}
const serviceFieldConstraints = ['min', 'max', 'integer', 'enum', 'enumLabels', 'pattern', 'minItems', 'maxItems', 'step', 'unit', 'selector', 'advanced', 'section', 'example', 'default'] as const
/** A known service field as an input spec, so catalog literal constraints (C2) apply to it. */
function serviceFieldInput(key: string, field: ServiceFieldSchema): InputSpec {
  const port: InputSpec = { types: [...(field.types ?? [])], required: field.required === true, label: text(field.label, key), description: text(field.description) || `Service field ${key}` }
  for (const name of serviceFieldConstraints) if (field[name] !== null && field[name] !== undefined) Object.assign(port, { [name]: structuredClone(field[name]) })
  return port
}
/**
 * Contract C6, mirrored by `VisualNodeCatalog.TriggerOutputs`: the rows of `triggerOutputs` whose `|`-separated
 * key lists the trigger type (or `ha:<platform>`), after `*`. Each port keeps its JSON pointer; `refineFrom` narrows its type.
 */
export function triggerOutputs(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, OutputSpec> {
  const type = text(node.config.type, text(nodeSpec(node.kind, catalog)?.config.type?.default))
  const native = record(node.config.trigger) ? node.config.trigger : undefined
  const key = type === 'ha' ? 'ha:' + (typeof native?.trigger === 'string' ? native.trigger : text(native?.platform)) : type
  const ports = Object.create(null) as Record<string, OutputSpec>
  for (const [keys, outputs] of Object.entries(catalog.triggerOutputs ?? {}))
    if (record(outputs) && (keys === '*' || keys.split('|').includes(key)))
      for (const [port, output] of Object.entries(outputs)) if (record(output)) {
        const refined = output.refineFrom ? refinedType(output.type, configValue(node.config, output.refineFrom), catalog) : undefined
        ports[port] = { ...output, ...(refined ? { type: refined } : {}) }
      }
  return ports
}
const configValue = (config: Record<string, unknown>, path: string) => path.split('.').reduce<unknown>((current, segment) => record(current) ? current[segment] : undefined, config)
/** `<type>:<param>` when the value, or every item of a non-empty list, is a literal of that one parametric type. */
function refinedType(type: string, value: unknown, catalog: VisualCatalogSnapshot): string | undefined {
  let refined: string | undefined
  for (const item of Array.isArray(value) ? value : [value]) {
    if (!validLiteral(type, item, catalog) || typeof item !== 'string' || item.indexOf('.') <= 0) return
    const candidate = `${type}:${item.slice(0, item.indexOf('.'))}`
    if ((refined !== undefined && refined !== candidate) || !validLiteral(candidate, item, catalog)) return
    refined = candidate
  }
  return refined
}
export function dataInputs(node: GraphNode, loopOwner?: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, InputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.inputs ?? {}).map(([key, value]) => [key, { ...value, types: value.types.flatMap(t => t === '$any' ? Object.keys(catalog.types) : [configuredType(t, node)]) }]))) as Record<string, InputSpec>
  if (node.kind === 'foreach' && ports.items) ports.items.types = ['array', ...Object.entries(catalog.types).filter(([, type]) => !type.nullable && type.elementType && isAssignable(type.elementType, text(node.config.itemType), catalog)).map(([id]) => id)]
  const types = Object.keys(catalog.types)
  const dynamic = spec?.dynamicInputs
  if (dynamic === 'fields' && record(node.config.fields)) for (const key of Object.keys(node.config.fields)) ports[`field:${key}`] = { types, required: true, label: key, description: `Object field ${key}` }
  if (dynamic === 'service') {
    // Ports are the keys of config.fields (D3); a known service only refines their types and the target's.
    const schema = serviceSchema(node, catalog)
    if (record(node.config.fields)) for (const key of Object.keys(node.config.fields)) {
      const field = schema && record(schema.fields) && Object.hasOwn(schema.fields, key) ? schema.fields[key] : undefined
      ports[`field:${key}`] = record(field) ? serviceFieldInput(key, field) : { types, required: false, label: key, description: `Service field ${key}` }
    }
    if (schema && ports.target) {
      const domains = serviceTargetDomains(schema)
      ports.target.types = ['target', ...(domains.length ? domains.map(d => `entity_id:${d}`) : ['entity_id'])]
    }
  }
  if (dynamic === 'items' && Array.isArray(node.config.items)) node.config.items.forEach((_, i) => { ports[`item:${i}`] = { types, required: true, label: `Item ${i}`, description: `Array item ${i}` } })
  if (dynamic === 'loop' && loopOwner) for (const key of ['carry', 'exports'] as const) for (const item of declarations(loopOwner, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { types: [item.type], required: false, label: item.name, description: `Next ${key} value` }
  }
  return ports
}
/** An own property only, as `TryGetInputValue` reads it: a field named `constructor` or `toString` is not inherited. */
const own = (container: unknown, key: string): unknown => record(container) && Object.hasOwn(container, key) ? container[key] : undefined
export function inputValue(node: GraphNode, port: string, catalog: VisualCatalogSnapshot): unknown {
  if (nodeSpec(node.kind, catalog)?.argumentStorage === 'arguments') return own(node.config.arguments, port)
  if (port.startsWith('field:')) return own(node.config.fields, port.slice(6))
  if (port.startsWith('item:') && Array.isArray(node.config.items)) {
    const index = Number(port.slice(5))
    return Number.isInteger(index) && index >= 0 && index < node.config.items.length ? node.config.items[index] : undefined
  }
  if (node.kind === 'loop_result') return own(node.config.values, port)
  return own(node.config, port)
}
export function setInput(node: GraphNode, port: string, value: Operand, catalog: VisualCatalogSnapshot) {
  if (nodeSpec(node.kind, catalog)?.argumentStorage === 'arguments') {
    if (!record(node.config.arguments)) node.config.arguments = {}
    ;(node.config.arguments as Record<string, unknown>)[port] = value
    return
  }
  if (port.startsWith('field:')) (node.config.fields as Record<string, unknown>)[port.slice(6)] = value
  else if (port.startsWith('item:')) (node.config.items as unknown[])[Number(port.slice(5))] = value
  else if (node.kind === 'loop_result') { node.config.values ??= {}; (node.config.values as Record<string, unknown>)[port] = value }
  else node.config[port] = value
}
/** A node's structured execution scope: each enclosing owner and the scope port it was entered through, outermost first. */
export type Scope = { owner: string; port: string }[]
const isAncestor = (a: Scope, b: Scope) => a.length <= b.length && a.every((s, i) => s.owner === b[i].owner && s.port === b[i].port)
const scopePort = (node: GraphNode, port: string, catalog: VisualCatalogSnapshot) => nodeSpec(node.kind, catalog)?.scopePorts?.includes(port) || (node.kind === 'parallel' && port.startsWith('branch:'))
const flowPort = (node: GraphNode, edge: ExecutionEdge) => node.kind === 'condition' && (!edge.port || edge.port === 'next') ? 'true' : edge.port ?? 'next'

const catalogExecutionPorts = executionPorts, catalogDataOutputs = dataOutputs, catalogDataInputs = dataInputs, catalogIsAssignable = isAssignable, catalogScopePort = scopePort
const jsonEqual = (a: unknown, b: unknown): boolean =>
  a === b || (Array.isArray(a) && Array.isArray(b) ? a.length === b.length && a.every((v, i) => jsonEqual(v, b[i])) :
    record(a) && record(b) && Object.keys(a).length === Object.keys(b).length && Object.keys(a).every(k => Object.hasOwn(b, k) && jsonEqual(a[k], b[k])))
/** Literal kind of the type itself, then pattern, numeric bounds and enumValues of every type in its chain. */
export function validLiteral(type: string, value: unknown, catalog: VisualCatalogSnapshot = nodeCatalog): boolean {
  const chain = typeChain(type, catalog)
  if (!chain.length || value === undefined) return false
  const spec = resolveTypeSpec(type, catalog)!
  if (value === null) return spec.nullable === true
  const kind = (() => {
    switch (spec.literalKind) {
      case 'boolean': return typeof value === 'boolean'
      case 'string': return typeof value === 'string' && (spec.character !== true || value.length === 1)
      case 'object': return record(value)
      case 'array': return Array.isArray(value)
      case 'enum': return (spec.enumValues ?? []).some(v => jsonEqual(v, value))
      case 'integer': case 'number': return typeof value === 'number' && Number.isFinite(value) && (spec.literalKind !== 'integer' || Number.isInteger(value))
      default: return false
    }
  })()
  return kind && chain.map(id => resolveTypeSpec(id, catalog)!).every(t =>
    (!t.pattern || typeof value === 'string' && matches(value, t.pattern)) &&
    (typeof value !== 'number' || ((t.min === undefined || value >= t.min) && (t.max === undefined || value <= t.max))) &&
    (t.enumValues === undefined || t.enumValues.some(v => jsonEqual(v, value))))
}

// The catalog pattern checks ISO syntax, wall-clock fields and the explicit offset.
// Date.parse alone normalizes impossible days, so validate the calendar separately.
function validOffsetDateTime(value: string): boolean {
  const year = Number(value.slice(0, 4)), month = Number(value.slice(5, 7)), day = Number(value.slice(8, 10))
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0)
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
  const instant = Date.parse(value)
  return year >= 1 && day <= days[month - 1] && Number.isFinite(instant) &&
    instant >= Date.parse('0001-01-01T00:00:00Z') && instant <= Date.parse('9999-12-31T23:59:59.999Z')
}

/** Keys a service target is flattened into; a field with one of them would be sent twice. */
const targetKeys = ['entity_id', 'device_id', 'area_id', 'floor_id', 'label_id']
/**
 * The wording of validator messages, which people read on node cards and under fields. Byte-identical to
 * `VisualGraphValidator.Words`; type names are those of `typeLabel`.
 */
export const words = {
  /** A label as a noun inside a sentence: `Light entity` → `light entity`; words with more capitals stay. */
  noun: (label: string) => label === 'Yes/No' ? 'yes/no value' : label.split(' ').map(w => /^[A-Z][^A-Z]*$/.test(w) ? w.toLowerCase() : w).join(' '),
  /** The noun with its article; "text" takes none. */
  article: (noun: string) => noun === 'text' ? noun : (/^[aeiouAEIOU]/.test(noun) ? 'an ' : 'a ') + noun,
  aType: (id: string, catalog: VisualCatalogSnapshot = nodeCatalog) => words.article(words.noun(typeLabel(id, catalog))),
  or: (items: string[]) => items.length <= 1 ? items.join('') : items.slice(0, -1).join(', ') + ' or ' + items.at(-1),
  show: (value: unknown) => typeof value === 'string' ? value : typeof value === 'boolean' || typeof value === 'number' ? String(value) : JSON.stringify(value),
  /** The allowed values: one, a short list, or a reference to the listed options. */
  choices: (values: unknown[], many: string) => values.length === 1 ? words.show(values[0]) : values.length <= 6 ? many + words.or(values.map(words.show)) : many + 'the listed options',
  range: (min: number | undefined, max: number | undefined, integer: boolean) =>
    min !== undefined && max !== undefined ? `${integer ? 'a whole number between ' : 'between '}${min} and ${max}`
      : min !== undefined ? `${integer ? 'a whole number of at least ' : 'at least '}${min}`
        : max !== undefined ? `${integer ? 'a whole number of at most ' : 'at most '}${max}` : integer ? 'a whole number' : 'a number',
  items: (min: number | undefined, max: number | undefined) => {
    const count = (n: number) => `${n} ${n === 1 ? 'item' : 'items'}`
    return min !== undefined && max !== undefined ? min === max ? `exactly ${count(min)}` : `between ${min} and ${max} items`
      : min !== undefined ? `at least ${count(min)}` : max !== undefined ? `at most ${count(max)}` : 'any number of items'
  },
  /** How a config rule's JSON type reads in a message. */
  configKind: (type: string) => ({ array: 'a list', object: 'an object', integer: 'a whole number', number: 'a number', string: 'text', boolean: 'yes or no' } as Record<string, string>)[type] ?? words.article(type),
  /** The label people see for an input: its spec label, else a name derived from the port id. */
  portLabel: (spec: { label?: unknown } | undefined, port: string) => typeof spec?.label === 'string' && spec.label ? spec.label
    : port.startsWith('field:') ? port.slice(6) : port.startsWith('item:') ? `Item ${port.slice(5)}`
      : port.startsWith('carry:') ? port.slice(6) : port.startsWith('export:') ? port.slice(7) : port,
  /** A type mismatch: the accepted types (named, at most four) and the one given, unless that is unknown. */
  incompatible: (subject: string, type: string, expected: string[], catalog: VisualCatalogSnapshot = nodeCatalog) => {
    if (!typeChain(type, catalog).length) return `${subject} is connected to a value of unknown type`
    const names = [...new Set(expected.map(t => words.aType(t, catalog)))]
    return names.length > 0 && names.length <= 4 ? `${subject} must be ${words.or(names)}, not ${words.aType(type, catalog)}` : `${subject} cannot take ${words.aType(type, catalog)}`
  },
}
/** The service a service node calls, named in its messages; undefined for other nodes or without a service id. */
const serviceName = (node: GraphNode, catalog: VisualCatalogSnapshot) => nodeSpec(node.kind, catalog)?.dynamicInputs === 'service' && typeof node.config.service === 'string' && node.config.service ? node.config.service : undefined
/** What a message about one input of the node starts with: the service and a colon on a service node, then the input's label. */
export const inputSubject = (node: GraphNode, spec: InputSpec | undefined, port: string, catalog: VisualCatalogSnapshot = nodeCatalog) => {
  const service = serviceName(node, catalog)
  return (service ? service + ': ' : '') + words.portLabel(spec, port)
}
/** The error a data wire into `port` gets when its type is not accepted there; shared with the connection index. */
export const incompatibleInput = (node: GraphNode, spec: InputSpec, port: string, type: string, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  words.incompatible(inputSubject(node, spec, port, catalog), type, spec.types, catalog)
/**
 * The keys a supplied target brings into the call: entity_id for an entity id, the object's keys for a target value,
 * and every target key for a connected or variable target object (`targetType` is what it resolves to).
 */
function targetCallKeys(target: unknown, wired: boolean, targetType: string | undefined, catalog: VisualCatalogSnapshot): string[] {
  if (!wired && record(target) && target.kind === 'literal') return typeof target.value === 'string' ? ['entity_id'] : record(target.value) ? Object.keys(target.value) : []
  return targetType !== undefined && refinesType(targetType, 'string', catalog) ? ['entity_id'] : targetKeys
}
/**
 * The diagnostics of a service node that depend on whether `target` is connected, mirrored by
 * `VisualGraphValidator.Service` and `TargetCollisions`: a target the service does not take (with services) and a
 * field that repeats a key the target brings (always, since the runtime rejects it). `connected` says which inputs
 * are wired; `targetType` is the type the target resolves to.
 */
export function serviceTargetDiagnostics(node: GraphNode, connected: (port: string) => boolean, targetType: string | undefined, catalog: VisualCatalogSnapshot = nodeCatalog): GraphDiagnostic[] {
  const errors: GraphDiagnostic[] = []
  const error = (message: string) => errors.push({ nodeId: node.id, message, severity: 3, inputId: 'target' })
  const service = serviceName(node, catalog), schema = serviceSchema(node, catalog), wired = connected('target'), target = node.config.target
  const supplied = (port: string, operand: unknown) => connected(port) || (record(operand) && operand.kind !== 'unset')
  const pattern = nodeSpec(node.kind, catalog)?.config.service?.pattern ?? ''
  if (schema && service && matches(service, pattern) && !record(schema.target) && supplied('target', target)) error(`${service} does not take a target`)
  if (!supplied('target', target) || !record(node.config.fields)) return errors
  const keys = targetCallKeys(target, wired, targetType, catalog)
  for (const [name, operand] of Object.entries(node.config.fields))
    if (keys.includes(name) && supplied(`field:${name}`, operand)) error(`${service ?? 'This service'} sets ${name} both as a field and in the target`)
  return errors
}

/**
 * Which part of the validator produced a diagnostic. The connection index re-derives only the parts a
 * candidate data edge can change; every other diagnostic is carried over unchanged.
 */
export type DiagnosticOrigin =
  | { kind: 'graph' }
  /** Structure or typing of `graph.edges[edge]`. */
  | { kind: 'edge'; edge: number }
  | { kind: 'dataCycle' }
  /** The operand bound to one input of one node. */
  | { kind: 'operand'; node: string; port: string }
  | { kind: 'comparison'; node: string }
  /** Service checks that depend on whether `target` is wired. */
  | { kind: 'serviceTarget'; node: string }
/** The validator's view of a graph. `complete` is false when validation stopped before analysing nodes. */
export interface GraphAnalysis {
  diagnostics: GraphDiagnostic[]
  /** Aligned with `diagnostics`. */
  origins: DiagnosticOrigin[]
  complete: boolean
  /** Well-formed nodes by id (first occurrence wins). */
  nodes: Map<string, GraphNode>
  /** Well-formed data edges whose endpoints exist. */
  data: DataEdge[]
  scopes: Map<string, Scope>
  /** The type each input resolves to (wire, literal or first visible variable producer); undefined when unbound. */
  resolved: Map<string, Record<string, string | undefined>>
  /** The loop whose body directly contains the node, as the validator resolves loop ports. */
  ownerOf(node: GraphNode): GraphNode | undefined
  /** Whether a data output of `source` may be read by `target` across structured scopes. */
  visible(source: GraphNode, target: GraphNode, port: OutputSpec): boolean
}
/** The comparison rule of a node, given its resolved input types. Used by the validator and the connection index. */
export function comparisonDiagnostics(node: GraphNode, spec: NodeSpec, resolved: Record<string, string | undefined>, catalog: VisualCatalogSnapshot = nodeCatalog): GraphDiagnostic[] {
  const rule = spec.comparison
  if (!rule) return []
  const errors: GraphDiagnostic[] = []
  const error = (message: string, inputId?: string) => errors.push({ nodeId: node.id, message, severity: 3, ...(inputId ? { inputId } : {}) })
  const left = rule.leftInput ? resolved[rule.leftInput] : text(node.config[rule.typeConfig!])
  const right = resolved[rule.rightInput]
  if (left && right && !(isAssignable(left, right, catalog) || isAssignable(right, left, catalog))) error('Both sides of the comparison must have the same type', rule.rightInput)
  if (rule.orderingOperators.includes(text(node.config[rule.operatorConfig])) && left !== rule.orderingType) error(`Less-than and greater-than comparisons need ${words.aType(rule.orderingType, catalog)}`, rule.leftInput ?? rule.typeConfig)
  return errors
}
/** Static validation only: evaluating data never schedules a producer. */
export function validateGraph(value: unknown, catalog: VisualCatalogSnapshot = nodeCatalog): GraphDiagnostic[] {
  return analyzeGraph(value, catalog).diagnostics
}
/** The validator's structured scope of every node reachable from a trigger. */
export const analyzeScopes = (value: unknown, catalog: VisualCatalogSnapshot = nodeCatalog) => analyzeGraph(value, catalog).scopes
/** Runs the validator once and keeps what it learned: diagnostics with origins, scopes and resolved input types. */
export function analyzeGraph(value: unknown, catalog: VisualCatalogSnapshot = nodeCatalog): GraphAnalysis {
  const nodeSpec = (kind: unknown) => typeof kind === 'string' && Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined
  const knownType = (type: string) => typeChain(type, catalog).length > 0
  const executionPorts = (node: GraphNode) => catalogExecutionPorts(node, catalog)
  const dataOutputs = (node: GraphNode) => catalogDataOutputs(node, catalog)
  const dataInputs = (node: GraphNode, owner?: GraphNode) => catalogDataInputs(node, owner, catalog)
  const isAssignable = (source: string, target: string) => catalogIsAssignable(source, target, catalog)
  const scopePort = (node: GraphNode, port: string) => catalogScopePort(node, port, catalog)
  const errors: GraphDiagnostic[] = [], origins: DiagnosticOrigin[] = []
  const graphOrigin: DiagnosticOrigin = { kind: 'graph' }
  let origin: DiagnosticOrigin = graphOrigin
  const error = (nodeId: string | null, message: string, inputId?: string, portId?: string) => { errors.push({ nodeId, message, severity: 3, ...(inputId ? { inputId } : {}), ...(portId ? { portId } : {}) }); origins.push(origin) }
  const warning = (nodeId: string, message: string, inputId: string) => { errors.push({ nodeId, message, severity: 2, inputId }); origins.push(origin) }
  const nodes = new Map<string, GraphNode>()
  const data: DataEdge[] = []
  const scopes = new Map<string, Scope>()
  const resolvedTypes = new Map<string, Record<string, string | undefined>>()
  const ownerOf = (node: GraphNode) => { const s = scopes.get(node.id)?.at(-1); return s?.port === 'body' ? nodes.get(s.owner) : undefined }
  function visible(source: GraphNode, target: GraphNode, port: OutputSpec) {
    const a = scopes.get(source.id), b = scopes.get(target.id)
    if (!a || !b || !isAncestor(a, b)) return false
    const owned = b[a.length]?.owner === source.id
    if (port.visibility === 'outside') return !owned
    if (port.visibility === 'body') return owned && b[a.length]?.port === 'body'
    if (port.visibility === 'loop') return owned && ['body', 'condition'].includes(b[a.length]?.port)
    return true
  }
  const analysis = (complete: boolean): GraphAnalysis => ({ diagnostics: errors, origins, complete, nodes, data, scopes, resolved: resolvedTypes, ownerOf, visible })
  if (!record(value) || value.version !== catalog.graphVersion) { error(null, `Expected graph version ${catalog.graphVersion}`); return analysis(false) }
  if (typeof value.name !== 'string' || !value.name.trim()) error(null, 'Graph name is required')
  if (!Array.isArray(value.nodes) || !Array.isArray(value.edges)) { error(null, 'Nodes and edges arrays are required'); return analysis(false) }
  const graph = value as unknown as Graph
  if (!graph.nodes.length || graph.nodes.length > catalog.limits.nodes || graph.edges.length > catalog.limits.edges) { error(null, 'Graph limit: 1–256 nodes and at most 512 edges'); return analysis(false) }
  for (const node of graph.nodes) {
    if (!record(node) || typeof node.id !== 'string' || !node.id || !record(node.config)) { error(null, 'Each node requires id and config'); continue }
    if (nodes.has(node.id)) error(node.id, 'Duplicate node id')
    else nodes.set(node.id, node)
    if (!nodeSpec(node.kind)) error(node.id, 'Unknown node kind')
  }
  const outgoing = new Map([...nodes.keys()].map(id => [id, [] as ExecutionEdge[]]))
  const dataIndex = new Map<DataEdge, number>()
  const edgeIds = new Set<string>()
  for (const [index, edge] of graph.edges.entries()) {
    origin = { kind: 'edge', edge: index }
    if (!record(edge) || typeof edge.id !== 'string' || !edge.id) { error(null, 'Edge id is required'); continue }
    if (edgeIds.has(edge.id)) error(edge.source, 'Duplicate edge id'); edgeIds.add(edge.id)
    if (!nodes.has(edge.source) || !nodes.has(edge.target)) { error(null, 'Edge endpoint missing'); continue }
    if (edge.kind === 'data') {
      if ('port' in edge || typeof edge.sourcePort !== 'string' || typeof edge.targetPort !== 'string') error(edge.source, 'Do not mix execution and data ports')
      else { data.push(edge); dataIndex.set(edge, index) }
    } else if (edge.kind === undefined || edge.kind === 'execution') {
      if ('sourcePort' in edge || 'targetPort' in edge) error(edge.source, 'Do not mix data and execution ports')
      if (edge.port !== undefined && typeof edge.port !== 'string') { error(edge.source, 'Execution port must be a string'); continue }
      const node = nodes.get(edge.source)!, port = flowPort(node, edge)
      if (!executionPorts(node).includes(port)) error(node.id, 'Unknown execution port', undefined, port)
      if (outgoing.get(node.id)!.some(e => flowPort(node, e) === port)) error(node.id, 'Only one execution edge per port', undefined, port)
      if (nodes.get(edge.target)!.kind === 'trigger') error(edge.target, 'Trigger cannot have incoming execution edges')
      outgoing.get(node.id)!.push(edge)
    } else error(edge.source, 'Unknown edge kind')
  }
  origin = graphOrigin
  function cycles(edges: { source: string; target: string }[], label: string) {
    const active = new Set<string>(), done = new Set<string>()
    function visit(id: string) {
      if (active.has(id)) { error(id, `${label} cycle is forbidden`); return }
      if (done.has(id)) return
      active.add(id)
      for (const edge of edges) if (edge.source === id) visit(edge.target)
      active.delete(id); done.add(id)
    }
    for (const id of nodes.keys()) visit(id)
  }
  cycles([...outgoing.values()].flat(), 'Execution')
  origin = { kind: 'dataCycle' }; cycles(data, 'Data'); origin = graphOrigin
  function scope(id: string, path: Scope) {
    if (path.length > catalog.limits.nesting) { error(id, 'Structured nesting limit is 8'); return }
    const old = scopes.get(id)
    if (old) { if (!isAncestor(old, path) || old.length !== path.length) error(id, 'Execution edge crosses a structured scope'); return }
    scopes.set(id, path)
    const node = nodes.get(id)!
    for (const edge of outgoing.get(id) ?? []) {
      const port = flowPort(node, edge)
      scope(edge.target, scopePort(node, port) ? [...path, { owner: id, port }] : path)
    }
  }
  const roots = [...nodes.values()].filter(n => n.kind === 'trigger')
  if (!roots.length) error(null, 'At least one trigger is required')
  roots.forEach(n => scope(n.id, []))
  for (const id of nodes.keys()) if (!scopes.has(id)) error(id, 'Every node must be reachable by execution from a trigger')
  const incoming = new Map<string, DataEdge>()
  const key = (node: string, port: string) => JSON.stringify([node, port])
  for (const edge of data) {
    origin = { kind: 'edge', edge: dataIndex.get(edge)! }
    const source = nodes.get(edge.source)!, target = nodes.get(edge.target)!
    const output = dataOutputs(source)[edge.sourcePort], input = dataInputs(target, ownerOf(target))[edge.targetPort]
    if (!output) error(source.id, 'Unknown data output port', undefined, edge.sourcePort)
    if (!input) error(target.id, 'Unknown data input port', edge.targetPort, edge.targetPort)
    if (incoming.has(key(target.id, edge.targetPort))) error(target.id, 'Only one data source per input', edge.targetPort, edge.targetPort)
    incoming.set(key(target.id, edge.targetPort), edge)
    if (output && input) {
      if (!input.types.some(t => isAssignable(output.type, t))) error(target.id, incompatibleInput(target, input, edge.targetPort, output.type, catalog), edge.targetPort, edge.targetPort)
      if (!visible(source, target, output)) error(target.id, 'Data cannot cross this structured scope', edge.targetPort, edge.targetPort)
    }
  }
  origin = graphOrigin
  function operand(node: GraphNode, port: string, raw: unknown, spec: InputSpec): string | undefined {
    const outer = origin
    origin = { kind: 'operand', node: node.id, port }
    try { return operandType(node, port, raw, spec) } finally { origin = outer }
  }
  function operandType(node: GraphNode, port: string, raw: unknown, spec: InputSpec): string | undefined {
    const edge = incoming.get(key(node.id, port))
    if (edge) return dataOutputs(nodes.get(edge.source)!)[edge.sourcePort]?.type
    if (raw === undefined && !spec.required) return
    const label = words.portLabel(spec, port), subject = inputSubject(node, spec, port, catalog), service = serviceName(node, catalog)
    if (!record(raw) || raw.kind === 'unset') { if (spec.required) error(node.id, service ? `${service} needs a value for ${label}` : `${label} needs a value`, port, port); else if (raw !== undefined && !record(raw)) error(node.id, `${subject} has an invalid value`, port, port); return }
    let type: string | undefined
    if (raw.kind === 'variable') {
      if (typeof raw.name !== 'string' || !new RegExp(catalog.structures.loopDeclaration.name.pattern).test(raw.name)) { error(node.id, `${subject} uses a variable without a valid name`, port, port); return }
      const candidates = [...nodes.values()].filter(n => typeof n.config.output === 'string' && n.config.output === raw.name && dataOutputs(n).value && visible(n, node, dataOutputs(n).value))
      type = candidates[0] ? dataOutputs(candidates[0]).value.type : undefined
      if (!type || candidates.some(n => dataOutputs(n).value.type !== type)) error(node.id, `${subject} uses variable ${raw.name}, which no earlier step provides with a single type`, port)
    } else if (raw.kind === 'literal') {
      type = text(raw.type, 'unknown')
      const v = raw.value
      const valid = validLiteral(type, v, catalog)
      if (!valid) {
        const noun = knownType(type) ? words.noun(typeLabel(type, catalog)) : undefined
        error(node.id, typeof raw.type !== 'string' ? `${subject} has a value without a type` : noun === undefined ? `${subject} has a value of unknown type ${type}` : noun === 'text' ? `${subject} is not valid text` : `${subject} is not a valid ${noun}`, port)
      }
      if (typeof v === 'number' && ((spec.min !== undefined && v < spec.min) || (spec.max !== undefined && v > spec.max) || (spec.integer && !Number.isInteger(v)))) error(node.id, `${subject} must be ${words.range(spec.min, spec.max, spec.integer === true)}`, port)
      // Input constraints restrict authored values only; a wire is constrained by its type. A list, where the
      // input accepts one, is checked item by item (a multiple select); anything else is one value.
      if (spec.enum) {
        const allowed = spec.enum
        if (Array.isArray(v) && spec.types.includes('array')) { if (!v.every(item => allowed.some(a => jsonEqual(a, item)))) error(node.id, `${subject} may only contain ${words.choices(allowed, '')}`, port) }
        else if (!allowed.some(a => jsonEqual(a, v))) error(node.id, `${subject} must be ${words.choices(allowed, 'one of ')}`, port)
      }
      if (spec.pattern && typeof v === 'string' && !matches(v, spec.pattern)) error(node.id, `${subject} is not in the expected format`, port)
      if (Array.isArray(v) && ((spec.minItems !== undefined && v.length < spec.minItems) || (spec.maxItems !== undefined && v.length > spec.maxItems))) error(node.id, `${subject} must have ${words.items(spec.minItems, spec.maxItems)}`, port)
    } else error(node.id, `${subject} has an invalid value`, port)
    // A value of unknown type was reported above; it is not also a mismatch.
    if (type && knownType(type) && !spec.types.some(t => isAssignable(type!, t))) error(node.id, words.incompatible(subject, type, spec.types, catalog), port)
    return type
  }
  // Memoize by the full analysis context. Shared successors are visited once,
  // even when a DAG describes exponentially many possible execution routes.
  const terminalMemo = new Map<string, boolean>(), terminalActive = new Set<string>()
  function terminalsValid(start: string, terminal: string, owner: string): boolean {
    const memoKey = JSON.stringify([start, terminal, owner])
    if (terminalMemo.has(memoKey)) return terminalMemo.get(memoKey)!
    if (terminalActive.has(memoKey)) return false
    terminalActive.add(memoKey)
    const node = nodes.get(start)!
    const next = (outgoing.get(start) ?? []).filter(e => !scopePort(node, flowPort(node, e)))
    const normalPorts = executionPorts(node).filter(p => p !== 'error' && !scopePort(node, p))
    const valid = node.kind === terminal ? scopes.get(start)?.at(-1)?.owner === owner : node.kind === 'stop' ? true :
      normalPorts.length > 0 && normalPorts.every(p => next.some(e => flowPort(node, e) === p)) &&
      next.every(e => terminalsValid(e.target, terminal, owner))
    terminalActive.delete(memoKey); terminalMemo.set(memoKey, valid)
    return valid
  }
  /**
   * Contract C5, mirrored by VisualGraphValidator.Service. Without a services section (Init, library rebuild)
   * there is no service diagnostic at all; an unknown service is one warning. dataInputs already refined the types.
   */
  function validateService(node: GraphNode, spec: NodeSpec) {
    const service = node.config.service, services = catalog.services
    if (!services || typeof service !== 'string' || !matches(service, spec.config.service?.pattern ?? '')) return
    const schema = serviceSchema(node, catalog)
    if (!schema) { warning(node.id, `Unknown service ${service}; fields are not type-checked`, 'service'); return }
    const known = record(schema.fields) ? schema.fields : {}
    const fields = record(node.config.fields) ? node.config.fields : {}
    for (const name of Object.keys(fields)) if (!Object.hasOwn(known, name)) error(node.id, `${service} has no field ${name}`, `field:${name}`)
    for (const [name, field] of Object.entries(known)) if (record(field) && field.required === true && !Object.hasOwn(fields, name)) error(node.id, `${service} needs a value for ${words.portLabel(field, name)}`, 'fields')
    const wired = incoming.has(key(node.id, 'target')), target = node.config.target
    const response = text(schema.response, 'none'), returnResponse = node.config.returnResponse === true
    if (returnResponse && response === 'none') error(node.id, `${service} returns no response; turn off Return response`, 'returnResponse')
    if (!returnResponse && response === 'only') error(node.id, `${service} only returns a response; turn on Return response`, 'returnResponse')
    // A target value object is checked by its entity_id domains; device, area, floor and label ids are not.
    const domains = serviceTargetDomains(schema)
    if (!domains.length || wired || !record(target) || target.kind !== 'literal' || !record(target.value)) return
    const entities = Array.isArray(target.value.entity_id) ? target.value.entity_id : target.value.entity_id === undefined ? [] : [target.value.entity_id]
    const accepted = words.or(domains.map(d => domainWords(d).toLowerCase()))
    origin = { kind: 'serviceTarget', node: node.id }
    for (const entity of entities)
      if (typeof entity === 'string' && entity !== 'all' && entity !== 'none' && !domains.includes(entity.split('.')[0]))
        error(node.id, `${service} only accepts ${accepted} entities; ${entity.indexOf('.') > 0 ? `${entity} is ${words.article(domainWords(entity.split('.')[0]).toLowerCase())}` : `${entity} is not an entity`}`, 'target')
    origin = graphOrigin
  }
  for (const node of nodes.values()) {
    const spec = nodeSpec(node.kind); if (!spec) continue
    for (const [field, rule] of Object.entries(spec.config)) {
      const v = node.config[field]
      const label = text(rule.label) || field
      if (v === undefined) { if (rule.required) error(node.id, `${label} needs a value`, field); continue }
      const valid = rule.type === 'array' ? Array.isArray(v) : rule.type === 'object' ? record(v) : rule.type === 'integer' ? typeof v === 'number' && Number.isInteger(v) : typeof v === rule.type
      if (!valid) { error(node.id, `${label} must be ${words.configKind(rule.type)}`, field); continue }
      // Trigger type changes retain inactive fields so drafts and overrides are not lost.
      if (node.kind === 'trigger' && field === 'at' && (node.config.type ?? spec.config.type.default) !== 'once') continue
      // Type choices accept every resolvable id, so a parametric type can be selected for narrowing.
      if (rule.enumSource === 'types') { if (typeof v !== 'string' || !knownType(v)) error(node.id, `${label} must be a known type`, field) }
      else if (rule.enum && !rule.enum.some(choice => jsonEqual(choice, v))) error(node.id, `${label} must be ${words.choices(rule.enum, 'one of ')}`, field)
      if (typeof v === 'number' && ((rule.min !== undefined && v < rule.min) || (rule.max !== undefined && v > rule.max))) error(node.id, `${label} must be ${words.range(rule.min, rule.max, false)}`, field)
      if (typeof v === 'string' && rule.pattern && !matches(v, rule.pattern)) error(node.id, `${label} is not in the expected format`, field)
      // A value already out of format is not also judged as a date (Date.parse accepts 24:00, .NET does not).
      if (typeof v === 'string' && rule.format === 'date-time-offset' && (!rule.pattern || matches(v, rule.pattern)) && !validOffsetDateTime(v)) error(node.id, `${label} must be a date and time with a time zone offset or Z`, field)
      if (Array.isArray(v) && ((rule.minItems !== undefined && v.length < rule.minItems) || (rule.maxItems !== undefined && v.length > rule.maxItems))) error(node.id, `${label} must have ${words.items(rule.minItems, rule.maxItems)}`, field)
    }
    for (const port of spec.requiredExecution ?? []) if (!outgoing.get(node.id)!.some(e => flowPort(node, e) === port)) error(node.id, 'Execution connection is required', port, port)
    if (spec.argumentStorage === 'arguments' && record(node.config.arguments))
      for (const name of Object.keys(node.config.arguments)) if (!Object.hasOwn(dataInputs(node, ownerOf(node)), name)) error(node.id, `Unknown input ${name}`, name)
    const resolved: Record<string, string | undefined> = Object.create(null)
    for (const [port, rule] of Object.entries(dataInputs(node, ownerOf(node)))) resolved[port] = operand(node, port, inputValue(node, port, catalog), rule)
    resolvedTypes.set(node.id, resolved)
    if (spec.comparison) {
      origin = { kind: 'comparison', node: node.id }
      for (const d of comparisonDiagnostics(node, spec, resolved, catalog)) error(d.nodeId, d.message, d.inputId)
      origin = graphOrigin
    }
    if (spec.dynamicInputs === 'service') {
      validateService(node, spec)
      origin = { kind: 'serviceTarget', node: node.id }
      const connected = (port: string) => incoming.has(key(node.id, port))
      for (const d of serviceTargetDiagnostics(node, connected, resolved.target, catalog)) error(d.nodeId, d.message, d.inputId)
      origin = graphOrigin
    }
    if (node.config.output !== undefined && (typeof node.config.output !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(node.config.output))) error(node.id, `${text(spec.config.output?.label) || 'output'} must use only letters, digits and underscores and not start with a digit`, 'output')
    if (node.kind === 'trigger') {
      const type = node.config.type ?? spec.config.type.default
      const required = type === 'ha' ? ['trigger'] : type === 'entity' ? ['entity'] : type === 'daily' ? ['time','timeZone'] : type === 'once' ? ['at'] : []
      for (const field of required) if (node.config[field] === undefined || node.config[field] === '') error(node.id, `${text(spec.config[field]?.label) || field} needs a value`, field)
      if (type === 'ha' && record(node.config.trigger) && ![node.config.trigger.trigger, node.config.trigger.platform].some(p => typeof p === 'string' && p.trim())) error(node.id, 'Home Assistant trigger needs a platform', 'trigger')
    }
    if (['entity_field','wait_until'].includes(node.kind) && node.config.field === 'attribute' && (typeof node.config.attribute !== 'string' || !node.config.attribute.trim())) error(node.id, 'Attribute needs a value', 'attribute')
    if (node.kind === 'get_field' && (typeof node.config.pointer !== 'string' || !/^(?:\/(?:[^~]|~[01])*)*$/.test(node.config.pointer))) error(node.id, 'Pointer must be a JSON pointer such as /state', 'pointer')
    if (node.kind === 'parallel' && Array.isArray(node.config.branches)) {
      const ids = new Set<string>()
      for (const branch of node.config.branches) {
        if (typeof branch !== 'string' || !new RegExp(catalog.structures.branchId.pattern).test(branch) || ids.has(branch)) error(node.id, 'Branch ids must be unique stable names', 'branches')
        if (typeof branch === 'string') ids.add(branch)
      }
    }
    if (spec.dynamicOutputs === 'loop') {
      for (const group of ['carry','exports'] as const) {
        const names = new Set<string>()
        if (Array.isArray(node.config[group]) && node.config[group].some(d => !record(d))) error(node.id, 'Invalid loop declaration', group)
        for (const decl of declarations(node, group)) {
          if (!record(decl) || typeof decl.name !== 'string' || !new RegExp(catalog.structures.loopDeclaration.name.pattern).test(decl.name) || names.has(decl.name) || typeof decl.type !== 'string' || !knownType(decl.type)) { error(node.id, 'Loop declarations require unique names and valid types', group); continue }
          names.add(decl.name)
          operand(node, `${group}.${decl.name}.initial`, decl.initial, { types: [decl.type], required: true, description: 'Initial value', label: `Initial ${decl.name}` })
        }
      }
      if (['while','until'].includes(node.kind)) {
        const condition = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'condition')
        if (!condition || !terminalsValid(condition.target, 'condition_result', node.id)) error(node.id, 'Every condition path must end in a Condition result step', 'condition', 'condition')
      }
      const body = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'body')
      if ((declarations(node, 'carry').length || declarations(node, 'exports').length) && (!body || !terminalsValid(body.target, 'loop_result', node.id))) error(node.id, 'Every loop body path must end in a Loop result step when the loop carries or exports values', 'body', 'body')
    }
    if (node.kind === 'condition_result') { const s = scopes.get(node.id)?.at(-1); if (!s || s.port !== 'condition' || !['while','until'].includes(nodes.get(s.owner)!.kind)) error(node.id, 'A Condition result step must be inside the condition of a While or Until loop') }
    if (node.kind === 'loop_result') {
      const owner = ownerOf(node)
      if (!owner || nodeSpec(owner.kind)?.dynamicOutputs !== 'loop') error(node.id, 'A Loop result step must be inside the body of a loop')
      if (record(node.config.values)) for (const name of Object.keys(node.config.values)) if (!dataInputs(node, owner)[name]) error(node.id, `Unknown loop value ${name}`, name)
    }
  }
  return analysis(true)
}

export function fillGraphLayout<T extends { nodes: GraphNode[] }>(graph: T): T {
  return { ...graph, nodes: graph.nodes.map((node, i) => ({ ...node, position: node.position ?? { x: 60 + (i % 4) * 300, y: 100 + Math.floor(i / 4) * 180 } })) }
}
export function addGraphNode(graph: Graph, kind: string, id: string, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  if (graph.version !== catalog.graphVersion) throw new Error(`Only graph version ${catalog.graphVersion} is supported`)
  const spec = nodeSpec(kind, catalog); if (!spec) throw new Error('Unknown node kind')
  if (graph.nodes.some(n => n.id === id)) throw new Error('Duplicate node id')
  const config: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(spec.config)) if (value.default !== undefined) config[key] = structuredClone(value.default)
  const storage = spec.argumentStorage === 'arguments' ? (config.arguments ??= {}) as Record<string, unknown> : config
  for (const [key, value] of Object.entries(spec.inputs)) if (value.default !== undefined) storage[key] = structuredClone(value.default)
  return fillGraphLayout({ ...graph, nodes: [...graph.nodes, { id, kind, label: spec.label, config }] })
}
/**
 * The cycle and scope errors, which reject any new connection. Matched whole: messages now quote labels and
 * variable names, and a field labelled "Cycle" must not block every connection in the graph.
 */
export const blockingDiagnostic = /^(?:(?:Execution|Data) cycle is forbidden|Execution edge crosses a structured scope|Data cannot cross this structured scope)$/
/** Returns a new immutable graph; the caller records that graph as one undo entry. */
export function connectData(graph: Graph, source: string, sourcePort: string, target: string, targetPort: string, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  if (graph.version !== catalog.graphVersion) throw new Error(`Only graph version ${catalog.graphVersion} is supported`)
  const next = structuredClone(graph), node = next.nodes.find(n => n.id === target)
  if (!node) throw new Error('Target node missing')
  next.edges = next.edges.filter(e => !(e.kind === 'data' && e.target === target && e.targetPort === targetPort))
  const base = JSON.stringify([source, sourcePort, target, targetPort]); let id = base
  for (let suffix = 2; next.edges.some(e => e.id === id); suffix++) id = `${base}#${suffix}`
  next.edges.push({ id, kind: 'data', source, target, sourcePort, targetPort })
  setInput(node, targetPort, { kind: 'unset' }, catalog)
  const old = validateGraph(graph, catalog).map(e => JSON.stringify(e))
  const addedErrors = validateGraph(next, catalog).filter(e => !old.includes(JSON.stringify(e)) ||
    (e.nodeId === target && (e.inputId === targetPort || e.portId === targetPort)) ||
    (e.nodeId === source && e.portId === sourcePort) || blockingDiagnostic.test(e.message))
  if (addedErrors.length) throw new Error(addedErrors.map(e => e.message).join('; '))
  return next
}
export function disconnectData(graph: Graph, target: string, targetPort: string, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  const next = structuredClone(graph), node = next.nodes.find(n => n.id === target)
  next.edges = next.edges.filter(e => !(e.kind === 'data' && e.target === target && e.targetPort === targetPort))
  if (node) setInput(node, targetPort, { kind: 'unset' }, catalog)
  return next
}
