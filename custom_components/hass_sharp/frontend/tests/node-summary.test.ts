import { describe, expect, it } from 'vitest'
import { nodeCatalog, withGraphTypes, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../src/visual/contract'
import { nodeSummary, stepSummary } from '../src/visual/nodeSummary'

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
/** An Entity node of the entity `key` (also its id), named only when `name` is given. */
const hub = (key: string, domain: string, name?: string): GraphNode => ({ id: key, kind: 'entity', config: { key, domain, ...(name === undefined ? {} : { name }) } })
/** An entity definition whose entities are the Entity nodes `hubs`, with no settings unless given. */
const definitionOf = (hubs: GraphNode[], settings: unknown[] = []): Graph =>
  ({ version: 5, name: 'Plug', nodes: hubs, edges: [], definition: { settings } as unknown as Graph['definition'] })
const graph = definitionOf([hub('relay', 'switch', 'Relay'), hub('power', 'sensor', 'Power'), hub('online', 'binary_sensor')],
  [{ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' }, { key: 'port', type: 'number' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: null, hasValue: true }])
const node = (kind: string, config: Record<string, unknown>): GraphNode => ({ id: 'n', kind, config })
/** The summary with the definition lifted into the catalog (the canvas), and from the graph alone (the inspector). */
const both = (n: GraphNode) => {
  const lifted = nodeSummary(n, withGraphTypes(nodeCatalog, graph), undefined, graph)
  expect(nodeSummary(n, nodeCatalog, undefined, graph)).toBe(lifted)
  return lifted
}

describe('the summaries of an entity definition\'s steps', () => {
  it('name the entity by its name, else its key', () => {
    expect(both(node('set_entity_state', { entity: 'power', value: lit('number', 4) }))).toBe('Set state ▸ Power')
    expect(both(node('set_entity_state', { entity: 'online' }))).toBe('Set state ▸ online')
    expect(both(node('set_entity_available', { entity: 'relay', available: lit('boolean', true) }))).toBe('Set available ▸ Relay')
  })
  it('of State, When commanded and When a frame is needed name the entity by its name, else its key (the old steps, until Task 12)', () => {
    expect(both(node('entity_state', { entity: 'power' }))).toBe('State ▸ Power')
    expect(both(node('entity_state', { entity: 'heater' }))).toBe('State ▸ heater')
    expect(both(node('entity_command', { entity: 'heater', command: 'turn_on' }))).toBe('When commanded ▸ heater: turn_on')
    expect(stepSummary(node('entity_state', {}), nodeCatalog, undefined, graph)).toBeUndefined()
    expect(nodeSummary(node('entity_state', {}), nodeCatalog, undefined, graph)).toBe(nodeCatalog.nodes.entity_state.description)
    const cameras = definitionOf([hub('annotated', 'camera', 'Annotated'), hub('yard', 'camera')])
    const summary = (n: GraphNode) => nodeSummary(n, withGraphTypes(nodeCatalog, cameras), undefined, cameras)
    expect(summary(node('camera_frame', { entity: 'annotated' }))).toBe('When a frame is needed ▸ Annotated')
    expect(summary(node('camera_frame', { entity: 'yard' }))).toBe('When a frame is needed ▸ yard')
    expect(summary(node('camera_frame', {}))).toBe(nodeCatalog.nodes.camera_frame.description)
  })
  it('name the command by its label (When commanded, until Task 12)', () => {
    expect(both(node('entity_command', { entity: 'relay', command: 'turn_off' }))).toBe('When commanded ▸ Relay: Turn off')
    expect(both(node('entity_command', { entity: 'relay', command: 'toggle' }))).toBe('When commanded ▸ Relay: toggle')
  })
  it('name the command by the catalog\'s label of its type, else in words (When commanded, until Task 12)', () => {
    const catalog = { ...nodeCatalog, entityDomains: { ...nodeCatalog.entityDomains, fan: { label: 'Fan', state: {}, commands: { turn_on: { label: 'Spin up' }, set_speed: {} }, options: {} } } } as unknown as VisualCatalogSnapshot
    const fanGraph = definitionOf([hub('blades', 'fan', 'Blades')])
    const summary = (config: Record<string, unknown>) => nodeSummary(node('entity_command', config), withGraphTypes(catalog, fanGraph), undefined, fanGraph)
    expect(summary({ entity: 'blades', command: 'turn_on' })).toBe('When commanded ▸ Blades: Spin up')
    expect(summary({ entity: 'blades', command: 'set_speed' })).toBe('When commanded ▸ Blades: Set speed')
  })
  it('name the new types\' commands by their labels (When commanded, until Task 12)', () => {
    const things = definitionOf([hub('level', 'number', 'Level'), hub('lamp', 'light', 'Lamp'), hub('blind', 'cover', 'Blind'), hub('mode', 'select'), hub('bell', 'button', 'Bell')])
    const summary = (kind: string, config: Record<string, unknown>) => nodeSummary(node(kind, config), withGraphTypes(nodeCatalog, things), undefined, things)
    expect(summary('entity_command', { entity: 'level', command: 'set_value' })).toBe('When commanded ▸ Level: Set value')
    expect(summary('entity_command', { entity: 'blind', command: 'set_position' })).toBe('When commanded ▸ Blind: Set position')
    expect(summary('entity_command', { entity: 'mode', command: 'select_option' })).toBe('When commanded ▸ mode: Select option')
    expect(summary('entity_command', { entity: 'bell', command: 'press' })).toBe('When commanded ▸ Bell: Press')
    expect(summary('set_entity_state', { entity: 'lamp', brightness: lit('number', 128) })).toBe('Set state ▸ Lamp')
  })
  it('name a setting by its label, else its key, and never show its value', () => {
    expect(both(node('setting', { key: 'host' }))).toBe('Setting ▸ Address')
    expect(both(node('setting', { key: 'port' }))).toBe('Setting ▸ port')
    expect(both(node('setting', { key: 'password' }))).toBe('Setting ▸ Password')
  })
  it('show a key the definition no longer has as it is', () => {
    expect(both(node('set_entity_state', { entity: 'heater' }))).toBe('Set state ▸ heater')
    expect(both(node('set_entity_available', { entity: 'heater', available: lit('boolean', true) }))).toBe('Set available ▸ heater')
    expect(both(node('setting', { key: 'token' }))).toBe('Setting ▸ token')
  })
  it('fall back to the description while nothing is chosen', () => {
    expect(stepSummary(node('set_entity_state', {}), nodeCatalog, undefined, graph)).toBeUndefined()
    expect(stepSummary(node('setting', { key: '' }), nodeCatalog, undefined, graph)).toBeUndefined()
    expect(nodeSummary(node('set_entity_available', {}), nodeCatalog, undefined, graph)).toBe(nodeCatalog.nodes.set_entity_available.description)
  })
})

describe('an Entity node', () => {
  it('says its type and its key', () => {
    expect(both(node('entity', { key: 'lamp', domain: 'light', name: 'Lamp' }))).toBe('Light · lamp')
    expect(both(node('entity', { key: '', domain: 'light', name: 'Lamp' }))).toBe('Light')
    expect(both(node('entity', { key: '  ', domain: 'light' }))).toBe('Light')
    expect(both(node('entity', { domain: 'binary_sensor' }))).toBe('Binary sensor')
    // A type the catalog does not have, in words.
    expect(both(node('entity', { key: 'fan', domain: 'fan_speed' }))).toBe('Fan speed · fan')
  })
})

describe('Has state and Current state', () => {
  it('name the Home Assistant entity and the state', () => {
    expect(both(node('entity_is', { entity: lit('entity_id', 'binary_sensor.motion_a'), state: lit('string', 'on') }))).toBe('Has state ▸ binary_sensor.motion_a is on')
    expect(both(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
    // In an automation as well.
    expect(nodeSummary(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
  })
  it('name the entity as Home Assistant names it, and by its id when Home Assistant does not know it', () => {
    const registry = { entities: [{ entity_id: 'binary_sensor.motion_a', name: 'Hall motion' }, { entity_id: 'sensor.power', name: null }] }
    const summary = (n: GraphNode) => nodeSummary(n, nodeCatalog, registry, graph)
    expect(summary(node('entity_is', { entity: lit('entity_id', 'binary_sensor.motion_a'), state: lit('string', 'on') }))).toBe('Has state ▸ Hall motion is on')
    expect(summary(node('entity_current', { entity: lit('entity_id', 'binary_sensor.motion_a') }))).toBe('Current state ▸ Hall motion')
    expect(summary(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
    expect(summary(node('entity_is', { entity: lit('entity_id', 'binary_sensor.gone'), state: lit('string', 'on') }))).toBe('Has state ▸ binary_sensor.gone is on')
    // In an automation as well.
    expect(nodeSummary(node('entity_current', { entity: lit('entity_id', 'binary_sensor.motion_a') }), nodeCatalog, registry)).toBe('Current state ▸ Hall motion')
  })
  it('name a wired entity by the output it comes from', () => {
    const wired: Graph = { version: 5, name: 'A', nodes: [
      { id: 't', kind: 'trigger', config: { type: 'entity', entity: 'binary_sensor.motion' } },
      node('entity_is', { entity: { kind: 'unset' }, state: lit('string', 'off') }),
    ], edges: [{ id: 'w', kind: 'data', source: 't', sourcePort: 'entity_id', target: 'n', targetPort: 'entity' }] }
    expect(nodeSummary(wired.nodes[1], nodeCatalog, undefined, wired)).toMatch(/^Has state ▸ .+ is off$/)
  })
})

describe('HTTP request and Download file', () => {
  const settingWire: Graph = { ...graph, nodes: [node('setting', { key: 'password' })].map(n => ({ ...n, id: 'k' })), edges: [{ id: 'w', kind: 'data', source: 'k', sourcePort: 'value', target: 'n', targetPort: 'url' }] }
  it('say the method, host and path of an address typed in, never its query', () => {
    expect(nodeSummary(node('http_request', { method: 'GET', url: lit('string', 'http://192.168.1.40/rpc/Switch.GetStatus?id=0&auth=hunter2') }))).toBe('GET 192.168.1.40/rpc/Switch.GetStatus')
    expect(nodeSummary(node('http_request', { method: 'POST', url: lit('string', 'https://user:pass@plug.local:8443/relay/0#x') }))).toBe('POST plug.local:8443/relay/0')
    // The method's default, and an address without a path.
    expect(nodeSummary(node('http_request', { url: lit('string', 'http://192.168.1.40/?token=hunter2') }))).toBe('GET 192.168.1.40')
    expect(both(node('http_request', { method: 'PUT', url: lit('string', 'http://10.0.0.7/settings') }))).toBe('PUT 10.0.0.7/settings')
  })
  it('say HTTP request for a connected address, which may be a secret\'s', () => {
    const summary = nodeSummary(node('http_request', { method: 'GET', url: { kind: 'unset' } }), nodeCatalog, undefined, settingWire)
    expect(summary).toBe('HTTP request')
  })
  it('fall back to the description for an address that is not one', () => {
    const description = nodeCatalog.nodes.http_request.description
    expect(nodeSummary(node('http_request', { method: 'GET', url: lit('string', 'ftp://192.168.1.40/file') }))).toBe(description)
    expect(nodeSummary(node('http_request', { method: 'GET' }))).toBe(description)
  })
  it('name the file a download makes: its name, else the last part of the address typed in', () => {
    const sha = lit('string', 'a'.repeat(64))
    expect(both(node('download_file', { name: 'model.tflite', url: lit('string', 'https://example.com/get?id=1'), sha256: sha }))).toBe('Download model.tflite')
    expect(both(node('download_file', { url: lit('string', 'https://example.com/models/model%20v2.tflite?token=hunter2'), sha256: sha }))).toBe('Download model v2.tflite')
    expect(nodeSummary(node('download_file', { url: { kind: 'unset' }, sha256: sha }), nodeCatalog, undefined, settingWire)).toBe(nodeCatalog.nodes.download_file.description)
  })
})

describe('Take a picture, Draw rectangle and Draw text', () => {
  it('name the camera and the colour', () => {
    expect(both(node('camera_snapshot', { entity: lit('entity_id:camera', 'camera.front') }))).toBe('Take a picture · camera.front')
    expect(nodeSummary(node('camera_snapshot', { entity: lit('entity_id:camera', 'camera.front') }), nodeCatalog, { entities: [{ entity_id: 'camera.front', name: 'Front door' }] })).toBe('Take a picture · Front door')
    expect(both(node('image_draw_rect', { image: { kind: 'unset' } }))).toBe('Draw rectangle · Red')
    expect(both(node('image_draw_rect', { image: { kind: 'unset' }, color: 'Green' }))).toBe('Draw rectangle · Green')
    expect(both(node('image_draw_text', { image: { kind: 'unset' }, text: lit('string', 'Front door') }))).toBe('Draw text · White')
    expect(both(node('image_draw_text', { image: { kind: 'unset' }, color: 'Yellow' }))).toBe('Draw text · Yellow')
  })
  it('fall back to the description for a camera not chosen', () => {
    expect(nodeSummary(node('camera_snapshot', {}))).toBe(nodeCatalog.nodes.camera_snapshot.description)
  })
})

describe('Show frame', () => {
  const cameras = definitionOf([hub('annotated', 'camera', 'Annotated'), hub('yard', 'camera')])
  const summary = (n: GraphNode) => {
    const lifted = nodeSummary(n, withGraphTypes(nodeCatalog, cameras), undefined, cameras)
    expect(nodeSummary(n, nodeCatalog, undefined, cameras)).toBe(lifted)
    return lifted
  }
  it('name the camera by its name, else its key', () => {
    expect(summary(node('show_frame', { entity: 'annotated', image: { kind: 'unset' } }))).toBe('Show frame ▸ Annotated')
    expect(summary(node('show_frame', { entity: 'yard', image: { kind: 'unset' } }))).toBe('Show frame ▸ yard')
    expect(summary(node('show_frame', { entity: 'gone' }))).toBe('Show frame ▸ gone')
  })
  it('fall back to the description for a camera not chosen', () => {
    expect(summary(node('show_frame', {}))).toBe(nodeCatalog.nodes.show_frame.description)
  })
})
