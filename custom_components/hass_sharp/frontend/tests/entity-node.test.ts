import { expect, it } from 'vitest'
import { addGraphNode, connectData, dataInputs, dataOutputs, declaredEntities, definitionCatalog, diagnosticCodes, entityNodeName, executionLabel, executionPorts, flowMethod, nodeCatalog, setInput, validateGraph, withGraphTypes, words } from '../src/visual/contract'
import { definitionNames, runName } from '../src/components/TriggerSummary'
import { catalogPalette, connectNodes, dataConnectionError, updateNode, type VisualGraph } from '../src/visual/graph'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { copySteps, pasteSteps } from '../src/visual/clipboard'
import { editorGraph, newGraphDefinition } from '../src/entities/model'
import { renameDefinition } from '../src/entities/definitionEdits'
import { refitNotice, retypeNode } from '../src/entities/refit'
import { add, empty, lit } from './visual-fixture'

it('an Entity node declares an entity', () => {
  let graph = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'entity', 'b', { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  const declared = declaredEntities(graph, nodeCatalog)
  expect(declared.map(d => [d.nodeId, d.entity.key])).toEqual([['a', 'door'], ['b', 'power']])
  expect(declared[1].entity).toMatchObject({ key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  expect(declared[1].entity).not.toHaveProperty('history')
  expect(declared[1].entity).not.toHaveProperty('arguments')
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => e.key)).toEqual(['door', 'power'])
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
})

it('a list on the graph is ignored', () => {
  const node = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' })
  const graph = { ...node, definition: { settings: [], entities: [{ key: 'old', domain: 'switch' }, { key: 'door', domain: 'switch' }] } as never }
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => `${e.key}:${e.domain}`)).toEqual(['door:binary_sensor'])
  expect(definitionCatalog(nodeCatalog, graph).definition!.entities.map(e => `${e.key}:${e.domain}`)).toEqual(['door:binary_sensor'])
  // A list the definition has, even one that cannot be read, is no problem of it; one of no Entity node gives none.
  expect(validateGraph(graph).some(d => d.code === 'definition.invalid')).toBe(false)
  expect(validateGraph({ ...node, definition: { settings: [], entities: 5 } as never }).some(d => d.code === 'definition.invalid')).toBe(false)
  expect(withGraphTypes(nodeCatalog, { ...empty(), definition: { settings: [], entities: [{ key: 'old', domain: 'switch' }] } as never }).definition!.entities).toEqual([])
})

it('a new Set state step chooses a node\'s entity', () => {
  const graph = add(add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }), 'set_entity_state', 'set')
  expect(graph.nodes.at(-1)!.config.entity).toBe('door')
})

it('the editor lists no entity of a read answer: its entities are its nodes\'', () => {
  const node = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }).nodes[0]
  const body = { name: 'Nodes', device: null, settings: [], entities: [{ key: 'door', domain: 'binary_sensor' }, { key: 'old', domain: 'switch' }], logic: { kind: 'graph', graph: { version: 5, name: 'Nodes', nodes: [node], edges: [] } } }
  const graph = editorGraph(body as never)
  expect(graph.definition).toEqual({ settings: [], device: null })
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => e.key)).toEqual(['door'])
})

it('a new Entity node does not take the key of the definition\'s one setting', () => {
  const graph: VisualGraph = { version: 5, name: 'Nodes', nodes: [], edges: [], definition: { settings: [{ key: 'delay', type: 'number' }] } as never }
  const node = addGraphNode(graph, 'entity', 'a', nodeCatalog).nodes[0]
  expect(node.config.key).toBe('binary_sensor')
  expect(Object.values(node.config)).not.toContain('delay')
})

it('a dropped Entity node arrives named and with a key of its own', () => {
  let graph = add(empty(), 'entity', 'a')
  expect(graph.nodes[0].config).toMatchObject({ domain: 'binary_sensor', name: 'Binary sensor', key: 'binary_sensor' })
  graph = add(graph, 'entity', 'b')
  expect(graph.nodes[1].config.key).toBe('binary_sensor_2')
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
  // A key only a list on the graph has is free: that list is no entity.
  expect(addGraphNode({ ...empty(), definition: { settings: [], entities: [{ key: 'binary_sensor', domain: 'switch' }] } as never }, 'entity', 'c', nodeCatalog).nodes[0].config.key).toBe('binary_sensor')
  // Its name is its own: the node carries no label, and no member is null.
  expect(graph.nodes[0]).not.toHaveProperty('label')
  expect(Object.values(graph.nodes[0].config)).not.toContain(null)
  // Any other step keeps its kind's label.
  expect(add(graph, 'log', 'log').nodes.at(-1)!.label).toBe('Log')
})

it('a type change drops what the new type does not have and says so', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp', options: { dimmable: true }, arguments: { is_on: lit('boolean', true), brightness: lit('number', 128) } })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true), brightness: lit('number', 10) })
  graph = connectNodes(graph, 'l', 'set', 'command:turn_on')
  const { graph: retyped, removed } = retypeNode(graph, 'l', 'binary_sensor', nodeCatalog)
  const node = retyped.nodes.find(n => n.id === 'l')!
  expect(node.config).toMatchObject({ domain: 'binary_sensor' })
  // A light's Can be dimmed goes: a binary sensor has no options.
  expect(node.config.options).toEqual({})
  expect(node.config.arguments).toEqual({ is_on: lit('boolean', true) })
  expect(retyped.edges.some(e => e.source === 'l')).toBe(false)
  expect(retyped.nodes.find(n => n.id === 'set')!.config).not.toHaveProperty('brightness')
  expect(validateGraph(retyped).some(d => d.message.startsWith('Unknown input'))).toBe(false)
  expect(refitNotice(removed)).toMatch(/^Removed 1 connection/)
  expect(refitNotice(removed)).toBe('Removed 1 connection that no longer fits: Turn on. Cleared 2 values that no longer fit: Brightness.')
  // The same type changes nothing.
  expect(retypeNode(graph, 'l', 'light', nodeCatalog).graph).toBe(graph)
})

it('a type change removes the wires into a value the new type does not have, and out of a value it does not give', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'constant', 'c', { type: 'number', value: lit('number', 128) })
  graph = connectData(graph, 'c', 'value', 'l', 'brightness')
  graph = add(graph, 'log', 'log')
  graph = connectData(connectNodes(graph, 'l', 'log', 'command:turn_on'), 'l', 'command:turn_on:brightness', 'log', 'value')
  const { graph: retyped, removed } = retypeNode(graph, 'l', 'switch', nodeCatalog)
  expect(retyped.edges.filter(e => e.source === 'l' || e.target === 'l').map(e => e.kind === 'data' ? `${e.sourcePort}->${e.targetPort}` : e.port))
    .toEqual(['command:turn_on'])
  expect(refitNotice(removed)).toBe('Removed 2 connections that no longer fit: Brightness.')
  expect(validateGraph(retyped).some(d => d.code === 'port.unknownInput' || d.code === 'port.unknownOutput' || d.code === 'port.unknownExecution')).toBe(false)
})

/** Copies the steps `ids` with the clipboard and pastes them beside where they were. */
const paste = (graph: VisualGraph, ids: string[]) => {
  const result = pasteSteps(graph, copySteps(graph, ids)!, nodeCatalog, { offset: { x: 40, y: 40 } })
  if ('error' in result) throw new Error(result.error)
  return result.graph
}

it('a pasted Entity node gets a key of its own and its pasted steps follow it', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  graph = add(graph, 'set_entity_available', 'outside', { entity: 'lamp' })
  const pasted = paste(graph, ['l', 'set'])
  const copies = pasted.nodes.slice(graph.nodes.length)
  expect(copies.find(n => n.kind === 'entity')!.config.key).toBe('lamp_2')
  expect(copies.find(n => n.kind === 'set_entity_state')!.config.entity).toBe('lamp_2')
  expect(pasted.nodes.find(n => n.id === 'outside')!.config.entity).toBe('lamp')
  expect(pasted.nodes.find(n => n.id === 'set')!.config.entity).toBe('lamp')
  expect(validateGraph(pasted).some(d => d.code === 'definition.duplicateEntity')).toBe(false)
  // A step pasted without its Entity node keeps the key it named.
  expect(paste(graph, ['set']).nodes.at(-1)!.config.entity).toBe('lamp')
  // Pasted again, the next copy takes the next free key.
  expect(paste(pasted, ['l']).nodes.at(-1)!.config.key).toBe('lamp_3')
})

it('a pasted copy of an Entity node whose key is 40 characters long gets a valid key of its own, its key shortened for the number', () => {
  // Gap found converting these tests: the copy's key was the original's with `_2` added, 42 characters, which is no key.
  const long = 'a_very_long_entity_key_for_the_hall_lamp'
  expect(long).toHaveLength(40)
  const graph = add(empty(), 'entity', 'l', { key: long, domain: 'light', name: 'Lamp' })
  const pattern = new RegExp(words.entityKeyPattern)
  const first = paste(graph, ['l']).nodes.at(-1)!.config.key as string
  expect(first).toBe('a_very_long_entity_key_for_the_hall_la_2')
  expect(first.length).toBeLessThanOrEqual(40)
  expect(pattern.test(first)).toBe(true)
  // Each copy's key is still unused, however many there are.
  const keys = [long]
  let next = graph
  for (let i = 0; i < 10; i++) {
    next = paste(next, ['l'])
    keys.push(next.nodes.at(-1)!.config.key as string)
  }
  expect(new Set(keys).size).toBe(keys.length)
  for (const key of keys) {
    expect(key.length, key).toBeLessThanOrEqual(40)
    expect(pattern.test(key), key).toBe(true)
  }
  expect(keys.at(-1)).toBe('a_very_long_entity_key_for_the_hall_l_11')
  expect(validateGraph(next).filter(d => d.severity === 3 && d.code !== 'entity.name').map(d => d.code)).toEqual([])
  // A 39-character key, too.
  const shorter = paste(add(empty(), 'entity', 'l', { key: long.slice(0, 39), domain: 'light', name: 'Lamp' }), ['l']).nodes.at(-1)!.config.key as string
  expect(shorter).toBe('a_very_long_entity_key_for_the_hall_la_2')
  // A shortened base that ends in `_` loses it before the number.
  const underscored = `${'x'.repeat(37)}_ab`
  expect(paste(add(empty(), 'entity', 'l', { key: underscored, domain: 'light', name: 'Lamp' }), ['l']).nodes.at(-1)!.config.key).toBe(`${'x'.repeat(37)}_2`)
})

it('a new definition starts with one Entity node that follows its name until it is saved', () => {
  const graph = editorGraph(newGraphDefinition('Front door'))
  expect(graph.nodes.map(n => [n.kind, n.config.key, n.config.name])).toEqual([['entity', 'front_door', 'Front door']])
  expect(graph.definition).toEqual({ settings: [], device: null })
  const renamed = renameDefinition(graph, 'Back door', false)
  expect(renamed.nodes[0].config).toMatchObject({ key: 'back_door', name: 'Back door' })
  expect(renameDefinition(graph, 'Back door', true).nodes[0].config.key).toBe('front_door')
  // A binary sensor, valid as it is, with no label and no null member.
  expect(graph.nodes[0].config.domain).toBe('binary_sensor')
  expect(graph.nodes[0]).not.toHaveProperty('label')
  expect(Object.values(graph.nodes[0].config)).not.toContain(null)
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
  // Cleared, the name is left out (never null or blank) and the key is the one a nameless entity gets.
  const cleared = renameDefinition(renamed, '', false)
  expect(cleared.nodes[0].config).not.toHaveProperty('name')
  expect(cleared.nodes[0].config.key).toBe('entity')
  // Once the person has named or keyed it themselves, it no longer follows.
  const named = updateNode(graph, graph.nodes[0].id, { config: { ...graph.nodes[0].config, name: 'Porch' } })
  expect(renameDefinition(named, 'Back door', false).nodes[0].config).toMatchObject({ key: 'front_door', name: 'Porch' })
})

/** The labels of a node's right column, top to bottom: each execution pin with the values under it. */
const rightColumn = (graph: VisualGraph, id: string) =>
  buildNodeView(graph.nodes.find(n => n.id === id)!, { graph, catalog: nodeCatalog, diagnostics: [] }).rows.flatMap(r => (r.output ? [r.output.label] : []))

it('the node has the pins of its type', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' }).nodes[0]
  expect(Object.keys(dataInputs(light))).toEqual(['is_on', 'brightness'])
  expect(Object.values(dataInputs(light)).every(input => input.required === false)).toBe(true)
  expect(executionPorts(light)).toEqual(['command:turn_on', 'command:turn_off'])
  expect(executionLabel(light, 'command:turn_on')).toBe('Turn on')
  expect(dataOutputs(light)['command:turn_on:brightness']).toMatchObject({ pointer: '/data/brightness', group: 'command:turn_on' })
  const camera = add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera' }).nodes[0]
  expect(executionPorts(camera)).toEqual(['frame'])
  expect(executionLabel(camera, 'frame')).toBe('When a frame is needed')
  expect(Object.keys(dataInputs({ ...light, config: { ...light.config, key: '' } }))).toEqual(['is_on', 'brightness'])
})

it('a value set on the node is kept under arguments, and one can be connected', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = connectData(graph, 'is', 'value', 'l', 'is_on')
  expect(graph.edges.some(e => e.kind === 'data' && e.target === 'l' && e.targetPort === 'is_on')).toBe(true)
  // setInput changes the node it is given, as InputRow does on a copy.
  const set = structuredClone(graph.nodes[0])
  setInput(set, 'brightness', lit('number', 128), nodeCatalog)
  // The connected input is cleared under arguments too, as connectData clears any input it wires.
  expect(set.config.arguments).toEqual({ is_on: { kind: 'unset' }, brightness: lit('number', 128) })
  expect(set.config).not.toHaveProperty('brightness')
})

it('a step can be connected to a command, and the card lists each command with its values under it', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'log', 'log')
  graph = connectNodes(graph, 'l', 'log', 'command:turn_on')
  // A step order wire names its execution pin as port (sourcePort is a value wire's).
  expect(graph.edges.some(e => e.kind !== 'data' && e.source === 'l' && e.target === 'log' && e.port === 'command:turn_on')).toBe(true)
  expect(rightColumn(graph, 'l')).toEqual(['Turn on', 'Brightness', 'Turn off'])
})

it('a flow is named by node and port, as the server names it', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' }).nodes[0]
  expect(flowMethod(light, 'state')).toBe('Run_6C_7374617465')
  // VisualNodeCatalog.FlowMethod: the node's method, then the hex of the port's UTF-8.
  expect(flowMethod(light, 'command:turn_on')).toBe('Run_6C_636F6D6D616E643A7475726E5F6F6E')
})

it('a run of a node\'s flow reads as the entity and the flow', () => {
  const graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const names = definitionNames(graph, nodeCatalog)!
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_on'), names)).toBe('Lamp: Turn on')
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_off'), names)).toBe('Lamp: Turn off')
  expect(runName(flowMethod(graph.nodes[0], 'state'), names)).toBe('Lamp: State')
  expect(names.entities!.map(e => e.key)).toEqual(['lamp'])
  // A camera's frame flow; a nameless entity by its key, and one with neither as Entity.
  let cameras = add(empty(), 'entity', 'c', { key: 'door_cam', domain: 'camera', name: 'Front door' })
  cameras = add(cameras, 'entity', 'k', { key: 'yard', domain: 'camera', name: '' })
  cameras = add(cameras, 'entity', 'n', { key: '', domain: 'switch', name: '' })
  const named = definitionNames(cameras, nodeCatalog)!
  expect(runName(flowMethod(cameras.nodes[0], 'frame'), named)).toBe('Front door: When a frame is needed')
  expect(runName(flowMethod(cameras.nodes[1], 'state'), named)).toBe('yard: State')
  expect(runName(flowMethod(cameras.nodes[2], 'command:turn_on'), named)).toBe('Entity: Turn on')
  // A port the node does not have is no run of it, and keeps its recorded name.
  expect(runName(flowMethod(graph.nodes[0], 'frame'), names)).toBe(flowMethod(graph.nodes[0], 'frame'))
})

it('a light\'s Brightness can be connected before On, and the node then asks for On', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'constant', 'c', { type: 'number', value: lit('number', 128) })
  const wired = connectData(graph, 'c', 'value', 'l', 'brightness')
  expect(wired).not.toBe(graph)
  expect(validateGraph(wired).filter(d => d.severity === 3)).toMatchObject([{ nodeId: 'l', inputId: 'is_on', code: 'entity.partialState', message: 'Lamp has a value for Brightness but none for On: connect or set On too' }])
  // A hover says what the drop says.
  expect(dataConnectionError(graph, 'c', 'value', 'l', 'brightness', nodeCatalog, { quick: true })).toBeUndefined()
  // With On connected too, the node is whole again.
  const whole = connectData(add(wired, 'constant', 'on', { type: 'boolean', value: lit('boolean', true) }), 'on', 'value', 'l', 'is_on')
  expect(validateGraph(whole).filter(d => d.severity === 3)).toEqual([])
})

it('the editor refuses a wire that reads a command\'s value from a step another command also runs', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  graph = connectNodes(connectNodes(graph, 'l', 'set', 'command:turn_on'), 'l', 'set', 'command:turn_off')
  expect(graph.edges.flatMap(e => e.kind === 'data' ? [] : [e.port])).toEqual(['command:turn_on', 'command:turn_off'])
  const refusal = 'Set state reads Brightness from Turn on of Lamp but also runs from Turn off of Lamp, where there is no Brightness'
  // connectData refuses by throwing what is wrong, as every refused connection does; the hover says the same.
  expect(() => connectData(graph, 'l', 'command:turn_on:brightness', 'set', 'brightness')).toThrow(refusal)
  expect(dataConnectionError(graph, 'l', 'command:turn_on:brightness', 'set', 'brightness', nodeCatalog, { quick: true })).toBe(refusal)
  const one = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const alone = connectNodes(add(one, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) }), 'l', 'set', 'command:turn_on')
  const read = connectData(alone, 'l', 'command:turn_on:brightness', 'set', 'brightness')
  expect(read).not.toBe(alone)
  expect(validateGraph(read).filter(d => d.severity === 3)).toEqual([])
  expect(dataConnectionError(alone, 'l', 'command:turn_on:brightness', 'set', 'brightness', nodeCatalog, { quick: true })).toBeUndefined()
})

it('a state value of the node cannot be connected to a step, and a hover says so', () => {
  let graph = add(empty(), 'entity', 'd', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'trigger', 't', { type: 'manual' })
  graph = add(graph, 'read_entity', 'read', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'convert', 'c', { type: 'boolean' })
  graph = connectData(connectNodes(graph, 't', 'read'), 'read', 'value', 'c', 'value')
  const refusal = 'On of Door cannot read Read entity, which runs in a flow: use a Set state step after Read entity instead'
  expect(() => connectData(graph, 'c', 'value', 'd', 'is_on')).toThrow(refusal)
  expect(dataConnectionError(graph, 'c', 'value', 'd', 'is_on', nodeCatalog, { quick: true })).toBe(refusal)
})

it('a declaring node is named by its entity in messages and run names', () => {
  const graph = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor', name: '  Front door ' })
  const named = graph.nodes[0]
  expect(words.stepName(named)).toBe('Front door')
  expect(entityNodeName(named)).toBe('Front door')
  expect(words.stepName({ ...named, config: { ...named.config, name: ' ' } })).toBe('door')
  // Its own label is not its name: a node is named by its entity, else its kind.
  expect(words.stepName({ ...named, label: 'Something else', config: { ...named.config, name: '', key: '' } })).toBe('Entity')
  // Any other step keeps its label, and declares no entity.
  expect(entityNodeName({ id: 'l', kind: 'log', label: 'Log', config: {} })).toBeUndefined()
  expect(runName(flowMethod(named, 'state'), definitionNames(graph, nodeCatalog))).toBe('Front door: State')
})

it('the old steps are gone from the catalog and the step library', () => {
  for (const kind of ['entity_state', 'entity_command', 'camera_frame']) expect(nodeCatalog.nodes).not.toHaveProperty(kind)
  const entity = catalogPalette(nodeCatalog, 'entity').filter(item => item.category === 'Entity').map(item => item.kind)
  expect(entity.slice(0, 3)).toEqual(['entity', 'entity_setup', 'set_entity_state'])
  expect(diagnosticCodes).not.toContain('state.duplicate')
})
