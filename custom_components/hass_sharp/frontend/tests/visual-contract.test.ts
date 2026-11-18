import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import entityGraphOrder from '../../../../tests/fixtures/entity-graph-order.json'
import * as contract from '../src/visual/contract'
import type { Graph } from '../src/visual/contract'
import { connectionIndex } from '../src/visual/connectionIndex'
import { constantTypes } from '../src/visual/constant'
import { readFileSync } from 'node:fs'
import { distinctTypeLabels, labelDepth, typeColor, typeLabel } from '../src/visual/typeLabels'

interface ValidationCase {
  name: string; graph: unknown; valid: boolean; nodeId?: string; inputId?: string; contains?: string
  warning?: string; warningInputId?: string; warningNodeId?: string | null; noDiagnostics?: boolean; services?: boolean; maxValidationMs?: number; catalogPatch?: Record<string, unknown>
  /** Every diagnostic message, sorted by code unit: both validators word them byte for byte alike. */
  messages?: string[]
  /** The code of every diagnostic that has one, sorted: both validators classify structural problems alike. */
  codes?: string[]
}
/** Messages are one short sentence for people: no full stop, no validator vocabulary, no CLR type names. */
/** Also no port ids (`field:`, `data:`), GUIDs or "type-checked". */
const jargon = /\b(?:literal|unset|operand|payload)\b|global::|System\.|\.$|\b(?:field|data):|type-checked|\b[0-9a-f]{32}\b|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/i
const cases = fixtureCases as ValidationCase[]
// The golden services section (tests/fixtures/visual-services.json), attached to the snapshot of cases that opt in.
const services = servicesFixture as unknown as contract.ServicesSection

// A case may extend the built-in catalog, e.g. with input constraints no built-in node declares yet.
function patched(patch: Record<string, unknown>): contract.VisualCatalogSnapshot {
  const merge = (target: Record<string, unknown>, source: Record<string, unknown>) => {
    for (const [key, value] of Object.entries(source)) {
      const existing = target[key]
      if (value && typeof value === 'object' && !Array.isArray(value) && existing && typeof existing === 'object' && !Array.isArray(existing))
        merge(existing as Record<string, unknown>, value as Record<string, unknown>)
      else target[key] = structuredClone(value)
    }
  }
  const catalog = structuredClone(contract.nodeCatalog)
  merge(catalog as unknown as Record<string, unknown>, patch)
  return catalog
}

describe('shared visual graph validation contract', () => {
  for (const fixture of cases) it(fixture.name, () => {
    // Without "services" the snapshot has no services section, as at Init.
    let catalog = fixture.catalogPatch ? patched(fixture.catalogPatch) : contract.nodeCatalog
    if (fixture.services === true) catalog = { ...catalog, services }
    const started = performance.now()
    const diagnostics = contract.validateGraph(fixture.graph as Graph, catalog)
    if (fixture.maxValidationMs) expect(performance.now() - started).toBeLessThan(fixture.maxValidationMs)
    const errors = diagnostics.filter(d => d.severity === 3)
    expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
    if (!fixture.valid) expect(errors.some(e =>
      (!fixture.nodeId || e.nodeId === fixture.nodeId) && (!fixture.inputId || e.inputId === fixture.inputId) &&
      (!fixture.contains || e.message.toLowerCase().includes(fixture.contains.toLowerCase())))).toBe(true)
    const warning = fixture.warning
    if (warning !== undefined) expect(diagnostics.some(d => d.severity === 2 && d.message.toLowerCase().includes(warning.toLowerCase()) &&
      (fixture.warningInputId === undefined || d.inputId === fixture.warningInputId) &&
      // warningNodeId, when the case has the key, is the node the warning is on; null for one on no node.
      (!('warningNodeId' in fixture) || (d.nodeId ?? null) === fixture.warningNodeId)), `expected warning "${warning}", got ${JSON.stringify(diagnostics)}`).toBe(true)
    if (fixture.noDiagnostics) expect(diagnostics).toEqual([])
    // A case without `messages` expects none: a warning only one validator raises cannot pass by omission.
    expect(diagnostics.map(d => d.message).sort()).toEqual([...(fixture.messages ?? [])].sort())
    expect(diagnostics.flatMap(d => d.code ? [d.code] : []).sort()).toEqual([...(fixture.codes ?? [])].sort())
    // Every code the validator emits is declared, as the C# runner checks against VisualGraphValidator.Codes.
    for (const d of diagnostics) if (d.code !== undefined) expect(contract.diagnosticCodes as readonly string[], fixture.name).toContain(d.code)
    for (const d of diagnostics) expect(d.message, 'message wording').not.toMatch(jargon)
  })
})
// The shared fixture compares diagnostics sorted; the order of the later nodes a rule reports is pinned here and in the
// runtime's --entity-graph-order, from one graph.
it('rules allowing one node of a key report every later one in id order', () => {
  const codes = ['node.singleton', 'state.duplicate', 'command.duplicate']
  const got = contract.validateGraph(entityGraphOrder.graph as unknown as Graph).filter(d => codes.includes(d.code ?? '')).map(d => `${d.code} ${d.nodeId}`)
  expect(got).toEqual(entityGraphOrder.order)
})
it('missing layout is deterministic and supplied positions remain unchanged', () => {
  const graph = { version: 5 as const, name: 'Layout', nodes: [
    { id: 'trigger/é', kind: 'trigger', config: { type: 'manual' }, position: { x: 13, y: 42 }, size: { width: 300, height: 100 } },
    { id: 'code', kind: 'custom', config: { code: 'x++;' } },
  ], edges: [] }
  const laidOut = contract.fillGraphLayout(graph)
  expect(laidOut.nodes[0].position).toEqual({ x: 13, y: 42 })
  expect(laidOut.nodes[1].position).toEqual({ x: 360, y: 100 })
  expect(graph.nodes[1].position).toBeUndefined()
  expect(contract.fillGraphLayout(graph)).toEqual(laidOut)
  expect(contract.effectiveTriggerMethod(graph.nodes[0])).toBe('Run_747269676765722FC3A9')
})
it('connect replaces literals, disconnect remains unset, immutable snapshots enable undo', () => {
  const graph = structuredClone(cases[0].graph) as Graph
  graph.edges = graph.edges.filter(e => e.kind !== 'data')
  graph.nodes[2].config.value = { kind: 'literal', type: 'boolean', value: false }
  const connected = contract.connectData(graph, 'a', 'value', 'b', 'value')
  expect(connected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(graph.nodes[2].config.value).toEqual({ kind: 'literal', type: 'boolean', value: false })
  const disconnected = contract.disconnectData(connected, 'b', 'value')
  expect(disconnected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(contract.validateGraph(disconnected)).toEqual(expect.arrayContaining([expect.objectContaining({ nodeId: 'b', inputId: 'value' })]))
})
it('graph creation rejects other versions and consumes catalog defaults', () => {
  for (const version of [3, 4]) expect(() => contract.addGraphNode({ version, nodes: [], edges: [] } as unknown as Graph, 'parallel', 'p')).toThrow(/version 5/i)
  const graph = contract.addGraphNode({ version: 5, name: 'New', nodes: [], edges: [] }, 'parallel', 'p')
  expect(graph.nodes[0].config.branches).toEqual(['a', 'b'])
  expect(contract.executionPorts(graph.nodes[0])).toEqual(['branch:a', 'branch:b', 'next', 'error'])
})
it('replacing an invalid connection still rejects the same type error', () => {
  const graph = structuredClone(cases.find(c => c.name === 'incompatible data type')!.graph) as Graph
  expect(() => contract.connectData(graph, 'a', 'value', 'b', 'value')).toThrow('Condition accepts True/False, not Number')
})
it('resolves parametric types and decides refinement before root assignability', () => {
  expect(contract.resolveTypeSpec('entity_id:light')).toMatchObject({ refines: 'entity_id', pattern: '^light\\.[a-z0-9_]+$', label: 'Light entity', clrType: 'System.String', literalKind: 'string', category: 'String' })
  expect(contract.typeChain('entity_id:light')).toEqual(['entity_id:light', 'entity_id', 'string'])
  expect([contract.rootType('entity_id:binary_sensor'), contract.rootType('target')]).toEqual(['string', 'object'])
  expect(contract.refinesType('entity_id:light', 'entity_id')).toBe(true)
  expect(contract.refinesType('string', 'entity_id')).toBe(false)
  for (const id of ['entity_id:Light', 'foo:bar', 'entity_id:', 'string:x', 'global::HassSharp.EntityStateSnapshot:x']) {
    expect(contract.resolveTypeSpec(id)).toBeUndefined()
    expect(contract.typeChain(id)).toEqual([])
  }
  expect(contract.resolveTypeSpec('global::HassSharp.EntityStateSnapshot')).toBeDefined()
  const cases: [string, string, boolean][] = [
    ['entity_id:light', 'entity_id', true], ['entity_id:light', 'string', true], ['target', 'object', true], ['entity_id', 'entity_id', true],
    ['entity_id', 'entity_id:light', false], ['string', 'entity_id', false], ['entity_id:light', 'entity_id:switch', false], ['object', 'target', false], ['foo:bar', 'foo:bar', false],
  ]
  for (const [source, target, expected] of cases) expect(contract.isAssignable(source, target), `${source} -> ${target}`).toBe(expected)
  expect(contract.validLiteral('entity_id:light', 'light.kitchen')).toBe(true)
  expect(contract.validLiteral('entity_id:light', 'switch.x')).toBe(false)
  expect(contract.validLiteral('entity_id:light', 'bad id')).toBe(false)
})
it('read entity exposes a concrete snapshot without scalar configuration', () => {
  const node = { id: 'r', kind: 'read_entity', config: {} }
  expect(contract.dataOutputs(node).value.type).toBe('global::HassSharp.EntityStateSnapshot')
  expect(contract.nodeCatalog.nodes.read_entity.config.type).toBeUndefined()
})
it('trigger outputs follow the trigger type and refine entity ids from config', () => {
  const snapshot = 'global::HassSharp.EntityStateSnapshot'
  const describe = (config: Record<string, unknown>) => Object.entries(contract.dataOutputs({ id: 't', kind: 'trigger', config })).map(([port, output]) => `${port}=${output.type}`).join(',')
  const states = (entity: string) => `data=object,entity_id=${entity},to_state=${snapshot},from_state=${snapshot}`
  const ha = (trigger: Record<string, unknown>) => ({ type: 'ha', trigger })
  const cases: [Record<string, unknown>, string][] = [
    [{ type: 'manual' }, 'data=object'],
    [{ type: 'startup' }, 'data=object'],
    [{ type: 'daily', time: '07:30', timeZone: 'UTC' }, 'data=object,planned=string'],
    [{ type: 'once', at: '2030-01-01T10:00:00Z' }, 'data=object,planned=string'],
    [{ type: 'entity', entity: 'light.kitchen' }, states('entity_id:light')],
    [{ entity: 'light.kitchen' }, states('entity_id:light')],
    [{ type: 'entity' }, states('entity_id')],
    [{ type: 'entity', entity: 'Light.Kitchen' }, states('entity_id')],
    [ha({ trigger: 'state', entity_id: 'light.kitchen' }), states('entity_id:light')],
    [ha({ platform: 'state', entity_id: 'light.kitchen' }), states('entity_id:light')],
    [ha({ trigger: 'state', entity_id: ['light.a', 'light.b'] }), states('entity_id:light')],
    [ha({ trigger: 'state', entity_id: ['light.a', 'switch.b'] }), states('entity_id')],
    [ha({ trigger: 'state', entity_id: [] }), states('entity_id')],
    [ha({ trigger: 'numeric_state', entity_id: 'sensor.temperature', above: 20 }), states('entity_id:sensor')],
    [ha({ trigger: 'template', value_template: '{{ true }}' }), states('entity_id')],
    [ha({ trigger: 'zone', entity_id: 'person.ana', zone: 'zone.home', event: 'enter' }), 'data=object,entity_id=entity_id:person,zone=string,event=string'],
    [ha({ trigger: 'event', event_type: 'test' }), 'data=object,event_type=string,event_data=object'],
    [ha({ trigger: 'time', at: '07:00:00' }), 'data=object,now=string'],
    [ha({ trigger: 'time_pattern', minutes: '/5' }), 'data=object,now=string'],
    [ha({ trigger: 'sun', event: 'sunset' }), 'data=object,event=string'],
    [ha({ trigger: 'homeassistant', event: 'start' }), 'data=object,event=string'],
    [ha({ trigger: 'webhook', webhook_id: 'hook' }), 'data=object,json=object,query=object'],
    [ha({ trigger: 'mqtt', topic: 'a/b' }), 'data=object,topic=string,payload=string'],
    [ha({ trigger: 'device', device_id: 'x' }), 'data=object'],
  ]
  for (const [config, expected] of cases) expect(describe(config), JSON.stringify(config)).toBe(expected)
  const ports = contract.dataOutputs({ id: 't', kind: 'trigger', config: { type: 'entity', entity: 'light.kitchen' } })
  expect([ports.to_state.pointer, ports.data.pointer]).toEqual(['/to_state', ''])
  expect(contract.nodeCatalog.nodes.trigger_data).toBeUndefined()
})
it('accepts explicit per-document catalogs without contaminating built-ins', () => {
  const catalog = structuredClone(contract.nodeCatalog)
  catalog.types['global::Example.Reading'] = { description: 'Reading', assignableTo: ['global::Example.IReading'], nullable: false }
  catalog.types['global::Example.IReading'] = { description: 'IReading', assignableTo: [], nullable: false }
  expect(contract.isAssignable('global::Example.Reading', 'global::Example.IReading', catalog)).toBe(true)
  expect(contract.isAssignable('global::Example.Reading', 'global::Example.IReading')).toBe(false)
  expect(contract.isAssignable('invented', 'invented', catalog)).toBe(false)
})

import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
const richCatalog = rich.catalog as unknown as contract.VisualCatalogSnapshot
for (const fixture of rich.cases) it(`shared CLR catalog: ${fixture.name}`, () => {
  const errors = contract.validateGraph(fixture.graph, richCatalog)
  expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
  if (fixture.inputId) expect(errors.some(e => e.inputId === fixture.inputId)).toBe(true)
})
describe('the members of an entity id and the enums of the entity classes (server catalog)', () => {
  const names = (type: string) => contract.typeProperties(richCatalog, type).map(p => p.name)
  const BASE = ['EntityId', 'RawState', 'NumericState', 'Attributes']
  const STATE = 'global::HassSharp.Entities.BinarySensorState'
  it('lists Entity\'s members for any entity id, and a domain\'s class\'s for its own', () => {
    expect(names('entity_id')).toEqual(BASE)
    expect(names('entity_id:light').slice(0, 4)).toEqual(BASE)
    expect(names('entity_id:light')).toEqual(expect.arrayContaining(['State', 'Brightness']))
    expect(names('entity_id:made_up')).toEqual(names('entity_id'))
    const type = (domain: string, name: string) => contract.typeProperties(richCatalog, `entity_id:${domain}`).find(p => p.name === name)?.type
    expect(type('light', 'State')).toBe('global::HassSharp.Entities.LightState')
    expect(type('sensor', 'State')).toBe('global::System.String')
    expect(type('light', 'ColorMode')).toBe('global::HassSharp.Entities.LightColorMode?')
    // The bundled catalog knows no classes until the server's arrives: no members at all.
    expect(contract.typeProperties(contract.nodeCatalog, 'entity_id:light')).toEqual([])
  })
  it('takes Home Assistant\'s texts as an entity enum\'s values, never a state it no longer lists or a number', () => {
    expect(contract.validLiteral(STATE, 'on', richCatalog)).toBe(true)
    expect(contract.validLiteral(STATE, 'jammed', richCatalog)).toBe(false)
    expect(contract.validLiteral(STATE, 1, richCatalog)).toBe(false)
    expect(richCatalog.types[STATE]).toMatchObject({ literalKind: 'enum', enumValues: ['off', 'on', 'unknown', 'unavailable'], enumLabels: ['Off', 'On', 'Unknown', 'Unavailable'], listed: false })
    // Its entry lists what Roslyn says it converts to, as the C# validator decides.
    expect(contract.isAssignable(STATE, 'global::System.Object', richCatalog)).toBe(true)
    expect(contract.isAssignable('global::HassSharp.Entities.LightColorMode', 'global::HassSharp.Entities.LightColorMode?', richCatalog)).toBe(true)
    expect(contract.isAssignable(STATE, 'string', richCatalog)).toBe(false)
  })
  it('leaves the entity enums out of the Constant, Convert and loop type lists', () => {
    const entities = (ids: string[]) => ids.filter(id => id.startsWith('global::HassSharp.Entities.'))
    expect(entities(constantTypes(richCatalog))).toEqual([])
    expect(entities(contract.listedTypes(richCatalog))).toEqual([])
    expect(contract.listedTypes(richCatalog)).toEqual(expect.arrayContaining(['global::NodeLib.Mode', 'entity_id', 'string']))
    expect(contract.listedTypes(contract.nodeCatalog)).toEqual(Object.keys(contract.nodeCatalog.types).filter(id => id !== 'image'))
  })
})
describe('the image type', () => {
  it('is a picture: named Image, in a colour of its own, in no type list, never typed in and going into nothing else', () => {
    expect(contract.nodeCatalog.types.image).toMatchObject({ label: 'Image', clrType: 'HassSharp.SharpImage', listed: false })
    expect(contract.nodeCatalog.types.image.literalKind).toBeUndefined()
    expect(typeLabel('image')).toBe('Image')
    expect(typeColor('image')).toBe('var(--hs-port-image)')
    expect(contract.listedTypes(contract.nodeCatalog)).not.toContain('image')
    expect(constantTypes(contract.nodeCatalog)).not.toContain('image')
    expect(contract.validLiteral('image', 'picture', contract.nodeCatalog)).toBe(false)
    for (const other of Object.keys(contract.nodeCatalog.types).filter(id => id !== 'image')) {
      expect(contract.isAssignable('image', other, contract.nodeCatalog), other).toBe(false)
      expect(contract.isAssignable(other, 'image', contract.nodeCatalog), other).toBe(false)
    }
  })
  it('has its pin colour beside the other type colours, mixed toward the text colour in both colour schemes', () => {
    const tokens = readFileSync('src/styles/tokens.css', 'utf8')
    expect(tokens).toMatch(/--hs-port-target: [^;]+;\n\s*--hs-port-image: color-mix\(in oklab, #[0-9a-f]{6} var\(--hs-hue-mix\), var\(--hs-text, #202535\)\);/)
  })
  it('is taken and drawn on only in an entity definition, by steps that draw on a copy', () => {
    const { camera_snapshot: take, image_draw_rect: rect, image_draw_text: text } = contract.nodeCatalog.nodes
    for (const spec of [take, rect, text]) expect(spec.documents).toEqual(['entity'])
    expect(take).toMatchObject({ label: 'Take a picture', category: 'Entity', execution: ['next', 'error'] })
    expect(take.inputs.entity.types).toEqual(['entity_id:camera'])
    expect(take.outputs.image.type).toBe('image')
    for (const spec of [rect, text]) expect(spec).toMatchObject({ pure: true, inputs: { image: { types: ['image'], required: true } }, outputs: { image: { type: 'image' } } })
    expect(rect.config).toMatchObject({ color: { default: 'Red' }, thickness: { min: 1, max: 50, default: 2 } })
    expect(text.config).toMatchObject({ color: { default: 'White' }, size: { min: 8, max: 200, default: 16 }, background: { default: true } })
    for (const spec of [rect, text]) expect(spec.config.color.enum).toEqual(['Red', 'Green', 'Blue', 'Yellow', 'Orange', 'White', 'Black'])
  })
})
it('any number goes into any numeric pin, except a nullable one into a pin that cannot hold null', () => {
  const cases: [string, string, boolean][] = [
    ['number', 'global::System.Int32', true], ['global::System.Int32', 'number', true], ['global::System.Double', 'global::System.Int32', true],
    ['number', 'global::System.Single', true], ['global::System.Single', 'number', true], ['number', 'global::System.Double?', true],
    ['global::System.Int32', 'global::System.Single?', true], ['global::System.Single?', 'global::System.Double?', true],
    ['global::System.Double?', 'number', false], ['global::System.Single?', 'global::System.Int32', false], ['global::System.Double?', 'global::System.Single', false],
    ['global::System.Int32', 'string', false], ['string', 'global::System.Int32', false], ['boolean', 'number', false],
  ]
  for (const [source, target, expected] of cases) expect(contract.isAssignable(source, target, richCatalog), `${source} -> ${target}`).toBe(expected)
})
it('a type entry\'s assignableFrom admits a source the source\'s own entry does not list', () => {
  const catalog = structuredClone(contract.nodeCatalog)
  catalog.types['global::Example.Words'] = { description: 'Words', assignableTo: ['global::Example.Words'], assignableFrom: ['string'], nullable: false }
  expect(contract.isAssignable('string', 'global::Example.Words', catalog)).toBe(true)
  expect(contract.isAssignable('global::Example.Words', 'string', catalog)).toBe(false)
  expect(contract.isAssignable('boolean', 'global::Example.Words', catalog)).toBe(false)
})
it('the static catalog knows the CLR types the compiler writes for the built-in ones', () => {
  for (const [clr, root] of [['global::System.String', 'string'], ['global::System.Double', 'number'], ['global::System.Boolean', 'boolean']] as const) {
    expect(contract.isAssignable(root, clr), `${root} -> ${clr}`).toBe(true)
    expect(contract.isAssignable(clr, root), `${clr} -> ${root}`).toBe(true)
  }
  const graph = contract.addGraphNode({ version: 5, name: 'New', nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } }], edges: [] }, 'custom', 'x')
  expect(contract.validateGraph({ ...graph, edges: [{ id: 'e', source: 't', target: 'x', port: 'next' }] })).toEqual([])
})
it('custom node creation and connections preserve reserved argument names', () => {
  const original = rich.cases[0].graph as Graph
  const consumer = original.nodes[3]
  const connected = contract.connectData(original, 'describe', 'result', consumer.id, 'reading', richCatalog)
  expect(connected.nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' }, type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(connected.nodes[3].config.type).toBeUndefined()
  expect(contract.disconnectData(connected, consumer.id, 'reading', richCatalog).nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' } })
  const created = contract.addGraphNode({ version: 5, name: 'Create', nodes: [], edges: [] }, consumer.kind, 'c', richCatalog).nodes[0]
  expect(created.config.arguments).toMatchObject({ type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(contract.validateGraph(original).some(d => d.message === contract.words.missingLibraryNode && d.code === 'node.unknownKind')).toBe(true)
  expect(contract.validateGraph(original, richCatalog)).toEqual([])
  expect(contract.validateGraph(original).some(d => d.message === contract.words.missingLibraryNode && d.code === 'node.unknownKind')).toBe(true)
})

it('nullable non-finite compiler defaults remain omitted when creating custom nodes', () => {
  const kind = Object.entries(richCatalog.nodes).find(([, spec]) => spec.label === 'Nonfinite defaults')![0]
  const node = contract.addGraphNode({ version: 5, name: 'Defaults', nodes: [], edges: [] }, kind, 'd', richCatalog).nodes[0]
  expect(node.config.arguments).toEqual({})
  for (const input of Object.values(richCatalog.nodes[kind].inputs)) expect(Object.hasOwn(input, 'default')).toBe(false)
})

it('rejects stale literal arguments after an optional custom parameter rename', () => {
  const graph = structuredClone(rich.cases[0].graph) as Graph
  const consumer = graph.nodes[3]
  ;(consumer.config.arguments as Record<string, unknown>).removedParameter = { kind: 'literal', type: 'string', value: 'old value' }
  expect(contract.validateGraph(graph, richCatalog)).toEqual(expect.arrayContaining([
    expect.objectContaining({ nodeId: consumer.id, inputId: 'removedParameter', message: 'Unknown input removedParameter' }),
  ]))
})

describe('service node ports (C5)', () => {
  const withServices = { ...contract.nodeCatalog, services }
  const node = (config: Record<string, unknown>) => ({ id: 's', kind: 'service', config: { fields: {}, returnResponse: false, ...config } })
  it('has one field port per config.fields key, typed from a known service', () => {
    const light = node({ service: 'light.turn_on', fields: { brightness_pct: { kind: 'unset' }, glow: { kind: 'unset' } } })
    const inputs = contract.dataInputs(light, undefined, withServices)
    expect(Object.keys(inputs)).toEqual(['target', 'field:brightness_pct', 'field:glow'])
    expect(inputs['field:brightness_pct']).toMatchObject({ types: ['number'], required: false, min: 0, max: 100, integer: false, unit: '%', step: 1 })
    expect(inputs['field:brightness_pct']).not.toHaveProperty('section')
    expect(inputs['field:glow'].types).toEqual(Object.keys(contract.nodeCatalog.types))
    expect(inputs.target.types).toEqual(['target', 'entity_id:light'])
    expect(contract.dataInputs(node({ service: 'notify.notify', fields: { target: { kind: 'unset' } } }), undefined, withServices)).toMatchObject({ target: { types: ['target', 'entity_id'] }, 'field:target': { label: 'target', required: false } })
    expect(contract.dataInputs(node({ service: 'homeassistant.turn_on' }), undefined, withServices).target.types).toEqual(['target', 'entity_id'])
  })
  it('keeps ports and permissive types without a services section or for an unknown service', () => {
    for (const [catalog, service] of [[contract.nodeCatalog, 'light.turn_on'], [withServices, 'foo.bar']] as const) {
      const inputs = contract.dataInputs(node({ service, fields: { brightness_pct: { kind: 'unset' } } }), undefined, catalog)
      expect(Object.keys(inputs)).toEqual(['target', 'field:brightness_pct'])
      expect(inputs.target.types).toEqual(['target', 'entity_id'])
      expect(inputs['field:brightness_pct']).toMatchObject({ types: Object.keys(contract.nodeCatalog.types), required: false })
    }
  })
  it('exposes the response output only while returnResponse is true', () => {
    expect(Object.keys(contract.dataOutputs(node({ service: 'weather.get_forecasts' })))).toEqual([])
    expect(contract.dataOutputs(node({ service: 'weather.get_forecasts', returnResponse: true })).response).toMatchObject({ type: 'object', when: 'returnResponse' })
    expect(Object.keys(contract.dataOutputs(node({ service: 'weather.get_forecasts', returnResponse: 'yes' })))).toEqual([])
  })
  it('copies a service field default (C4) into its input spec as an operand, as the C# catalog does', () => {
    const inputs = contract.dataInputs(node({ service: 'hass_sharp_test.sectioned', fields: { level: { kind: 'unset' }, speed: { kind: 'unset' } } }), undefined, withServices)
    expect(inputs['field:level'].default).toEqual({ kind: 'literal', type: 'number', value: 3 })
    expect(inputs['field:speed']).not.toHaveProperty('default')
  })
})

it('reads an input only from an own property, never from the prototype', () => {
  const service = { id: 's', kind: 'service', config: { fields: {} } }
  for (const port of ['field:constructor', 'field:toString', 'field:__proto__']) expect(contract.inputValue(service, port, contract.nodeCatalog), port).toBeUndefined()
  const operand = { kind: 'literal', type: 'string', value: 'x' }
  expect(contract.inputValue({ ...service, config: { fields: { constructor: operand } } }, 'field:constructor', contract.nodeCatalog)).toBe(operand)
  expect(contract.inputValue({ id: 'i', kind: 'array_create', config: { items: [operand] } }, 'item:length', contract.nodeCatalog)).toBeUndefined()
  // An optional input named like an Object.prototype member, with nothing set, is not an invalid value.
  const catalog = patched({ nodes: { log: { inputs: { toString: { types: ['string'], required: false, description: 'Optional' } } } } })
  const graph = { version: 5, name: 'Own', nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } }, { id: 'l', kind: 'log', config: { value: operand } }], edges: [{ id: 'tl', source: 't', target: 'l' }] }
  expect(contract.validateGraph(graph, catalog)).toEqual([])
})
it('blocks a connection only on cycle and scope errors themselves, not on a name that contains those words', () => {
  const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
  const graph = { version: 5 as const, name: 'Names', nodes: [
    { id: 't', kind: 'trigger', config: { type: 'manual' } },
    { id: 'a', kind: 'constant', label: 'Cycle scope', config: { type: 'number', value: lit('number', 1) } },
    { id: 'l', kind: 'log', label: 'Scope cycle', config: { value: { kind: 'unset' } } },
    { id: 'd', kind: 'delay', config: { seconds: lit('number', 2) } },
  ], edges: [{ id: 'tl', source: 't', target: 'l' }, { id: 'ld', source: 'l', target: 'd' }] }
  expect(contract.validateGraph(graph).map(d => d.message)).toEqual(['Cycle scope is not used', 'Message is required'])
  expect(() => contract.connectData(graph, 'a', 'value', 'd', 'seconds')).not.toThrow()
})
describe('refusing a connection only for a problem it causes (fix round 2)', () => {
  const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
  const trigger = { id: 't', kind: 'trigger', config: { type: 'manual' } }
  const producer = { id: 'a', kind: 'constant', config: { type: 'number', value: lit('number', 1) } }
  const delay = { id: 'd', kind: 'delay', config: { seconds: lit('number', 2) } }
  // The Constant is a value: it has no place in the run order.
  const base = (extra: object[], edges: object[]) => ({ version: 5 as const, name: 'Blocking', nodes: [trigger, producer, delay, ...extra],
    edges: [{ id: 'td', source: 't', target: 'd' }, ...edges] })
  const cycle = () => base([{ id: 'x', kind: 'log', config: { value: lit('string', 'x') } }, { id: 'y', kind: 'log', config: { value: lit('string', 'y') } }],
    [{ id: 'dx', source: 'd', target: 'x' }, { id: 'xy', source: 'x', target: 'y' }, { id: 'yx', source: 'y', target: 'x' }])
  const conditionResult = () => base([{ id: 'r', kind: 'condition_result', config: { value: lit('boolean', true) } }], [{ id: 'dr', source: 'd', target: 'r' }])
  // A value a step makes in a For each body cannot reach the step after the loop.
  const loop = (extra: object[] = [], edges: object[] = []) => ({ version: 5 as const, name: 'Scope', nodes: [trigger,
    { id: 'f', kind: 'foreach', config: { items: lit('array', [1, 2]), itemType: 'number' } },
    { id: 'b', kind: 'read_entity', config: { entity: lit('entity_id', 'sensor.a') } }, delay, { id: 'm', kind: 'log', config: { value: { kind: 'unset' } } }, ...extra],
    edges: [{ id: 'tf', source: 't', target: 'f' }, { id: 'fb', source: 'f', target: 'b', port: 'body' }, { id: 'fd', source: 'f', target: 'd', port: 'next' }, { id: 'dm', source: 'd', target: 'm' }, ...edges] })
  const crossing = 'Message cannot use this value here: it is on the other side of a loop or parallel branch boundary'
  const quick = (graph: contract.Graph, ...wire: [string, string, string, string]) => connectionIndex(graph).verdict(...wire).message

  it('accepts an unrelated wire while the graph already has an execution cycle', () => {
    const graph = cycle()
    expect(contract.validateGraph(graph).filter(contract.blockingDiagnostic).map(d => d.code)).toContain('cycle.execution')
    expect(() => contract.connectData(graph, 'a', 'value', 'd', 'seconds')).not.toThrow()
    expect(quick(graph, 'a', 'value', 'd', 'seconds')).toBeUndefined()
  })
  it('accepts an unrelated wire while the graph already has a step outside its loop', () => {
    for (const graph of [conditionResult(), base([{ id: 'r', kind: 'loop_result', config: { values: {} } }], [{ id: 'dr', source: 'd', target: 'r' }])]) {
      expect(contract.validateGraph(graph).some(contract.blockingDiagnostic)).toBe(true)
      expect(() => contract.connectData(graph, 'a', 'value', 'd', 'seconds')).not.toThrow()
      expect(quick(graph, 'a', 'value', 'd', 'seconds')).toBeUndefined()
    }
  })
  it('still refuses a wire that itself makes a data cycle, with or without a cycle already there', () => {
    const own = loop([{ id: 'c', kind: 'convert', config: { type: 'number', value: { kind: 'unset' } } }])
    for (const graph of [own, { ...own, edges: [...own.edges, ...cycle().edges.slice(2).map(e => ({ ...e, id: 'z' + e.id }))], nodes: [...own.nodes, ...cycle().nodes.slice(3)] }]) {
      expect(() => contract.connectData(graph, 'c', 'value', 'c', 'value')).toThrow('Convert would depend on its own result')
      expect(quick(graph, 'c', 'value', 'c', 'value')).toContain('Convert would depend on its own result')
    }
  })
  it('still refuses a wire that itself crosses a scope, with or without a scope error already there', () => {
    const plain = loop()
    const broken = loop([{ id: 'r', kind: 'condition_result', config: { value: lit('boolean', true) } }], [{ id: 'dr', source: 'd', target: 'r' }])
    expect(contract.validateGraph(broken).map(d => d.code)).toContain('scope.conditionResult')
    for (const graph of [plain, broken]) {
      expect(() => contract.connectData(graph, 'b', 'value', 'm', 'value')).toThrow(crossing)
      expect(quick(graph, 'b', 'value', 'm', 'value')).toBe(crossing)
    }
  })
  it('does not refuse a wire from an output because the input of the same name has a problem (fix round 3)', () => {
    const graph = { version: 5 as const, name: 'Not', nodes: [trigger, producer,
      { id: 'n', kind: 'not', config: { value: { kind: 'unset' } } }, { id: 'i', kind: 'if', config: { value: { kind: 'unset' } } }],
    edges: [{ id: 'ti', source: 't', target: 'i' },
      { id: 'a.value:n.value', kind: 'data', source: 'a', target: 'n', sourcePort: 'value', targetPort: 'value' }] }
    // The existing problem names the Not step's input pin `value`, the same name as its output.
    expect(contract.validateGraph(graph)).toContainEqual(expect.objectContaining({ nodeId: 'n', inputId: 'value', portId: 'value', message: 'Value accepts True/False, not Number' }))
    expect(() => contract.connectData(graph, 'n', 'value', 'i', 'value')).not.toThrow()
    expect(quick(graph, 'n', 'value', 'i', 'value')).toBeUndefined()
  })
  it('judges a data cycle by the new wire when the graph already has one (fix round 3)', () => {
    const convert = (id: string) => ({ id, kind: 'convert', config: { type: 'number', value: { kind: 'unset' } } })
    const data = (s: string, t: string) => ({ id: `${s}:${t}`, kind: 'data', source: s, target: t, sourcePort: 'value', targetPort: 'value' })
    const graph = { version: 5 as const, name: 'Cycle', nodes: [trigger, convert('c1'), convert('c2'), convert('c3'), convert('c4')],
      edges: [data('c1', 'c2'), data('c2', 'c1')] }
    expect(contract.validateGraph(graph).map(d => d.code)).toContain('cycle.data')
    // (a) An unrelated wire is made by both checks.
    expect(() => contract.connectData(graph, 'c3', 'value', 'c4', 'value')).not.toThrow()
    expect(quick(graph, 'c3', 'value', 'c4', 'value')).toBeUndefined()
    // (b) A wire (not a self-wire) that closes a second cycle is refused by both.
    const linked = { ...graph, edges: [...graph.edges, data('c3', 'c4')] }
    expect(() => contract.connectData(linked, 'c4', 'value', 'c3', 'value')).toThrow('Convert would depend on its own result')
    expect(quick(linked, 'c4', 'value', 'c3', 'value')).toContain('Convert would depend on its own result')
  })
  it('does not count a node whose id merely reads like a code as blocking', () => {
    const graph = base([{ id: 'cycle_count', kind: 'log', config: { value: { kind: 'variable', name: 'scope.data' } } }], [{ id: 'dc', source: 'd', target: 'cycle_count' }])
    expect(contract.validateGraph(graph).some(contract.blockingDiagnostic)).toBe(false)
    expect(contract.validateGraph(graph)).not.toEqual([])
    expect(() => contract.connectData(graph, 'a', 'value', 'd', 'seconds')).not.toThrow()
  })
  it('gives every structural diagnostic a declared code', () => {
    for (const d of contract.validateGraph({ version: 5, name: 'Codes', nodes: [trigger, { id: 'q', kind: 'nope', config: {} }], edges: [] }))
      if (d.code) expect(contract.diagnosticCodes).toContain(d.code)
    expect(contract.validateGraph({ version: 5, name: 'Codes', nodes: [trigger, { id: 'q', kind: 'nope', config: {} }], edges: [] }).map(d => d.code)).toContain('node.unknownKind')
  })
})

describe('Custom C# node signatures', () => {
  const reading = (label: string) => ({ label, description: label, category: 'Object', nullable: false, literalKind: null, assignableTo: ['global::NodeLib.Custom_a.Reading'] })
  const signature = (types: Record<string, unknown> = {}) => ({ title: 'Check', description: 'Checks.', pure: false, inputs: { level: { types: ['number'], required: true } }, outputs: { result: { type: 'number' } }, types })
  const node = (id: string, config: Record<string, unknown>) => ({ id, kind: 'custom', config })
  it('reads the pins of a well-formed signature only, and only on a Custom C# node', () => {
    expect(contract.customSignature(node('a', { signature: signature() }))?.title).toBe('Check')
    expect(Object.keys(contract.dataInputs(node('a', { signature: signature(), arguments: {} }), contract.nodeCatalog))).toEqual(['level'])
    expect(Object.keys(contract.dataOutputs(node('a', { signature: signature() })))).toEqual(['result'])
    for (const broken of [{ ...signature(), pure: 'no' }, { ...signature(), inputs: [] }, { ...signature(), outputs: { 'not a pin': { type: 'number' } } }, { ...signature(), types: { x: 1 } }, null])
      expect(contract.customSignature(node('a', { signature: broken })), JSON.stringify(broken)).toBeUndefined()
    expect(contract.customSignature({ id: 'l', kind: 'log', config: { signature: signature() } })).toBeUndefined()
  })
  it('adds the types of the graph\'s signatures to the catalog: a catalog type and the first node\'s entry win', () => {
    const graph = { version: 5, name: 'Types', nodes: [
      node('a', { signature: signature({ 'global::NodeLib.Custom_a.Reading': reading('First'), number: reading('Not the catalog number') }) }),
      node('b', { signature: signature({ 'global::NodeLib.Custom_a.Reading': reading('Second') }) }),
    ], edges: [] }
    const merged = contract.withGraphTypes(contract.nodeCatalog, graph)
    expect(merged.types['global::NodeLib.Custom_a.Reading'].label).toBe('First')
    expect(merged.types.number).toBe(contract.nodeCatalog.types.number)
    expect(contract.withGraphTypes(contract.nodeCatalog, { ...graph, nodes: [] })).toBe(contract.nodeCatalog)
    expect(contract.nodeCatalog.types['global::NodeLib.Custom_a.Reading']).toBeUndefined()
  })
})

it('chooses the article by the first sound, as the C# validator does (same list in VisualGraphContractTests)', () => {
  for (const [word, article] of [['input boolean', 'an'], ['update', 'an'], ['utility meter', 'a'], ['unit', 'a'], ['european', 'a'], ['one', 'a'], ['light', 'a'], ['alarm control panel', 'an'], ['humidifier', 'a'], ['image', 'an'], ['ozone', 'an']]) expect(contract.words.article(word), word).toBe(article)
})

describe('type and service names that cannot run away or repeat (final fix round)', () => {
  const withTypes = (types: Record<string, Record<string, unknown>>) => patched({ types: Object.fromEntries(Object.entries(types).map(([id, entry]) => [id, { description: '', ...entry }])) })
  it('words a self-referencing element type, a pair naming each other and a long chain only so deep (Words.TypeLabel)', () => {
    const chain: Record<string, Record<string, unknown>> = {}
    for (let i = 0; i < 2000; i++) chain[`T${i}`] = { elementType: `T${i + 1}` }
    const catalog = withTypes({ X: { elementType: 'X' }, A: { elementType: 'B' }, B: { elementType: 'A' }, ...chain })
    expect(typeLabel('X', catalog)).toBe('List of Object')
    expect(typeLabel('A', catalog)).toBe('List of List of Object')
    expect(typeLabel('T0', catalog)).toBe('List of '.repeat(labelDepth) + 'Object')
    expect(typeLabel('int' + '?'.repeat(100000), catalog)).toBe('Object')
    expect(typeLabel('(int A, double B)', catalog)).toBe('Object')
    expect(typeLabel('global::System.Int32[,]?', catalog)).toBe('List')
  })
  it('names types that share a label by the shortest C# name that tells them apart (Words.DistinctLabels)', () => {
    const catalog = withTypes({ 'global::NodeLib.A.Mode': { label: 'Mode', category: 'Object' }, 'global::NodeLib.B.Mode': { label: 'Mode', category: 'Object' } })
    const labels = distinctTypeLabels(['global::NodeLib.A.Mode', 'global::NodeLib.B.Mode', 'number'], catalog)
    expect([...labels.values()]).toEqual(['Mode (A.Mode)', 'Mode (B.Mode)', 'Number'])
  })
  it('does not write a service name twice when it is only the id, and keeps a name with brackets (Words.ServiceName)', () => {
    expect(contract.words.serviceName({ name: 'light.turn_on' } as contract.ServiceSchema, 'light.turn_on')).toBe('Turn on')
    expect(contract.words.serviceName({ name: 'turn_on' } as contract.ServiceSchema, 'light.turn_on')).toBe('Turn on')
    expect(contract.words.serviceName({ name: 'Turn on (beta)' } as contract.ServiceSchema, 'light.turn_on')).toBe('Turn on (beta)')
  })
})

it('a new step tracks run history by its document: a trigger of an entity definition does not', () => {
  const automation: Graph = { version: 5, name: 'Hall', nodes: [], edges: [] }
  const entity: Graph = { ...automation, name: 'Relay', definition: { entities: [{ key: 'relay', domain: 'switch' }], settings: [] } }
  const catalog = contract.nodeCatalog
  expect(contract.trackHistoryDefault('trigger', 'automation', catalog)).toBe(true)
  expect(contract.trackHistoryDefault('trigger', 'entity', catalog)).toBe(false)
  for (const document of ['automation', 'entity'] as const) {
    expect(contract.trackHistoryDefault('entity_state', document, catalog)).toBe(false)
    expect(contract.trackHistoryDefault('entity_command', document, catalog)).toBe(true)
    expect(contract.trackHistoryDefault('entity_setup', document, catalog)).toBe(true)
    // A kind without the setting, or one the catalog lacks, records its runs.
    expect(contract.trackHistoryDefault('log', document, catalog)).toBe(true)
    expect(contract.trackHistoryDefault('missing', document, catalog)).toBe(true)
  }
  const added = (graph: Graph, kind: string) => contract.addGraphNode(graph, kind, 'n', catalog).nodes.at(-1)!.config
  expect(added(automation, 'trigger').trackHistory).toBe(true)
  expect(added(entity, 'trigger').trackHistory).toBe(false)
  expect(added(entity, 'entity_state').trackHistory).toBe(false)
  expect(added(entity, 'entity_command').trackHistory).toBe(true)
  expect(added(entity, 'entity_setup').trackHistory).toBe(true)
  expect(Object.hasOwn(added(entity, 'log'), 'trackHistory')).toBe(false)
  // An automation's trigger keeps the catalog's config, key for key and in order.
  expect(added(automation, 'trigger')).toEqual(Object.fromEntries(Object.entries(catalog.nodes.trigger.config).filter(([, rule]) => rule.default !== undefined).map(([key, rule]) => [key, rule.default])))
  expect(Object.keys(added(entity, 'trigger'))).toEqual(Object.keys(added(automation, 'trigger')))
})

describe('the labels of entity types and commands', () => {
  /** The catalog with a type the real one does not have: a labelled fan with a labelled and an unlabelled command, and a row with no label. */
  const catalog = {
    ...contract.nodeCatalog,
    entityDomains: {
      ...contract.nodeCatalog.entityDomains,
      fan: { label: 'Ceiling fan', state: {}, commands: { turn_on: { label: 'Spin up' }, set_speed: {} }, options: {} },
      air_purifier: { state: {}, commands: {}, options: {} },
    },
  } as unknown as contract.VisualCatalogSnapshot
  it('name a type by its row\'s label, else in words', () => {
    expect(contract.entityTypeLabel(contract.nodeCatalog, 'binary_sensor')).toBe('Binary sensor')
    expect(contract.entityTypeLabel(contract.nodeCatalog, 'switch')).toBe('Switch')
    expect(contract.entityTypeLabel(catalog, 'fan')).toBe('Ceiling fan')
    expect(contract.entityTypeLabel(catalog, 'air_purifier')).toBe('Air purifier')
    expect(contract.entityTypeLabel(contract.nodeCatalog, 'fan_speed')).toBe('Fan speed')
    expect(contract.entityTypeLabel(contract.nodeCatalog, 'constructor')).toBe('Constructor')
    expect(contract.entityTypeLabel({} as contract.VisualCatalogSnapshot, 'switch')).toBe('Switch')
  })
  it('name a command by its label, else in words', () => {
    expect(contract.entityCommandLabel(contract.nodeCatalog, 'switch', 'turn_on')).toBe('Turn on')
    expect(contract.entityCommandLabel(catalog, 'fan', 'turn_on')).toBe('Spin up')
    expect(contract.entityCommandLabel(catalog, 'fan', 'set_speed')).toBe('Set speed')
    expect(contract.entityCommandLabel(contract.nodeCatalog, 'switch', 'fan_speed')).toBe('Fan speed')
    expect(contract.entityCommandLabel(contract.nodeCatalog, 'fan', 'fan_speed')).toBe('Fan speed')
    expect(contract.entityCommandLabel(contract.nodeCatalog, 'switch', 'toString')).toBe('ToString')
  })
  it('word them as VisualGraphValidator does: a text label as it is, even empty, and a blank command as Unnamed', () => {
    const blank = { entityDomains: { fan: { label: '', state: {}, commands: { turn_on: { label: '' }, set_speed: { label: 7 } }, options: {} } } } as unknown as contract.VisualCatalogSnapshot
    expect(contract.entityTypeLabel(blank, 'fan')).toBe('')
    expect(contract.entityCommandLabel(blank, 'fan', 'turn_on')).toBe('')
    expect(contract.entityCommandLabel(blank, 'fan', 'set_speed')).toBe('Set speed')
    expect(contract.entityCommandLabel(blank, 'fan', '_')).toBe('Unnamed')
  })
})
