import { dataInputs, entityNodeName, isPure, nodeCatalog, pictureInput, type Graph, type GraphNode, type VisualCatalogSnapshot } from './contract'

/**
 * How a Camera frame or a Video frame node takes its frames, as its wiring decides it. The runtime takes a live frame
 * while a camera's frame is being made, and anywhere else a snapshot (Camera frame) or the newest frame (Video frame); a
 * frame is made from a camera's Image, so a node whose picture reaches a camera Entity node's Image, directly or through
 * value nodes (Draw rectangle, Draw text), is live for that camera. One read by a step, or by an Entity node's state,
 * takes a snapshot or the newest frame.
 */
export interface CameraFrameMode {
  /** The cameras (their Entity nodes' ids, in graph order) whose Image the frame reaches through value nodes alone. */
  live: string[]
  /** A step or an Entity node's state reads the frame too, through value nodes or directly. */
  snapshot: boolean
  /**
   * The badge: `Live · raw frames`, `Snapshot · JPEG`, or `Live for Porch's frames · Snapshot elsewhere`; for a Video
   * frame, `Newest frame` and `… · Newest frame elsewhere` instead of the snapshot.
   */
  label: string
  /** What the two modes are, and how the node's wiring chooses one. */
  description: string
}

/** What the badge's tooltip says, whatever the mode. */
export const cameraFrameModeDescription =
  'Live: raw frames from one shared ffmpeg feed of the source camera, at its full frame rate. Snapshot: one picture from Home Assistant, passed on unchanged. ' +
  'Camera frame is live while it makes a camera\'s frames, connected into that camera\'s Image directly or through value nodes such as Draw rectangle; read anywhere else, it takes a snapshot.'

/** What the badge's tooltip says for a Video frame, whatever the mode. */
export const videoFrameModeDescription =
  'Live: raw frames from one shared ffmpeg feed of the video, at its own frame rate. Newest frame: the newest frame of that feed while it is open, else one frame read by a short run of ffmpeg. ' +
  'Video frame is live while it makes a camera\'s frames, connected into that camera\'s Image directly or through value nodes such as Draw rectangle; read anywhere else, it takes the newest frame.'

/** The kinds whose frames are live in a camera's frames and taken otherwise elsewhere: Camera frame and Video frame. */
export const frameSourceKinds: readonly string[] = ['camera_frame', 'video_frame']

/** Names as a sentence lists them: `A`, `A and B`, `A, B and C`. */
const listed = (names: string[]) => names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`

/**
 * The mode of the Camera frame or Video frame `nodeId` (`CameraFrameMode`), from the graph's value wires: each wire out of it, and out
 * of every value node it reaches, ends in a camera's picture input (live for that camera), in another value node (followed
 * on) or in anything else (a snapshot). A frame nothing reads yet says Snapshot: that is what it takes until it is
 * connected into a camera's Image.
 */
export function cameraFrameMode(graph: Pick<Graph, 'nodes' | 'edges'>, nodeId: string, catalog: VisualCatalogSnapshot = nodeCatalog): CameraFrameMode {
  const nodes = new Map<string, GraphNode>()
  for (const node of Array.isArray(graph.nodes) ? graph.nodes : []) if (node && typeof node.id === 'string' && !nodes.has(node.id)) nodes.set(node.id, node)
  const edges = (Array.isArray(graph.edges) ? graph.edges : []).filter(e => e?.kind === 'data')
  const live = new Set<string>(), seen = new Set([nodeId])
  let snapshot = false
  for (const stack = [nodeId]; stack.length; ) {
    const id = stack.pop()!
    for (const edge of edges) {
      if (edge.source !== id || edge.kind !== 'data') continue
      const target = nodes.get(edge.target)
      if (!target) continue
      if (pictureInput(dataInputs(target, undefined, catalog)[edge.targetPort])) live.add(target.id)
      else if (isPure(target, catalog)) { if (!seen.has(target.id)) { seen.add(target.id); stack.push(target.id) } }
      else snapshot = true
    }
  }
  const cameras = [...nodes.values()].filter(n => live.has(n.id))
  const video = nodes.get(nodeId)?.kind === 'video_frame'
  const elsewhere = video ? 'Newest frame' : 'Snapshot'
  const label = !cameras.length ? (video ? 'Newest frame' : 'Snapshot · JPEG')
    : !snapshot ? 'Live · raw frames'
      : `Live for ${listed(cameras.map(n => `${entityNodeName(n, catalog) ?? n.id}'s`))} frames · ${elsewhere} elsewhere`
  return { live: cameras.map(n => n.id), snapshot, label, description: video ? videoFrameModeDescription : cameraFrameModeDescription }
}
