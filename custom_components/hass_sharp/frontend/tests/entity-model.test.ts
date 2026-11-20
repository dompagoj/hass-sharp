import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import type { Graph } from '../src/visual/contract'
import { serializeGraph } from '../src/visual/graph'
import {
  cardEntities, definitionBody, definitionEntityKinds, definitionId, draftGraph, editorGraph, newGraphDefinition, rangeHint, renameEntityKey,
  type DefinitionBody, type DefinitionSetting,
} from '../src/entities/model'
import type { ClassSetting } from '../src/visual/contract'

interface ValidationCase { name: string; graph: any; valid: boolean; messages?: string[]; codes?: string[]; catalogPatch?: Record<string, unknown> }
const cases = fixtureCases as ValidationCase[]
const fixtureGraph = (name: string) => structuredClone(cases.find(c => c.name === name)!.graph)
const withoutDefinition = ({ definition: _definition, ...graph }: any) => graph as Graph

/** The derived motion sensor: the shared fixture's graph, whose Entity node `s` declares it, so the body lists no entity. */
const motion = (): DefinitionBody => {
  const graph = fixtureGraph('entity graph: derived binary sensor')
  return { name: graph.name, settings: [], logic: { kind: 'graph', graph: withoutDefinition(graph) } }
}
/** The plug's three Entity nodes (its power reading set by a typed value) and Turn on of the relay → Set state. */
const plugGraph = (): Graph => ({
  version: 5, name: 'Plug', nodes: [
    { id: 'p', kind: 'entity', position: { x: 60, y: 100 },
      config: { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', options: { unit: 'W', stateClass: 'measurement' }, arguments: { value: { kind: 'literal', type: 'number', value: 12 } } } },
    { id: 'online', kind: 'entity', position: { x: 60, y: 200 }, config: { key: 'online', domain: 'binary_sensor', deviceClass: 'connectivity', entityCategory: 'diagnostic' } },
    { id: 'relay', kind: 'entity', position: { x: 60, y: 300 }, config: { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket', enabledByDefault: false } },
    { id: 'set_on', kind: 'set_entity_state', config: { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: true } }, position: { x: 360, y: 300 } },
  ],
  edges: [{ id: 'relay:command:turn_on->set_on', source: 'relay', target: 'set_on', port: 'command:turn_on' }],
})
/** The plug, as the panel sends it: settings of every kind (a secret typed in) and three Entity nodes. */
const plug = (): DefinitionBody => ({
  name: 'Plug',
  settings: [
    { key: 'offset', value: 4, label: 'Offset', type: 'number' },
    { key: 'name', value: '', type: 'string' },
    { key: 'token', value: 'hunter2', label: 'Token', type: 'string', secret: true },
  ],
  logic: { kind: 'graph', graph: plugGraph() },
})
/** The plug as an answer gives it back: its secret masked, the value stored. */
const maskedPlug = (): DefinitionBody => {
  const body = plug()
  body.settings![2] = { key: 'token', value: null, label: 'Token', type: 'string', secret: true, hasValue: true }
  return body
}
const setting = (graph: Graph, key: string) => (graph.definition!.settings as DefinitionSetting[]).find(s => s.key === key)!

describe('editor graph', () => {
  it('carries the whole definition beside the logic graph and splits back to the same body', () => {
    for (const body of [motion(), plug()]) {
      const graph = editorGraph(body)
      expect(graph.name).toBe(body.name)
      // Its entities are its Entity nodes and its devices its Device nodes: the definition carries its settings only.
      expect(graph.definition).toEqual({ settings: body.settings })
      expect(definitionBody(graph)).toEqual(body)
    }
  })
  it('the body sent to the server has no entities, and the editor ignores the answer\'s', () => {
    const graph = editorGraph(newGraphDefinition('Front door'))
    expect(definitionBody(graph)).not.toHaveProperty('entities')
    expect(graph.definition).toEqual({ settings: [] })
    const answered = { ...definitionBody(graph), entities: [{ key: 'front_door', domain: 'binary_sensor' }] }
    expect(editorGraph(answered as never).definition).toEqual({ settings: [] })
    // Nor does any other definition send one, whatever its read answer listed.
    for (const body of [motion(), plug()]) {
      expect(definitionBody(editorGraph(body))).not.toHaveProperty('entities')
      expect(definitionBody(editorGraph({ ...body, entities: [{ key: 'door', domain: 'binary_sensor', name: 'Door' }] } as never))).toEqual(body)
    }
  })
  it('the editor ignores an answer\'s devices and each entity\'s device, and the body sent has none', () => {
    for (const body of [motion(), plug()]) {
      const answered = {
        ...body, version: 3, devices: [{ key: 'plug', name: 'Hall plug', model: 'P1' }],
        entities: [{ key: 'door', domain: 'binary_sensor', name: 'Door', device: 'plug' }],
      }
      const graph = editorGraph(answered as never)
      expect(graph.definition).toEqual({ settings: body.settings })
      expect(graph).not.toHaveProperty('devices')
      expect(graph.nodes).toEqual(body.logic.kind === 'graph' ? body.logic.graph.nodes : [])
      const sent = definitionBody(graph)
      expect(sent).not.toHaveProperty('devices')
      expect(sent).not.toHaveProperty('entities')
      expect(sent).toEqual(body)
    }
  })
  it('a new definition has no device, and the body sent has none: its devices are its Device nodes', () => {
    const fresh = newGraphDefinition('Front door')
    expect(fresh).not.toHaveProperty('device')
    expect(editorGraph(fresh).definition).toEqual({ settings: [] })
    expect(definitionBody(editorGraph(fresh))).not.toHaveProperty('device')
    // A read answer's device of null, or one an earlier build stored, is not carried along.
    for (const device of [null, { manufacturer: 'Acme', model: 'P1', via: { definition: 'hub', deviceId: null } }]) {
      const graph = editorGraph({ ...plug(), device } as never)
      expect(graph.definition).toEqual({ settings: plug().settings })
      expect(definitionBody(graph)).not.toHaveProperty('device')
      expect(definitionBody(graph)).toEqual(plug())
    }
  })
  it('never sends the definition inside the logic graph', () => {
    const body = definitionBody(editorGraph(plug()))
    expect(body.logic.kind).toBe('graph')
    expect(Object.keys((body.logic as { graph: Graph }).graph)).toEqual(['version', 'name', 'nodes', 'edges'])
  })
  it('takes the name from the graph, where the editor renames it', () => {
    const graph = { ...editorGraph(plug()), name: 'Kitchen plug' }
    const body = definitionBody(graph)
    expect(body.name).toBe('Kitchen plug')
    expect((body.logic as { graph: Graph }).graph.name).toBe('Kitchen plug')
  })
  it('builds the same editor graph again from its body, member for member', () => {
    for (const body of [motion(), plug()]) {
      const graph = editorGraph(body)
      expect(editorGraph(definitionBody(graph))).toEqual(graph)
      // The session compares serialised graphs: the order of members holds too.
      expect(serializeGraph(editorGraph(definitionBody(graph)))).toBe(serializeGraph(graph))
    }
  })
  it('does not change the body it is built from, nor the graph it splits', () => {
    const body = maskedPlug(), before = structuredClone(body)
    const graph = editorGraph(body)
    const copy = structuredClone(graph)
    definitionBody(graph)
    draftGraph(graph)
    expect(body).toEqual(before)
    expect(graph).toEqual(copy)
  })
  it('refuses a graph with no definition', () => {
    expect(() => definitionBody(plugGraph())).toThrow(/definition/)
  })

  // The validator reads the definition's settings; its devices are its Device nodes, and an entity needs no name only
  // when one is wired to it.
  describe('validates as the shared fixture expects, with every member of the definition in it', () => {
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
    // A definition whose settings are not a list cannot be sent as a body. Its entities are its Entity nodes: none sends a list.
    const entityCases = cases.filter(c => c.graph && typeof c.graph === 'object' && c.graph.definition && typeof c.graph.definition === 'object' && !Array.isArray(c.graph.definition) &&
      Array.isArray(c.graph.definition.settings))
    it('has the fixture entity cases', () => expect(entityCases.length).toBeGreaterThan(30))
    for (const fixture of entityCases) it(fixture.name, () => {
      const graph = structuredClone(fixture.graph)
      const body: DefinitionBody = {
        name: graph.name,
        settings: graph.definition.settings.map((s: any, i: number) => i % 2
          ? { ...s, value: null, label: `Setting ${s.key}`, secret: true, hasValue: true }
          : { ...s, value: s.type === 'number' ? 4 : s.type === 'boolean' ? true : 'text', label: `Setting ${s.key}` }),
        logic: { kind: 'graph', graph: withoutDefinition(graph) },
      }
      const diagnostics = contract.validateGraph(editorGraph(body), fixture.catalogPatch ? patched(fixture.catalogPatch) : contract.nodeCatalog)
      expect(diagnostics.filter(d => d.severity === 3).length === 0, JSON.stringify(diagnostics)).toBe(fixture.valid)
      expect(diagnostics.map(d => d.message).sort()).toEqual([...(fixture.messages ?? [])].sort())
      expect(diagnostics.flatMap(d => d.code ? [d.code] : []).sort()).toEqual([...(fixture.codes ?? [])].sort())
    })
  })
})

describe('secret settings', () => {
  it('sends a masked secret the user did not change as keep, with its label, type and secret flag', () => {
    const body = definitionBody(editorGraph(maskedPlug()))
    expect(body.settings![2]).toEqual({ key: 'token', label: 'Token', type: 'string', secret: true, keep: true })
    expect(body.settings!.slice(0, 2)).toEqual(plug().settings!.slice(0, 2))
  })
  it('sends a retyped secret with its value and without hasValue', () => {
    const graph = editorGraph(maskedPlug())
    setting(graph, 'token').value = 'new-secret'
    expect(definitionBody(graph).settings![2]).toEqual({ key: 'token', value: 'new-secret', label: 'Token', type: 'string', secret: true })
  })
  it('sends a secret retyped as empty text as that value', () => {
    const graph = editorGraph(maskedPlug())
    setting(graph, 'token').value = ''
    expect(definitionBody(graph).settings![2]).toEqual({ key: 'token', value: '', label: 'Token', type: 'string', secret: true })
  })
  it('clears a secret with a null value and hasValue false', () => {
    const graph = editorGraph(maskedPlug())
    Object.assign(setting(graph, 'token'), { value: null, hasValue: false })
    expect(definitionBody(graph).settings![2]).toEqual({ key: 'token', value: null, label: 'Token', type: 'string', secret: true })
  })
  it('keeps every setting of a class answer masked under the fallback the same way', () => {
    // A class whose library does not compile: every setting answered masked. The form edits the body; the same rule applies.
    const graph = editorGraph({ ...maskedPlug(), settings: [{ key: 'Host', value: null, secret: true, hasValue: true }, { key: 'Port', value: null, secret: true, hasValue: false }] })
    expect(definitionBody(graph).settings).toEqual([{ key: 'Host', secret: true, keep: true }, { key: 'Port', value: null, secret: true }])
  })
  it('drops every secret value from a draft and leaves hasValue as stored', () => {
    const graph = editorGraph(maskedPlug())
    setting(graph, 'token').value = 'typed-replacement'
    const draft = draftGraph(graph)
    expect(draft).not.toBe(graph)
    expect(JSON.stringify(draft)).not.toContain('typed-replacement')
    expect(setting(draft, 'token')).toEqual({ key: 'token', value: null, label: 'Token', type: 'string', secret: true, hasValue: true })
    // Restored, the stored secret is kept; the typed replacement is gone.
    expect(definitionBody(draft).settings![2]).toEqual({ key: 'token', label: 'Token', type: 'string', secret: true, keep: true })
    // Other settings as they were.
    expect(setting(draft, 'offset')).toEqual(setting(graph, 'offset'))
    expect(setting(graph, 'token').value).toBe('typed-replacement')
  })
  it('leaves a never-saved secret empty in a draft', () => {
    const draft = draftGraph(editorGraph(plug()))
    expect(JSON.stringify(draft)).not.toContain('hunter2')
    expect(setting(draft, 'token')).toEqual({ key: 'token', value: null, label: 'Token', type: 'string', secret: true })
    expect(definitionBody(draft).settings![2]).toEqual({ key: 'token', value: null, label: 'Token', type: 'string', secret: true })
  })
  it('drops a value typed into a C# node\'s secret setting pin from a draft, and keeps the rest of its values', () => {
    // A pasted graph whose plug holds its token as typed text, beside a value of a setting that is not secret.
    const graph = fixtureGraph('code setting.secretLiteral: a typed secret') as Graph
    const plug = graph.nodes.find(n => n.id === 'plug')!
    const address = { kind: 'literal', type: 'string', value: 'http://192.168.1.50' }
    ;(plug.config.arguments as Record<string, unknown>).Address = address
    const draft = draftGraph(graph)
    expect(JSON.stringify(draft)).not.toContain('s3cr3t-token')
    expect(draft.nodes.find(n => n.id === 'plug')!.config.arguments).toEqual({ Address: address })
    // The graph itself keeps it, and every other part of the node is as it was.
    expect((plug.config.arguments as Record<string, any>).Token.value).toBe('s3cr3t-token')
    const { arguments: _kept, ...rest } = draft.nodes.find(n => n.id === 'plug')!.config
    const { arguments: _own, ...own } = plug.config
    expect(rest).toEqual(own)
  })
  it('gives a draft of a graph with no definition as a copy', () => {
    const graph = plugGraph()
    expect(draftGraph(graph)).toEqual(graph)
    expect(draftGraph(graph)).not.toBe(graph)
  })
})

describe('a new graph definition', () => {
  it('has one Entity node, a binary sensor named after it, and no device, settings or other step', () => {
    expect(newGraphDefinition('Any motion')).toEqual({
      name: 'Any motion', settings: [],
      logic: { kind: 'graph', graph: { version: 5, name: 'Any motion', edges: [], nodes: [{
        id: 'entity-1', kind: 'entity', position: { x: 60, y: 100 },
        config: { key: 'any_motion', domain: 'binary_sensor', name: 'Any motion', enabledByDefault: true, options: {}, history: {}, arguments: {} },
      }] } },
    })
    // A name that is blank gives the node no name, never a blank or null one.
    const unnamed = newGraphDefinition('  ').logic as { graph: contract.Graph }
    expect(unnamed.graph.nodes[0].config).not.toHaveProperty('name')
    expect(unnamed.graph.nodes[0].config.key).toBe('entity')
  })
  it('keys its entity by the name within the entity key rule', () => {
    const key = (name: string) => (newGraphDefinition(name).logic as { graph: contract.Graph }).graph.nodes[0].config.key
    expect(key('Hall — Motion 2')).toBe('hall_motion_2')
    expect(key('2nd floor')).toBe('nd_floor')
    expect(key('Été')).toBe('ete')
    expect(key('Café Ü')).toBe('cafe_u')
    expect(key('!!!')).toBe('entity')
    expect(key('123')).toBe('entity')
    expect(key('a'.repeat(30) + ' ' + 'b'.repeat(30))).toBe('a'.repeat(30) + '_' + 'b'.repeat(9))
    expect(key('a'.repeat(39) + ' b')).toBe('a'.repeat(39))
    for (const name of ['Any motion', '2nd floor', '!!!', 'x'.repeat(200), 'A_b-C d']) expect(key(name)).toMatch(/^[a-z][a-z0-9_]{0,39}$/)
  })
  it('validates as an empty entity graph', () => {
    expect(contract.validateGraph(editorGraph(newGraphDefinition('Any motion')))).toEqual([])
  })
})

describe('a definition card\'s entities', () => {
  it('reads a unique id without the definition\'s prefix whole, as a key and not as a C# device\'s entity', () => {
    const live = (uniqueId: string, name: string | null) => ({ uniqueId, key: uniqueId, domain: 'switch', name, entityId: `switch.${uniqueId.replaceAll(':', '_')}` })
    const listed = cardEntities({ id: 'hall', entities: [], registeredEntities: [], liveEntities: [live('hall:plug:relay', 'Relay'), live('plug:relay', 'Stray')] })
    expect(listed).toEqual([
      { key: 'plug:relay', name: 'Stray', domain: 'switch', entityId: 'switch.plug_relay', disabled: false },
      { key: 'relay', name: 'Relay', domain: 'switch', entityId: 'switch.hall_plug_relay', disabled: false, device: 'plug' },
    ])
  })
})

describe('the range a C# node\'s setting declares', () => {
  it('says at least, at most or between for a whole or decimal setting, and nothing without one', () => {
    const range = (type: ClassSetting['type'], minimum?: number, maximum?: number) => rangeHint({ type, minimum, maximum })
    expect([
      // An int's own bounds stand for the side it does not declare.
      range('integer', 1, 2147483647), range('integer', -2147483648, 60), range('integer', 1, 3600), range('integer', -2147483648, 2147483647),
      range('number', 0.5), range('number', undefined, 99.5), range('number', -1.5, 1.5), range('number'), range('integer'),
    ]).toEqual(['At least 1.', 'At most 60.', 'Between 1 and 3600.', undefined, 'At least 0.5.', 'At most 99.5.', 'Between -1.5 and 1.5.', undefined, undefined])
    // A decimal setting has no bounds of its own: the int's are a range like any other.
    expect(range('number', -2147483648, 2147483647)).toBe('Between -2147483648 and 2147483647.')
    // Only a number has a range.
    for (const type of ['string', 'boolean', 'enum'] as const) expect(range(type, 1, 10), type).toBeUndefined()
  })
})

describe('definition ids', () => {
  it('slugs the name', () => {
    expect(definitionId('Any motion', [])).toBe('any_motion')
    expect(definitionId('  Kitchen -- Plug!! ', [])).toBe('kitchen_plug')
    expect(definitionId('__Hall__', [])).toBe('hall')
    expect(definitionId('Plug 2', [])).toBe('plug_2')
    expect(definitionId('', [])).toBe('entity')
    expect(definitionId('!!!', [])).toBe('entity')
  })
  it('drops accents rather than letters', () => {
    expect(definitionId('Café', [])).toBe('cafe')
    expect(definitionId('Čajna kuhinja — Žarulja', [])).toBe('cajna_kuhinja_zarulja')
    expect(definitionId('Ølstue', [])).toBe('lstue')
  })
  it('keeps to 80 characters', () => {
    expect(definitionId('a'.repeat(100), [])).toBe('a'.repeat(80))
    expect(definitionId('a'.repeat(79) + ' b', [])).toBe('a'.repeat(79))
  })
  it('never answers new, the address of a definition not saved yet (ruling R17)', () => {
    expect(definitionId('New', [])).toBe('new_2')
    expect(definitionId('  new!! ', ['new_2'])).toBe('new_3')
    expect(definitionId('Newer', [])).toBe('newer')
  })
  it('avoids taken ids', () => {
    expect(definitionId('Any motion', ['any_motion'])).toBe('any_motion_2')
    expect(definitionId('Any motion', ['any_motion', 'any_motion_2'])).toBe('any_motion_3')
    expect(definitionId('', ['entity'])).toBe('entity_2')
    expect(definitionId('a'.repeat(100), ['a'.repeat(80)])).toBe('a'.repeat(78) + '_2')
    const taken = ['a'.repeat(80), ...Array.from({ length: 8 }, (_, i) => 'a'.repeat(78) + `_${i + 2}`), 'a'.repeat(77) + '_10']
    expect(definitionId('a'.repeat(100), taken)).toBe('a'.repeat(77) + '_11')
    for (const id of [definitionId('Any motion', []), definitionId('a'.repeat(100), taken), definitionId('-x-', [])]) expect(id).toMatch(/^[a-z0-9][a-z0-9_-]{0,79}$/)
  })
})

describe('renaming an entity key', () => {
  const keys = (graph: Graph) => contract.declaredEntities(graph).map(d => d.entity.key)
  const named = (graph: Graph) => Object.fromEntries(graph.nodes.filter(n => n.kind !== 'entity').map(n => [n.id, n.config.entity]))
  it('renames the Entity node\'s entity and every step that names it', () => {
    const graph = editorGraph(plug())
    const renamed = renameEntityKey(graph, 'relay', 'outlet', 'relay')
    expect(renamed).not.toBe(graph)
    expect(keys(renamed)).toEqual(['power', 'online', 'outlet'])
    expect(renamed.nodes.find(n => n.id === 'relay')!.config).toEqual({ ...plugGraph().nodes[2].config, key: 'outlet' })
    expect(named(renamed)).toEqual({ set_on: 'outlet' })
    expect(named(graph)).toEqual({ set_on: 'relay' })
    expect(keys(graph)).toEqual(['power', 'online', 'relay'])
    // A node is renamed by its own id: another node's id renames nothing.
    expect(renameEntityKey(graph, 'relay', 'outlet', 'p')).toBe(graph)
  })
  it('leaves an entity operand alone', () => {
    const graph = editorGraph(motion())
    const renamed = renameEntityKey(graph, 'any_motion', 'motion', 's')
    expect(renamed.nodes.find(n => n.id === 's')!.config.key).toBe('motion')
    expect(renamed.nodes.find(n => n.id === 'a')!.config.entity).toEqual(graph.nodes.find(n => n.id === 'a')!.config.entity)
  })
  it('refuses a key another entity has', () => {
    const graph = editorGraph(plug())
    expect(renameEntityKey(graph, 'relay', 'power', 'relay')).toBe(graph)
  })
  it('refuses a key outside the entity key rule', () => {
    const graph = editorGraph(plug())
    for (const to of ['', 'Relay', '1relay', 'relay-2', 'r'.repeat(41)]) expect(renameEntityKey(graph, 'relay', to, 'relay')).toBe(graph)
    expect(renameEntityKey(graph, 'relay', 'r'.repeat(40), 'relay')).not.toBe(graph)
  })
  it('renames only the steps whose entity is a definition entity', () => {
    const graph = editorGraph(plug())
    graph.nodes.push(
      { id: 't', kind: 'trigger', config: { type: 'entity', entity: 'relay' } },
      { id: 'avail', kind: 'set_entity_available', config: { entity: 'relay', available: { kind: 'literal', type: 'boolean', value: true } } },
    )
    const renamed = renameEntityKey(graph, 'relay', 'outlet', 'relay')
    expect(named(renamed)).toEqual({ set_on: 'outlet', t: 'relay', avail: 'outlet' })
  })
  it('knows the catalog steps that name a definition entity by its key', () => {
    const kinds = Object.entries(contract.nodeCatalog.nodes)
      .filter(([, spec]) => Object.hasOwn(spec.config ?? {}, 'entity') && ((spec as { documents?: string[] }).documents ?? []).join() === 'entity')
      .map(([kind]) => kind)
    expect([...definitionEntityKinds].sort()).toEqual(kinds.sort())
  })
})
