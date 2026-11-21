import { describe, expect, it } from 'vitest'
import { connectData, nodeCatalog, type Graph } from '../src/visual/contract'
import { connectNodes } from '../src/visual/graph'
import { cameraFrameMode, cameraFrameModeDescription, videoFrameModeDescription } from '../src/visual/cameraFrameMode'
import { buildNodeView, viewKey } from '../src/visual/flow/flowModel'
import { add, empty, lit } from './visual-fixture'

// Camera frame is live while it makes a camera's frames (its picture reaches a camera's Image through value nodes alone),
// and takes a snapshot anywhere else, as the runtime decides it.
const box = { x: lit('number', 10), y: lit('number', 10), width: lit('number', 80), height: lit('number', 40) }
/** Porch, a camera, and Camera frame `snap` of camera.front_door, connected to nothing yet. */
const porch = (): Graph => {
  let graph: Graph = add(empty('Porch'), 'entity', 'porch', { key: 'porch', domain: 'camera', name: 'Porch' })
  graph = add(graph, 'ha_entity', 'door', { entity: 'camera.front_door' })
  graph = add(graph, 'camera_frame', 'snap')
  return connectData(graph, 'door', 'entity', 'snap', 'entity')
}
/** The graph with a Draw rectangle `id` reading the picture of `from`. */
const drawn = (graph: Graph, from: string, id: string) => connectData(add(graph, 'image_draw_rect', id, box), from, 'image', id, 'image')
/** A step that runs on a trigger and reads the picture: a service that sends it, as a doorbell notification would. */
const notified = (graph: Graph, from: string) => {
  let next = add(add(graph, 'trigger', 'ring'), 'log', 'send')
  next = connectNodes(next, 'ring', 'send')
  return connectData(next, from, 'image', 'send', 'value')
}
const mode = (graph: Graph) => cameraFrameMode(graph, 'snap', nodeCatalog)

describe('the mode of a Camera frame', () => {
  it('is a snapshot while nothing reads it', () => {
    expect(mode(porch())).toEqual({ live: [], snapshot: false, label: 'Snapshot · JPEG', description: cameraFrameModeDescription })
  })
  it('is live when it goes into a camera\'s Image, directly or through Draw rectangle and Draw text', () => {
    expect(mode(connectData(porch(), 'snap', 'image', 'porch', 'image'))).toMatchObject({ live: ['porch'], snapshot: false, label: 'Live · raw frames' })
    let graph = drawn(porch(), 'snap', 'box')
    graph = connectData(add(graph, 'image_draw_text', 'words', { text: lit('string', 'Porch'), x: lit('number', 8), y: lit('number', 8) }), 'box', 'image', 'words', 'image')
    graph = connectData(graph, 'words', 'image', 'porch', 'image')
    expect(mode(graph)).toMatchObject({ live: ['porch'], snapshot: false, label: 'Live · raw frames' })
  })
  it('is a snapshot when only a step reads it, directly or through a value node', () => {
    expect(mode(notified(porch(), 'snap'))).toMatchObject({ live: [], snapshot: true, label: 'Snapshot · JPEG' })
    expect(mode(notified(drawn(porch(), 'snap', 'box'), 'box'))).toMatchObject({ live: [], snapshot: true, label: 'Snapshot · JPEG' })
    // A Draw rectangle that goes nowhere reads it for no one.
    expect(mode(drawn(porch(), 'snap', 'box'))).toMatchObject({ live: [], snapshot: false, label: 'Snapshot · JPEG' })
  })
  it('names the camera it is live for when a step reads it too', () => {
    let graph = drawn(porch(), 'snap', 'box')
    graph = connectData(graph, 'box', 'image', 'porch', 'image')
    graph = notified(graph, 'snap')
    expect(mode(graph)).toMatchObject({ live: ['porch'], snapshot: true, label: 'Live for Porch\'s frames · Snapshot elsewhere' })
    // Two cameras, the second by its key as it has no name.
    graph = add(graph, 'entity', 'yard', { key: 'yard', domain: 'camera', name: '' })
    graph = connectData(graph, 'box', 'image', 'yard', 'image')
    expect(mode(graph)).toMatchObject({ live: ['porch', 'yard'], label: 'Live for Porch\'s and yard\'s frames · Snapshot elsewhere' })
  })
  it('says what each mode is and how its wiring chooses one', () => {
    expect(cameraFrameModeDescription).toContain('Live: raw frames from one shared ffmpeg feed of the source camera, at its full frame rate.')
    expect(cameraFrameModeDescription).toContain('Snapshot: one picture from Home Assistant, passed on unchanged.')
  })
})

describe('the card of a Camera frame', () => {
  const view = (graph: Graph, id: string) => buildNodeView(graph.nodes.find(n => n.id === id)!, { graph, catalog: nodeCatalog, diagnostics: [] })
  it('shows its mode, and says it to a screen reader', () => {
    expect(view(porch(), 'snap').frameMode?.label).toBe('Snapshot · JPEG')
    const live = connectData(drawn(porch(), 'snap', 'box'), 'box', 'image', 'porch', 'image')
    expect(view(live, 'snap').frameMode).toMatchObject({ label: 'Live · raw frames', description: cameraFrameModeDescription })
    expect(view(live, 'snap').label).toContain('Live · raw frames')
    // No other card has one.
    expect(view(live, 'box').frameMode).toBeUndefined()
  })
  it('is drawn again when a wire past its own changes its mode', () => {
    const before = drawn(porch(), 'snap', 'box'), after = connectData(before, 'box', 'image', 'porch', 'image')
    const node = before.nodes.find(n => n.id === 'snap')!
    expect(viewKey(node, { graph: before, catalog: nodeCatalog, diagnostics: [] })).not.toBe(viewKey(node, { graph: after, catalog: nodeCatalog, diagnostics: [] }))
  })
  it('names a camera chosen in it, and the Entity connected to it by its entity\'s name, else its id', () => {
    const typed = add(empty('Porch'), 'camera_frame', 'snap', { entity: lit('entity_id:camera', 'camera.front_door') })
    expect(view(typed, 'snap').summary).toBe('Camera frame · camera.front_door')
    const names = { entities: [{ entity_id: 'camera.front_door', name: 'Front door' }] }
    const node = porch().nodes.find(n => n.id === 'snap')!
    const named = buildNodeView(node, { graph: porch(), catalog: nodeCatalog, diagnostics: [], names })
    expect(named.summary).toBe('Camera frame · Front door')
    expect(named.rows.find(r => r.input?.port === 'entity')!.input).toMatchObject({ mode: 'connected', source: 'Front door · Entity' })
    expect(view(porch(), 'snap').rows.find(r => r.input?.port === 'entity')!.input!.source).toBe('camera.front_door · Entity')
  })
})

// Video frame is live in a camera's frames as Camera frame is, and takes the newest frame anywhere else.
describe('the mode of a Video frame', () => {
  const clip = (source = '/media/clip.mp4'): Graph => add(add(empty('Porch'), 'entity', 'porch', { key: 'porch', domain: 'camera', name: 'Porch' }), 'video_frame', 'snap', { source })
  it('is the newest frame while nothing reads it, or only a step does', () => {
    expect(mode(clip())).toEqual({ live: [], snapshot: false, label: 'Newest frame', description: videoFrameModeDescription })
    expect(mode(notified(clip(), 'snap'))).toMatchObject({ live: [], snapshot: true, label: 'Newest frame' })
  })
  it('is live when it goes into a camera\'s Image through Draw rectangle, and names the camera when a step reads it too', () => {
    const live = connectData(drawn(clip(), 'snap', 'box'), 'box', 'image', 'porch', 'image')
    expect(mode(live)).toMatchObject({ live: ['porch'], snapshot: false, label: 'Live · raw frames' })
    expect(mode(notified(live, 'snap'))).toMatchObject({ live: ['porch'], snapshot: true, label: 'Live for Porch\'s frames · Newest frame elsewhere' })
  })
  it('says what each mode is', () => {
    expect(videoFrameModeDescription).toContain('Live: raw frames from one shared ffmpeg feed of the video')
    expect(videoFrameModeDescription).toContain('else one frame read by a short run of ffmpeg')
  })
  it('shows its mode on its card, and names its file or its address\'s host, never a password', () => {
    const view = (graph: Graph) => buildNodeView(graph.nodes.find(n => n.id === 'snap')!, { graph, catalog: nodeCatalog, diagnostics: [] })
    expect(view(clip()).frameMode?.label).toBe('Newest frame')
    expect(view(clip()).summary).toBe('Video frame · clip.mp4')
    expect(view(clip('rtsp://admin:hunter2@door.local:8554/live?token=x')).summary).toBe('Video frame · door.local:8554')
    expect(view(clip('')).summary).not.toContain('Video frame ·')
  })
})

describe('the card of a Home Assistant entity', () => {
  const graph = () => add(add(empty(), 'ha_entity', 'door', { entity: 'camera.front_door' }), 'ha_entity', 'none', { entity: '' })
  it('is titled by its entity\'s name, else its id, with its domain under it, and gives an entity id of that domain', () => {
    const names = { entities: [{ entity_id: 'camera.front_door', name: 'Dummy Front Door' }] }
    const node = graph().nodes[0]
    const named = buildNodeView(node, { graph: graph(), catalog: nodeCatalog, diagnostics: [], names })
    expect(named).toMatchObject({ title: 'Dummy Front Door', summary: 'Home Assistant entity · camera', pure: true })
    expect(named.execIn).toBeUndefined()
    const plain = buildNodeView(node, { graph: graph(), catalog: nodeCatalog, diagnostics: [] })
    expect(plain.title).toBe('camera.front_door')
    // Its one pin, on the right, typed as an entity id of its entity's domain.
    expect(plain.rows).toHaveLength(1)
    expect(plain.rows[0].input).toBeUndefined()
    expect(plain.rows[0].output).toMatchObject({ id: 'data:entity', side: 'out', label: 'Entity', typeId: 'entity_id:camera' })
  })
  it('is titled Home Assistant entity until its entity is chosen, and its pin has no type then', () => {
    const view = buildNodeView(graph().nodes[1], { graph: graph(), catalog: nodeCatalog, diagnostics: [] })
    expect(view.title).toBe('Home Assistant entity')
    expect(view.rows[0].output!.typeId).toBe('')
  })
})
