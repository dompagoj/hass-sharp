import rawCatalog from '../../../visual_node_catalog.json'

/** V4 is the visual graph format. The document/API envelope remains version 1. */
export type DataType = string
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type V4Operand = { kind: 'literal'; type: DataType; value: JsonValue } | { kind: 'variable'; name: string } | { kind: 'unset' }
export interface V4Node { id: string; kind: string; config: Record<string, unknown>; label?: string; position?: { x: number; y: number }; size?: { width: number; height: number } }
export interface ExecutionEdge { id: string; kind?: 'execution'; source: string; target: string; port?: string }
export interface DataEdge { id: string; kind: 'data'; source: string; target: string; sourcePort: string; targetPort: string }
export type V4Edge = ExecutionEdge | DataEdge
export interface GraphV4 { version: 4; name: string; nodes: V4Node[]; edges: V4Edge[]; imports?: string; members?: string }
export interface V4Diagnostic { nodeId: string | null; message: string; severity: number; inputId?: string; portId?: string }
export interface ConfigSpec { label?: string; description?: string; type: string; required?: boolean; default?: unknown; enum?: unknown[]; enumSource?: 'types'; min?: number; max?: number; pattern?: string; minItems?: number; maxItems?: number }
export interface InputSpec { label?: string; types: string[]; required: boolean; description: string; default?: unknown; min?: number; max?: number; integer?: boolean }
export interface OutputSpec { label?: string; type: string; description: string; visibility?: string }
export interface NodeSpec { argumentStorage?: 'arguments'; label: string; category: string; description: string; minGraphVersion: number; config: Record<string, ConfigSpec>; inputs: Record<string, InputSpec>; outputs: Record<string, OutputSpec>; execution: string[]; scopePorts?: string[]; requiredExecution?: string[]; dynamicExecution?: string; dynamicInputs?: string; dynamicOutputs?: string; comparison?: { leftInput?: string; typeConfig?: string; rightInput: string; operatorConfig: string; orderingOperators: string[]; orderingType: string } }
export interface TypeSpec { elementType?: string; description: string; label?: string; category?: string; nullable?: boolean; literalKind?: string | null; assignableTo?: string[]; refines?: string; pattern?: string; min?: number; max?: number; character?: boolean; enumValues?: JsonValue[] }
export interface VisualCatalogSnapshot { version: number; graphVersion: number; limits: { nodes: number; edges: number; nesting: number; executionSteps: number; loopIterations: number }; types: Record<string, TypeSpec>; nodes: Record<string, NodeSpec>; structures: { loopDeclaration: { name: { pattern: string } }; branchId: { pattern: string } } }
/** Pass the server-produced catalog explicitly for each document; graph metadata is never type authority. */
export const nodeCatalog = rawCatalog as unknown as VisualCatalogSnapshot
const nodeSpec = (kind: unknown, catalog: VisualCatalogSnapshot = nodeCatalog) => typeof kind === 'string' && Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const declarations = (node: V4Node, key: 'carry' | 'exports') => (Array.isArray(node.config[key]) ? node.config[key] as unknown[] : []).filter(record)
const text = (value: unknown, fallback = '') => typeof value === 'string' ? value : fallback
const resolveType = (type: string, node: V4Node) => type.startsWith('$') ? text(node.config[type.slice(1)]) : type
export const effectiveTriggerMethod = (node: V4Node) => 'Run_' + Array.from(new TextEncoder().encode(node.id), byte => byte.toString(16).padStart(2, '0').toUpperCase()).join('')
export const compatibleTypes = (source: string, target: string, catalog: VisualCatalogSnapshot = nodeCatalog) => Object.hasOwn(catalog.types, source) && Object.hasOwn(catalog.types, target) && (source === target || (catalog.types[source].assignableTo ?? []).includes(target))
export function executionPorts(node: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog): string[] {
  const spec = nodeSpec(node.kind, catalog)
  return spec ? [...(spec.dynamicExecution === 'branches' && Array.isArray(node.config.branches) ? node.config.branches.filter((id): id is string => typeof id === 'string').map(id => `branch:${id}`) : []), ...spec.execution] : []
}
export function dataOutputs(node: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, OutputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.outputs ?? {}).map(([key, value]) => [key, { ...value, type: resolveType(value.type, node) }]))) as Record<string, OutputSpec>
  if (spec?.dynamicOutputs === 'loop') for (const key of ['carry', 'exports'] as const) for (const item of declarations(node, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { type: item.type, label: item.name, description: `${key} value ${item.name}`, visibility: key === 'carry' ? 'loop' : 'outside' }
  }
  return ports
}
export function dataInputs(node: V4Node, loopOwner?: V4Node, catalog: VisualCatalogSnapshot = nodeCatalog): Record<string, InputSpec> {
  const spec = nodeSpec(node.kind, catalog)
  const ports = Object.assign(Object.create(null), Object.fromEntries(Object.entries(spec?.inputs ?? {}).map(([key, value]) => [key, { ...value, types: value.types.flatMap(t => t === '$any' ? Object.keys(catalog.types) : [resolveType(t, node)]) }]))) as Record<string, InputSpec>
  if (node.kind === 'foreach' && ports.items) ports.items.types = ['array', ...Object.entries(catalog.types).filter(([, type]) => !type.nullable && type.elementType && compatibleTypes(type.elementType, text(node.config.itemType), catalog)).map(([id]) => id)]
  const types = Object.keys(catalog.types)
  const dynamic = spec?.dynamicInputs
  if (dynamic === 'fields' && record(node.config.fields)) for (const key of Object.keys(node.config.fields)) ports[`field:${key}`] = { types, required: true, label: key, description: `Object field ${key}` }
  if (dynamic === 'items' && Array.isArray(node.config.items)) node.config.items.forEach((_, i) => { ports[`item:${i}`] = { types, required: true, label: `Item ${i}`, description: `Array item ${i}` } })
  if (dynamic === 'loop' && loopOwner) for (const key of ['carry', 'exports'] as const) for (const item of declarations(loopOwner, key)) {
    if (typeof item.name !== 'string' || typeof item.type !== 'string') continue
    ports[`${key === 'carry' ? 'carry' : 'export'}:${item.name}`] = { types: [item.type], required: false, label: item.name, description: `Next ${key} value` }
  }
  return ports
}
function inputValue(node: V4Node, port: string, catalog: VisualCatalogSnapshot): unknown {
  if (nodeSpec(node.kind, catalog)?.argumentStorage === 'arguments') return (node.config.arguments as Record<string, unknown> | undefined)?.[port]
  if (port.startsWith('field:')) return (node.config.fields as Record<string, unknown> | undefined)?.[port.slice(6)]
  if (port.startsWith('item:')) return (node.config.items as unknown[] | undefined)?.[Number(port.slice(5))]
  if (node.kind === 'loop_result') return (node.config.values as Record<string, unknown> | undefined)?.[port]
  return node.config[port]
}
function setInput(node: V4Node, port: string, value: V4Operand, catalog: VisualCatalogSnapshot) {
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
type Scope = { owner: string; port: string }[]
const isAncestor = (a: Scope, b: Scope) => a.length <= b.length && a.every((s, i) => s.owner === b[i].owner && s.port === b[i].port)
const scopePort = (node: V4Node, port: string, catalog: VisualCatalogSnapshot) => nodeSpec(node.kind, catalog)?.scopePorts?.includes(port) || (node.kind === 'parallel' && port.startsWith('branch:'))
const flowPort = (node: V4Node, edge: ExecutionEdge) => node.kind === 'condition' && (!edge.port || edge.port === 'next') ? 'true' : edge.port ?? 'next'

const catalogExecutionPorts = executionPorts, catalogDataOutputs = dataOutputs, catalogDataInputs = dataInputs, catalogCompatibleTypes = compatibleTypes, catalogScopePort = scopePort
function validLiteral(type: string, value: unknown, catalog: VisualCatalogSnapshot): boolean {
  if (!Object.hasOwn(catalog.types, type) || value === undefined) return false
  const spec = catalog.types[type]
  if (value === null) return spec.nullable === true
  if (type === 'entity_id') return typeof value === 'string' && new RegExp(spec.pattern!).test(value)
  switch (spec.literalKind) {
    case 'boolean': return typeof value === 'boolean'
    case 'string': return typeof value === 'string' && (spec.character !== true || value.length === 1)
    case 'object': return record(value)
    case 'array': return Array.isArray(value)
    case 'enum': return (spec.enumValues ?? []).some(v => v === value)
    case 'integer': case 'number': return typeof value === 'number' && Number.isFinite(value) && (spec.literalKind !== 'integer' || Number.isInteger(value)) && (spec.min === undefined || value >= spec.min) && (spec.max === undefined || value <= spec.max)
    default: return false
  }
}

/** Static validation only: evaluating data never schedules a producer. */
export function validateGraphV4(value: unknown, catalog: VisualCatalogSnapshot = nodeCatalog): V4Diagnostic[] {
  const types = Object.keys(catalog.types)
  const nodeSpec = (kind: unknown) => typeof kind === 'string' && Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind] : undefined
  const executionPorts = (node: V4Node) => catalogExecutionPorts(node, catalog)
  const dataOutputs = (node: V4Node) => catalogDataOutputs(node, catalog)
  const dataInputs = (node: V4Node, owner?: V4Node) => catalogDataInputs(node, owner, catalog)
  const compatibleTypes = (source: string, target: string) => catalogCompatibleTypes(source, target, catalog)
  const scopePort = (node: V4Node, port: string) => catalogScopePort(node, port, catalog)
  const errors: V4Diagnostic[] = []
  const error = (nodeId: string | null, message: string, inputId?: string, portId?: string) => { errors.push({ nodeId, message, severity: 3, ...(inputId ? { inputId } : {}), ...(portId ? { portId } : {}) }) }
  if (!record(value) || value.version !== 4) { error(null, 'Expected graph version 4'); return errors }
  if (typeof value.name !== 'string' || !value.name.trim()) error(null, 'Graph name is required')
  if (!Array.isArray(value.nodes) || !Array.isArray(value.edges)) { error(null, 'Nodes and edges arrays are required'); return errors }
  const graph = value as unknown as GraphV4
  if (!graph.nodes.length || graph.nodes.length > catalog.limits.nodes || graph.edges.length > catalog.limits.edges) { error(null, 'Graph limit: 1–256 nodes and at most 512 edges'); return errors }
  const nodes = new Map<string, V4Node>()
  for (const node of graph.nodes) {
    if (!record(node) || typeof node.id !== 'string' || !node.id || !record(node.config)) { error(null, 'Each node requires id and config'); continue }
    if (nodes.has(node.id)) error(node.id, 'Duplicate node id')
    else nodes.set(node.id, node)
    if (!nodeSpec(node.kind)) error(node.id, 'Unknown node kind')
  }
  const outgoing = new Map([...nodes.keys()].map(id => [id, [] as ExecutionEdge[]]))
  const data: DataEdge[] = []
  const edgeIds = new Set<string>()
  for (const edge of graph.edges) {
    if (!record(edge) || typeof edge.id !== 'string' || !edge.id) { error(null, 'Edge id is required'); continue }
    if (edgeIds.has(edge.id)) error(edge.source, 'Duplicate edge id'); edgeIds.add(edge.id)
    if (!nodes.has(edge.source) || !nodes.has(edge.target)) { error(null, 'Edge endpoint missing'); continue }
    if (edge.kind === 'data') {
      if ('port' in edge || typeof edge.sourcePort !== 'string' || typeof edge.targetPort !== 'string') error(edge.source, 'Do not mix execution and data ports')
      else data.push(edge)
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
  cycles([...outgoing.values()].flat(), 'Execution'); cycles(data, 'Data')
  const scopes = new Map<string, Scope>()
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
  const ownerOf = (node: V4Node) => { const s = scopes.get(node.id)?.at(-1); return s?.port === 'body' ? nodes.get(s.owner) : undefined }
  function visible(source: V4Node, target: V4Node, port: OutputSpec) {
    const a = scopes.get(source.id), b = scopes.get(target.id)
    if (!a || !b || !isAncestor(a, b)) return false
    const owned = b[a.length]?.owner === source.id
    if (port.visibility === 'outside') return !owned
    if (port.visibility === 'body') return owned && b[a.length]?.port === 'body'
    if (port.visibility === 'loop') return owned && ['body', 'condition'].includes(b[a.length]?.port)
    return true
  }
  const incoming = new Map<string, DataEdge>()
  const key = (node: string, port: string) => JSON.stringify([node, port])
  for (const edge of data) {
    const source = nodes.get(edge.source)!, target = nodes.get(edge.target)!
    const output = dataOutputs(source)[edge.sourcePort], input = dataInputs(target, ownerOf(target))[edge.targetPort]
    if (!output) error(source.id, 'Unknown data output port', undefined, edge.sourcePort)
    if (!input) error(target.id, 'Unknown data input port', edge.targetPort, edge.targetPort)
    if (incoming.has(key(target.id, edge.targetPort))) error(target.id, 'Only one data source per input', edge.targetPort, edge.targetPort)
    incoming.set(key(target.id, edge.targetPort), edge)
    if (output && input) {
      if (!input.types.some(t => compatibleTypes(output.type, t))) error(target.id, `Incompatible input type ${output.type}; expected ${input.types.join('/')}`, edge.targetPort, edge.targetPort)
      if (!visible(source, target, output)) error(target.id, 'Data cannot cross this structured scope', edge.targetPort, edge.targetPort)
    }
  }
  function operand(node: V4Node, port: string, raw: unknown, spec: InputSpec): string | undefined {
    const edge = incoming.get(key(node.id, port))
    if (edge) return dataOutputs(nodes.get(edge.source)!)[edge.sourcePort]?.type
    if (raw === undefined && !spec.required) return
    if (!record(raw) || raw.kind === 'unset') { if (spec.required) error(node.id, 'Input is required; connect a source or set a literal', port, port); else if (raw !== undefined && !record(raw)) error(node.id, 'Expected a typed operand', port, port); return }
    let type: string | undefined
    if (raw.kind === 'variable') {
      if (typeof raw.name !== 'string' || !new RegExp(catalog.structures.loopDeclaration.name.pattern).test(raw.name)) { error(node.id, 'Variable name is required and must be a valid identifier', port, port); return }
      const candidates = [...nodes.values()].filter(n => typeof n.config.output === 'string' && n.config.output === raw.name && dataOutputs(n).value && visible(n, node, dataOutputs(n).value))
      type = candidates[0] ? dataOutputs(candidates[0]).value.type : undefined
      if (!type || candidates.some(n => dataOutputs(n).value.type !== type)) error(node.id, 'Variable has no visible producer or conflicting types', port)
    } else if (raw.kind === 'literal') {
      type = text(raw.type, 'unknown')
      const v = raw.value
      const valid = validLiteral(type, v, catalog)
      if (!valid) error(node.id, `Invalid ${type} literal`, port)
      if (typeof v === 'number' && ((spec.min !== undefined && v < spec.min) || (spec.max !== undefined && v > spec.max) || (spec.integer && !Number.isInteger(v)))) error(node.id, `Input must be ${spec.integer ? 'an integer ' : ''}between ${spec.min ?? '-infinity'} and ${spec.max ?? 'infinity'}`, port)
    } else error(node.id, 'Operand kind must be literal, variable or unset', port)
    if (type && !spec.types.some(t => compatibleTypes(type!, t))) error(node.id, `Incompatible input type ${type}; expected ${spec.types.join('/')}`, port)
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
  for (const node of nodes.values()) {
    const spec = nodeSpec(node.kind); if (!spec) continue
    for (const [field, rule] of Object.entries(spec.config)) {
      const v = node.config[field]
      if (v === undefined) { if (rule.required) error(node.id, `${field} is required`, field); continue }
      const valid = rule.type === 'array' ? Array.isArray(v) : rule.type === 'object' ? record(v) : rule.type === 'integer' ? typeof v === 'number' && Number.isInteger(v) : typeof v === rule.type
      if (!valid) { error(node.id, `${field} must be ${rule.type}`, field); continue }
      const choices = rule.enumSource === 'types' ? types : rule.enum
      if (choices && !choices.includes(v as string)) error(node.id, `${field} must be one of ${choices.join(', ')}`, field)
      if (typeof v === 'number' && ((rule.min !== undefined && v < rule.min) || (rule.max !== undefined && v > rule.max))) error(node.id, `${field} must be between ${rule.min} and ${rule.max}`, field)
      if (typeof v === 'string' && rule.pattern && !new RegExp(rule.pattern).test(v)) error(node.id, `${field} has invalid format`, field)
      if (Array.isArray(v) && ((rule.minItems !== undefined && v.length < rule.minItems) || (rule.maxItems !== undefined && v.length > rule.maxItems))) error(node.id, `${field} item count is out of range`, field)
    }
    for (const port of spec.requiredExecution ?? []) if (!outgoing.get(node.id)!.some(e => flowPort(node, e) === port)) error(node.id, 'Execution connection is required', port, port)
    const resolved: Record<string, string | undefined> = {}
    for (const [port, rule] of Object.entries(dataInputs(node, ownerOf(node)))) resolved[port] = operand(node, port, inputValue(node, port, catalog), rule)
    if (spec.comparison) {
      const rule = spec.comparison
      const left = rule.leftInput ? resolved[rule.leftInput] : text(node.config[rule.typeConfig!])
      const right = resolved[rule.rightInput]
      if (left && right && !(compatibleTypes(left, right) || compatibleTypes(right, left))) error(node.id, 'Comparison operands must have matching types', rule.rightInput)
      if (rule.orderingOperators.includes(text(node.config[rule.operatorConfig])) && left !== rule.orderingType) error(node.id, `Ordering comparison requires ${rule.orderingType} type`, rule.leftInput ?? rule.typeConfig)
    }
    if (node.config.output !== undefined && (typeof node.config.output !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(node.config.output))) error(node.id, 'Invalid variable output name', 'output')
    if (node.kind === 'trigger') {
      const type = node.config.type ?? 'entity'
      const required = type === 'ha' ? ['trigger'] : type === 'entity' ? ['entity'] : type === 'daily' ? ['time','timeZone'] : type === 'once' ? ['at'] : []
      for (const field of required) if (node.config[field] === undefined || node.config[field] === '') error(node.id, `${field} is required`, field)
      if (type === 'ha' && record(node.config.trigger) && ![node.config.trigger.trigger, node.config.trigger.platform].some(p => typeof p === 'string' && p.trim())) error(node.id, 'Native trigger requires a platform', 'trigger')
    }
    if (['entity_field','wait_until'].includes(node.kind) && node.config.field === 'attribute' && (typeof node.config.attribute !== 'string' || !node.config.attribute.trim())) error(node.id, 'Attribute name is required', 'attribute')
    if (node.kind === 'get_field' && (typeof node.config.pointer !== 'string' || !/^(?:\/(?:[^~]|~[01])*)*$/.test(node.config.pointer))) error(node.id, 'Invalid JSON pointer', 'pointer')
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
          if (!record(decl) || typeof decl.name !== 'string' || !new RegExp(catalog.structures.loopDeclaration.name.pattern).test(decl.name) || names.has(decl.name) || typeof decl.type !== 'string' || !types.includes(decl.type)) { error(node.id, 'Loop declarations require unique names and valid types', group); continue }
          names.add(decl.name)
          operand(node, `${group}.${decl.name}.initial`, decl.initial, { types: [decl.type], required: true, description: 'Initial value' })
        }
      }
      if (['while','until'].includes(node.kind)) {
        const condition = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'condition')
        if (!condition || !terminalsValid(condition.target, 'condition_result', node.id)) error(node.id, 'Every condition path must end in Boolean condition_result', 'condition', 'condition')
      }
      const body = outgoing.get(node.id)!.find(e => flowPort(node, e) === 'body')
      if ((declarations(node, 'carry').length || declarations(node, 'exports').length) && (!body || !terminalsValid(body.target, 'loop_result', node.id))) error(node.id, 'Every loop body path must end in loop_result when carry/exports are declared', 'body', 'body')
    }
    if (node.kind === 'condition_result') { const s = scopes.get(node.id)?.at(-1); if (!s || s.port !== 'condition' || !['while','until'].includes(nodes.get(s.owner)!.kind)) error(node.id, 'condition_result must be inside a loop condition scope') }
    if (node.kind === 'loop_result') {
      const owner = ownerOf(node)
      if (!owner || nodeSpec(owner.kind)?.dynamicOutputs !== 'loop') error(node.id, 'loop_result must be inside a sequential loop body scope')
      if (record(node.config.values)) for (const name of Object.keys(node.config.values)) if (!dataInputs(node, owner)[name]) error(node.id, 'Unknown loop carry/export input', name)
    }
  }
  return errors
}

export function fillGraphLayout<T extends { nodes: V4Node[] }>(graph: T): T {
  return { ...graph, nodes: graph.nodes.map((node, i) => ({ ...node, position: node.position ?? { x: 60 + (i % 4) * 300, y: 100 + Math.floor(i / 4) * 180 }, size: node.size ?? { width: 220, height: 112 } })) }
}
export function addV4Node(graph: GraphV4, kind: string, id: string, catalog: VisualCatalogSnapshot = nodeCatalog): GraphV4 {
  if (graph.version !== 4) throw new Error('Only graph version 4 is supported')
  const spec = nodeSpec(kind, catalog); if (!spec) throw new Error('Unknown node kind')
  if (graph.nodes.some(n => n.id === id)) throw new Error('Duplicate node id')
  const config: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(spec.config)) if (value.default !== undefined) config[key] = structuredClone(value.default)
  const storage = spec.argumentStorage === 'arguments' ? (config.arguments ??= {}) as Record<string, unknown> : config
  for (const [key, value] of Object.entries(spec.inputs)) if (value.default !== undefined) storage[key] = structuredClone(value.default)
  return fillGraphLayout({ ...graph, nodes: [...graph.nodes, { id, kind, label: spec.label, config }] })
}
/** Returns a new immutable graph; the caller records that graph as one undo entry. */
export function connectData(graph: GraphV4, source: string, sourcePort: string, target: string, targetPort: string, catalog: VisualCatalogSnapshot = nodeCatalog): GraphV4 {
  if (graph.version !== 4) throw new Error('Only graph version 4 is supported')
  const next = structuredClone(graph), node = next.nodes.find(n => n.id === target)
  if (!node) throw new Error('Target node missing')
  next.edges = next.edges.filter(e => !(e.kind === 'data' && e.target === target && e.targetPort === targetPort))
  const base = JSON.stringify([source, sourcePort, target, targetPort]); let id = base
  for (let suffix = 2; next.edges.some(e => e.id === id); suffix++) id = `${base}#${suffix}`
  next.edges.push({ id, kind: 'data', source, target, sourcePort, targetPort })
  setInput(node, targetPort, { kind: 'unset' }, catalog)
  const old = validateGraphV4(graph, catalog).map(e => JSON.stringify(e))
  const addedErrors = validateGraphV4(next, catalog).filter(e => !old.includes(JSON.stringify(e)) ||
    (e.nodeId === target && (e.inputId === targetPort || e.portId === targetPort)) ||
    (e.nodeId === source && e.portId === sourcePort) || /cycle|scope/.test(e.message))
  if (addedErrors.length) throw new Error(addedErrors.map(e => e.message).join('; '))
  return next
}
export function disconnectData(graph: GraphV4, target: string, targetPort: string, catalog: VisualCatalogSnapshot = nodeCatalog): GraphV4 {
  const next = structuredClone(graph), node = next.nodes.find(n => n.id === target)
  next.edges = next.edges.filter(e => !(e.kind === 'data' && e.target === target && e.targetPort === targetPort))
  if (node) setInput(node, targetPort, { kind: 'unset' }, catalog)
  return next
}
