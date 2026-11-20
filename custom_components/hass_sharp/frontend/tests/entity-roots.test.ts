import { expect, it } from 'vitest'
import { addGraphNode, connectData, dataInputs, dataOutputs, executionLabel, executionPorts, nodeCatalog, stateInputs, validateGraph, withGraphTypes, type Graph } from '../src/visual/contract'
import { addNode, canConnect, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { addConnected, canAddFor } from '../src/visual/flow/autoConnect'
import { tidyLayout } from '../src/visual/layout'
import { add, empty, lit } from './visual-fixture'

// The roots of an entity definition's graph start a flow as a trigger does. An Entity node is one: each command pin on
// its right runs the steps connected to it, its state is worked out from its value inputs on the left, and nothing
// runs into it. When started runs the steps after it.
const relay = () => add(empty('Relay'), 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' })
const view = (graph: Graph, id: string) => buildNodeView(graph.nodes.find(n => n.id === id)!, { graph, catalog: nodeCatalog, diagnostics: [] })
/** The labels of a card's right column, top to bottom. */
const outputs = (graph: Graph, id: string) => view(graph, id).rows.flatMap(r => (r.output ? [r.output.label] : []))
/** Turn on of the relay → Read entity, When started, the relay's On fed by Has state, a door that runs nothing, and a loose Log. */
const roots = () => {
  let graph = add(relay(), 'read_entity', 'read', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'entity_setup', 'setup')
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'entity', 'door', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'log', 'log')
  graph = connectNodes(graph, 'relay', 'read', 'command:turn_on')
  return connectData(graph, 'is', 'value', 'relay', 'is_on')
}

it('no step order wire can be drawn into an Entity node or another root of an entity graph', () => {
  // The door runs nothing: a wire from Read entity into it would make no cycle, so only the root guard refuses it
  // (one into the relay, whose Turn on runs Read entity, would also be a cycle).
  const graph = roots()
  expect(graph.edges.some(e => e.kind !== 'data' && e.source === 'relay' && e.target === 'read' && e.port === 'command:turn_on')).toBe(true)
  expect(graph.edges.some(e => e.source === 'door' || e.target === 'door')).toBe(false)
  for (const target of ['door', 'relay', 'setup']) {
    expect(canConnect(graph, 'read', target), target).toBe(false)
    expect(connectNodes(graph, 'read', target), target).toBe(graph)
  }
  // A command pin runs one flow: Turn on is taken, Turn off is free; the node has no `next`, and a binary sensor no
  // command, so no execution output at all. The steps that start flows still run what comes after them.
  expect(canConnect(graph, 'relay', 'log', 'command:turn_on')).toBe(false)
  expect(canConnect(graph, 'relay', 'log', 'command:turn_off')).toBe(true)
  expect(canConnect(graph, 'relay', 'log', 'next')).toBe(false)
  expect(executionPorts(graph.nodes.find(n => n.id === 'door')!)).toEqual([])
  expect(canConnect(graph, 'door', 'log')).toBe(false)
  expect(canConnect(graph, 'setup', 'log')).toBe(true)
  expect(canConnect(graph, 'read', 'log')).toBe(true)
})

it('the roots of an entity graph have no execution input pin, and an Entity node one execution output for each command', () => {
  const graph = roots()
  for (const id of ['relay', 'door', 'setup']) {
    expect(view(graph, id).execIn, id).toBeUndefined()
    expect(view(graph, id).unreachable, id).toBe(false)
  }
  expect(view(graph, 'setup').next?.id).toBe('next')
  // The node's header has no `next`: its execution outputs are its commands, in the right column.
  expect(view(graph, 'relay').next).toBeUndefined()
  expect(view(graph, 'relay').footer).toBeUndefined()
  expect(view(graph, 'relay').rows.flatMap(r => (r.output?.kind === 'exec' ? [r.output.id] : []))).toEqual(['command:turn_on', 'command:turn_off'])
  expect(outputs(graph, 'relay')).toEqual(['Turn on', 'Turn off'])
  // Its state values in the rows, level with its commands; its Device pin in the header.
  expect(view(graph, 'relay').rows.flatMap(r => (r.input ? [r.input.port] : []))).toEqual(['is_on'])
  expect(view(graph, 'relay').rows[0]).toMatchObject({ input: { port: 'is_on' }, output: { id: 'command:turn_on' } })
  expect(view(graph, 'relay').headerInput?.pin.id).toBe('data:device')
  expect(view(graph, 'door').headerInput?.pin.id).toBe('data:device')
  // Only a root has one: When started has no Device pin, and a step keeps its execution input there.
  expect(view(graph, 'setup').headerInput).toBeUndefined()
  expect(view(graph, 'read').headerInput).toBeUndefined()
  // A binary sensor has its state and no command: no execution pin at all.
  expect(view(graph, 'door').next).toBeUndefined()
  expect(view(graph, 'door').rows.some(r => r.output)).toBe(false)
  // A step a command runs runs; a step no root reaches does not.
  expect(view(graph, 'read').execIn?.id).toBe('in')
  expect(view(graph, 'read').unreachable).toBe(false)
  expect(view(graph, 'log').unreachable).toBe(true)
})

it('a step added from the Device pin can only be a Device node, which is connected and never asked into the flow', () => {
  const graph = roots()
  const pin = { nodeId: 'relay', handle: 'data:device', type: 'target' as const }
  // Nothing that gives a value can go into the Device pin: no Constant, no Convert, no step.
  const offered = Object.keys(nodeCatalog.nodes).filter(kind => canAddFor(graph, kind, pin, nodeCatalog))
  expect(offered).toEqual(['device'])
  const added = addConnected(graph, 'device', { x: 0, y: 0 }, pin, nodeCatalog, 'plug')!
  expect(added.graph.edges.filter(e => e.kind === 'data' && e.target === 'relay' && e.targetPort === 'device').map(e => e.source)).toEqual(['plug'])
  // A Device node never runs: the notice does not ask for it to be connected into the flow.
  expect(added.notice).toBe('Added Device and connected Device')
  expect(added.error).toBeUndefined()
})

it('auto-connect never wires into an Entity node or another root of an entity graph', () => {
  const graph = roots()
  const fromNext = { nodeId: 'read', handle: 'next', type: 'source' as const }
  for (const kind of ['entity', 'entity_setup', 'trigger']) expect(canAddFor(graph, kind, fromNext, nodeCatalog), kind).toBe(false)
  expect(canAddFor(graph, 'log', fromNext, nodeCatalog)).toBe(true)
  // A new step's value never feeds a new root, nor a new Entity node's state.
  const fromValue = { nodeId: 'read', handle: 'data:value', type: 'source' as const }
  for (const kind of ['entity', 'entity_setup']) expect(canAddFor(graph, kind, fromValue, nodeCatalog), kind).toBe(false)
})

it('auto-connect treats the steps after a command pin as running', () => {
  const result = addConnected(roots(), 'log', { x: 600, y: 0 }, { nodeId: 'read', handle: 'data:value', type: 'source' }, nodeCatalog, 'added')!
  expect(result).toBeDefined()
  // Put into the flow after Read entity, not left beside it.
  expect(result.graph.edges.some(e => e.kind !== 'data' && e.source === 'read' && e.target === 'added')).toBe(true)
  expect(result.graph.edges.some(e => e.kind === 'data' && e.source === 'read' && e.target === 'added')).toBe(true)
  expect(result.notice ?? '').not.toContain('into the flow')
  expect(result.error).toBeUndefined()
})

it('quick-add from a command pin connects the new step to that pin', () => {
  const graph = roots()
  expect(canAddFor(graph, 'log', { nodeId: 'relay', handle: 'command:turn_off', type: 'source' }, nodeCatalog)).toBe(true)
  const added = addConnected(graph, 'log', { x: 600, y: 300 }, { nodeId: 'relay', handle: 'command:turn_off', type: 'source' }, nodeCatalog, 'added')!
  expect(added.graph.edges.filter(e => e.target === 'added')).toMatchObject([{ source: 'relay', port: 'command:turn_off' }])
  expect(added.error).toBeUndefined()
  // Turn on already runs Read entity, and a command pin runs one flow: as from any execution output that has its wire,
  // no step is offered there.
  expect(canAddFor(graph, 'delay', { nodeId: 'relay', handle: 'command:turn_on', type: 'source' }, nodeCatalog)).toBe(false)
  // Nothing runs into an Entity node; a value its state reads is added and connected to that state pin.
  expect(canAddFor(graph, 'log', { nodeId: 'relay', handle: 'command:turn_on', type: 'target' }, nodeCatalog)).toBe(false)
  const valued = addConnected(relay(), 'entity_is', { x: 0, y: 0 }, { nodeId: 'relay', handle: 'data:is_on', type: 'target' }, nodeCatalog, 'is')!
  expect(valued.graph.edges).toMatchObject([{ kind: 'data', source: 'is', sourcePort: 'value', target: 'relay', targetPort: 'is_on' }])
})

it('quick-add from a command\'s value runs the new step from that command, as a step reading it must', () => {
  // Gap found converting the When commanded tests: a command's value output is drawn under its command (`group`), and
  // quick-add put the new step beside the node, out of every flow ("Connect Log into the flow to run it").
  let graph = add(empty('Lamp'), 'entity', 'lamp', { key: 'lamp', domain: 'light', name: 'Lamp', options: { dimmable: true } })
  const pin = { nodeId: 'lamp', handle: 'data:command:turn_on:brightness', type: 'source' as const }
  const result = addConnected(graph, 'log', { x: 600, y: 0 }, pin, nodeCatalog, 'added')!
  expect(result.graph.edges.filter(e => e.target === 'added').map(e => (e.kind === 'data' ? `${e.sourcePort}->${e.targetPort}` : e.port)).sort())
    .toEqual(['command:turn_on', 'command:turn_on:brightness->value'])
  expect(result.notice ?? '').not.toContain('into the flow')
  expect(result.error).toBeUndefined()
  expect(validateGraph(result.graph).filter(d => d.severity === 3)).toEqual([])
  // With a step on Turn on already, the new one goes between the node and that step.
  graph = add(graph, 'delay', 'wait')
  graph = connectNodes(graph, 'lamp', 'wait', 'command:turn_on')
  const between = addConnected(graph, 'log', { x: 600, y: 0 }, pin, nodeCatalog, 'added')!
  expect(between.graph.edges.filter(e => e.kind !== 'data').map(e => `${e.source}->${e.target}`)).toEqual(['lamp->added', 'added->wait'])
  expect(between.notice).toBe('Added Log between Lamp and Delay')
})

it('layout puts the bands of an entity graph\'s roots first, each led by its root', () => {
  // Steps no root runs (Delay → Stop) sit highest; the relay is below the Log its Turn on runs, When started in between.
  let graph = add(empty('Relay'), 'delay', 'wait', {}, { x: 0, y: 0 })
  graph = add(graph, 'stop', 'halt', {}, { x: 300, y: 0 })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' }, { x: 0, y: 900 })
  graph = add(graph, 'log', 'log', {}, { x: 300, y: 100 })
  graph = add(graph, 'entity_setup', 'setup', {}, { x: 0, y: 500 })
  graph = add(graph, 'delay', 'later', {}, { x: 300, y: 500 })
  graph = connectNodes(connectNodes(connectNodes(graph, 'wait', 'halt'), 'relay', 'log', 'command:turn_on'), 'setup', 'later')
  const positions = tidyLayout(graph, nodeCatalog)
  const y = (id: string) => positions.get(id)!.y
  // Bands with a root come before the one without; among them, the root higher on the canvas first.
  expect(y('setup')).toBeLessThan(y('relay'))
  expect(y('relay')).toBeLessThan(y('wait'))
  // A root heads its flows: the steps it runs are right of it.
  expect(positions.get('relay')!.x).toBeLessThan(positions.get('log')!.x)
  expect(positions.get('setup')!.x).toBeLessThan(positions.get('later')!.x)
})

it('an Entity node\'s flows lay out from it, one after another', () => {
  // Turn on runs Log, Turn off runs Delay → Stop: both are right of the node, each in the column its depth gives it.
  let graph = add(empty('Relay'), 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' }, { x: 500, y: 500 })
  graph = add(graph, 'log', 'log', {}, { x: 0, y: 0 })
  graph = add(graph, 'delay', 'wait', {}, { x: 0, y: 300 })
  graph = add(graph, 'stop', 'halt', {}, { x: 0, y: 600 })
  graph = connectNodes(connectNodes(graph, 'relay', 'log', 'command:turn_on'), 'relay', 'wait', 'command:turn_off')
  graph = connectNodes(graph, 'wait', 'halt')
  const positions = tidyLayout(graph, nodeCatalog)
  const at = (id: string) => positions.get(id)!
  for (const id of ['log', 'wait']) expect(at('relay').x, id).toBeLessThan(at(id).x)
  expect(at('log').x).toBe(at('wait').x)
  expect(at('wait').x).toBeLessThan(at('halt').x)
  // One band: the node and both its flows, the steps of one command apart from the other's.
  expect(at('log').y).not.toBe(at('wait').y)
})

it('an Entity node with only its state counts as a rooted flow in the layout', () => {
  let graph = add(empty('Relay'), 'delay', 'wait', {}, { x: 0, y: 0 })
  graph = add(graph, 'stop', 'halt', {}, { x: 300, y: 0 })
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') }, { x: 0, y: 600 })
  graph = add(graph, 'entity', 'door', { key: 'door', domain: 'binary_sensor', name: 'Door' }, { x: 300, y: 600 })
  graph = connectData(connectNodes(graph, 'wait', 'halt'), 'is', 'value', 'door', 'is_on')
  const positions = tidyLayout(graph, nodeCatalog)
  expect(positions.get('door')!.y).toBeLessThan(positions.get('wait')!.y)
  // The node reads its value: it sits right of what feeds it.
  expect(positions.get('is')!.x).toBeLessThan(positions.get('door')!.x)
})

it('an automation keeps its trigger-only roots on the canvas', () => {
  const graph = addNode(createGraph('Hall'), 'log', 'log')
  expect(canConnect(graph, 'service-1', 'trigger-1')).toBe(false)
  expect(canConnect(graph, 'service-1', 'log')).toBe(true)
  const trigger = buildNodeView(graph.nodes[0], { graph, catalog: nodeCatalog, diagnostics: [] })
  expect(trigger.execIn).toBeUndefined()
  expect(buildNodeView(graph.nodes.find(n => n.id === 'log')!, { graph, catalog: nodeCatalog, diagnostics: [] }).unreachable).toBe(true)
})

/** A light that can be dimmed, and a cover, of one definition. */
const lampGraph = () => {
  const graph = add(empty('Lamp'), 'entity', 'lamp', { key: 'lamp', domain: 'light', name: 'Lamp', options: { dimmable: true } })
  return add(graph, 'entity', 'blind', { key: 'blind', domain: 'cover', name: 'Blind' })
}

it('an Entity node has an output for each value of its commands\' data, typed and named as its row has it, under its command', () => {
  let graph = add(lampGraph(), 'entity', 'plug', { key: 'plug', domain: 'switch', name: 'Plug' })
  const node = (id: string) => graph.nodes.find(n => n.id === id)!
  expect(dataOutputs(node('lamp'))).toEqual({ 'command:turn_on:brightness': { type: 'number', label: 'Brightness', pointer: '/data/brightness', group: 'command:turn_on' } })
  expect(dataOutputs(node('blind'))).toEqual({ 'command:set_position:position': { type: 'number', label: 'Position', pointer: '/data/position', group: 'command:set_position' } })
  // A type whose commands carry no data has none; the node needs no definition for them, its type gives them.
  expect(dataOutputs(node('plug'))).toEqual({})
  expect(dataOutputs(node('lamp'), withGraphTypes(nodeCatalog, graph))).toEqual(dataOutputs(node('lamp'), nodeCatalog))
  expect(executionLabel(node('blind'), 'command:set_position')).toBe('Set position')
  // The canvas shows it as a value pin under its command, and a wire from it into a number input is taken.
  expect(outputs(graph, 'lamp')).toEqual(['Turn on', 'Brightness', 'Turn off'])
  graph = add(graph, 'set_entity_state', 'dim', { entity: 'lamp' })
  graph = connectData(connectNodes(graph, 'lamp', 'dim', 'command:turn_on'), 'lamp', 'command:turn_on:brightness', 'dim', 'brightness')
  expect(graph.edges.some(e => e.kind === 'data' && e.source === 'lamp' && e.sourcePort === 'command:turn_on:brightness' && e.target === 'dim')).toBe(true)
  expect(() => connectData(graph, 'lamp', 'command:turn_on:brightness', 'dim', 'is_on')).toThrow('On accepts True/False, not Number')
})

it('an Entity node asks for the values its type requires once it has one, and a Set state needs at least one', () => {
  let graph = add(lampGraph(), 'set_entity_state', 'set', { entity: 'lamp' })
  const required = (id: string) => Object.fromEntries(Object.entries(dataInputs(graph.nodes.find(n => n.id === id)!, undefined, withGraphTypes(nodeCatalog, graph))).map(([port, input]) => [port, input.required]))
  // Every state pin of the node can be left empty: the node then has no state flow. Its Device pin can be too.
  expect(required('lamp')).toEqual({ device: false, is_on: false, brightness: false })
  expect(required('blind')).toEqual({ device: false, is_closed: false, position: false })
  expect(required('set')).toEqual({ is_on: false, brightness: false })
  // Errors on the three steps, a Set state's not being in a flow aside.
  const problems = (g: Graph) => validateGraph(g, nodeCatalog).filter(d => d.severity === 3 && d.code !== 'node.unreachable' && ['lamp', 'blind', 'set'].includes(d.nodeId ?? '')).map(d => `${d.nodeId}: ${d.message}`).sort()
  expect(problems(graph)).toEqual(['set: Set state needs at least one value'])
  // A light given its Brightness only asks for On, which its type requires; a cover's position needs nothing more.
  const argued = (g: Graph, id: string, values: Record<string, unknown>) => updateNode(g, id, { config: { ...g.nodes.find(n => n.id === id)!.config, arguments: values } })
  graph = argued(argued(graph, 'lamp', { brightness: lit('number', 128) }), 'blind', { position: lit('number', 40) })
  graph = updateNode(graph, 'set', { config: { entity: 'lamp', brightness: lit('number', 128) } })
  expect(problems(graph)).toEqual(['lamp: Lamp has a value for Brightness but none for On: connect or set On too'])
  graph = argued(graph, 'lamp', { is_on: lit('boolean', true), brightness: lit('number', 128) })
  expect(problems(graph)).toEqual([])
})

it('an Entity node of a button has no state pins, and a Set state of it is refused', () => {
  let graph = add(empty('Bell'), 'entity', 'bell', { key: 'bell', domain: 'button', name: 'Bell' })
  graph = connectNodes(add(graph, 'set_entity_state', 'set', { entity: 'bell' }), 'bell', 'set', 'command:press')
  const bell = graph.nodes.find(n => n.id === 'bell')!
  // Its one pin is Device, which is no state value.
  expect(Object.keys(dataInputs(bell))).toEqual(['device'])
  expect(stateInputs(bell, nodeCatalog)).toEqual({})
  expect(executionPorts(bell)).toEqual(['command:press'])
  expect(Object.keys(dataOutputs(bell))).toEqual([])
  expect(validateGraph(graph, nodeCatalog).filter(d => d.code === 'entity.stateless')).toEqual([
    { nodeId: 'set', message: 'A button has no state to set', severity: 3, inputId: 'entity', code: 'entity.stateless' },
  ])
  expect(validateGraph(graph, nodeCatalog).filter(d => d.nodeId === 'bell')).toEqual([])
})

it('a camera\'s When a frame is needed starts a flow as a command does: no step order wire leads into it, and its steps run', () => {
  let graph = add(empty('Annotated'), 'entity', 'annotated', { key: 'annotated', domain: 'camera', name: 'Annotated' })
  graph = add(graph, 'camera_snapshot', 'snap', { entity: lit('entity_id:camera', 'camera.front') })
  graph = add(graph, 'show_frame', 'show')
  graph = connectNodes(connectNodes(graph, 'annotated', 'snap', 'frame'), 'snap', 'show')
  graph = connectData(graph, 'snap', 'image', 'show', 'image')
  // A new step for the definition's only camera names it.
  expect(graph.nodes.find(n => n.id === 'show')!.config.entity).toBe('annotated')
  expect(validateGraph(graph, nodeCatalog)).toEqual([])
  expect(canConnect(graph, 'snap', 'annotated')).toBe(false)
  expect(canConnect(graph, 'show', 'annotated')).toBe(false)
  expect(view(graph, 'annotated').execIn).toBeUndefined()
  expect(view(graph, 'annotated').rows.flatMap(r => (r.output?.kind === 'exec' ? [r.output.id] : []))).toEqual(['frame'])
  expect(outputs(graph, 'annotated')).toEqual(['When a frame is needed'])
  expect(view(graph, 'show').execIn?.id).toBe('in')
  expect(view(graph, 'show').unreachable).toBe(false)
  expect(canAddFor(graph, 'entity', { nodeId: 'snap', handle: 'next', type: 'source' }, nodeCatalog)).toBe(false)
  // Quick-add from its frame pin runs the new step when a frame is needed.
  const fresh = add(empty('Annotated'), 'entity', 'annotated', { key: 'annotated', domain: 'camera', name: 'Annotated' })
  const added = addConnected(fresh, 'camera_snapshot', { x: 400, y: 0 }, { nodeId: 'annotated', handle: 'frame', type: 'source' }, nodeCatalog, 'snap')!
  expect(added.graph.edges).toMatchObject([{ source: 'annotated', target: 'snap', port: 'frame' }])
})
