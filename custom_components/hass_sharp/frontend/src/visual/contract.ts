import rawCatalog from '../../../visual_node_catalog.json'
import { distinctTypeLabels, domainWords, typeLabel } from './typeLabels'

/** The catalog's graphVersion is the visual graph format. The document/API envelope remains version 1. */
export type DataType = string
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type Operand = { kind: 'literal'; type: DataType; value: JsonValue } | { kind: 'unset' }
export interface GraphNode { id: string; kind: string; config: Record<string, unknown>; label?: string; position?: { x: number; y: number } }
export interface ExecutionEdge { id: string; kind?: 'execution'; source: string; target: string; port?: string }
export interface DataEdge { id: string; kind: 'data'; source: string; target: string; sourcePort: string; targetPort: string }
export type GraphEdge = ExecutionEdge | DataEdge
export interface Graph { version: 5; name: string; nodes: GraphNode[]; edges: GraphEdge[] }
/** `code` names structural kinds (see `diagnosticCodes`), the same in both validators; other diagnostics have none. */
export interface GraphDiagnostic { nodeId: string | null; message: string; severity: number; inputId?: string; portId?: string; code?: DiagnosticCode }
export interface ConfigSpec { label?: string; description?: string; type: string; required?: boolean; default?: unknown; enum?: unknown[]; enumSource?: 'types'; min?: number; max?: number; pattern?: string; format?: 'date-time-offset'; minItems?: number; maxItems?: number }
/**
 * Constraints (enum, pattern, minItems, maxItems, min, max, integer) apply to literal operands only; wires are constrained by type.
 * `connectable: false` marks an input that takes a value only (a Constant's value): it has no pin, and a wire into it is
 * refused as into an input the step does not have. `wireOnly` is set by `dataInputs` (never in the catalog) on an input
 * whose catalog types are `$properties`: it takes a wire only, and the editor offers no value to type in.
 */
export interface InputSpec {
  connectable?: boolean; wireOnly?: boolean; nullable?: boolean; hasDefaultValue?: boolean; label?: string; types: string[]; required: boolean; description?: string; default?: unknown; min?: number; max?: number; integer?: boolean
  enum?: JsonValue[]; enumLabels?: string[]; pattern?: string; minItems?: number; maxItems?: number; step?: number; unit?: string
  selector?: Record<string, unknown>; advanced?: boolean; section?: string | null; example?: JsonValue
}
/** `when` names a boolean config key; the output exists only while it is true. */
/**
 * An output pin. `propertyOf` names an input: the output has the type of the property `config.property` names, of the
 * value wired into that input (as `withGraphTypes` resolves it under `resolvedOutputs`), and `type` is empty.
 */
export interface OutputSpec { label?: string; type: string; description?: string; visibility?: string; pointer?: string; refineFrom?: string; when?: string; propertyOf?: string }
export interface NodeSpec { sourceId?: string; argumentStorage?: 'arguments'; label: string; category: string; description: string; minGraphVersion: number; config: Record<string, ConfigSpec>; inputs: Record<string, InputSpec>; outputs: Record<string, OutputSpec>; execution?: string[]; pure?: boolean; keywords?: string[]; scopePorts?: string[]; requiredExecution?: string[]; dynamicExecution?: string; dynamicInputs?: string; dynamicOutputs?: string; comparison?: { leftInput?: string; typeConfig?: string; rightInput: string; operatorConfig: string; orderingOperators: string[]; orderingType: string } }
/** A public instance property or field a value of a C# type has, inherited ones included, as the compiler lists it. */
export interface PropertySpec { name: string; type: string; nullable: boolean; description?: string }
export interface TypeSpec { properties?: PropertySpec[]; clrType?: string; elementType?: string; description: string; label?: string; category?: string; nullable?: boolean; literalKind?: string | null; assignableTo?: string[]; assignableFrom?: string[]; refines?: string; pattern?: string; min?: number; max?: number; character?: boolean; enumValues?: JsonValue[]; enumLabels?: string[] }
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
export interface VisualCatalogSnapshot { services?: ServicesSection; version: number; graphVersion: number; limits: { nodes: number; edges: number; nesting: number; executionSteps: number; loopIterations: number }; types: Record<string, TypeSpec>; parametricTypes?: Record<string, ParametricTypeSpec>; nodes: Record<string, NodeSpec>; triggerOutputs?: Record<string, Record<string, OutputSpec>>; /** Per graph (`withGraphTypes`): the type of each `propertyOf` output, by node id and port. */ resolvedOutputs?: Record<string, Record<string, string>>; structures: { loopDeclaration: { name: { pattern: string } }; branchId: { pattern: string } } }
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
/** Whether the value matches; undefined when JavaScript cannot compile the pattern (never thrown out of the validator). */
const tryMatches = (value: string, pattern: string): boolean | undefined => { let compiled: RegExp; try { compiled = new RegExp(pattern) } catch { return undefined } return compiled.test(value) }
const matches = (value: string, pattern: string) => tryMatches(value, pattern) ?? false
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
    current = typeof spec.refines === 'string' ? spec.refines : undefined
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
  if (root === target) return true
  // Every number type (the built-in number and each CLR number pin) goes into every other; a value that does not fit
  // (a fraction for a whole number, or out of range) fails when the step runs. A number that may be missing does not
  // go into one that may not.
  if (numeric(root, catalog) && numeric(target, catalog) && !(nullableType(root, catalog) && !nullableType(target, catalog))) return true
  // Otherwise the entries decide: the source lists the target, or the target (a Custom C# node's own type, which only
  // its node's compile knows) lists the source.
  return listed(catalog.types[root].assignableTo, target) || listed(Object.hasOwn(catalog.types, target) ? catalog.types[target].assignableFrom : undefined, root)
}
const listed = (list: unknown, id: string) => Array.isArray(list) && list.includes(id)
const nullableType = (id: string, catalog: VisualCatalogSnapshot) => resolveTypeSpec(id, catalog)?.nullable === true
const numeric = (id: string, catalog: VisualCatalogSnapshot) => { const kind = resolveTypeSpec(id, catalog)?.literalKind; return kind === 'integer' || kind === 'number' }
export function executionPorts(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): string[] {
  const spec = nodeSpec(node.kind, catalog)
  if (isPure(node, catalog)) return []
  return spec ? [...(spec.dynamicExecution === 'branches' && Array.isArray(node.config.branches) ? node.config.branches.filter((id): id is string => typeof id === 'string').map(id => `branch:${id}`) : []), ...(spec.execution ?? [])] : []
}
/**
 * Whether the node is a value node: worked out each time a step reads it, with no place in the run order. A kind is
 * one by its catalog entry; a Custom C# node by its signature.
 */
export const isPure = (node: GraphNode | string, catalog: VisualCatalogSnapshot = nodeCatalog) => {
  const spec = nodeSpec(typeof node === 'string' ? node : node.kind, catalog)
  return typeof node !== 'string' && spec?.dynamicInputs === 'custom' ? customSignature(node, catalog)?.pure === true : spec?.pure === true
}
/** The pins a Custom C# node's code gives, as the compiler wrote them into `config.signature`. */
export interface CustomSignature { title: string; description: string; pure: boolean; inputs: Record<string, InputSpec>; outputs: Record<string, OutputSpec>; types: Record<string, TypeSpec> }
const pinId = '^[A-Za-z_][A-Za-z0-9_]*$'
/**
 * Whether a stored signature is one the compiler writes: a text `title` and `description`, a true/false `pure`, and
 * `inputs`, `outputs` and `types` objects; each input has a non-empty list of type ids and a true/false `required`, each
 * output a type id, each pin an identifier for its id, each type an object. Mirrors `VisualNodeCatalog.WellFormedSignature`.
 */
export function wellFormedSignature(signature: unknown): signature is CustomSignature {
  if (!record(signature) || typeof signature.title !== 'string' || typeof signature.description !== 'string' || typeof signature.pure !== 'boolean') return false
  const { inputs, outputs, types } = signature
  if (!record(inputs) || !record(outputs) || !record(types)) return false
  for (const [id, input] of Object.entries(inputs))
    if (!matches(id, pinId) || !record(input) || !Array.isArray(input.types) || !input.types.length || !input.types.every(t => typeof t === 'string') || typeof input.required !== 'boolean') return false
  for (const [id, output] of Object.entries(outputs)) if (!matches(id, pinId) || !record(output) || typeof output.type !== 'string') return false
  return Object.values(types).every(record)
}
/** The signature of a Custom C# node when its kind takes its pins from one and it is well formed; else undefined. */
export function customSignature(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): CustomSignature | undefined {
  const signature = nodeSpec(node.kind, catalog)?.dynamicInputs === 'custom' && record(node.config) ? node.config.signature : undefined
  return wellFormedSignature(signature) ? signature : undefined
}
/**
 * The catalog with the types of the graph's Custom C# nodes added: each well-formed signature's `types` whose id the
 * catalog does not have. Each node lists what its own compile knows a type converts to and from, so the entries of one
 * id are merged: the first node's entry, with `assignableTo` and `assignableFrom` the union of every node's (in node
 * order). The order of the nodes never changes what is accepted. The catalog itself when there are none, and for a
 * graph past the catalog's limits: the validator answers that graph with `graph.limit` alone, and nothing is merged or
 * resolved for it, whoever asks. Mirrors `VisualNodeCatalog.WithGraphTypes`; both validators compare these types by
 * their entries alone.
 */
export function withGraphTypes(catalog: VisualCatalogSnapshot, graph: unknown): VisualCatalogSnapshot {
  if (overLimit(catalog, graph)) return catalog
  const added: Record<string, TypeSpec> = Object.create(null)
  const union = (list: string[] | undefined, more: string[]) => {
    const merged = Array.isArray(list) ? [...list] : []
    for (const id of more) if (typeof id === 'string' && !merged.includes(id)) merged.push(id)
    return merged
  }
  for (const node of record(graph) && Array.isArray(graph.nodes) ? graph.nodes : [])
    if (record(node)) for (const [id, entry] of Object.entries(customSignature(node as unknown as GraphNode, catalog)?.types ?? {})) {
      if (Object.hasOwn(catalog.types, id)) continue
      const first = Object.hasOwn(added, id) ? added[id] : undefined
      if (!first) { added[id] = entry; continue }
      const next: TypeSpec = { ...first }
      for (const key of ['assignableTo', 'assignableFrom'] as const)
        if (Array.isArray(entry[key])) next[key] = union(first[key], entry[key])
      added[id] = next
    }
  const selecting = selectingNodes(catalog, graph)
  if (!Object.keys(added).length && !selecting.length) return catalog
  const merged: VisualCatalogSnapshot = { ...catalog, types: Object.keys(added).length ? Object.assign(Object.create(null), catalog.types, added) : catalog.types }
  if (selecting.length) resolveOutputs(merged, graph)
  return merged
}
/**
 * Whether the graph has more nodes or more value and step order wires than the catalog's `limits` allow (the
 * validator's `graph.limit`). Mirrors `VisualNodeCatalog.OverLimit`.
 */
export const overLimit = (catalog: VisualCatalogSnapshot, graph: unknown) =>
  record(graph) && (Array.isArray(graph.nodes) && graph.nodes.length > catalog.limits.nodes || Array.isArray(graph.edges) && graph.edges.length > catalog.limits.edges)
/** The well-formed nodes of a graph by id (the first of an id), as both validators read them. */
function graphNodes(graph: unknown): Map<string, GraphNode> {
  const nodes = new Map<string, GraphNode>()
  for (const node of record(graph) && Array.isArray(graph.nodes) ? graph.nodes : [])
    if (record(node) && typeof node.id === 'string' && node.id && record(node.config) && !nodes.has(node.id)) nodes.set(node.id, node as unknown as GraphNode)
  return nodes
}
const propertyOf = (output: unknown) => record(output) ? text(output.propertyOf) : ''
const selectingNodes = (catalog: VisualCatalogSnapshot, graph: unknown) =>
  [...graphNodes(graph).values()].filter(node => Object.values(nodeSpec(node.kind, catalog)?.outputs ?? {}).some(output => propertyOf(output) !== ''))
/** Whether the kind's input takes a value whose type has properties (`$properties`): a value of exactly such a type, by its own entry. */
export const selectsProperties = (spec: NodeSpec | undefined, port: string) =>
  !!spec && Object.hasOwn(spec.inputs, port) && Array.isArray(spec.inputs[port].types) && spec.inputs[port].types.includes('$properties')
/** The properties a value of the type has, as its catalog entry lists them; empty for a type without any. */
export function typeProperties(catalog: VisualCatalogSnapshot, type: string): PropertySpec[] {
  const entry = Object.hasOwn(catalog.types, type) ? catalog.types[type] : undefined
  return record(entry) && Array.isArray(entry.properties) ? entry.properties.filter(record) as unknown as PropertySpec[] : []
}
/**
 * The type of each `propertyOf` output of the graph, under `resolvedOutputs` (node id, then port): the type of the
 * chosen property of the type the first value wire into its input brings. A node reading another such node is resolved
 * after it: each waits for the ones it reads, and is resolved once none is left (in the order the graph lists the ready
 * ones), so no call goes one level deeper per node, however long the chain. A node on a cycle of such nodes, or reading
 * one, is never ready and has none; so has an output whose input, property or source type is missing. Mirrors
 * `VisualNodeCatalog.ResolveOutputs`.
 */
export function resolveOutputs(catalog: VisualCatalogSnapshot, graph: unknown) {
  const resolved: Record<string, Record<string, string>> = Object.create(null)
  catalog.resolvedOutputs = resolved
  const nodes = graphNodes(graph)
  const incoming = new Map<string, Record<string, unknown>>()
  for (const edge of record(graph) && Array.isArray(graph.edges) ? graph.edges : [])
    if (record(edge) && typeof edge.id === 'string' && edge.id && edge.kind === 'data' && !Object.hasOwn(edge, 'port') && typeof edge.sourcePort === 'string' && edge.sourcePort &&
      typeof edge.targetPort === 'string' && edge.targetPort && typeof edge.source === 'string' && nodes.has(edge.source) && typeof edge.target === 'string' && nodes.has(edge.target)) {
      const key = JSON.stringify([edge.target, edge.targetPort])
      if (!incoming.has(key)) incoming.set(key, edge)
    }
  // Each propertyOf output and the wire into its input.
  const wires = (node: GraphNode) => {
    const found: { port: string; edge: Record<string, unknown> }[] = []
    for (const [port, output] of Object.entries(nodeSpec(node.kind, catalog)?.outputs ?? {})) {
      const input = propertyOf(output), edge = input ? incoming.get(JSON.stringify([node.id, input])) : undefined
      if (edge) found.push({ port, edge })
    }
    return found
  }
  const selecting = selectingNodes(catalog, graph)
  const ids = new Set(selecting.map(node => node.id))
  const waiting = new Map<string, number>(), readers = new Map<string, GraphNode[]>()
  for (const node of selecting) {
    const sources = new Set(wires(node).map(w => w.edge.source as string).filter(id => ids.has(id)))
    waiting.set(node.id, sources.size)
    for (const source of sources) { const list = readers.get(source); if (list) list.push(node); else readers.set(source, [node]) }
  }
  const ready = selecting.filter(node => waiting.get(node.id) === 0)
  for (let next = 0; next < ready.length; next++) {
    const node = ready[next]
    for (const { port, edge } of wires(node)) {
      const outputs = dataOutputs(nodes.get(edge.source as string)!, catalog), sourcePort = edge.sourcePort as string
      const type = Object.hasOwn(outputs, sourcePort) ? text(outputs[sourcePort].type) : ''
      const found = text(typeProperties(catalog, type).find(p => text(p.name) === text(node.config.property))?.type)
      if (found) (resolved[node.id] ??= Object.create(null) as Record<string, string>)[port] = found
    }
    for (const reader of readers.get(node.id) ?? []) {
      const left = waiting.get(reader.id)! - 1
      waiting.set(reader.id, left)
      if (left === 0) ready.push(reader)
    }
  }
}
export function dataOutputs(node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, OutputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  // A propertyOf output has the type the graph's catalog resolved for it (withGraphTypes), and none until it is resolved.
  const resolved = (port: string) => {
    const outputs = catalog.resolvedOutputs && Object.hasOwn(catalog.resolvedOutputs, node.id) ? catalog.resolvedOutputs[node.id] : undefined
    return record(outputs) && Object.hasOwn(outputs, port) ? text(outputs[port]) : ''
  }
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.outputs ?? {}).map(([key, value]) => [key, { ...value, type: propertyOf(value) ? resolved(key) : configuredType(value.type, node) }]))) as Record<string, OutputSpec>
  if (spec?.dynamicOutputs === 'loop') for (const key of ['carry', 'exports'] as const) for (const item of declarations(node, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { type: item.type, label: item.name, description: `${key === 'carry' ? 'Kept between iterations' : 'Available after the loop'}: ${item.name}`, visibility: key === 'carry' ? 'loop' : 'outside' }
  }
  if (spec?.dynamicOutputs === 'custom') Object.assign(ports, structuredClone(customSignature(node, catalog)?.outputs ?? {}))
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
const serviceFieldConstraints = ['min', 'max', 'integer', 'enum', 'enumLabels', 'pattern', 'minItems', 'maxItems', 'step', 'unit', 'selector', 'advanced', 'section', 'example'] as const
/**
 * A known service field as an input spec, so catalog literal constraints (C2) apply to it. Its C4 default (a plain
 * value) becomes an operand, the shape catalog inputs use: a literal of the first field type it is a valid value
 * of; a default no field type accepts is left out. Mirrored by `VisualNodeCatalog.ServiceFieldInput`.
 */
function serviceFieldInput(key: string, field: ServiceFieldSchema, catalog: VisualCatalogSnapshot): InputSpec {
  const port: InputSpec = { types: [...(field.types ?? [])], required: field.required === true, label: text(field.label, key), ...(text(field.description) ? { description: text(field.description) } : {}) }
  for (const name of serviceFieldConstraints) if (field[name] !== null && field[name] !== undefined) Object.assign(port, { [name]: structuredClone(field[name]) })
  const fallback = field.default
  const type = fallback === null || fallback === undefined ? undefined : port.types.find(t => validLiteral(t, fallback, catalog))
  if (type !== undefined) port.default = { kind: 'literal', type, value: structuredClone(fallback) }
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
/** The ids of every type whose entry lists properties: what `$properties` (a Select property node's input) accepts. */
const withProperties = (catalog: VisualCatalogSnapshot) => Object.entries(catalog.types).filter(([, type]) => record(type) && Array.isArray(type.properties) && type.properties.length > 0).map(([id]) => id)
export function dataInputs(node: GraphNode, loopOwner?: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, InputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.inputs ?? {}).map(([key, value]) => [key, { ...value, ...(value.types.includes('$properties') ? { wireOnly: true } : {}), types: value.types.flatMap(t => t === '$any' ? Object.keys(catalog.types) : t === '$properties' ? withProperties(catalog) : [configuredType(t, node)]) }]))) as Record<string, InputSpec>
  if (node.kind === 'foreach' && ports.items) ports.items.types = ['array', ...Object.entries(catalog.types).filter(([, type]) => !type.nullable && type.elementType && isAssignable(type.elementType, text(node.config.itemType), catalog)).map(([id]) => id)]
  const types = Object.keys(catalog.types)
  const dynamic = spec?.dynamicInputs
  if (dynamic === 'fields' && record(node.config.fields)) for (const key of Object.keys(node.config.fields)) ports[`field:${key}`] = { types, required: true, label: key, description: `Field ${key}` }
  if (dynamic === 'service') {
    // Ports are the keys of config.fields (D3); a known service only refines their types and the target's.
    const schema = serviceSchema(node, catalog)
    if (record(node.config.fields)) for (const key of Object.keys(node.config.fields)) {
      const field = schema && record(schema.fields) && Object.hasOwn(schema.fields, key) ? schema.fields[key] : undefined
      ports[`field:${key}`] = record(field) ? serviceFieldInput(key, field, catalog) : { types, required: false, label: key }
    }
    if (schema && ports.target) {
      const domains = serviceTargetDomains(schema)
      ports.target.types = ['target', ...(domains.length ? domains.map(d => `entity_id:${d}`) : ['entity_id'])]
    }
  }
  if (dynamic === 'custom') Object.assign(ports, structuredClone(customSignature(node, catalog)?.inputs ?? {}))
  if (dynamic === 'items' && Array.isArray(node.config.items)) node.config.items.forEach((_, i) => { ports[`item:${i}`] = { types, required: true, label: `Item ${i + 1}`, description: `Item ${i + 1}` } })
  if (dynamic === 'loop' && loopOwner) for (const key of ['carry', 'exports'] as const) for (const item of declarations(loopOwner, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { types: [item.type], required: false, label: item.name, description: `New value for ${item.name}` }
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
/** Only a missing port means the default one (`next`, a condition's `true`); an empty port is a port of that name, as in C#. */
const flowPort = (node: GraphNode, edge: ExecutionEdge) => node.kind === 'condition' && (edge.port === undefined || edge.port === 'next') ? 'true' : edge.port ?? 'next'

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
      case 'enum': return Array.isArray(spec.enumValues) && spec.enumValues.some(v => jsonEqual(v, value))
      case 'integer': case 'number': return typeof value === 'number' && Number.isFinite(value) && (spec.literalKind !== 'integer' || Number.isInteger(value))
      default: return false
    }
  })()
  // An entry's rule of the wrong kind (a crafted `"min": "abc"`) is no rule, as in C#.
  return kind && chain.map(id => resolveTypeSpec(id, catalog)!).every(t =>
    (typeof t.pattern !== 'string' || !t.pattern || typeof value === 'string' && matches(value, t.pattern)) &&
    (typeof value !== 'number' || ((typeof t.min !== 'number' || value >= t.min) && (typeof t.max !== 'number' || value <= t.max))) &&
    (!Array.isArray(t.enumValues) || t.enumValues.some(v => jsonEqual(v, value))))
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

/** The keys a service target may hold; a node with a target may not also have a field with one of them. */
export const targetKeys = ['entity_id', 'device_id', 'area_id', 'floor_id', 'label_id']
/** The message for a target object holding another key; the runtime throws the same sentence. */
export const targetKeyMessage = (key: string) => `Target can only contain entity_id, device_id, area_id, floor_id and label_id; found ${key}`
/**
 * The wording of validator messages, which people read on node cards and under fields. Byte-identical to
 * `VisualGraphValidator.Words`; type names are those of `typeLabel`. No indefinite article precedes a word the
 * validator does not choose itself (a type name, a domain, a label).
 */
/** JavaScript's whitespace, spelled out so `VisualGraphValidator.Words.Name` trims the very same characters. */
const blankEnds = /^[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+|[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+$/g
const youSound = ['uni', 'use', 'usa', 'usu', 'uti', 'ura', 'ure', 'uri', 'uro', 'ubi']
export const words = {
  /** A key or value people did not name, in words: `needs_action` → `Needs action`; never empty. */
  key: (key: string) => { const w = domainWords(key); return w.replace(blankEnds, '') ? w : 'Unnamed' },
  /** "a" or "an" before a word, by its first sound: an input boolean entity, an update entity, a utility meter entity, a European one. */
  article: (word: string) => {
    const w = word.toLowerCase()
    if (w.startsWith('eu') || w.startsWith('one')) return 'a'
    if (/^[aeio]/.test(w)) return 'an'
    return w.startsWith('u') && !youSound.some(p => w.startsWith(p)) ? 'an' : 'a'
  },
  /** A service as people know it: the name its description gives, else its key in words (`set_hvac_mode` → `Set hvac mode`), as the editor names it. */
  serviceName: (schema: ServiceSchema, service: string) => {
    const key = service.slice(service.indexOf('.') + 1)
    const name = text(schema.name).replace(blankEnds, '')
    return name && name !== key && name !== service ? name : words.key(key)
  },
  /** A time of day as the daily trigger runs it: 24-hour HH:MM. */
  timeOfDay: '^(?:[01][0-9]|2[0-3]):[0-5][0-9]$',
  timeOfDayMessage: (label: string) => `${label} must be HH:MM in 24-hour time, from 00:00 to 23:59`,
  /** A Get field path with a ~ that is not ~0 or ~1. */
  pathTilde: 'Path has a ~ that is not followed by 0 or 1; inside a name, write ~0 for a ~ and ~1 for a /, such as /data/on~1off for the name on/off',
  /**
   * A step as people see it on the canvas: its own label, else (for a Custom C# node) its method's title, else its
   * kind's catalog label, else its kind in words.
   */
  stepName: (node: GraphNode, catalog: VisualCatalogSnapshot = nodeCatalog): string => {
    const label = typeof node.label === 'string' ? node.label.replace(blankEnds, '') : ''
    const title = customSignature(node, catalog)?.title.replace(blankEnds, '') ?? ''
    if (label || title) return label || title
    const kindLabel = typeof node.kind === 'string' && Object.hasOwn(catalog.nodes, node.kind) && typeof catalog.nodes[node.kind].label === 'string' ? catalog.nodes[node.kind].label.replace(blankEnds, '') : ''
    return label || kindLabel || words.key(typeof node.kind === 'string' ? node.kind : '')
  },
  /** A step that execution never reaches, and what to do about it. */
  unreachable: (step: string) => `${step} is not connected into the flow; connect a step to its execution input`,
  /** A step order wire into or out of a value node. */
  notInFlow: (node: string) => `${node} is a value and is not part of the run order`,
  /** A node on a cycle of value wires. */
  ownResult: (node: string) => `${node} would depend on its own result`,
  /** A value node none of whose outputs is connected. */
  unused: (node: string) => `${node} is not used`,
  /** A step reads a value node that uses an output the step could not read over a wire of its own. */
  pureScope: (node: string, source: string, output: string, step: string) =>
    `${node} uses ${source} · ${output}, which ${step} cannot use here: it is on the other side of a loop or parallel branch boundary`,
  /** A name or id quoted as written, without surrounding whitespace; `Unnamed` when nothing is left. */
  name: (name: string) => name.replace(blankEnds, '') || 'Unnamed',
  or: (items: string[]) => items.length <= 1 ? items.join('') : items.slice(0, -1).join(', ') + ' or ' + items.at(-1),
  /** A value in words: text as words (`twice_daily` → `Twice daily`), numbers as `String(n)` writes them, True or False. */
  show: (value: unknown): string => value === null ? 'Nothing' : typeof value === 'string' ? (value === '' ? 'Empty text' : words.key(value))
    : typeof value === 'boolean' ? (value ? 'True' : 'False') : typeof value === 'number' ? String(value) : JSON.stringify(value),
  /** The allowed values by their labels where the input has them, else in words: one, a short list, or a reference to the listed options. */
  choices: (values: unknown[], labels: unknown, many: string) => {
    const named = Array.isArray(labels) && labels.length === values.length && labels.every(l => typeof l === 'string' && l !== '') ? labels as string[] : values.map(words.show)
    return named.length === 1 ? named[0] : named.length <= 6 ? many + words.or(named) : many + 'the listed options'
  },
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
  configKind: (type: string) => ({ array: 'a list', object: 'an object', integer: 'a whole number', number: 'a number', string: 'text', boolean: 'true or false' } as Record<string, string>)[type] ?? 'the right kind of value',
  /** The label people see for an input: its spec label, else (or when the label is only the key) the key in words. List items count from 1. */
  portLabel: (spec: { label?: unknown } | undefined, port: string) => {
    if (port.startsWith('item:')) return /^[0-9]+$/.test(port.slice(5)) ? `Item ${BigInt(port.slice(5)) + 1n}` : 'Item'
    const key = port.startsWith('field:') || port.startsWith('carry:') ? port.slice(6) : port.startsWith('export:') ? port.slice(7) : port
    return typeof spec?.label === 'string' && spec.label && spec.label !== key && spec.label.trim() ? spec.label : words.key(key)
  },
  /**
   * A type mismatch: the accepted types (named, at most four) and the one given, unless that is unknown. An accepted
   * type that reads as the given one does is told apart by its C# name (`distinctTypeLabels`): "accepts Mode (A.Mode),
   * not Mode (B.Mode)".
   */
  incompatible: (subject: string, type: string, expected: string[], catalog: VisualCatalogSnapshot = nodeCatalog) => {
    if (!typeChain(type, catalog).length) return `${subject} is connected to a value of unknown type`
    const label = typeLabel(type, catalog)
    const plain = [...new Set(expected.map(t => typeLabel(t, catalog)))]
    // The same kind of value, but one that may be empty where the pin needs it set (a nullable C# type).
    if (resolveTypeSpec(type, catalog)?.nullable === true && plain.includes(label)) return `${subject} needs a value that is always set, and this one may be empty`
    const apart = distinctTypeLabels([type, ...expected.filter(t => typeLabel(t, catalog) === label)], catalog)
    const names = [...new Set(expected.map(t => apart.get(t) ?? typeLabel(t, catalog)))], given = apart.get(type)!
    return names.length > 0 && names.length <= 4 ? `${subject} accepts ${words.or(names)}, not ${given}` : `${subject} does not accept ${given}`
  },
  /** A wire from a Select property node whose property is not chosen yet: its value has no type. */
  /** A value typed into an input that takes a wire only (a Select property node's value): a record, class or struct comes from a step. */
  wireOnly: (step: string) => `Connect a value to ${step}; nothing can be typed in here`,
  chooseProperty: (step: string) => `Choose a property in ${step} to use its value`,
  /** A Select property node with a value connected and no property chosen. */
  propertyUnset: (type: string) => `Choose a property of ${type}`,
  /** A Select property node whose chosen property the connected value's type does not have (the wire or the library changed). */
  propertyUnknown: (type: string, name: string) => `${type} has no property ${words.name(name)}; choose another`,
  /**
   * A value whose type has no properties, wired into a Select property node, and what reads such a value instead:
   * Read entity for an entity, Get field for an object or a JSON list, For each for a type with an element type (what
   * For each goes through).
   */
  noProperties: (type: string, catalog: VisualCatalogSnapshot = nodeCatalog) => {
    const label = typeLabel(type, catalog), root = rootType(type, catalog)
    const instead = refinesType(type, 'entity_id', catalog) ? 'use Read entity to get its state'
      : root === 'object' ? 'use Get field to take a value out of an object'
        : root === 'array' ? 'use Get field to take a value out of a list'
          : text(resolveTypeSpec(type, catalog)?.elementType) ? 'use For each to go through its items'
            : 'connect a value that has some, such as a record from a node library'
    return `${label} has no properties to select; ${instead}`
  },
  /** A Repeat count typed in above the loop's Maximum iterations. */
  repeatCount: (label: string, max: number, maxLabel: string) => `${label} must be at most ${max}, as ${maxLabel} is ${max}`,
  /** A wire whose value is not visible where it arrives. */
  scopeData: (subject: string) => `${subject} cannot use this value here: it is on the other side of a loop or parallel branch boundary`,
  /** A wire into an input the step (of this kind) does not have; a Constant's value says what to do instead. */
  unknownInput: (port: string, kind?: string) => kind === 'constant' && port === 'value' ? words.intoConstant : `This value wire goes into ${words.portLabel(undefined, port)}, which this step does not have`,
  /** A wire into a Constant: it takes a typed-in value only, and Convert is the step that changes a value's type. */
  intoConstant: "Nothing can be connected to a Constant; remove this connection, or use Convert to change a value's type",
  /**
   * A Constant whose stored value is of another type than its Type (a graph from before the Type set it): how to repair
   * it. A value that may be empty, of a Type that may not, says so; two types that read alike are told apart.
   */
  constantRetyped: (label: string, type: string, expected: string, catalog: VisualCatalogSnapshot = nodeCatalog) => {
    const names = distinctTypeLabels([type, expected], catalog)
    if (resolveTypeSpec(type, catalog)?.nullable === true && resolveTypeSpec(expected, catalog)?.nullable !== true && typeLabel(type, catalog) === typeLabel(expected, catalog))
      return `${label} may be empty, but this Constant's Type, ${names.get(expected)}, is always set; type the value again, or change Type`
    return `${label} is ${names.get(type)}, but this Constant's Type is ${names.get(expected)}; type the value again, or change Type`
  },
  /** A Custom C# node whose stored signature is not one the compiler writes. */
  malformedSignature: 'The pins of this node cannot be read; open Edit code and apply it',
  /** A Custom C# node whose stored signature is not what its code gives (only the backend, which compiles it, knows). */
  staleSignature: 'The pins are out of date with the code: open Edit code and apply it',
  /** The things hand-written automations left behind, and what to do instead. */
  oldCustomCode: "Custom C# code is now one static method marked [Node]; rewrite this node's code in that form",
  missingLibraryNode: 'This step is no longer in its node library; delete it and add it again from the node library',
  imports: "Usings are no longer part of an automation; put using lines at the top of a Custom C# node's code",
  members: "Helper members are no longer part of an automation; write them in a Custom C# node's code or a node library",
  exposedValue: 'Steps no longer expose values to C# by name; connect a wire from this step to each step that uses its value',
  variable: (subject: string) => `${subject} reads a variable, and variables are gone; connect a wire to it instead`,
  entitiesExpression: 'Entities expression is gone; choose the entity in Entity',
  schedulesExpression: 'Schedules expression is gone; set the time in Time of day',
}
/** The service a service node calls, named in its messages; undefined for other nodes or without a service id. */
const serviceName = (node: GraphNode, catalog: VisualCatalogSnapshot) => nodeSpec(node.kind, catalog)?.dynamicInputs === 'service' && typeof node.config.service === 'string' && node.config.service ? node.config.service : undefined
/** What a message about one input of the node starts with: the service and a colon on a service node, then the input's label. */
export const inputSubject = (node: GraphNode, spec: InputSpec | undefined, port: string, catalog: VisualCatalogSnapshot = nodeCatalog) => {
  const service = serviceTitle(node, catalog)
  return (service ? service + ': ' : '') + words.portLabel(spec, port)
}
/**
 * A service as every message names it: its name and then its id in brackets where the services section describes it
 * ("Turn on (light.turn_on)"), else its id alone; undefined for other nodes.
 */
const serviceTitle = (node: GraphNode, catalog: VisualCatalogSnapshot) => {
  const service = serviceName(node, catalog), schema = serviceSchema(node, catalog)
  return service && schema ? `${words.serviceName(schema, service)} (${service})` : service
}
/**
 * The message for a wire into a service's target that brings an entity of a domain the service does not take, worded
 * as the value case is; undefined for any other mismatch. Mirrors `VisualGraphValidator` `WrongDomain`.
 */
function wrongDomain(node: GraphNode, port: string, type: string, catalog: VisualCatalogSnapshot): string | undefined {
  const schema = port === 'target' && serviceName(node, catalog) ? serviceSchema(node, catalog) : undefined
  const domains = schema ? serviceTargetDomains(schema) : []
  if (!domains.length) return
  const entity = /^entity_id:([a-z0-9_]+)$/.exec(type)
  let carried: string
  if (type === 'entity_id') carried = 'can be any entity'
  else if (entity) { const named = `${domainWords(entity[1]).toLowerCase()} entity`; carried = `is ${words.article(named)} ${named}` }
  else return
  return `${serviceTitle(node, catalog)} only accepts ${words.or(domains.map(d => domainWords(d).toLowerCase()))} entities; the connected value ${carried}`
}
/** The input when a wire may go into it: undefined for an input that takes a value only (`connectable: false`). */
export const connectable = (input: InputSpec | undefined) => input?.connectable === false ? undefined : input
/** The error a data wire into `port` gets when its type is not accepted there; shared with the connection index. */
export const incompatibleInput = (node: GraphNode, spec: InputSpec, port: string, type: string, catalog: VisualCatalogSnapshot = nodeCatalog) =>
  wrongDomain(node, port, type, catalog) ?? words.incompatible(inputSubject(node, spec, port, catalog), type, spec.types, catalog)
/**
 * Why a data wire from `output` of `source` into `input` (`port` of `target`) does not fit, by its types; shared with the
 * connection index and mirrored by the data wire check in `VisualGraphValidator`. An output whose property is not
 * chosen yet has no type: a wire from it says to choose one; when one is chosen but cannot be read (a property the
 * value's type lacks), the node itself says so and the wire gets nothing. A Select property node's input takes a value
 * whose own type has properties.
 */
export function wireTypeError(source: GraphNode, output: OutputSpec, target: GraphNode, input: InputSpec, port: string, catalog: VisualCatalogSnapshot = nodeCatalog,
  assignable: (source: string, target: string) => boolean = (a, b) => isAssignable(a, b, catalog)): { message: string; code?: DiagnosticCode } | undefined {
  if (propertyOf(output) && !output.type) return text(source.config.property) ? undefined : { message: words.chooseProperty(words.stepName(source, catalog)), code: 'property.unset' }
  const selects = selectsProperties(nodeSpec(target.kind, catalog), port)
  if (selects ? typeProperties(catalog, output.type).length > 0 : input.types.some(t => assignable(output.type, t))) return
  return { message: wrongDomain(target, port, output.type, catalog) ?? (selects && typeChain(output.type, catalog).length > 0 ? words.noProperties(output.type, catalog) : words.incompatible(inputSubject(target, input, port, catalog), output.type, input.types, catalog)) }
}
/**
 * The target rules of a service node, mirrored by `VisualGraphValidator.TargetRules`; `connected` says which inputs
 * are wired. A target the service does not take is an error (with services only). A target value holds only
 * `targetKeys`, as the runtime enforces. A node whose target is set in any form (a value, a connection or a variable)
 * may not also have a field named like a target key; a node without a target may.
 */
export function serviceTargetDiagnostics(node: GraphNode, connected: (port: string) => boolean, catalog: VisualCatalogSnapshot = nodeCatalog): GraphDiagnostic[] {
  const errors: GraphDiagnostic[] = []
  const error = (message: string) => errors.push({ nodeId: node.id, message, severity: 3, inputId: 'target' })
  const service = serviceName(node, catalog), schema = serviceSchema(node, catalog), wired = connected('target'), target = node.config.target
  const targetSet = wired || (record(target) && target.kind !== 'unset')
  const pattern = nodeSpec(node.kind, catalog)?.config.service?.pattern ?? ''
  if (schema && service && matches(service, pattern) && !record(schema.target) && targetSet) error(`${serviceTitle(node, catalog)} does not take a target`)
  if (!targetSet) return errors
  if (!wired && record(target) && target.kind === 'literal' && record(target.value))
    for (const key of Object.keys(target.value)) if (!targetKeys.includes(key)) error(targetKeyMessage(key))
  if (record(node.config.fields)) for (const key of Object.keys(node.config.fields))
    if (targetKeys.includes(key)) error(`${serviceTitle(node, catalog) ?? 'This service'} has a target, so it cannot also have a field named ${key}`)
  return errors
}
/**
 * The codes of structural diagnostics, identical in `VisualGraphValidator.Codes`. `connectData` does not read them:
 * it refuses a wire for any diagnostic the wire adds (compared by full content), any diagnostic on the input it fills
 * or about the output it reads, and a data cycle the wire itself lies on. A code starting with `cycle.` or `scope.`
 * marks a blocking diagnostic (`blockingDiagnostic`), which the canvas uses when it places a new step.
 */
export const diagnosticCodes = [
  'graph.version', 'graph.name', 'graph.shape', 'graph.limit', 'graph.noTrigger',
  'node.invalid', 'node.duplicate', 'node.unknownKind', 'node.unreachable', 'limit.nesting',
  'edge.invalid', 'edge.duplicate', 'edge.endpoint', 'edge.unknownKind', 'edge.valueWirePins', 'edge.orderWirePins', 'edge.orderPinName',
  'port.unknownExecution', 'port.executionTaken', 'port.unknownOutput', 'port.unknownInput', 'port.inputTaken', 'trigger.incoming',
  'cycle.execution', 'cycle.data', 'scope.execution', 'scope.data', 'scope.conditionResult', 'scope.loopResult',
  'flow.pure', 'pure.unused', 'custom.signature', 'property.unset', 'property.unknown',
] as const
export type DiagnosticCode = typeof diagnosticCodes[number]
/**
 * Whether a diagnostic is a cycle or a scope error, by its code, never its wording. A connection that adds one is
 * refused; one the graph already has is not a reason to refuse an unrelated connection (it may be the one that fixes it).
 */
export const blockingDiagnostic = (d: { code?: string }) => d.code !== undefined && (d.code.startsWith('cycle.') || d.code.startsWith('scope.'))

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
  /** The step outputs a value node reads, directly or through other value nodes: each once, in data wire order, depth first. */
  pureSources(pure: GraphNode): { node: GraphNode; port: string; output: OutputSpec }[]
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
  // Ordering takes any number type on either side (number and every CLR number type); the run time widens both.
  const ordered = (type: string | undefined) => type === rule.orderingType || (!!type && numeric(rootType(type, catalog), catalog))
  if (rule.orderingOperators.includes(text(node.config[rule.operatorConfig])) && (!ordered(left) || (!!right && !ordered(right))))
    error(`Less-than and greater-than comparisons need ${typeLabel(rule.orderingType, catalog)} values`, !ordered(left) ? (rule.leftInput ?? rule.typeConfig) : rule.rightInput)
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
  const error = (nodeId: string | null, message: string, inputId?: string, portId?: string, code?: DiagnosticCode) => { errors.push({ nodeId, message, severity: 3, ...(inputId ? { inputId } : {}), ...(portId ? { portId } : {}), ...(code ? { code } : {}) }); origins.push(origin) }
  const configLabel = (rule: { label?: unknown } | undefined, field: string) => typeof rule?.label === 'string' && rule.label.trim() ? rule.label : words.key(field)
  /** Checks a value against an input's format rule; a rule JavaScript cannot compile is reported once, not thrown. */
  const formatted = (nodeId: string, subject: string, value: string, pattern: string, inputId: string) => {
    const matched = tryMatches(value, pattern)
    if (matched === undefined) error(nodeId, `${subject} has a format rule that cannot be checked`, inputId)
    return matched ?? true
  }
  const warning = (nodeId: string, message: string, inputId: string) => { errors.push({ nodeId, message, severity: 2, inputId }); origins.push(origin) }
  const nodes = new Map<string, GraphNode>()
  const data: DataEdge[] = []
  const scopes = new Map<string, Scope>()
  const resolvedTypes = new Map<string, Record<string, string | undefined>>()
  const ownerOf = (node: GraphNode) => { const s = scopes.get(node.id)?.at(-1); return s?.port === 'body' ? nodes.get(s.owner) : undefined }
  const pure = (node: GraphNode) => isPure(node, catalog)
  function visible(source: GraphNode, target: GraphNode, port: OutputSpec) {
    const b = scopes.get(target.id)
    return b !== undefined && visibleIn(source, b, port)
  }
  /** Whether an output of `source` may be read by a step in scope `b`. */
  function visibleIn(source: GraphNode, b: Scope, port: OutputSpec) {
    const a = scopes.get(source.id)
    if (!a || !isAncestor(a, b)) return false
    const owned = b[a.length]?.owner === source.id
    if (port.visibility === 'outside') return !owned
    if (port.visibility === 'body') return owned && b[a.length]?.port === 'body'
    if (port.visibility === 'loop') return owned && ['body', 'condition'].includes(b[a.length]?.port)
    return true
  }
  /**
   * The step outputs a value node reads, directly or through other value nodes: each once, in the order of the
   * graph's value wires, depth first. A step reading the value node must be able to read each of them.
   */
  function pureSources(start: GraphNode) {
    const found: { node: GraphNode; port: string; output: OutputSpec }[] = [], seen = new Set([start.id])
    const visit = (node: GraphNode) => {
      for (const edge of data) if (edge.target === node.id) {
        const source = nodes.get(edge.source)!, port = edge.sourcePort
        if (pure(source)) { if (!seen.has(source.id)) { seen.add(source.id); visit(source) } }
        else { const output = dataOutputs(source)[port]; if (output && !found.some(f => f.node === source && f.port === port)) found.push({ node: source, port, output }) }
      }
    }
    visit(start)
    return found
  }
  const analysis = (complete: boolean): GraphAnalysis => ({ diagnostics: errors, origins, complete, nodes, data, scopes, resolved: resolvedTypes, ownerOf, visible, pureSources })
  if (!record(value) || value.version !== catalog.graphVersion) { error(null, `Expected graph version ${catalog.graphVersion}`, undefined, undefined, 'graph.version'); return analysis(false) }
  if (typeof value.name !== 'string' || !value.name.trim()) error(null, 'Graph name is required', undefined, undefined, 'graph.name')
  if (Object.hasOwn(value, 'imports')) error(null, words.imports)
  if (Object.hasOwn(value, 'members')) error(null, words.members)
  if (!Array.isArray(value.nodes) || !Array.isArray(value.edges)) { error(null, 'Nodes and edges arrays are required', undefined, undefined, 'graph.shape'); return analysis(false) }
  const graph = value as unknown as Graph
  if (!graph.nodes.length || overLimit(catalog, graph)) { error(null, 'Graph limit: 1–256 nodes and at most 512 edges', undefined, undefined, 'graph.limit'); return analysis(false) }
  // The types of the graph's Custom C# nodes join the catalog's; they are compared by their entries alone. Only once
  // the graph's shape and size are known: nothing is resolved for a graph answered with graph.limit.
  catalog = withGraphTypes(catalog, value)
  for (const node of graph.nodes) {
    if (!record(node) || typeof node.id !== 'string' || !node.id || !record(node.config)) { error(null, 'Each node requires id and config', undefined, undefined, 'node.invalid'); continue }
    if (nodes.has(node.id)) error(node.id, 'Duplicate node id', undefined, undefined, 'node.duplicate')
    else nodes.set(node.id, node)
    if (!nodeSpec(node.kind)) error(node.id, typeof node.kind === 'string' && node.kind.startsWith('library:') ? words.missingLibraryNode : 'Unknown node kind', undefined, undefined, 'node.unknownKind')
  }
  const outgoing = new Map([...nodes.keys()].map(id => [id, [] as ExecutionEdge[]]))
  const dataIndex = new Map<DataEdge, number>()
  const edgeIds = new Set<string>()
  for (const [index, edge] of graph.edges.entries()) {
    origin = { kind: 'edge', edge: index }
    if (!record(edge) || typeof edge.id !== 'string' || !edge.id) { error(null, 'Edge id is required', undefined, undefined, 'edge.invalid'); continue }
    if (edgeIds.has(edge.id)) error(edge.source, 'Duplicate edge id', undefined, undefined, 'edge.duplicate'); edgeIds.add(edge.id)
    if (!nodes.has(edge.source) || !nodes.has(edge.target)) { error(null, 'Edge endpoint missing', undefined, undefined, 'edge.endpoint'); continue }
    if (edge.kind === 'data') {
      if ('port' in edge || typeof edge.sourcePort !== 'string' || typeof edge.targetPort !== 'string' || edge.sourcePort === '' || edge.targetPort === '') error(edge.source, 'This value wire does not join two value pins; connect value pins to value pins', undefined, undefined, 'edge.valueWirePins')
      else { data.push(edge); dataIndex.set(edge, index) }
    } else if (edge.kind === undefined || edge.kind === 'execution') {
      if ('sourcePort' in edge || 'targetPort' in edge) error(edge.source, 'This step order wire names value pins; connect step order pins to step order pins', undefined, undefined, 'edge.orderWirePins')
      if (edge.port !== undefined && typeof edge.port !== 'string') { error(edge.source, 'This step order wire leaves from a pin without a valid name', undefined, undefined, 'edge.orderPinName'); continue }
      // A value node has no execution pins; the wire is reported on it and otherwise ignored.
      const from = nodes.get(edge.source)!, to = nodes.get(edge.target)!
      if (pure(from) || pure(to)) {
        if (pure(from)) error(from.id, words.notInFlow(words.stepName(from, catalog)), undefined, undefined, 'flow.pure')
        if (pure(to) && to.id !== from.id) error(to.id, words.notInFlow(words.stepName(to, catalog)), undefined, undefined, 'flow.pure')
        continue
      }
      const node = nodes.get(edge.source)!, port = flowPort(node, edge)
      if (!executionPorts(node).includes(port)) error(node.id, 'Unknown execution port', undefined, port, 'port.unknownExecution')
      if (outgoing.get(node.id)!.some(e => flowPort(node, e) === port)) error(node.id, 'Only one execution edge per port', undefined, port, 'port.executionTaken')
      if (nodes.get(edge.target)!.kind === 'trigger') error(edge.target, 'Trigger cannot have incoming execution edges', undefined, undefined, 'trigger.incoming')
      outgoing.get(node.id)!.push(edge)
    } else error(edge.source, 'This wire is neither a step order wire nor a value wire', undefined, undefined, 'edge.unknownKind')
  }
  origin = graphOrigin
  function cycles(edges: { source: string; target: string }[], label: string) {
    const active = new Set<string>(), done = new Set<string>()
    function visit(id: string) {
      if (active.has(id)) { if (label === 'Data') error(id, words.ownResult(words.stepName(nodes.get(id)!, catalog)), undefined, undefined, 'cycle.data'); else error(id, 'Execution cycle is forbidden', undefined, undefined, 'cycle.execution'); return }
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
    if (path.length > catalog.limits.nesting) { error(id, 'Structured nesting limit is 8', undefined, undefined, 'limit.nesting'); return }
    const old = scopes.get(id)
    if (old) { if (!isAncestor(old, path) || old.length !== path.length) error(id, 'A wire cannot go into or out of a loop or parallel branch from outside it', undefined, undefined, 'scope.execution'); return }
    scopes.set(id, path)
    const node = nodes.get(id)!
    for (const edge of outgoing.get(id) ?? []) {
      const port = flowPort(node, edge)
      scope(edge.target, scopePort(node, port) ? [...path, { owner: id, port }] : path)
    }
  }
  const roots = [...nodes.values()].filter(n => n.kind === 'trigger')
  if (!roots.length) error(null, 'At least one trigger is required', undefined, undefined, 'graph.noTrigger')
  roots.forEach(n => scope(n.id, []))
  // A value node is never reached by execution; it is a warning when nothing reads it.
  for (const [id, node] of nodes)
    if (pure(node)) { if (!data.some(e => e.source === id)) { errors.push({ nodeId: id, message: words.unused(words.stepName(node, catalog)), severity: 2, code: 'pure.unused' }); origins.push(origin) } }
    else if (!scopes.has(id)) error(id, words.unreachable(words.stepName(node, catalog)), undefined, undefined, 'node.unreachable')
  const incoming = new Map<string, DataEdge>()
  const key = (node: string, port: string) => JSON.stringify([node, port])
  for (const edge of data) {
    origin = { kind: 'edge', edge: dataIndex.get(edge)! }
    const source = nodes.get(edge.source)!, target = nodes.get(edge.target)!
    const output = dataOutputs(source)[edge.sourcePort], input = connectable(dataInputs(target, ownerOf(target))[edge.targetPort])
    if (!output) error(source.id, 'Unknown data output port', undefined, edge.sourcePort, 'port.unknownOutput')
    if (!input) error(target.id, words.unknownInput(edge.targetPort, target.kind), edge.targetPort, edge.targetPort, 'port.unknownInput')
    // The first wire into an input is the one the step reads (C# keeps it too); a later one is only reported.
    if (incoming.has(key(target.id, edge.targetPort))) error(target.id, 'Only one data source per input', edge.targetPort, edge.targetPort, 'port.inputTaken')
    else incoming.set(key(target.id, edge.targetPort), edge)
    if (output && input) {
      const wrong = wireTypeError(source, output, target, input, edge.targetPort, catalog, isAssignable)
      if (wrong) error(target.id, wrong.message, edge.targetPort, edge.targetPort, wrong.code)
      // A wire to or from a step not in the flow yet gets nothing: that step carries node.unreachable.
      if (scopes.has(source.id) && scopes.has(target.id) && !visible(source, target, output))
        error(target.id, words.scopeData(inputSubject(target, input, edge.targetPort, catalog)), edge.targetPort, edge.targetPort, 'scope.data')
      // A step reading a value node reads, in effect, every step output that value node uses.
      if (pure(source) && !pure(target) && scopes.has(target.id))
        for (const { node: producer, port, output: spec } of pureSources(source))
          if (scopes.has(producer.id) && !visible(producer, target, spec))
            error(target.id, words.pureScope(words.stepName(source, catalog), words.stepName(producer, catalog), words.portLabel(spec, port), words.stepName(target, catalog)), edge.targetPort, edge.targetPort, 'scope.data')
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
    if (!record(raw) || raw.kind === 'unset') { if (spec.required) error(node.id, service ? `${serviceTitle(node, catalog)} needs ${label}` : `${label} is required`, port, port); else if (raw !== undefined && !record(raw)) error(node.id, `${subject} has an invalid value`, port, port); return }
    let type: string | undefined
    if (raw.kind === 'variable') { error(node.id, words.variable(subject), port, port); return }
    // A value with properties (a record, class or struct) comes from a step: nothing can be typed in, null included.
    if (selectsProperties(nodeSpec(node.kind), port)) { error(node.id, words.wireOnly(words.stepName(node, catalog)), port, port); return }
    if (raw.kind === 'literal') {
      type = text(raw.type, 'unknown')
      const v = raw.value
      const valid = validLiteral(type, v, catalog)
      if (!valid) {
        // A number that does not fit a narrower number type (2.5 for a C# int) says what does fit.
        const ownType = typeof v === 'number' && Number.isFinite(v) && knownType(type) && numeric(type, catalog) ? resolveTypeSpec(type, catalog) : undefined
        error(node.id, typeof raw.type !== 'string' || raw.type === '' ? `${subject} has a value without a type` : !knownType(type) ? `${subject} has a value of unknown type ${words.name(type)}`
          : ownType ? `${subject} must be ${words.range(ownType.min, ownType.max, ownType.literalKind === 'integer')}` : `${subject} is not a valid ${typeLabel(type, catalog)} value`, port)
      }
      if (typeof v === 'number' && Number.isFinite(v) && ((spec.min !== undefined && v < spec.min) || (spec.max !== undefined && v > spec.max) || (spec.integer && !Number.isInteger(v)))) error(node.id, `${subject} must be ${words.range(spec.min, spec.max, spec.integer === true)}`, port)
      // A number typed into a pin of a narrower number type (a C# int, say) must fit that type, as it must when it runs.
      if (valid && typeof v === 'number' && Number.isFinite(v) && numeric(type, catalog)) {
        const fits = spec.types.filter(t => t !== type && isAssignable(type!, t))
        const narrow = fits.length && fits.every(t => numeric(t, catalog) && !validLiteral(t, v, catalog)) ? resolveTypeSpec(fits[0], catalog) : undefined
        if (narrow) error(node.id, `${subject} must be ${words.range(narrow.min, narrow.max, narrow.literalKind === 'integer')}`, port)
      }
      // Input constraints restrict authored values only; a wire is constrained by its type. When any allowed value
      // is itself a list (a constant list), only a value equal to one of them is accepted. When every allowed value
      // is a scalar, a list, where the input accepts one, is checked item by item (a multiple select; an empty list
      // passes and minItems decides), and anything else must equal an allowed value.
      if (spec.enum && !spec.enum.some(a => jsonEqual(a, v))) {
        const allowed = spec.enum
        if (Array.isArray(v) && spec.types.includes('array') && !allowed.some(a => Array.isArray(a))) {
          if (!v.every(item => allowed.some(a => jsonEqual(a, item)))) error(node.id, `${subject} may only contain ${words.choices(allowed, spec.enumLabels, '')}`, port)
        } else error(node.id, `${subject} must be ${words.choices(allowed, spec.enumLabels, 'one of ')}`, port)
      }
      // Items of a list of entities (an input taking entity ids or a list of them, as a multiple entity selector
      // does) must be entity ids of an accepted domain.
      const entityTypes = spec.types.filter(t => t !== 'array')
      if (Array.isArray(v) && spec.types.includes('array') && entityTypes.length && entityTypes.every(t => refinesType(t, 'entity_id', catalog)) && !v.every(item => entityTypes.some(t => validLiteral(t, item, catalog))))
        error(node.id, `${subject} may only contain ${words.or([...new Set(entityTypes.map(t => typeLabel(t, catalog)))])} values`, port)
      if (spec.pattern && typeof v === 'string' && !formatted(node.id, subject, v, spec.pattern, port)) error(node.id, `${subject} is not in the expected format`, port)
      if (Array.isArray(v) && ((spec.minItems !== undefined && v.length < spec.minItems) || (spec.maxItems !== undefined && v.length > spec.maxItems))) error(node.id, `${subject} must have ${words.items(spec.minItems, spec.maxItems)}`, port)
    } else error(node.id, `${subject} has an invalid value`, port)
    // A value of unknown type was reported above; it is not also a mismatch. Nor is a value for an input that accepts no
    // known type (a Constant whose Type is missing or unknown): that is reported where the type is set.
    if (type && knownType(type) && spec.types.some(knownType) && !spec.types.some(t => isAssignable(type!, t)))
      error(node.id, node.kind === 'constant' && port === 'value' ? words.constantRetyped(label, type, spec.types.find(knownType)!, catalog) : words.incompatible(subject, type, spec.types, catalog), port)
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
    if (!schema) { warning(node.id, `${service} is not available in this Home Assistant, so its fields cannot be checked`, 'service'); return }
    const known = record(schema.fields) ? schema.fields : {}
    const fields = record(node.config.fields) ? node.config.fields : {}
    for (const name of Object.keys(fields)) if (!Object.hasOwn(known, name)) error(node.id, `${serviceTitle(node, catalog)} has no field ${words.name(name)}`, `field:${name}`)
    for (const [name, field] of Object.entries(known)) if (record(field) && field.required === true && !Object.hasOwn(fields, name)) error(node.id, `${serviceTitle(node, catalog)} needs ${words.portLabel(field, name)}`, 'fields')
    const wired = incoming.has(key(node.id, 'target')), target = node.config.target
    const response = text(schema.response, 'none'), returnResponse = node.config.returnResponse === true
    if (returnResponse && response === 'none') error(node.id, `${serviceTitle(node, catalog)} returns no response; turn off Return response`, 'returnResponse')
    if (!returnResponse && response === 'only') error(node.id, `${serviceTitle(node, catalog)} only returns a response; turn on Return response`, 'returnResponse')
    // A target value object is checked by its entity_id domains; device, area, floor and label ids are not.
    const domains = serviceTargetDomains(schema)
    if (!domains.length || wired || !record(target) || target.kind !== 'literal' || !record(target.value)) return
    const entities = Array.isArray(target.value.entity_id) ? target.value.entity_id : target.value.entity_id === undefined ? [] : [target.value.entity_id]
    const accepted = words.or(domains.map(d => domainWords(d).toLowerCase()))
    origin = { kind: 'serviceTarget', node: node.id }
    for (const entity of entities)
      if (typeof entity === 'string' && entity !== 'all' && entity !== 'none' && !domains.includes(entity.split('.')[0]))
        error(node.id, `${serviceTitle(node, catalog)} only accepts ${accepted} entities; ${words.name(entity)} is not ${entity.indexOf('.') > 0 ? 'one' : 'an entity'}`, 'target')
    origin = graphOrigin
  }
  for (const node of nodes.values()) {
    const spec = nodeSpec(node.kind); if (!spec) continue
    // A statement-style Custom C# node is reported once, with what to do; nothing else about it is checked.
    if (spec.dynamicInputs === 'custom' && Object.hasOwn(node.config, 'code')) { error(node.id, words.oldCustomCode, 'source'); continue }
    for (const [field, rule] of Object.entries(spec.config)) {
      const v = node.config[field]
      const label = configLabel(rule, field)
      if (v === undefined) { if (rule.required) error(node.id, `${label} is required`, field); continue }
      const valid = rule.type === 'array' ? Array.isArray(v) : rule.type === 'object' ? record(v) : rule.type === 'integer' ? typeof v === 'number' && Number.isInteger(v) : typeof v === rule.type
      if (!valid) { error(node.id, `${label} must be ${words.configKind(rule.type)}`, field); continue }
      // Trigger type changes retain inactive fields so drafts and overrides are not lost.
      if (node.kind === 'trigger' && field === 'at' && (node.config.type ?? spec.config.type.default) !== 'once') continue
      // Type choices accept every resolvable id, so a parametric type can be selected for narrowing.
      if (rule.enumSource === 'types') { if (typeof v !== 'string' || !knownType(v)) error(node.id, `${label} must be a known type`, field) }
      else if (rule.enum && !rule.enum.some(choice => jsonEqual(choice, v))) error(node.id, `${label} must be ${words.choices(rule.enum, (rule as { enumLabels?: unknown }).enumLabels, 'one of ')}`, field)
      if (typeof v === 'number' && ((rule.min !== undefined && v < rule.min) || (rule.max !== undefined && v > rule.max))) error(node.id, `${label} must be ${words.range(rule.min, rule.max, false)}`, field)
      if (typeof v === 'string' && rule.pattern && !formatted(node.id, label, v, rule.pattern, field)) error(node.id, `${label} is not in the expected format`, field)
      // A value already out of format is not also judged as a date (Date.parse accepts 24:00, .NET does not).
      if (typeof v === 'string' && rule.format === 'date-time-offset' && (!rule.pattern || tryMatches(v, rule.pattern) !== false) && !validOffsetDateTime(v)) error(node.id, `${label} must be a date and time with a time zone offset or Z`, field)
      if (Array.isArray(v) && ((rule.minItems !== undefined && v.length < rule.minItems) || (rule.maxItems !== undefined && v.length > rule.maxItems))) error(node.id, `${label} must have ${words.items(rule.minItems, rule.maxItems)}`, field)
    }
    for (const port of spec.requiredExecution ?? []) if (!outgoing.get(node.id)!.some(e => flowPort(node, e) === port)) error(node.id, 'Execution connection is required', port, port)
    if (spec.argumentStorage === 'arguments' && record(node.config.arguments))
      for (const name of Object.keys(node.config.arguments)) if (!Object.hasOwn(dataInputs(node, ownerOf(node)), name)) error(node.id, `Unknown input ${words.name(name)}`, name)
    const resolved: Record<string, string | undefined> = Object.create(null)
    for (const [port, rule] of Object.entries(dataInputs(node, ownerOf(node)))) resolved[port] = operand(node, port, inputValue(node, port, catalog), rule)
    resolvedTypes.set(node.id, resolved)
    // A Select property node: with a value connected whose type has properties, the chosen one must be one of them.
    for (const input of new Set(Object.values(spec.outputs).map(propertyOf).filter(p => p !== ''))) {
      const edge = incoming.get(key(node.id, input))
      const valueType = edge ? text(dataOutputs(nodes.get(edge.source)!)[edge.sourcePort]?.type) : ''
      const members = valueType ? typeProperties(catalog, valueType) : []
      if (!members.length) continue
      const chosen = text(node.config.property)
      origin = { kind: 'operand', node: node.id, port: input }
      if (!chosen) error(node.id, words.propertyUnset(typeLabel(valueType, catalog)), 'property', undefined, 'property.unset')
      else if (!members.some(m => text(m.name) === chosen)) error(node.id, words.propertyUnknown(typeLabel(valueType, catalog), chosen), 'property', undefined, 'property.unknown')
      origin = graphOrigin
    }
    // A Repeat count typed in above the loop's own Maximum iterations would fail when the loop starts.
    const count = node.kind === 'repeat' && !incoming.has(key(node.id, 'count')) ? inputValue(node, 'count', catalog) : undefined
    const max = node.config.maxIterations, countSpec = dataInputs(node, ownerOf(node)).count
    if (record(count) && count.kind === 'literal' && typeof count.value === 'number' && Number.isFinite(count.value) && typeof max === 'number' && Number.isFinite(max) && count.value > max &&
      !(typeof countSpec?.max === 'number' && count.value > countSpec.max)) {
      origin = { kind: 'operand', node: node.id, port: 'count' }
      error(node.id, words.repeatCount(words.portLabel(countSpec, 'count'), max, configLabel(spec.config.maxIterations, 'maxIterations')), 'count')
      origin = graphOrigin
    }
    if (spec.comparison) {
      origin = { kind: 'comparison', node: node.id }
      for (const d of comparisonDiagnostics(node, spec, resolved, catalog)) error(d.nodeId, d.message, d.inputId)
      origin = graphOrigin
    }
    if (spec.dynamicInputs === 'service') {
      validateService(node, spec)
      origin = { kind: 'serviceTarget', node: node.id }
      const connected = (port: string) => incoming.has(key(node.id, port))
      for (const d of serviceTargetDiagnostics(node, connected, catalog)) error(d.nodeId, d.message, d.inputId)
      origin = graphOrigin
    }
    if (Object.hasOwn(node.config, 'output')) error(node.id, words.exposedValue, 'output')
    if (spec.dynamicInputs === 'custom' && record(node.config.signature) && !wellFormedSignature(node.config.signature)) error(node.id, words.malformedSignature, 'source', undefined, 'custom.signature')
    if (node.kind === 'trigger') {
      const type = node.config.type ?? spec.config.type.default
      const required = type === 'ha' ? ['trigger'] : type === 'entity' ? ['entity'] : type === 'daily' ? ['time','timeZone'] : type === 'once' ? ['at'] : []
      for (const field of required) if (node.config[field] === undefined || node.config[field] === '') error(node.id, `${configLabel(spec.config[field], field)} is required`, field)
      // The generator reads a daily time as exactly HH:MM.
      if (type === 'daily' && typeof node.config.time === 'string' && node.config.time !== '' && !matches(node.config.time, words.timeOfDay)) error(node.id, words.timeOfDayMessage(configLabel(spec.config.time, 'time')), 'time')
      if (type === 'ha' && record(node.config.trigger) && ![node.config.trigger.trigger, node.config.trigger.platform].some(p => typeof p === 'string' && p.trim())) error(node.id, 'Home Assistant trigger needs a platform', 'trigger')
      if (Object.hasOwn(node.config, 'entitiesExpression')) error(node.id, words.entitiesExpression, 'entitiesExpression')
      if (Object.hasOwn(node.config, 'schedulesExpression')) error(node.id, words.schedulesExpression, 'schedulesExpression')
    }
    if (['entity_field','wait_until'].includes(node.kind) && node.config.field === 'attribute' && (typeof node.config.attribute !== 'string' || !node.config.attribute.trim())) error(node.id, 'Attribute is required', 'attribute')
    // A path that starts with / can still be wrong in one way: a ~ that is not ~0 (a ~ in a key) or ~1 (a / in a key).
    if (node.kind === 'get_field' && (typeof node.config.pointer !== 'string' || !/^(?:\/(?:[^~]|~[01])*)*$/.test(node.config.pointer)))
      error(node.id, typeof node.config.pointer === 'string' && node.config.pointer.startsWith('/') ? words.pathTilde : 'Path must start with /, such as /state', 'pointer')
    if (node.kind === 'parallel' && Array.isArray(node.config.branches)) {
      const ids = new Set<string>()
      for (const branch of node.config.branches) {
        if (typeof branch !== 'string' || !matches(branch, catalog.structures.branchId.pattern) || ids.has(branch)) error(node.id, 'Branch ids must be unique stable names', 'branches')
        if (typeof branch === 'string') ids.add(branch)
      }
    }
    if (spec.dynamicOutputs === 'loop') {
      for (const group of ['carry','exports'] as const) {
        const names = new Set<string>()
        if (Array.isArray(node.config[group]) && node.config[group].some(d => !record(d))) error(node.id, `Each ${group === 'carry' ? 'carried' : 'exported'} value of this loop needs a name and a type`, group)
        for (const decl of declarations(node, group)) {
          if (!record(decl) || typeof decl.name !== 'string' || !matches(decl.name, catalog.structures.loopDeclaration.name.pattern) || names.has(decl.name) || typeof decl.type !== 'string' || !knownType(decl.type)) { error(node.id, `The ${group === 'carry' ? 'carried' : 'exported'} values of this loop need unique names and known types`, group); continue }
          names.add(decl.name)
          operand(node, `${group}.${decl.name}.initial`, decl.initial, { types: [decl.type], required: true, description: 'Starting value', label: `Starting value of ${decl.name}` })
        }
      }
      if (['while','until'].includes(node.kind)) {
        const condition = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'condition')
        if (!condition || !terminalsValid(condition.target, 'condition_result', node.id)) error(node.id, 'Every condition path must end in a Condition result step', 'condition', 'condition')
      }
      const body = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'body')
      if ((declarations(node, 'carry').length || declarations(node, 'exports').length) && (!body || !terminalsValid(body.target, 'loop_result', node.id))) error(node.id, 'Every loop body path must end in a Loop result step when the loop carries or exports values', 'body', 'body')
    }
    if (node.kind === 'condition_result') { const s = scopes.get(node.id)?.at(-1); if (!s || s.port !== 'condition' || !['while','until'].includes(nodes.get(s.owner)!.kind)) error(node.id, 'A Condition result step must be inside the condition of a While or Until loop', undefined, undefined, 'scope.conditionResult') }
    if (node.kind === 'loop_result') {
      const owner = ownerOf(node)
      if (!owner || nodeSpec(owner.kind)?.dynamicOutputs !== 'loop') error(node.id, 'A Loop result step must be inside the body of a loop', undefined, undefined, 'scope.loopResult')
      if (record(node.config.values)) for (const name of Object.keys(node.config.values)) if (!dataInputs(node, owner)[name]) error(node.id, `Unknown loop value ${words.name(name)}`, name)
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
  // A Custom C# node is titled by its method until someone names it.
  return fillGraphLayout({ ...graph, nodes: [...graph.nodes, { id, kind, ...(spec.dynamicInputs === 'custom' ? {} : { label: spec.label }), config }] })
}
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
  // A connection is refused only for a problem it causes: a new diagnostic (new by its full content, message
  // included), or any diagnostic on the input it fills or about the output it reads (one with no inputId: an input
  // of the same name as the output is another pin). A blocking diagnostic the graph already has is no reason. A cycle diagnostic names the
  // node where the search happened to close the loop, which another wire can move, so cycles are judged by whether
  // the new wire itself lies on one (a data wire never changes execution cycles). A value node left unused by the
  // wire it replaces is only a warning about that value node, not a problem the new wire causes.
  const after = analyzeGraph(next, catalog)
  // A Select property node that now has a value says which property to choose (or that its chosen one is missing): that
  // is about the node, not a problem the wire causes; one on the input the wire fills still refuses it.
  const addedErrors = after.diagnostics.filter(e => !e.code?.startsWith('cycle.') && e.code !== 'pure.unused' && (!old.includes(JSON.stringify(e)) && !(e.code?.startsWith('property.') && !(e.nodeId === target && e.inputId === targetPort)) ||
    (e.nodeId === target && (e.inputId === targetPort || e.portId === targetPort)) ||
    (e.nodeId === source && e.portId === sourcePort && e.inputId === undefined)))
  const messages = addedErrors.map(e => e.message)
  if (after.complete && closesDataCycle(after.data, source, target)) messages.push(words.ownResult(words.stepName(node, catalog)))
  if (messages.length) throw new Error(messages.join('; '))
  return next
}
/**
 * Whether a data wire from `source` into `target` lies on a cycle of the validator's data wires `edges` (which
 * include it): `source` can be reached from `target`.
 */
export function closesDataCycle(edges: readonly { source: string; target: string }[], source: string, target: string): boolean {
  if (source === target) return true
  const adjacency = new Map<string, string[]>()
  for (const e of edges) adjacency.set(e.source, [...(adjacency.get(e.source) ?? []), e.target])
  const seen = new Set([target])
  for (const stack = [target]; stack.length; ) for (const n of adjacency.get(stack.pop()!) ?? []) {
    if (n === source) return true
    if (!seen.has(n)) { seen.add(n); stack.push(n) }
  }
  return false
}
export function disconnectData(graph: Graph, target: string, targetPort: string, catalog: VisualCatalogSnapshot = nodeCatalog): Graph {
  const next = structuredClone(graph), node = next.nodes.find(n => n.id === target)
  next.edges = next.edges.filter(e => !(e.kind === 'data' && e.target === target && e.targetPort === targetPort))
  if (node) setInput(node, targetPort, { kind: 'unset' }, catalog)
  return next
}
