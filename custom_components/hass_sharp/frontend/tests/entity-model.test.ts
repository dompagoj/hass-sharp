import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import type { Graph } from '../src/visual/contract'
import { serializeGraph } from '../src/visual/graph'
import {
  definitionBody, definitionId, draftGraph, editorGraph, newGraphDefinition, renameEntityKey,
  type DefinitionBody, type DefinitionSetting,
} from '../src/entities/model'

interface ValidationCase { name: string; graph: any; valid: boolean; messages?: string[]; codes?: string[]; catalogPatch?: Record<string, unknown> }
const cases = fixtureCases as ValidationCase[]
const fixtureGraph = (name: string) => structuredClone(cases.find(c => c.name === name)!.graph)
const withoutDefinition = ({ definition: _definition, ...graph }: any) => graph as Graph

/** The derived motion sensor: the shared fixture's graph, its definition as a body's entities. */
const motion = (): DefinitionBody => {
  const graph = fixtureGraph('entity graph: derived binary sensor')
  return { name: graph.name, device: null, settings: [], entities: graph.definition.entities, logic: { kind: 'graph', graph: withoutDefinition(graph) } }
}
const plugGraph = (): Graph => ({
  version: 5, name: 'Plug', nodes: [
    { id: 'p', kind: 'entity_state', config: { entity: 'power' }, position: { x: 60, y: 100 } },
    { id: 'on', kind: 'entity_command', config: { entity: 'relay', command: 'turn_on' }, position: { x: 60, y: 300 } },
    { id: 'set_on', kind: 'set_entity_state', config: { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: true } }, position: { x: 360, y: 300 } },
  ],
  edges: [{ id: 'on:next:set_on', source: 'on', target: 'set_on', port: 'next' }],
})
/** The plug device, as the panel sends it: a device, settings of every kind (a secret typed in), three entities. */
const plug = (): DefinitionBody => ({
  name: 'Plug',
  device: { manufacturer: 'Acme', model: 'P1', swVersion: null, hwVersion: null, configurationUrl: null, suggestedArea: 'Kitchen', via: null },
  settings: [
    { key: 'offset', value: 4, label: 'Offset', type: 'number' },
    { key: 'name', value: '', type: 'string' },
    { key: 'token', value: 'hunter2', label: 'Token', type: 'string', secret: true },
  ],
  entities: [
    { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', options: { unit: 'W', stateClass: 'measurement' } },
    { key: 'online', domain: 'binary_sensor', deviceClass: 'connectivity', entityCategory: 'diagnostic' },
    { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket', enabledByDefault: false },
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
      expect(graph.definition).toEqual({ entities: body.entities, settings: body.settings, device: body.device })
      expect(definitionBody(graph)).toEqual(body)
    }
  })
  it('keeps a stored via untouched', () => {
    const body = plug()
    body.device = { ...body.device!, via: { definition: 'hub', deviceId: null } }
    expect(definitionBody(editorGraph(body))).toEqual(body)
    const external = plug()
    external.device = { manufacturer: 'Acme', via: { definition: null, deviceId: 'f00d' } }
    expect(definitionBody(editorGraph(external)).device).toEqual({ manufacturer: 'Acme', via: { definition: null, deviceId: 'f00d' } })
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
  it('refuses a class definition and a graph with no definition', () => {
    expect(() => editorGraph({ name: 'Plug one', settings: [{ key: 'Host', value: '10.0.0.1' }], logic: { kind: 'class', type: 'library:shelly:NodeLib.FakePlug' } })).toThrow(/class/)
    expect(() => definitionBody(plugGraph())).toThrow(/definition/)
  })

  // Decision 1: the device sits in graph.definition.device; the validator reads only entities and settings.
  describe('validates as the shared fixture expects, with the device and every member of the definition in it', () => {
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
    const entityCases = cases.filter(c => c.graph && typeof c.graph === 'object' && c.graph.definition && typeof c.graph.definition === 'object' && !Array.isArray(c.graph.definition))
    it('has the fixture entity cases', () => expect(entityCases.length).toBeGreaterThan(30))
    for (const fixture of entityCases) it(fixture.name, () => {
      const graph = structuredClone(fixture.graph)
      const body: DefinitionBody = {
        name: graph.name,
        device: { manufacturer: 'Acme', model: 'P1', swVersion: '1.2', hwVersion: null, configurationUrl: 'http://10.0.0.5', suggestedArea: 'Kitchen', via: { definition: 'hub', deviceId: null } },
        settings: graph.definition.settings.map((s: any, i: number) => i % 2
          ? { ...s, value: null, label: `Setting ${s.key}`, secret: true, hasValue: true }
          : { ...s, value: s.type === 'number' ? 4 : s.type === 'boolean' ? true : 'text', label: `Setting ${s.key}` }),
        entities: graph.definition.entities.map((e: any) => ({ ...e, name: `Entity ${e.key}`, icon: 'mdi:flash', entityCategory: null, enabledByDefault: true })),
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
  it('gives a draft of a graph with no definition as a copy', () => {
    const graph = plugGraph()
    expect(draftGraph(graph)).toEqual(graph)
    expect(draftGraph(graph)).not.toBe(graph)
  })
})

describe('a new graph definition', () => {
  it('has one binary sensor named after it, no device, no settings and no steps', () => {
    expect(newGraphDefinition('Any motion')).toEqual({
      name: 'Any motion', device: null, settings: [],
      entities: [{ key: 'any_motion', domain: 'binary_sensor', name: 'Any motion' }],
      logic: { kind: 'graph', graph: { version: 5, name: 'Any motion', nodes: [], edges: [] } },
    })
  })
  it('keys its entity by the name within the entity key rule', () => {
    const key = (name: string) => newGraphDefinition(name).entities![0].key
    expect(key('Hall — Motion 2')).toBe('hall_motion_2')
    expect(key('2nd floor')).toBe('nd_floor')
    expect(key('Été')).toBe('t')
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

describe('definition ids', () => {
  it('slugs the name', () => {
    expect(definitionId('Any motion', [])).toBe('any_motion')
    expect(definitionId('  Kitchen -- Plug!! ', [])).toBe('kitchen_plug')
    expect(definitionId('__Hall__', [])).toBe('hall')
    expect(definitionId('Plug 2', [])).toBe('plug_2')
    expect(definitionId('', [])).toBe('entity')
    expect(definitionId('!!!', [])).toBe('entity')
  })
  it('keeps to 80 characters', () => {
    expect(definitionId('a'.repeat(100), [])).toBe('a'.repeat(80))
    expect(definitionId('a'.repeat(79) + ' b', [])).toBe('a'.repeat(79))
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
  it('renames the entity and every step that names it', () => {
    const graph = editorGraph(plug())
    const renamed = renameEntityKey(graph, 'relay', 'outlet')
    expect(renamed).not.toBe(graph)
    expect(renamed.definition!.entities.map(e => e.key)).toEqual(['power', 'online', 'outlet'])
    expect(renamed.definition!.entities[2]).toEqual({ ...plug().entities![2], key: 'outlet' })
    expect(renamed.nodes.map(n => n.config.entity)).toEqual(['power', 'outlet', 'outlet'])
    expect(graph.nodes.map(n => n.config.entity)).toEqual(['power', 'relay', 'relay'])
    expect(graph.definition!.entities[2].key).toBe('relay')
  })
  it('leaves an entity operand alone', () => {
    const graph = editorGraph(motion())
    const renamed = renameEntityKey(graph, 'any_motion', 'motion')
    expect(renamed.nodes.find(n => n.id === 's')!.config.entity).toBe('motion')
    expect(renamed.nodes.find(n => n.id === 'a')!.config.entity).toEqual(graph.nodes.find(n => n.id === 'a')!.config.entity)
  })
  it('refuses a key another entity has', () => {
    const graph = editorGraph(plug())
    expect(renameEntityKey(graph, 'relay', 'power')).toBe(graph)
  })
})
