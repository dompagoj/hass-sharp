import { expect, it } from 'vitest'
import { addGraphNode, classSignature, connectData, csharpEntities, listedEntities, entityKeyHolders, hasCode, isClassNode, isHub, wellFormedClassSignature, dataInputs, declaredDevices, definitionDeviceKeys, definitionEntityKeys, derivedDevices, deviceNodeOf, disconnectData, isPure, valueUpstream, dataOutputs, declaredEntities, definitionCatalog, diagnosticCodes, entityNodeName, executionLabel, executionPorts, flowMethod, isDeclaration, listedTypes, nodeCatalog, setInput, settingPinType, stateInputs, structural, validateGraph, withGraphTypes, words, type ClassSetting, type Graph, type GraphNode, type NodeSpec, type VisualCatalogSnapshot } from '../src/visual/contract'
import { hubKind, relayKind, relaysCase, relaysGraph, withRelays } from './relays-library'
import { definitionNames, runName, summarizeTrigger } from '../src/components/TriggerSummary'
import { canConnect, catalogPalette, connectNodes, createGraph, createNodeAt, dataConnectionError, removeNode, updateNode, type VisualGraph } from '../src/visual/graph'
import { addConnected, canAddFor } from '../src/visual/flow/autoConnect'
import { connectionIndex } from '../src/visual/connectionIndex'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { copySteps, pasteSteps } from '../src/visual/clipboard'
import { cardEntities, draftGraph, editorGraph, newGraphDefinition, renameEntityKey } from '../src/entities/model'
import { entityAdapter } from '../src/entities/adapter'
import { entityRemoval, renameDefinition } from '../src/entities/definitionEdits'
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
  const body = { name: 'Nodes', settings: [], entities: [{ key: 'door', domain: 'binary_sensor' }, { key: 'old', domain: 'switch' }], logic: { kind: 'graph', graph: { version: 5, name: 'Nodes', nodes: [node], edges: [] } } }
  const graph = editorGraph(body as never)
  expect(graph.definition).toEqual({ settings: [] })
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

it('a type change keeps the wire from a Device node', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
  graph = add(graph, 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp', arguments: { brightness: lit('number', 128) } })
  graph = connectData(graph, 'plug', 'entities', 'l', 'device')
  const { graph: retyped, removed } = retypeNode(graph, 'l', 'switch', nodeCatalog)
  expect(retyped.edges.map(e => e.kind === 'data' ? `${e.source}.${e.sourcePort}->${e.target}.${e.targetPort}` : e.id)).toEqual(['plug.entities->l.device'])
  // Brightness goes, as a switch has none; the Device pin keeps its wire.
  expect(retyped.nodes.find(n => n.id === 'l')!.config.arguments).toEqual({ device: { kind: 'unset' } })
  expect(refitNotice(removed)).toBe('Cleared a value that no longer fits: Brightness.')
})

/** An entity definition's graph with a Device node, a switch, When started and a Log step it runs. */
const withDevice = () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' })
  graph = add(graph, 'entity_setup', 'start')
  graph = add(graph, 'log', 'log', { value: lit('string', 'Started') })
  return connectNodes(graph, 'start', 'log')
}

it('a Device node connects to an Entity node\'s Device pin and to nothing else', () => {
  const graph = withDevice()
  const wired = connectData(graph, 'plug', 'entities', 'relay', 'device')
  expect(wired.edges.filter(e => e.kind === 'data')).toEqual([expect.objectContaining({ kind: 'data', source: 'plug', sourcePort: 'entities', target: 'relay', targetPort: 'device' })])
  // The wire's place is held under the node's state values, as every input of an Entity node is.
  expect(wired.nodes.find(n => n.id === 'relay')!.config.arguments).toEqual({ device: { kind: 'unset' } })
  expect(validateGraph(wired).filter(d => d.severity === 3)).toEqual([])
  // Not into an input that takes any value, nor an object's field or a list's item.
  const notDevice = 'accepts True/False, Number, Text, Object and 5 more, not Device'
  expect(() => connectData(graph, 'plug', 'entities', 'log', 'value')).toThrow(`Message ${notDevice}`)
  expect(dataConnectionError(graph, 'plug', 'entities', 'log', 'value', nodeCatalog, { quick: true })).toBe(`Message ${notDevice}`)
  const fields = add(graph, 'object_create', 'obj', { fields: { room: { kind: 'unset' } } })
  expect(() => connectData(fields, 'plug', 'entities', 'obj', 'field:room')).toThrow(`Room ${notDevice}`)
  const items = add(graph, 'array_create', 'list', { items: [{ kind: 'unset' }] })
  expect(() => connectData(items, 'plug', 'entities', 'list', 'item:0')).toThrow(`Item 1 ${notDevice}`)
  // No step order wire leads into a Device node or out of it.
  expect(canConnect(graph, 'log', 'plug')).toBe(false)
  expect(connectNodes(graph, 'log', 'plug')).toBe(graph)
  expect(canConnect(graph, 'plug', 'log')).toBe(false)
  expect(connectNodes(graph, 'plug', 'log', 'next')).toBe(graph)
})

it('a new Device node has no step pins and no state', () => {
  const graph = withDevice()
  const device = graph.nodes.find(n => n.id === 'plug')!, relay = graph.nodes.find(n => n.id === 'relay')!
  expect(executionPorts(device)).toEqual([])
  expect(isDeclaration('device', nodeCatalog)).toBe(true)
  expect(isDeclaration('entity', nodeCatalog) || isDeclaration('log', nodeCatalog) || isDeclaration('missing', nodeCatalog)).toBe(false)
  expect(structural(nodeCatalog.types.device)).toBe(true)
  expect(structural(nodeCatalog.types.image) || structural(undefined)).toBe(false)
  expect(Object.keys(stateInputs(relay, nodeCatalog))).toEqual(['is_on'])
  expect(Object.keys(dataInputs(relay))).toEqual(['device', 'is_on'])
  expect(structural(dataInputs(relay).device)).toBe(true)
  // No list of every type has it: an input of any type, nor the type lists of Constant, Convert and the loops.
  const message = dataInputs(graph.nodes.find(n => n.id === 'log')!).value.types
  expect(message).toContain('image')
  expect(message).not.toContain('device')
  expect(listedTypes(nodeCatalog)).not.toContain('device')
  // A Device node is named by its name, has no problem but that it has no entities yet, and its card has no step pins and is not muted.
  const diagnostics = validateGraph(graph)
  expect(words.stepName(device)).toBe('Hall plug')
  expect(diagnostics.filter(d => d.nodeId === 'plug')).toEqual([{ nodeId: 'plug', message: 'Hall plug has no entities: connect its Entities pin to an entity\'s Device pin', severity: 2, code: 'device.empty' }])
  const view = buildNodeView(device, { graph, catalog: nodeCatalog, diagnostics })
  expect([view.execIn, view.next, view.footer, view.unreachable]).toEqual([undefined, undefined, undefined, false])
  expect(view.title).toBe('Hall plug')
  expect(view.rows.map(row => row.output?.label)).toEqual(['Entities'])
})

it('a new Device node is named and keyed', () => {
  // As the editor adds one: dropped from the palette, quick-added, or from a wire (each goes through addGraphNode).
  let graph = addGraphNode(empty(), 'device', 'a', nodeCatalog)
  graph = addGraphNode(graph, 'device', 'b', nodeCatalog)
  const [a, b] = graph.nodes
  expect(a.config).toEqual({ key: 'device', name: 'Device' })
  expect(b.config).toEqual({ key: 'device_2', name: 'Device' })
  // Its name is its own: no label, as an Entity node has none.
  expect(a).not.toHaveProperty('label')
  expect(b).not.toHaveProperty('label')
  // Its only problem is that it has no entities yet (a warning): no key or name to give first.
  expect(validateGraph(graph).filter(d => d.nodeId === 'a' || d.nodeId === 'b').map(d => `${d.nodeId}:${d.code}:${d.severity}`)).toEqual(['a:device.empty:2', 'b:device.empty:2'])
  // Dropped from the palette or quick-added on the canvas.
  const dropped = createNodeAt(graph, 'device', { x: 400, y: 200 }, undefined, nodeCatalog)
  expect(dropped.nodes.at(-1)!.config).toEqual({ key: 'device_3', name: 'Device' })
  expect(dropped.nodes.at(-1)!.position).toEqual({ x: 400, y: 200 })
  // Added from a wire dragged out of an Entity node's Device pin and dropped on empty canvas.
  const relay = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' })
  const wired = addConnected(relay, 'device', { x: 0, y: 0 }, { nodeId: 'relay', handle: 'data:device', type: 'target' }, nodeCatalog, 'c')!.graph
  expect(wired.nodes.find(n => n.id === 'c')!.config).toEqual({ key: 'device_3', name: 'Device' })
  expect(deviceNodeOf(wired, 'relay')).toBe('c')
  // Device keys and entity keys are separate: an entity keyed device does not take the key, nor a device an entity's.
  const entityKeyed = add(empty(), 'entity', 'e', { key: 'device', domain: 'switch', name: 'Device' })
  expect(addGraphNode(entityKeyed, 'device', 'd', nodeCatalog).nodes.at(-1)!.config.key).toBe('device')
  const deviceKeyed = add(empty(), 'device', 'd', { key: 'binary_sensor' })
  expect(addGraphNode(deviceKeyed, 'entity', 'e', nodeCatalog).nodes.at(-1)!.config.key).toBe('binary_sensor')
  // Two devices of one name are fine: names need not be unique.
  expect(validateGraph(dropped).filter(d => d.severity === 3)).toEqual([])
})

it('a dropped C# entity or C# device is keyed past the keys it shares, and has no label', () => {
  // A C# entity is keyed by its template's type, past every entity key (an Entity node's too); it has no name of its own.
  let graph = addGraphNode(empty(), 'csharp_entity', 'a', nodeCatalog)
  graph = add(graph, 'entity', 'e', { key: 'sensor_2', domain: 'sensor', name: 'Power' })
  graph = addGraphNode(graph, 'csharp_entity', 'b', nodeCatalog)
  const [a, , b] = graph.nodes
  expect([a.config.key, b.config.key]).toEqual(['sensor', 'sensor_3'])
  expect(a.config).not.toHaveProperty('name')
  expect(a.config.source).toBe(nodeCatalog.nodes.csharp_entity.config.source.default)
  expect(a.config.signature).toEqual(nodeCatalog.nodes.csharp_entity.config.signature.default)
  expect(a.config.arguments).toEqual({})
  // An Entity node (a binary sensor when dropped) is keyed past a C# entity's key.
  const taken = add(graph, 'csharp_entity', 'g', { key: 'binary_sensor' })
  expect(addGraphNode(taken, 'entity', 'f', nodeCatalog).nodes.at(-1)!.config.key).toBe('binary_sensor_2')
  // A C# device is keyed as a Device node is, past every device key, and named by its template's title.
  let devices = addGraphNode(empty(), 'device', 'd', nodeCatalog)
  devices = addGraphNode(devices, 'csharp_device', 'p', nodeCatalog)
  devices = addGraphNode(devices, 'csharp_device', 'q', nodeCatalog)
  expect(devices.nodes.map(n => [n.config.key, n.config.name])).toEqual([['device', 'Device'], ['device_2', 'Plug'], ['device_3', 'Plug']])
  expect(addGraphNode(devices, 'device', 'r', nodeCatalog).nodes.at(-1)!.config.key).toBe('device_4')
  // Neither has a label: each is named by what it declares.
  for (const node of [...graph.nodes, ...devices.nodes]) expect(node).not.toHaveProperty('label')
  // Each starts from its kind's template, as its own copy: the code editor's Apply writes into the node, never the catalog.
  const [p, q] = devices.nodes.slice(1)
  expect(p.config.source).toBe(nodeCatalog.nodes.csharp_device.config.source.default)
  expect(p.config.signature).toEqual(nodeCatalog.nodes.csharp_device.config.signature.default)
  expect(p.config.signature).not.toBe(nodeCatalog.nodes.csharp_device.config.signature.default)
  expect(p.config.signature).not.toBe(q.config.signature)
  expect(a.config.signature).not.toBe(nodeCatalog.nodes.csharp_entity.config.signature.default)
  expect(a.config.arguments).not.toBe(b.config.arguments)
  // Dropped from the palette, or from a wire dragged out of a Device node's Entities pin, alike.
  const dropped = createNodeAt(graph, 'csharp_entity', { x: 400, y: 200 }, undefined, nodeCatalog)
  expect(dropped.nodes.at(-1)!.config.key).toBe('sensor_4')
  const hall = add(empty(), 'device', 'hall', { key: 'hall', name: 'Hall' })
  const wired = addConnected(hall, 'csharp_entity', { x: 0, y: 0 }, { nodeId: 'hall', handle: 'data:entities', type: 'source' }, nodeCatalog, 'c')!.graph
  expect(wired.nodes.find(n => n.id === 'c')!.config.key).toBe('sensor')
  expect(deviceNodeOf(wired, 'c')).toBe('hall')
})

/** The built-in catalog with the relays library's classes, or with `entries` in their place. */
const withLibrary = (entries?: Record<string, NodeSpec>): VisualCatalogSnapshot => withRelays(nodeCatalog, entries)
/** The relay hub's entry as `changes` make it, under `kind`. */
const hubEntry = (kind: string, changes: Partial<NodeSpec>): Record<string, NodeSpec> => ({ [kind]: { ...relaysCase.catalogPatch!.nodes[hubKind], ...changes } })

it('a library class node has its class\'s settings and no code', () => {
  const catalog = withLibrary(), spec = catalog.nodes[hubKind]
  const hub: GraphNode = { id: 'hub', kind: hubKind, config: { key: 'hub', name: 'Hub', arguments: {} } }
  expect([isClassNode(spec), hasCode(spec)]).toEqual([true, false])
  expect(classSignature(hub, catalog)).toBe(spec.classSignature)
  expect(Object.keys(dataInputs(hub, undefined, catalog))).toEqual(['Address', 'Token', 'PollEvery'])
  // Code and a signature its config holds (hand-edited, or left by a converted C# node) change nothing: its settings are
  // its entry's, and the validator says nothing of either.
  const stale: GraphNode = { ...hub, config: { ...hub.config, source: 'public class Old : SharpDevice { }', signature: { title: 'Old', description: '', domain: null, settings: [{ key: 'Old', label: 'Old', description: '', type: 'string', secret: false }] } } }
  expect(classSignature(stale, catalog)).toBe(spec.classSignature)
  expect(Object.keys(dataInputs(stale, undefined, catalog))).toEqual(['Address', 'Token', 'PollEvery'])
  const graph = (node: GraphNode) => ({ ...empty(), nodes: [{ ...node, config: { ...node.config, arguments: { Address: lit('string', '10.0.0.2') } } }] })
  expect(validateGraph(graph(stale), catalog)).toEqual([])
  // An entry whose settings cannot be read gives none, never its config's; nor is that the node's unreadable settings,
  // which only a C# node's code, applied again, could write.
  const broken = withLibrary(hubEntry(hubKind, { classSignature: { title: 5 } as never }))
  expect([classSignature(stale, broken), Object.keys(dataInputs(stale, undefined, broken))]).toEqual([undefined, []])
  expect(validateGraph(graph(stale), broken).map(d => d.code)).not.toContain('csharp.signature')
  // A C# device node has its code and its config's signature, as before.
  const device = addGraphNode(empty(), 'csharp_device', 'd', catalog).nodes[0], deviceSpec = catalog.nodes.csharp_device
  expect([isClassNode(deviceSpec), hasCode(deviceSpec)]).toEqual([true, true])
  expect(classSignature(device, catalog)).toBe(device.config.signature)
  expect(Object.keys(dataInputs(device, undefined, catalog))).toEqual(['Address'])
})

it('dropping a library device names and keys it by its title', () => {
  const catalog = withLibrary()
  let graph = addGraphNode(empty(), hubKind, 'a', catalog)
  graph = addGraphNode(graph, hubKind, 'b', catalog)
  expect(graph.nodes.map(n => [n.config.key, n.config.name])).toEqual([['relay_hub', 'Relay hub'], ['relay_hub_2', 'Relay hub']])
  // No code, no signature and no label: its settings are its entry's, and it is named by its device.
  const [a, b] = graph.nodes
  expect(a).not.toHaveProperty('label')
  expect(Object.keys(a.config).sort()).toEqual(['arguments', 'key', 'name'])
  expect(a.config.arguments).toEqual({})
  expect(a.config.arguments).not.toBe(b.config.arguments)
  // Keyed past every device key, a Device node's too, and not past an entity's.
  const keyed = add(add(empty(), 'device', 'd', { key: 'relay_hub' }), 'entity', 'e', { key: 'relay_hub_2' })
  expect(addGraphNode(keyed, hubKind, 'h', catalog).nodes.at(-1)!.config.key).toBe('relay_hub_2')
})

it('dropping a library entity keys it by its title', () => {
  const catalog = withLibrary()
  let graph = addGraphNode(empty(), relayKind, 'a', catalog)
  graph = add(graph, 'entity', 'e', { key: 'relay_2', domain: 'switch', name: 'Spare' })
  graph = addGraphNode(graph, relayKind, 'b', catalog)
  const [a, , b] = graph.nodes
  expect([a.config.key, b.config.key]).toEqual(['relay', 'relay_3'])
  // It has no name of its own (its class names its entity), no code, no signature and no label.
  expect(Object.keys(a.config).sort()).toEqual(['arguments', 'key'])
  expect(a).not.toHaveProperty('label')
  // An Entity node is keyed past its key, as past a C# entity's.
  expect(entityKeyHolders(graph, catalog).map(h => h.key)).toEqual(['relay', 'relay_2', 'relay_3'])
})

it('a title that makes no key falls back', () => {
  // A title is made a key as an entity's name is: accents and what is no letter go, as do digits it starts with.
  const titled = (title: string) => withLibrary({ ...hubEntry(hubKind, { label: title }), [relayKind]: { ...relaysCase.catalogPatch!.nodes[relayKind], label: title } })
  const dropped = (title: string, kind: string) => addGraphNode(empty(), kind, 'n', titled(title)).nodes[0].config
  expect([dropped('2 Relays!', hubKind).key, dropped('Détecteur', hubKind).key, dropped('Détecteur', relayKind).key]).toEqual(['relays', 'detecteur', 'detecteur'])
  // One that keeps no letter makes none: a device is keyed device, as a Device node is, and named by its title; an
  // entity by its domain, as a C# entity is.
  for (const title of ['2024', '日本語', '!!!']) {
    expect(dropped(title, hubKind), title).toEqual({ key: 'device', name: title, arguments: {} })
    expect(dropped(title, relayKind), title).toEqual({ key: 'switch', arguments: {} })
  }
  let graph = addGraphNode(add(empty(), 'device', 'd'), hubKind, 'h', titled('2024'))
  graph = addGraphNode(graph, relayKind, 'r', titled('2024'))
  expect(graph.nodes.map(n => n.config.key)).toEqual(['device', 'device_2', 'switch'])
})

it('two libraries\' classes of one title drop as two nodes with two keys', () => {
  const first = 'library:a:global%3A%3ANodeLib.FirstHub', second = 'library:b:global%3A%3ANodeLib.SecondHub'
  const catalog = withLibrary({ ...hubEntry(first, { sourceId: 'a' }), ...hubEntry(second, { sourceId: 'b' }) })
  const graph = addGraphNode(addGraphNode(empty(), first, 'a', catalog), second, 'b', catalog)
  expect(graph.nodes.map(n => [n.kind, n.config.key, n.config.name])).toEqual([[first, 'relay_hub', 'Relay hub'], [second, 'relay_hub_2', 'Relay hub']])
  expect(validateGraph({ ...graph, nodes: graph.nodes.map(n => ({ ...n, config: { ...n.config, arguments: { Address: lit('string', '10.0.0.2') } } })) }, catalog)).toEqual([])
})

it('a library device and a library entity are listed as their C# kinds are', () => {
  // A hub, a Strip Device node and a relay on the strip.
  const catalog = withLibrary(), graph = relaysCase.graph as Graph
  expect(declaredEntities(graph, catalog)).toEqual([])
  expect(declaredDevices(graph, catalog)).toEqual([{ nodeId: 'hub', device: { key: 'hub', name: 'Hub', via: null, csharp: true } }, { nodeId: 'strip', device: { key: 'strip', name: 'Strip' } }])
  expect(derivedDevices(graph, catalog)).toEqual([{ key: 'strip', name: 'Strip' }, { key: 'hub', name: 'Hub', csharp: true }])
  expect(definitionDeviceKeys(graph, catalog)).toEqual(['hub', 'strip'])
  expect(csharpEntities(graph, catalog)).toEqual([{ key: 'relay', domain: 'switch', name: null, device: 'strip', csharp: true }])
  expect(listedEntities(graph, catalog)).toEqual([{ key: 'relay', domain: 'switch', name: null, device: 'strip', csharp: true }])
  // Each is named as a C# node is: the hub by its name, the relay by its class's title.
  expect(graph.nodes.map(n => entityNodeName(n, catalog))).toEqual(['Hub', 'Strip', 'Relay'])
  // A kind that declares one and is no class node, nor an Entity or Device node, is still refused.
  const odd = withLibrary({ ...relaysCase.catalogPatch!.nodes, odd: { ...relaysCase.catalogPatch!.nodes[relayKind], dynamicInputs: undefined } })
  const withOdd = { ...graph, nodes: [...graph.nodes, { id: 'o', kind: 'odd', config: { key: 'odd' } }] }
  expect(() => declaredEntities(withOdd, odd)).toThrow('The kind odd declares an entity that no list knows how to describe')
  expect(csharpEntities(withOdd, odd).map(e => e.key)).toEqual(['relay'])
})

/** The relays graph with a token typed into its hub's secret Token, beside its Address (not secret). */
const typedToken = () => {
  const graph = relaysGraph()
  const hub = graph.nodes.find(n => n.id === 'hub')!
  ;(hub.config.arguments as Record<string, unknown>).Token = lit('string', 'r3lay-t0ken')
  return graph
}

it('a value typed into a library class\'s secret setting is left out of a draft and an export, by the live catalog', () => {
  const catalog = withLibrary(), graph = typedToken()
  // The entity definition's drafts, its Export graph and its Graph JSON view all take the graph as its adapter's draft gives it.
  for (const draft of [draftGraph(graph, catalog), entityAdapter({ callWS: async () => ({}) }).draft!(graph, catalog)]) {
    expect(JSON.stringify(draft)).not.toContain('r3lay-t0ken')
    // Its other values are kept, and so is the rest of its config.
    expect(draft.nodes.find(n => n.id === 'hub')!.config).toEqual({ key: 'hub', name: 'Hub', via: null, arguments: { Address: lit('string', '10.0.0.2') } })
    expect(draft.nodes.find(n => n.id === 'relay')).toEqual(graph.nodes.find(n => n.id === 'relay'))
  }
  // The graph itself keeps it.
  expect(JSON.stringify(graph)).toContain('r3lay-t0ken')
  // A value typed into a setting that is not secret is kept.
  const relay = graph.nodes.find(n => n.id === 'relay')!
  const typed = updateNode(graph, 'relay', { config: { ...relay.config, arguments: { Address: lit('string', '7') } } })
  expect(draftGraph(typed, catalog).nodes.find(n => n.id === 'relay')!.config.arguments).toEqual({ Address: lit('string', '7') })
})

it('a library entity\'s key is renamed where a C# entity\'s is', () => {
  const catalog = withLibrary(), graph = relaysGraph()
  const renamed = renameEntityKey(graph, 'relay', 'outlet', 'relay', catalog)
  expect(renamed.nodes.find(n => n.id === 'relay')!.config.key).toBe('outlet')
  // Its key is an entity key: a rename of another to it is refused, as to a C# entity's.
  const lamp = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'light', name: 'Lamp' })
  expect(renameEntityKey(lamp, 'lamp', 'relay', 'lamp', catalog)).toBe(lamp)
  // A device's key is no entity key, as a C# device's is not: its Key field renames it (see the right panel's tests).
  expect(renameEntityKey(graph, 'hub', 'bridge', 'hub', catalog)).toBe(graph)
})

it('a definition\'s entities list a library entity by its class\'s title, and a library device\'s in its node\'s place', () => {
  // The hub, the strip, the relay and then a C# device (key device): the hub's entities come before the C# device's.
  const graph = addGraphNode(relaysGraph(), 'csharp_device', 'plug', withLibrary())
  const live = (uniqueId: string, name: string | null) => ({ uniqueId, key: uniqueId, domain: 'switch', name, entityId: `switch.${uniqueId.replaceAll(':', '_')}` })
  const item = {
    id: 'relays', entities: [{ key: 'relay', domain: 'switch', name: null, device: 'strip', csharp: true as const }], registeredEntities: [],
    liveEntities: [live('relays:device:outlet', 'Outlet'), live('relays:hub:channel', 'Channel'), live('relays:relay', null)], logic: { kind: 'graph' as const, graph },
  }
  expect(cardEntities(item, withLibrary()).map(e => [e.name, e.device ?? null])).toEqual([['Relay', null], ['Channel', 'hub'], ['Outlet', 'device']])
  // Without its library, neither is known: the relay is named by its key, and the hub's entities come last, as those of a node that is gone.
  expect(cardEntities(item).map(e => [e.name, e.device ?? null])).toEqual([['relay', null], ['Outlet', 'device'], ['Channel', 'hub']])
})

/** A Thermometer C# entity, a Plug C# device, a lamp Entity node and a Hall Device node the thermometer is on. */
const withCSharp = () => {
  let graph = add(empty(), 'csharp_entity', 'thermo', { key: 'thermometer', arguments: { Address: lit('string', 'http://192.168.1.60/temperature') } })
  graph = add(graph, 'csharp_device', 'plug', { key: 'plug', name: 'Plug', via: null })
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'light', name: 'Lamp', arguments: { is_on: lit('boolean', true) } })
  graph = add(graph, 'device', 'hall', { key: 'hall', name: 'Hall' })
  return connectData(graph, 'hall', 'entities', 'thermo', 'device')
}

it('a C# entity and a C# device are declarations, and no rule takes either for an Entity node', () => {
  const graph = withCSharp()
  const thermo = graph.nodes.find(n => n.id === 'thermo')!, plug = graph.nodes.find(n => n.id === 'plug')!
  expect([isHub(nodeCatalog.nodes.entity), isHub(nodeCatalog.nodes.csharp_entity), isHub(nodeCatalog.nodes.csharp_device), isHub(nodeCatalog.nodes.device)]).toEqual([true, false, false, false])
  expect([isClassNode(nodeCatalog.nodes.csharp_entity), isClassNode(nodeCatalog.nodes.csharp_device), isClassNode(nodeCatalog.nodes.custom), isClassNode(nodeCatalog.nodes.entity)]).toEqual([true, true, false, false])
  expect([hasCode(nodeCatalog.nodes.custom), hasCode(nodeCatalog.nodes.csharp_entity), hasCode(nodeCatalog.nodes.csharp_device), hasCode(nodeCatalog.nodes.device)]).toEqual([true, true, true, false])
  for (const node of [thermo, plug]) {
    expect(isDeclaration(node.kind, nodeCatalog)).toBe(true)
    expect([executionPorts(node), Object.keys(dataOutputs(node)), Object.keys(stateInputs(node, nodeCatalog))]).toEqual([[], [], []])
    expect(isPure(node)).toBe(false)
  }
  // Its pins: a C# entity's Device pin (the Entity node's), then its settings; a C# device's settings alone.
  expect(Object.keys(dataInputs(thermo))).toEqual(['device', 'Address'])
  expect(dataInputs(thermo).device).toEqual(nodeCatalog.nodes.entity.inputs.device)
  expect(dataInputs(thermo).Address).toEqual({ types: ['string'], required: false, label: 'Address', description: 'Where the thermometer answers with the temperature in degrees Celsius, such as http://192.168.1.60/temperature.', setting: { type: 'string', secret: false } })
  expect(Object.keys(dataInputs(plug))).toEqual(['Address'])
  // Each setting type takes the catalog type of its pin, and keeps its options and range.
  const typed = { ...thermo, config: { ...thermo.config, signature: { title: 'Typed', description: '', domain: 'sensor', settings: [
    { key: 'Mode', label: 'Mode', description: '', type: 'enum', secret: false, options: ['Fast', 'Slow'] },
    { key: 'Count', label: 'Count', description: '', type: 'integer', secret: false, minimum: 1, maximum: 10 },
    { key: 'Level', label: 'Level', description: '', type: 'number', secret: false },
    { key: 'On', label: 'On', description: '', type: 'boolean', secret: false },
    { key: 'Token', label: 'Token', description: '', type: 'string', secret: true }] } } }
  expect(Object.entries(dataInputs(typed)).slice(1).map(([key, input]) => `${key}:${input.types[0]}`)).toEqual(['Mode:string', 'Count:number', 'Level:number', 'On:boolean', 'Token:string'])
  expect([dataInputs(typed).Mode.setting, dataInputs(typed).Count.setting, dataInputs(typed).Token.setting?.secret]).toEqual([{ type: 'enum', secret: false, options: ['Fast', 'Slow'] }, { type: 'integer', secret: false, minimum: 1, maximum: 10 }, true])
  // A signature that cannot be read, or whose setting would take a static pin's place, gives the static pins alone.
  const unreadable = { ...thermo, config: { ...thermo.config, signature: { title: 5 } } }
  expect([Object.keys(dataInputs(unreadable)), classSignature(unreadable)]).toEqual([['device'], undefined])
  const clash = { ...thermo, config: { ...thermo.config, signature: { title: 'T', description: '', domain: 'sensor', settings: [{ key: 'device', label: 'Device', description: '', type: 'string', secret: false }] } } }
  expect(classSignature(clash)).toBeUndefined()
  for (const signature of [
    { title: 'T', description: '', settings: [] },
    { title: 'T', description: '', domain: 5, settings: [] },
    { title: 'T', description: '', domain: null, settings: {} },
    { title: 'T', description: '', domain: null, settings: [{ key: 'a b', label: 'A', description: '', type: 'string', secret: false }] },
    { title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', type: 'string', secret: false }, { key: 'A', label: 'A', description: '', type: 'string', secret: false }] },
    { title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', type: 'date', secret: false }] },
    { title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', type: 'string' }] },
    { title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', type: 'enum', secret: false, options: [1] }] },
    { title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', type: 'integer', secret: false, minimum: '1' }] },
  ]) expect(wellFormedClassSignature(signature), JSON.stringify(signature)).toBe(false)
  expect(wellFormedClassSignature(nodeCatalog.nodes.csharp_entity.config.signature.default) && wellFormedClassSignature(nodeCatalog.nodes.csharp_device.config.signature.default)).toBe(true)
  // A setting keyed __proto__ (a legal C# identifier) is a pin like any other: the ports have no prototype to set.
  const proto = { ...thermo, config: { ...thermo.config, signature: { title: 'T', description: '', domain: 'sensor', settings: [
    { key: '__proto__', label: 'Proto', description: '', type: 'string', secret: false }, { key: 'Level', label: 'Level', description: '', type: 'number', secret: false }] } } }
  const protoPorts = dataInputs(proto)
  expect(Object.keys(protoPorts)).toEqual(['device', '__proto__', 'Level'])
  expect(Object.getPrototypeOf(protoPorts)).toBeNull()
  expect(Object.getOwnPropertyDescriptor(protoPorts, '__proto__')?.value).toEqual({ types: ['string'], required: false, label: 'Proto', description: '', setting: { type: 'string', secret: false } })
  expect([protoPorts.device, protoPorts.Level]).toEqual([nodeCatalog.nodes.entity.inputs.device, { types: ['number'], required: false, label: 'Level', description: '', setting: { type: 'number', secret: false } }])
  // An entity setting's pin is an entity id, of its domain when it has one; a required setting's pin is required, and
  // the pin's setting carries the domain and that it is required.
  const entities = { ...thermo, config: { ...thermo.config, signature: { title: 'T', description: '', domain: 'sensor', settings: [
    { key: 'Light', label: 'Light', description: '', type: 'entity', secret: false, domain: 'light', required: true },
    { key: 'Any', label: 'Any', description: '', type: 'entity', secret: false },
    { key: 'Host', label: 'Host', description: '', type: 'string', secret: false, required: true }] } } }
  expect(Object.entries(dataInputs(entities)).slice(1).map(([key, input]) => `${key}:${input.types.join('|')}:${input.required}`)).toEqual(['Light:entity_id:light:true', 'Any:entity_id:false', 'Host:string:true'])
  expect([dataInputs(entities).Light.setting, dataInputs(entities).Any.setting, dataInputs(entities).Host.setting]).toEqual([
    { type: 'entity', secret: false, domain: 'light', required: true }, { type: 'entity', secret: false }, { type: 'string', secret: false, required: true }])
  const one = (setting: Record<string, unknown>) => ({ title: 'T', description: '', domain: null, settings: [{ key: 'A', label: 'A', description: '', secret: false, ...setting }] })
  expect([settingPinType(one({ type: 'enum' }).settings[0] as ClassSetting), settingPinType(one({ type: 'entity', domain: 'sensor' }).settings[0] as ClassSetting), settingPinType(one({ type: 'date' }).settings[0] as ClassSetting)])
    .toEqual(['string', 'entity_id:sensor', undefined])
  expect([one({ type: 'entity', domain: 'light', required: true }), one({ type: 'entity' }), one({ type: 'string', required: false })].every(wellFormedClassSignature)).toBe(true)
  for (const signature of [one({ type: 'string', domain: 'light' }), one({ type: 'entity', domain: 'Light Bulb' }), one({ type: 'entity', domain: '' }), one({ type: 'entity', domain: 5 }), one({ type: 'string', required: 'yes' })])
    expect(wellFormedClassSignature(signature), JSON.stringify(signature)).toBe(false)
  // Named by its class's title (a C# entity) or its name (a C# device).
  expect([words.stepName(thermo), words.stepName(plug), entityNodeName(thermo), entityNodeName(plug)]).toEqual(['Thermometer', 'Plug', 'Thermometer', 'Plug'])
  expect(words.stepName({ ...thermo, config: { ...thermo.config, signature: { ...thermo.config.signature as object, title: ' ' } } })).toBe('thermometer')
  expect(words.stepName({ ...plug, config: { key: '', name: '' } })).toBe('C# device')
  // The graph's entities are the Entity node's; its devices the Device node's, then the C# device's by its key, name and Part of.
  expect(declaredEntities(graph).map(d => d.nodeId)).toEqual(['lamp'])
  expect(definitionEntityKeys(graph)).toEqual(['thermometer', 'lamp'])
  expect(entityKeyHolders(graph)).toEqual([{ nodeId: 'thermo', key: 'thermometer' }, { nodeId: 'lamp', key: 'lamp' }])
  expect(declaredDevices(graph)).toEqual([{ nodeId: 'plug', device: { key: 'plug', name: 'Plug', via: null, csharp: true } }, { nodeId: 'hall', device: { key: 'hall', name: 'Hall' } }])
  expect(definitionDeviceKeys(graph)).toEqual(['plug', 'hall'])
  expect(derivedDevices(graph, nodeCatalog)).toEqual([{ key: 'hall', name: 'Hall' }, { key: 'plug', name: 'Plug', csharp: true }])
  const lifted = withGraphTypes(nodeCatalog, graph).definition!
  expect([lifted.devices, lifted.entities.map(e => e.key)]).toEqual([derivedDevices(graph, nodeCatalog), ['lamp']])
  expect(deviceNodeOf(graph, 'thermo')).toBe('hall')
  // Nothing derived carries a C# node's code, settings or their values.
  for (const value of [lifted, declaredDevices(graph), derivedDevices(graph, nodeCatalog)])
    for (const member of ['source', 'signature', 'arguments']) expect(JSON.stringify(value)).not.toContain(`"${member}"`)
  // No name, domain, state or command is asked of the C# entity, and no entity of the C# device: the lamp's commands
  // with no steps are the graph's only problems.
  expect(validateGraph(graph).map(d => `${d.nodeId}:${d.code}`)).toEqual(['lamp:command.unhandled', 'lamp:command.unhandled'])
  // A declaring kind the lists do not know is refused, never copied as it is.
  const other = { ...nodeCatalog, nodes: { ...nodeCatalog.nodes, other: { ...nodeCatalog.nodes.device, declares: 'entity' as const } } }
  expect(() => declaredEntities({ nodes: [{ id: 'o', kind: 'other', config: {} }] }, other)).toThrow('The kind other declares an entity that no list knows how to describe')
  const otherDevice = { ...nodeCatalog, nodes: { ...nodeCatalog.nodes, other: { ...nodeCatalog.nodes.device } } }
  expect(() => declaredDevices({ nodes: [{ id: 'o', kind: 'other', config: {} }] }, otherDevice)).toThrow('The kind other declares a device that no list knows how to describe')
})

it('a definition lists its Entity nodes\' entities, then each C# entity\'s, by its key, its class\'s domain and its device, and nothing of its code', () => {
  // The graph EntityStoreTests.TheDerivedListsHoldTheCSharpNodes stores: the same C# entries and devices on both sides
  // (an Entity node's entry is a copy of its config, here with the defaults a dropped node has).
  let graph = add(empty(), 'device', 'hall', { key: 'hall', name: 'Hall' })
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'binary_sensor', name: 'lamp', options: {}, arguments: { is_on: lit('boolean', true) } })
  graph = add(graph, 'entity', 'plug', { key: 'plug', domain: 'binary_sensor', name: 'Plug sensor', options: {}, arguments: { is_on: lit('boolean', false) } })
  graph = add(graph, 'csharp_entity', 'temp', { key: 'temp' })
  graph = add(graph, 'csharp_entity', 'outside', { key: 'outside' })
  graph = add(graph, 'csharp_device', 'plug_node', { key: 'plug', name: 'Plug' })
  graph = connectData(connectData(graph, 'hall', 'entities', 'lamp', 'device'), 'hall', 'entities', 'temp', 'device')
  expect(listedEntities(graph)).toEqual([
    { key: 'lamp', domain: 'binary_sensor', name: 'lamp', options: {}, enabledByDefault: true, device: 'hall' },
    { key: 'plug', domain: 'binary_sensor', name: 'Plug sensor', options: {}, enabledByDefault: true, device: null },
    { key: 'temp', domain: 'sensor', name: null, device: 'hall', csharp: true },
    { key: 'outside', domain: 'sensor', name: null, device: null, csharp: true },
  ])
  expect(derivedDevices(graph, nodeCatalog)).toEqual([{ key: 'hall', name: 'Hall' }, { key: 'plug', name: 'Plug', csharp: true }])
  // The catalog's entities, which steps name, stay the Entity nodes'.
  expect(withGraphTypes(nodeCatalog, { ...graph, definition: { settings: [] } }).definition!.entities.map(e => e.key)).toEqual(['lamp', 'plug'])
  // csharp comes from the kind alone: a config that claims it, or another domain, is not read; an unreadable signature has no domain.
  const claimed = updateNode(graph, 'lamp', { config: { ...graph.nodes.find(n => n.id === 'lamp')!.config, csharp: true } })
  expect(Object.hasOwn(listedEntities(claimed)[0], 'csharp')).toBe(false)
  expect(csharpEntities(claimed).map(e => e.csharp)).toEqual([true, true])
  const unreadable = updateNode(graph, 'outside', { config: { ...graph.nodes.find(n => n.id === 'outside')!.config, signature: { title: 5 }, csharp: false, name: 'Mine' } })
  expect(csharpEntities(unreadable)[1]).toEqual({ key: 'outside', domain: null, name: null, device: null, csharp: true })
  for (const member of ['source', 'signature', 'arguments', 'Address', 'relay']) expect(JSON.stringify(listedEntities(graph))).not.toContain(member)
  // A key an Entity node already has is listed once, as its Entity node's.
  const twice = updateNode(graph, 'outside', { config: { ...graph.nodes.find(n => n.id === 'outside')!.config, key: 'lamp' } })
  expect(listedEntities(twice).map(e => `${e.key}:${e.csharp === true}`)).toEqual(['lamp:false', 'plug:false', 'temp:true'])
})

it('a C# entity\'s Device pin sits in its header, and a type change leaves it alone', () => {
  const graph = withCSharp()
  const thermo = graph.nodes.find(n => n.id === 'thermo')!
  const view = buildNodeView(thermo, { graph, catalog: nodeCatalog, diagnostics: [] })
  expect(view.headerInput?.pin.id).toBe('data:device')
  expect(view.headerInput?.pin.name).toBe('Device, connected to Hall')
  expect(view.rows.map(row => row.input?.pin.id)).not.toContain('data:device')
  expect(view.rows.map(row => row.input?.pin.label)).toContain('Address')
  expect([view.execIn, view.next, view.title]).toEqual([undefined, undefined, 'Thermometer'])
  // A C# device has no Device pin, and so no header pin.
  expect(buildNodeView(graph.nodes.find(n => n.id === 'plug')!, { graph, catalog: nodeCatalog, diagnostics: [] }).headerInput).toBeUndefined()
  // Its type is its code's: a type change is no change.
  const { graph: retyped, removed } = retypeNode(graph, 'thermo', 'switch', nodeCatalog)
  expect(retyped).toBe(graph)
  expect(refitNotice(removed)).toBeUndefined()
})

it('a C# entity\'s key is an entity key: a rename does not take it', () => {
  const graph = withCSharp()
  expect(renameEntityKey(graph, 'lamp', 'thermometer', 'lamp')).toBe(graph)
  expect(renameEntityKey(graph, 'lamp', 'desk', 'lamp').nodes.find(n => n.id === 'lamp')!.config.key).toBe('desk')
})

/**
 * A Plug C# device (its template, with a secret Token setting added), a Thermometer C# entity, a Setting of the
 * definition's text setting address, a Convert of it, and When started running an HTTP request whose body is text.
 */
const withSettings = () => {
  const template = nodeCatalog.nodes.csharp_device.config.signature.default as { settings: object[] }
  const signature = { ...template, settings: [...template.settings, { key: 'Token', label: 'Token', description: 'The token the plug asks for.', type: 'string', secret: true }] }
  let graph: VisualGraph = { ...empty(), definition: { settings: [{ key: 'address', type: 'string' }] } as never }
  graph = add(graph, 'csharp_device', 'plug', { key: 'plug', name: 'Plug', signature })
  graph = add(graph, 'csharp_entity', 'thermo', { key: 'thermometer' })
  graph = add(graph, 'setting', 'address', { key: 'address' })
  graph = add(graph, 'convert', 'text', { type: 'string' })
  graph = connectData(graph, 'address', 'value', 'text', 'value')
  graph = add(graph, 'entity_setup', 'start')
  graph = add(graph, 'http_request', 'http', { responseType: 'string', url: lit('string', 'http://192.168.1.50/address') })
  return connectNodes(graph, 'start', 'http')
}

it('a setting pin takes a typed value or a Setting node, and a secret only a Setting node', () => {
  const graph = withSettings()
  // The Convert step is not used until it is wired: that warning is no problem of a setting.
  const problems = (value: VisualGraph) => validateGraph(value).filter(d => d.nodeId !== 'text' || d.code !== 'pure.unused')
  expect(problems(graph)).toEqual([])
  expect(diagnosticCodes).toContain('scope.settingSource')
  expect(diagnosticCodes).toContain('setting.secretLiteral')
  // A step's output is refused, on the drop and on the hover alike, in the same words; so is a value made from a Setting.
  const runs = words.settingSource('Address')
  expect(runs).toBe('Address takes a typed value or a Setting: it is set before anything runs')
  for (const [source, port] of [['http', 'body'], ['text', 'value']]) for (const target of ['plug', 'thermo']) {
    expect(() => connectData(graph, source, port, target, 'Address'), `${source} -> ${target}`).toThrow(runs)
    expect(dataConnectionError(graph, source, port, target, 'Address', nodeCatalog, { quick: true }), `${source} -> ${target}`).toBe(runs)
    expect(dataConnectionError(graph, source, port, target, 'Address', nodeCatalog), `${source} -> ${target}`).toBe(runs)
    expect(connectionIndex(graph).verdict(source, port, target, 'Address'), `${source} -> ${target}`).toEqual({ message: runs, fallback: false })
  }
  // The validator says the same of such a wire in a graph, on the pin, as a blocking scope problem.
  const stepWired = { ...graph, edges: [...graph.edges, { id: 'w', kind: 'data' as const, source: 'http', sourcePort: 'body', target: 'plug', targetPort: 'Address' }] }
  expect(problems(stepWired)).toEqual([{ nodeId: 'plug', message: runs, severity: 3, inputId: 'Address', portId: 'Address', code: 'scope.settingSource' }])
  // A Setting is accepted, into a C# device and a C# entity alike, and leaves no problem.
  expect(dataConnectionError(graph, 'address', 'value', 'plug', 'Address', nodeCatalog, { quick: true })).toBeUndefined()
  const wired = connectData(connectData(graph, 'address', 'value', 'plug', 'Address'), 'address', 'value', 'thermo', 'Address')
  expect(problems(wired)).toEqual([])
  // A Setting in place of the step's wire is accepted: the problem it replaces is no reason to refuse it.
  expect(dataConnectionError(stepWired, 'address', 'value', 'plug', 'Address', nodeCatalog, { quick: true })).toBeUndefined()
  expect(problems(connectData(stepWired, 'address', 'value', 'plug', 'Address'))).toEqual([])
  // A typed value is accepted, and a typed secret is not, in words that do not repeat it.
  const typed = updateNode(graph, 'plug', { config: { ...graph.nodes.find(n => n.id === 'plug')!.config, arguments: { Address: lit('string', 'http://192.168.1.50'), Token: lit('string', 's3cr3t-token') } } })
  const secret = problems(typed)
  expect(secret).toEqual([{ nodeId: 'plug', message: 'Token is a secret: connect a Setting instead of typing it here', severity: 3, inputId: 'Token', code: 'setting.secretLiteral' }])
  expect(JSON.stringify(secret)).not.toContain('s3cr3t')
  expect(words.secretLiteral('Token')).toBe(secret[0].message)
  // A Setting wired in its place takes it away, though that setting is not marked secret.
  expect(dataConnectionError(typed, 'address', 'value', 'plug', 'Token', nodeCatalog, { quick: true })).toBeUndefined()
  const fixed = connectData(typed, 'address', 'value', 'plug', 'Token')
  expect(fixed.nodes.find(n => n.id === 'plug')!.config.arguments).toEqual({ Address: lit('string', 'http://192.168.1.50'), Token: { kind: 'unset' } })
  expect(problems(fixed)).toEqual([])
  // A typed secret is refused under a wire too (a document saved by hand or over MCP), and whatever its claimed type, the
  // secret sentence is all that is said of it: no message quotes it.
  const typedSecret = (operand: unknown, edges = graph.edges) => problems({ ...typed, edges, nodes: typed.nodes.map(n => n.id !== 'plug' ? n : { ...n, config: { ...n.config, arguments: { Token: operand } } }) })
  const tokenWire = { id: 'token', kind: 'data' as const, source: 'address', sourcePort: 'value', target: 'plug', targetPort: 'Token' }
  for (const [operand, edges] of [[lit('string', 's3cr3t-token'), [...graph.edges, tokenWire]], [lit('number', 1234), graph.edges], [lit('global::HassSharp.Entities.BinarySensorState', 's3cr3t-token'), graph.edges]] as const) {
    const told = typedSecret(operand, [...edges])
    expect(told, JSON.stringify(operand)).toEqual([secret[0]])
    expect(JSON.stringify(told)).not.toContain('s3cr3t')
  }
  // Dropping a Setting on such a pin unsets the typed value, and the index agrees that the wire is fine.
  const underWire = { ...typed, edges: [...graph.edges, tokenWire] }
  expect(dataConnectionError(underWire, 'address', 'value', 'plug', 'Token', nodeCatalog, { quick: true })).toBeUndefined()
  expect(problems(connectData(underWire, 'address', 'value', 'plug', 'Token'))).toEqual([])
  // A step's output is refused into a secret too: a Setting is all it takes.
  expect(dataConnectionError(typed, 'http', 'body', 'plug', 'Token', nodeCatalog, { quick: true })).toBe(words.settingSource('Token'))
  // A value of another kind than typed in or connected is what any input says of it.
  const other = updateNode(graph, 'plug', { config: { ...graph.nodes.find(n => n.id === 'plug')!.config, arguments: { Address: { kind: 'expression', code: 'Address' } } } })
  expect(problems(other)).toEqual([{ nodeId: 'plug', message: 'Address has an invalid value', severity: 3, inputId: 'Address' }])
  // A secret left unset is no problem: the class keeps what it starts with.
  expect(problems(updateNode(typed, 'plug', { config: { ...typed.nodes.find(n => n.id === 'plug')!.config, arguments: {} } }))).toEqual([])
})

it('a class node\'s settings and a Setting\'s value are setting pins, and no other pin is', () => {
  let graph = withSettings()
  graph = add(graph, 'device', 'hall', { key: 'hall', name: 'Hall' })
  graph = connectData(graph, 'hall', 'entities', 'thermo', 'device')
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const automation = createGraph('Lights')
  const view = (value: VisualGraph, id: string) => buildNodeView(value.nodes.find(n => n.id === id)!, { graph: value, catalog: nodeCatalog, diagnostics: [] })
  const pins = (value: VisualGraph, id: string) => {
    const card = view(value, id)
    return [card.execIn, card.headerInput?.pin, card.next, card.footer, ...card.rows.flatMap(row => [row.input?.pin, row.output])].filter(pin => pin !== undefined)
  }
  const settingPins = (value: VisualGraph, id: string) => pins(value, id).filter(pin => pin.setting).map(pin => `${pin.side} ${pin.id}`)
  // The C# device's settings; the C# entity's settings, and not its Device pin; the Setting's value.
  expect(settingPins(graph, 'plug')).toEqual(['in data:Address', 'in data:Token'])
  expect(settingPins(graph, 'thermo')).toEqual(['in data:Address'])
  expect(view(graph, 'thermo').headerInput?.pin.setting).toBeFalsy()
  expect(settingPins(graph, 'address')).toEqual(['out data:value'])
  // No pin of a value node, a step, a root, a Device node, an Entity node or a trigger.
  for (const id of ['text', 'http', 'start', 'hall', 'lamp']) {
    expect(pins(graph, id).length, id).toBeGreaterThan(0)
    expect(settingPins(graph, id), id).toEqual([])
  }
  expect(pins(automation, 'trigger-1').length).toBeGreaterThan(0)
  for (const node of automation.nodes) expect(settingPins(automation, node.id), node.id).toEqual([])
  // A setting pin says what it takes, after what any input says; a Setting's value says where else it goes.
  const address = pins(graph, 'plug').find(pin => pin.id === 'data:Address')!
  expect(address.name).toBe('Input Address, Text, not connected. Takes a typed value or a Setting node')
  expect(pins(graph, 'address').find(pin => pin.side === 'out')!.name).toBe('Output Value, Text, connected. Can also set a setting of a C# entity or C# device')
  expect(pins(graph, 'text').find(pin => pin.side === 'in')!.name).toBe('Input Value, Text, required, connected to Setting · Value')
})

it('auto-connect offers no step for a setting pin but a Setting, and no class node for a step\'s value, and a paste does not bring such a wire', () => {
  const graph = withSettings()
  const setting = { nodeId: 'plug', handle: 'data:Address', type: 'target' as const }
  const convert = { nodeId: 'text', handle: 'data:value', type: 'source' as const }
  // From a setting pin: a value node whose output fits is not offered, and adding it anyway makes no wire.
  expect(canAddFor(graph, 'convert', setting, nodeCatalog)).toBe(false)
  expect(addConnected(graph, 'convert', { x: 0, y: 0 }, setting, nodeCatalog, 'new')).toBeUndefined()
  // From a value node's output: a C# device or a C# entity is not offered (its settings take no such wire).
  for (const kind of ['csharp_device', 'csharp_entity']) {
    expect(canAddFor(graph, kind, convert, nodeCatalog), kind).toBe(false)
    expect(addConnected(graph, kind, { x: 0, y: 0 }, convert, nodeCatalog, 'new'), kind).toBeUndefined()
  }
  // From a Setting's output a C# device is offered, and its setting gets the wire.
  const fromSetting = addConnected(graph, 'csharp_device', { x: 0, y: 0 }, { nodeId: 'address', handle: 'data:value', type: 'source' }, nodeCatalog, 'new')
  expect(fromSetting?.graph.edges.at(-1)).toMatchObject({ kind: 'data', source: 'address', target: 'new', targetPort: 'Address' })
  // A paste of steps with a wire a setting does not take is refused, in the validator's words; one with a Setting's wire is not.
  const stepWired = { ...graph, edges: [...graph.edges, { id: 'w', kind: 'data' as const, source: 'text', sourcePort: 'value', target: 'plug', targetPort: 'Address' }] }
  expect(pasteSteps(graph, copySteps(stepWired, ['text', 'plug'])!, nodeCatalog, { offset: { x: 40, y: 40 } })).toEqual({ error: words.settingSource('Address') })
  const settingWired = connectData(graph, 'address', 'value', 'plug', 'Address')
  const pasted = pasteSteps(graph, copySteps(settingWired, ['address', 'plug'])!, nodeCatalog, { offset: { x: 40, y: 40 } })
  expect('graph' in pasted && pasted.graph.edges.length).toBe(graph.edges.length + 1)
})

it('deleting a Device node asks nothing', () => {
  // Hub (hub), Hall plug (plug, part of hub) with a nameless relay on it, and a lamp a Set state step uses.
  let graph = add(empty(), 'device', 'hub', { key: 'hub', name: 'Hub' })
  graph = add(graph, 'device', 'plug', { key: 'plug', name: 'Hall plug', via: { device: 'hub' } })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: '' })
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'switch', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp' })
  graph = connectData(connectData(graph, 'plug', 'entities', 'relay', 'device'), 'hub', 'entities', 'lamp', 'device')
  expect(entityRemoval(graph, ['plug'], nodeCatalog)).toBeUndefined()
  expect(entityRemoval(graph, ['plug', 'hub'], nodeCatalog)).toBeUndefined()
  // With an Entity node a step uses, the question about that entity, in the same words.
  expect(entityRemoval(graph, ['plug', 'hub', 'lamp'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
  // Deleted, its wires go with it and its entities stand alone: a nameless one now needs its name.
  const withoutPlug = removeNode(graph, 'plug', nodeCatalog)
  expect(withoutPlug.edges.filter(e => e.source === 'plug' || e.target === 'plug')).toEqual([])
  expect(withoutPlug.nodes.map(n => n.id)).toEqual(['hub', 'relay', 'lamp', 'set'])
  expect(deviceNodeOf(withoutPlug, 'relay')).toBeUndefined()
  expect(validateGraph(withoutPlug).filter(d => d.nodeId === 'relay' && d.severity === 3).map(d => d.code)).toEqual(['entity.name'])
  // A device that was part of a deleted one says so.
  const withoutHub = removeNode(graph, 'hub', nodeCatalog)
  expect(deviceNodeOf(withoutHub, 'lamp')).toBeUndefined()
  expect(validateGraph(withoutHub).filter(d => d.nodeId === 'plug' && d.severity === 3).map(d => d.code)).toEqual(['device.viaUnknown'])
})

it('a Device wire is not walked upstream of an entity, and makes no run of it a state flow\'s', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
  graph = add(graph, 'entity', 'door', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'entity_is', 'e', { entity: lit('entity_id', 'binary_sensor.door'), state: lit('string', 'on') })
  graph = add(graph, 'entity_is', 'x', { entity: lit('entity_id', 'binary_sensor.stray'), state: lit('string', 'on') })
  graph = connectData(connectData(graph, 'plug', 'entities', 'door', 'device'), 'e', 'value', 'door', 'is_on')
  const upstream = (g: typeof graph) => {
    const nodes = new Map(g.nodes.map(n => [n.id, n]))
    const data = g.edges.flatMap(e => e.kind === 'data' ? [e] : [])
    return valueUpstream(nodes.get('door')!, nodes, data, n => isPure(n), n => dataInputs(n)).map(n => n.id)
  }
  // What is upstream of the entity is its value's only: the Device node is not walked.
  expect(upstream(graph)).toEqual(['e'])
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
  // Nor is a value wired into the Device pin (which the validators refuse).
  const stray = { ...graph, edges: [...graph.edges.filter(e => !(e.kind === 'data' && e.targetPort === 'device')), { id: 'x.value:door.device', kind: 'data' as const, source: 'x', sourcePort: 'value', target: 'door', targetPort: 'device' }] }
  expect(upstream(stray)).toEqual(['e'])
  // A Device node is no root: the run names are the entity's flows, and none is the device's.
  const names = definitionNames(graph, nodeCatalog)!
  expect(Object.values(names.roots!).filter(name => name.startsWith('Hall plug'))).toEqual([])
  expect(Object.values(names.roots!)).toContain('Door: State')
  expect(names.entities!.map(e => e.key)).toEqual(['door'])
})

it('wiring an entity to a device takes away its need for a name', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: ' ' })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'binary_sensor', name: '' })
  const nameless = { nodeId: 'relay', message: 'Name this entity: it has no device, so Home Assistant shows it by its own name', severity: 3, inputId: 'name', code: 'entity.name' }
  expect(validateGraph(graph).filter(d => d.nodeId === 'relay')).toEqual([nameless])
  // A wire is refused only for a problem it makes: the Device node's own (it has no name yet) is no reason, and the index agrees.
  expect(validateGraph(graph).filter(d => d.nodeId === 'plug').map(d => d.code)).toEqual(['device.name', 'device.empty'])
  expect(dataConnectionError(graph, 'plug', 'entities', 'relay', 'device', nodeCatalog, { quick: true })).toBeUndefined()
  expect(dataConnectionError(graph, 'plug', 'entities', 'relay', 'device', nodeCatalog)).toBeUndefined()
  const wired = connectData(graph, 'plug', 'entities', 'relay', 'device')
  expect(validateGraph(wired).filter(d => d.nodeId === 'relay')).toEqual([])
  expect(validateGraph(wired).filter(d => d.nodeId === 'plug').map(d => d.code)).toEqual(['device.name'])
  // Moved to another device, it leaves the first with no entities: a warning about that device, not a problem the wire makes.
  const desk = add(wired, 'device', 'desk', { key: 'desk', name: 'Desk' })
  expect(dataConnectionError(desk, 'desk', 'entities', 'relay', 'device', nodeCatalog, { quick: true })).toBeUndefined()
  const moved = connectData(desk, 'desk', 'entities', 'relay', 'device')
  expect(validateGraph(moved).filter(d => d.code === 'device.empty').map(d => d.nodeId)).toEqual(['plug'])
  expect(validateGraph(moved).filter(d => d.nodeId === 'relay')).toEqual([])
  // Without the wire it stands alone and needs its name again.
  expect(validateGraph(disconnectData(moved, 'relay', 'device')).filter(d => d.nodeId === 'relay')).toEqual([nameless])
})

it('a wire refused for its type onto a nameless entity\'s wired Device pin is refused for that alone', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'binary_sensor', name: '' })
  graph = add(graph, 'camera_frame', 'snap', { entity: lit('entity_id:camera', 'camera.front') })
  graph = connectData(graph, 'plug', 'entities', 'relay', 'device')
  expect(validateGraph(graph).filter(d => d.nodeId === 'relay' || d.nodeId === 'plug')).toEqual([])
  // An image is no device. Dropped on the Device pin, the wire would also leave the entity with no device, and so in
  // need of a name; that is not the wire's to report, as its device's having no entities is not.
  const typeOnly = 'Device accepts Device, not Image'
  expect(dataConnectionError(graph, 'snap', 'image', 'relay', 'device', nodeCatalog, { quick: true })).toBe(typeOnly)
  expect(dataConnectionError(graph, 'snap', 'image', 'relay', 'device', nodeCatalog)).toBe(typeOnly)
  expect(() => connectData(graph, 'snap', 'image', 'relay', 'device')).toThrow(new Error(typeOnly))
  // The mirror: another Device node's Entities pin on the already wired pin replaces the wire, and both accept it.
  const desk = add(graph, 'device', 'desk', { key: 'desk', name: 'Desk' })
  expect(dataConnectionError(desk, 'desk', 'entities', 'relay', 'device', nodeCatalog, { quick: true })).toBeUndefined()
  expect(dataConnectionError(desk, 'desk', 'entities', 'relay', 'device', nodeCatalog)).toBeUndefined()
  const moved = connectData(desk, 'desk', 'entities', 'relay', 'device')
  // Still one device for the entity: the new wire took the old one's place.
  expect(moved.edges.filter(e => e.kind === 'data' && e.target === 'relay' && e.targetPort === 'device').map(e => e.kind === 'data' && e.source)).toEqual(['desk'])
  expect(deviceNodeOf(moved, 'relay')).toBe('desk')
  expect(validateGraph(moved).filter(d => d.nodeId === 'relay' || d.nodeId === 'desk')).toEqual([])
})

it('a definition\'s devices are its Device nodes, and an entity is on the one wired to it', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug', model: 'P1' })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' })
  graph = add(graph, 'entity', 'power', { key: 'power', domain: 'sensor', name: 'Power' })
  graph = add(graph, 'device', 'hub', { key: 'hub', name: 'Hub', via: { deviceId: 'zigbee' } })
  graph = add(graph, 'entity', 'bare', { key: 'bare', domain: 'switch', name: 'Bare' })
  graph = add(graph, 'entity', 'stray', { key: 'stray', domain: 'binary_sensor', name: 'Stray' })
  graph = add(graph, 'device', 'keyless', { key: '', name: 'Keyless' })
  graph = connectData(connectData(graph, 'plug', 'entities', 'relay', 'device'), 'plug', 'entities', 'power', 'device')
  // A wire into the Device pin from a node that is not a Device node (which the validators refuse).
  graph = add(graph, 'constant', 'c', { type: 'string', value: lit('string', 'plug') })
  graph = { ...graph, edges: [...graph.edges, { id: 'c.value:stray.device', kind: 'data', source: 'c', sourcePort: 'value', target: 'stray', targetPort: 'device' }] }
  const devices = declaredDevices(graph)
  expect(devices.map(d => d.nodeId)).toEqual(['plug', 'hub', 'keyless'])
  expect(devices[0].device).toMatchObject({ key: 'plug', name: 'Hall plug', model: 'P1' })
  expect(devices[1].device.via).toEqual({ deviceId: 'zigbee' })
  expect(devices[0].device).not.toBe(graph.nodes[0].config)
  expect(definitionDeviceKeys(graph)).toEqual(['plug', 'hub'])
  expect([deviceNodeOf(graph, 'relay'), deviceNodeOf(graph, 'power')]).toEqual(['plug', 'plug'])
  expect([deviceNodeOf(graph, 'bare'), deviceNodeOf(graph, 'stray'), deviceNodeOf(graph, 'missing'), deviceNodeOf(graph, 'plug')]).toEqual([undefined, undefined, undefined, undefined])
  expect(declaredDevices({ nodes: 3 })).toEqual([])

  // The graph's lifted definition lists its devices, and each entity the key of the device it is on, as the server stores them.
  const lifted = withGraphTypes(nodeCatalog, graph).definition!
  expect(lifted.devices).toEqual([{ key: 'plug', name: 'Hall plug', model: 'P1' }, { key: 'hub', name: 'Hub', via: { deviceId: 'zigbee' } }])
  expect(derivedDevices(graph, nodeCatalog)).toEqual(lifted.devices)
  expect(lifted.devices[0]).not.toBe(graph.nodes[0].config)
  expect(lifted.entities.map(e => [e.key, e.device])).toEqual([['relay', 'plug'], ['power', 'plug'], ['bare', null], ['stray', null]])
  expect(lifted.entities.every(e => Object.hasOwn(e, 'device'))).toBe(true)
  // The canvas's lifted definition alike.
  const canvas = definitionCatalog(nodeCatalog, graph).definition!
  expect([canvas.devices, canvas.entities]).toEqual([lifted.devices, lifted.entities])
})

it('a Device node\'s device is the members a device has, and only the C# device kind makes a C# device', () => {
  // Every member the Device kind's config has, as a full Device node gives them: its device is all of them, as before.
  const full = { key: 'hall', name: 'Hall plug', manufacturer: 'Acme', model: 'P1', swVersion: '1.2', hwVersion: 'A', configurationUrl: 'http://192.168.1.50', suggestedArea: 'Hall', via: { device: 'hub' } }
  expect(Object.keys(full)).toEqual(Object.keys(nodeCatalog.nodes.device.config ?? {}))
  // A config that says csharp (an MCP save, or a copy of a read answer's devices) and has a member no device has.
  let graph = add(empty(), 'csharp_device', 'plug', { key: 'plug', name: 'Plug', via: null })
  graph = add(graph, 'device', 'stray', { key: 'stray', name: 'Stray', csharp: true, colour: 'red' })
  graph = add(graph, 'device', 'hall', full)
  graph = add(graph, 'device', 'hub', { key: 'hub', name: 'Hub' })
  const declared = Object.fromEntries(declaredDevices(graph).map(d => [d.nodeId, d.device]))
  expect(declared.stray).toEqual({ key: 'stray', name: 'Stray' })
  expect(declared.hall).toEqual(full)
  expect(declared.plug).toEqual({ key: 'plug', name: 'Plug', via: null, csharp: true })
  // Listed among the Device nodes, in node order, and the C# device after them.
  expect(derivedDevices(graph, nodeCatalog)).toEqual([{ key: 'stray', name: 'Stray' }, full, { key: 'hub', name: 'Hub' }, { key: 'plug', name: 'Plug', csharp: true }])
  expect(withGraphTypes(nodeCatalog, graph).definition!.devices).toEqual(derivedDevices(graph, nodeCatalog))
  // A config out of the catalog's order keeps its own: the generated source is the one a copy of the whole config gave.
  const unordered = { name: 'Hall plug', suggestedArea: 'Hall', key: 'plug', via: { deviceId: 'zigbee' }, colour: 'red', model: 'P1', manufacturer: 'Acme' }
  const out = { ...empty(), nodes: [{ id: 'plug', kind: 'device', config: unordered }] }
  expect(Object.keys(declaredDevices(out)[0].device)).toEqual(['name', 'suggestedArea', 'key', 'via', 'model', 'manufacturer'])
  expect(Object.keys(derivedDevices(out, nodeCatalog)[0])).toEqual(['name', 'suggestedArea', 'key', 'via', 'model', 'manufacturer'])
})

it('a text left blank and Part of nothing are no part of a listed device, and a device without a key is none', () => {
  let graph = add(empty(), 'device', 'a', { key: 'desk', name: 'Desk', manufacturer: ' \t ', model: '', via: null })
  graph = add(graph, 'device', 'b', { key: '  ', name: 'Blank' })
  graph = add(graph, 'device', 'c', { key: 5, name: 'Number' })
  graph = add(graph, 'device', 'd', { name: 'None' })
  graph = add(graph, 'device', 'e', { key: 'desk', name: 'Second desk', via: { device: 'desk' } })
  for (const id of ['on_blank', 'on_number', 'on_second']) graph = add(graph, 'entity', id, { key: id, domain: 'switch', name: id })
  graph = { ...graph, nodes: graph.nodes.map(n => n.id === 'd' ? { ...n, config: { name: 'None' } } : n) }
  graph = connectData(connectData(connectData(graph, 'b', 'entities', 'on_blank', 'device'), 'c', 'entities', 'on_number', 'device'), 'e', 'entities', 'on_second', 'device')
  const lifted = withGraphTypes(nodeCatalog, graph).definition!
  // Two of one key are both listed: the validator refuses the second.
  expect(lifted.devices).toEqual([{ key: 'desk', name: 'Desk' }, { key: 'desk', name: 'Second desk', via: { device: 'desk' } }])
  expect(lifted.entities.map(e => [e.key, e.device])).toEqual([['on_blank', null], ['on_number', null], ['on_second', 'desk']])
  expect(derivedDevices({ nodes: 3, definition: { settings: [] } }, nodeCatalog)).toEqual([])
  // An automation's graph has no definition, so nothing is lifted.
  const { definition: _definition, ...automation } = graph
  expect(withGraphTypes(nodeCatalog, automation).definition).toBeUndefined()
})

it('a device\'s problems are on its node: a circle of Part of on each of its devices, no entities on the node as a whole', () => {
  let graph = add(empty(), 'device', 'plug', { key: 'plug', name: 'Hall plug', via: { device: 'hub' } })
  graph = add(graph, 'device', 'hub', { key: 'hub', name: 'Hub', via: { device: 'plug' } })
  graph = add(graph, 'device', 'desk', { key: ' desk ', name: ' ' })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' })
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'switch', name: 'Lamp' })
  graph = connectData(connectData(graph, 'plug', 'entities', 'relay', 'device'), 'hub', 'entities', 'lamp', 'device')
  const found = validateGraph(graph).filter(d => d.code === 'device.viaCycle' || d.code === 'device.empty')
  expect(found.map(d => `${d.code}@${d.nodeId}:${d.inputId ?? ''}:${d.portId ?? ''}:${d.severity}`)).toEqual(['device.viaCycle@plug:via::3', 'device.viaCycle@hub:via::3', 'device.empty@desk:::2'])
  // A device without a name is named by its key, trimmed, else Device.
  expect(found[2].message).toBe('desk has no entities: connect its Entities pin to an entity\'s Device pin')
  expect(words.stepName({ id: 'd', kind: 'device', config: { key: '', name: '' } })).toBe('Device')
  for (const code of ['definition.deviceKey', 'definition.duplicateDevice', 'device.name', 'device.via', 'device.viaUnknown', 'device.viaCycle', 'device.configurationUrl', 'device.empty'])
    expect(diagnosticCodes as readonly string[]).toContain(code)
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
  expect(graph.definition).toEqual({ settings: [] })
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
  // Its Device pin, then its state values.
  expect(Object.keys(dataInputs(light))).toEqual(['device', 'is_on', 'brightness'])
  expect(Object.keys(stateInputs(light, nodeCatalog))).toEqual(['is_on', 'brightness'])
  expect(Object.values(dataInputs(light)).every(input => input.required === false)).toBe(true)
  expect(executionPorts(light)).toEqual(['command:turn_on', 'command:turn_off'])
  expect(executionLabel(light, 'command:turn_on')).toBe('Turn on')
  expect(dataOutputs(light)['command:turn_on:brightness']).toMatchObject({ pointer: '/data/brightness', group: 'command:turn_on' })
  // A camera has no execution pin: what it shows comes into its Image, a picture input that is no state value.
  const camera = add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera' }).nodes[0]
  expect(executionPorts(camera)).toEqual([])
  expect(Object.keys(dataInputs(camera))).toEqual(['device', 'image'])
  expect(dataInputs(camera).image).toMatchObject({ types: ['image'], required: false, label: 'Image', picture: true })
  expect(stateInputs(camera, nodeCatalog)).toEqual({})
  expect(Object.keys(stateInputs({ ...light, config: { ...light.config, key: '' } }, nodeCatalog))).toEqual(['is_on', 'brightness'])
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
  // A command's value says which command it belongs to, to a screen reader; its visible label stays its own.
  const view = buildNodeView(graph.nodes.find(n => n.id === 'l')!, { graph, catalog: nodeCatalog, diagnostics: [] })
  const brightness = view.rows.map(r => r.output).find(pin => pin?.id.endsWith('command:turn_on:brightness'))!
  expect(brightness.label).toBe('Brightness')
  expect(brightness.name).toMatch(/^Output Turn on: Brightness, /)
})

it('a camera\'s card shows its Image input on the left, and no pin on the right', () => {
  let graph = add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera', name: 'Door' })
  expect(rightColumn(graph, 'c')).toEqual([])
  let view = buildNodeView(graph.nodes[0], { graph, catalog: nodeCatalog, diagnostics: [] })
  // Its Device pin is in the header; its Image is the one row, an input that takes a wire.
  expect(view.headerInput?.port).toBe('device')
  expect(view.rows.map(r => [r.input?.port, r.output?.id])).toEqual([['image', undefined]])
  const image = view.rows[0].input!
  expect(image.pin).toMatchObject({ id: 'data:image', side: 'in', label: 'Image', typeId: 'image', connected: false })
  expect(image.pin.name).toBe('Input Image, Image, not connected')
  // A Camera frame connected into it is its source.
  graph = add(graph, 'camera_frame', 'take', { entity: lit('entity_id:camera', 'camera.front') })
  graph = connectData(graph, 'take', 'image', 'c', 'image')
  view = buildNodeView(graph.nodes[0], { graph, catalog: nodeCatalog, diagnostics: [] })
  expect(view.rows[0].input).toMatchObject({ mode: 'connected', source: 'Camera frame · Frame', sourceNode: 'take' })
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
  // A camera's frames run in a flow with no pin, named as the server names it.
  expect(runName(flowMethod(cameras.nodes[0], 'frame'), named)).toBe('Front door: When a frame is needed')
  expect(runName(flowMethod(cameras.nodes[1], 'frame'), named)).toBe('yard: When a frame is needed')
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
  for (const kind of ['entity_state', 'entity_command', 'camera_snapshot', 'show_frame']) expect(nodeCatalog.nodes).not.toHaveProperty(kind)
  expect(catalogPalette(nodeCatalog, 'entity').map(item => item.kind)).not.toContain('show_frame')
  const entity = catalogPalette(nodeCatalog, 'entity').filter(item => item.category === 'Entity').map(item => item.kind)
  expect(entity.slice(0, 6)).toEqual(['entity', 'device', 'csharp_entity', 'csharp_device', 'entity_setup', 'set_entity_state'])
  expect(diagnosticCodes).not.toContain('state.duplicate')
})

it('deleting Entity nodes says how many steps use them, and "these entities" only when more than one is used', () => {
  // Lamp (lamp) has two Set state steps, Desk (desk) one, Hall (hall) none.
  let graph = add(add(add(empty(), 'entity', 'l', { key: 'lamp', domain: 'switch', name: 'Lamp' }), 'entity', 'd', { key: 'desk', domain: 'switch', name: 'Desk' }), 'entity', 'h', { key: 'hall', domain: 'switch', name: 'Hall' })
  graph = add(add(add(graph, 'set_entity_state', 's1', { entity: 'lamp' }), 'set_entity_state', 's2', { entity: 'lamp' }), 'set_entity_state', 's3', { entity: 'desk' })
  expect(entityRemoval(graph, ['d'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
  expect(entityRemoval(graph, ['l'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '2 steps use this entity. They will show a problem until you change or remove them.' })
  // One used among several deleted.
  expect(entityRemoval(graph, ['d', 'h'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
  expect(entityRemoval(graph, ['l', 'h'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '2 steps use this entity. They will show a problem until you change or remove them.' })
  // Both used.
  expect(entityRemoval(graph, ['l', 'd', 'h'], nodeCatalog)).toEqual({ title: 'Delete these entities?', body: '3 steps use these entities. They will show a problem until you change or remove them.' })
  expect(entityRemoval(graph, ['l', 'd', 's1', 's2'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
  expect(entityRemoval(graph, ['h'], nodeCatalog)).toBeUndefined()
})

/** A saved definition's C# device Plug (key `device`, node `cam`) and C# entity Thermometer (key `sensor`, node `count`). */
const csharpNodes = () => addGraphNode(addGraphNode(empty(), 'csharp_device', 'cam', nodeCatalog), 'csharp_entity', 'count', nodeCatalog)
const storedQuestion = (name: string, key: string) => ({ title: 'Delete its stored data too?', body: `${name} has stored data. It is deleted when you save the entity definition without a node keyed ${key}.` })

it('removing a C# node with stored data asks first', () => {
  const saved = csharpNodes()
  expect(entityRemoval(saved, ['cam'], nodeCatalog, { saved, keys: ['device'] })).toEqual(storedQuestion('Plug', 'device'))
  expect(entityRemoval(saved, ['count'], nodeCatalog, { saved, keys: ['device', 'sensor'] })).toEqual(storedQuestion('Thermometer', 'sensor'))
  // Named as its card is now; its stored data is by the key it was saved with.
  const renamed = updateNode(saved, 'cam', { config: { ...saved.nodes[0].config, name: 'Driveway camera' } })
  expect(entityRemoval(renamed, ['cam'], nodeCatalog, { saved, keys: ['device'] })).toEqual(storedQuestion('Driveway camera', 'device'))
  // Both at once.
  expect(entityRemoval(saved, ['cam', 'count'], nodeCatalog, { saved, keys: ['device', 'sensor'] })).toEqual({
    title: 'Delete its stored data too?', body: 'Plug and Thermometer have stored data. It is deleted when you save the entity definition without nodes keyed device and sensor.',
  })
})

it('removing a C# node without stored data does not ask', () => {
  const saved = csharpNodes()
  expect(entityRemoval(saved, ['cam'], nodeCatalog, { saved, keys: ['sensor'] })).toBeUndefined()
  expect(entityRemoval(saved, ['cam', 'count'], nodeCatalog, { saved, keys: [] })).toBeUndefined()
  // Without what is stored (not read yet, or a definition never saved), it asks only what it asked before.
  expect(entityRemoval(saved, ['cam'], nodeCatalog)).toBeUndefined()
  // A node added since the last save has none, whatever key it was given.
  const added = addGraphNode(csharpNodes(), 'csharp_device', 'new', nodeCatalog)
  const keyed = updateNode(added, 'new', { config: { ...added.nodes.at(-1)!.config, key: 'garage' } })
  expect(entityRemoval(keyed, ['new'], nodeCatalog, { saved, keys: ['device', 'garage'] })).toBeUndefined()
  // A step of another kind with a key of stored data is no C# node.
  const lamp = add(saved, 'entity', 'lamp', { key: 'lamp', domain: 'switch', name: 'Lamp' })
  expect(entityRemoval(lamp, ['lamp'], nodeCatalog, { saved: lamp, keys: ['lamp'] })).toBeUndefined()
})

it('removing a library class node with stored data asks as for a C# node, and says its data stays with a node of its key', () => {
  const catalog = withLibrary(), saved = relaysGraph()
  expect(entityRemoval(saved, ['hub'], catalog, { saved, keys: ['hub'] })).toEqual(storedQuestion('Hub', 'hub'))
  expect(entityRemoval(saved, ['relay'], catalog, { saved, keys: ['relay'] })).toEqual(storedQuestion('Relay', 'relay'))
  // Its data is by the key it was saved with, whatever key it has now.
  const hub = saved.nodes.find(n => n.id === 'hub')!
  const rekeyed = updateNode(saved, 'hub', { config: { ...hub.config, key: 'bridge', name: 'Bridge' } })
  expect(entityRemoval(rekeyed, ['hub'], catalog, { saved, keys: ['hub'] })).toEqual(storedQuestion('Bridge', 'hub'))
  // None stored, or a node added since: nothing to ask.
  expect(entityRemoval(saved, ['hub'], catalog, { saved, keys: ['relay'] })).toBeUndefined()
  const added = addGraphNode(saved, hubKind, 'more', catalog)
  expect(entityRemoval(added, ['more'], catalog, { saved, keys: ['relay_hub'] })).toBeUndefined()
  // A C# device and a library one at once.
  const both = addGraphNode(saved, 'csharp_device', 'cam', catalog)
  expect(entityRemoval(both, ['cam', 'hub'], catalog, { saved: both, keys: ['device', 'hub'] })).toEqual({
    title: 'Delete its stored data too?', body: 'Hub and Plug have stored data. It is deleted when you save the entity definition without nodes keyed hub and device.',
  })
})

it('removing a node that steps still use and a C# node that has data shows both reasons', () => {
  let saved = add(csharpNodes(), 'entity', 'lamp', { key: 'lamp', domain: 'switch', name: 'Lamp' })
  saved = add(saved, 'set_entity_state', 'set', { entity: 'lamp' })
  expect(entityRemoval(saved, ['lamp', 'cam'], nodeCatalog, { saved, keys: ['device'] })).toEqual({
    title: 'Delete its stored data too?',
    body: 'Plug has stored data. It is deleted when you save the entity definition without a node keyed device. 1 step uses this entity. It will show a problem until you change or remove it.',
  })
  // Each reason alone, as before.
  expect(entityRemoval(saved, ['lamp'], nodeCatalog, { saved, keys: ['device'] })).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
  expect(entityRemoval(saved, ['lamp', 'cam'], nodeCatalog)).toEqual({ title: 'Delete this entity?', body: '1 step uses this entity. It will show a problem until you change or remove it.' })
})

it('a name of spaces is no name: the entity has none, and its runs and commands are named by its key', () => {
  const graph = add(empty(), 'entity', 'p', { key: 'power', domain: 'switch', name: ' \t ' })
  const [declared] = declaredEntities(graph, nodeCatalog)
  expect(declared.entity).not.toHaveProperty('name')
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities[0]).not.toHaveProperty('name')
  const names = definitionNames(graph, nodeCatalog)!
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_on'), names)).toBe('power: Turn on')
  expect(summarizeTrigger({ kind: 'command', entity: 'power', command: 'turn_on' }, names)).toBe('power: Turn on')
  // A name with text is kept as given; a name that is not text is the validator's to refuse.
  expect(declaredEntities(add(empty(), 'entity', 'p', { key: 'power', domain: 'switch', name: ' Power ' }), nodeCatalog)[0].entity.name).toBe(' Power ')
  expect(declaredEntities(add(empty(), 'entity', 'p', { key: 'power', domain: 'switch', name: 5 }), nodeCatalog)[0].entity.name).toBe(5)
})
