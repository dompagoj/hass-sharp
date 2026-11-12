import { describe, expect, it } from 'vitest'
import { createRoot } from 'solid-js'
import { addNode, connectNodes, createGraph, createGraphHistory, removeNode, updateNode, serializeGraph } from '../src/visual/graph'

describe('visual draft graph', () => {
  it('updates position/config without mutating an earlier history snapshot', () => {
    const before = createGraph('Heating')
    const after = updateNode(before, 'trigger-1', { position: { x: 210, y: 90 }, label: 'Door opens', config: { entity: 'binary_sensor.door' } })
    expect(before.nodes[0].position).toEqual({ x: 60, y: 100 })
    expect(before.nodes[0].label).toBe('Entity changes')
    expect(after.nodes[0]).toMatchObject({ position: { x: 210, y: 90 }, label: 'Door opens', config: { entity: 'binary_sensor.door' } })
  })
  it('keeps JSON versioned and excludes canvas/library state', () => {
    const graph = updateNode(createGraph('Heating'), 'trigger-1', { size: { width: 300, height: 180 } })
    const value = JSON.parse(serializeGraph(graph))
    expect(value).toMatchObject({ version: 1, name: 'Heating', nodes: [{ id: 'trigger-1', size: { width: 300, height: 180 } }, {}, {}] })
    expect(value.nodes[0]).not.toHaveProperty('selected')
    expect(value).not.toHaveProperty('generatedSource')
  })
  it('rejects self, duplicate, dangling and cyclic connections', () => {
    const graph = createGraph('Heating')
    expect(connectNodes(graph, 'service-1', 'condition-1')).toBe(graph)
    expect(connectNodes(graph, 'service-1', 'trigger-1')).toBe(graph)
    expect(connectNodes(graph, 'trigger-1', 'trigger-1')).toBe(graph)
    expect(connectNodes(graph, 'missing', 'service-1')).toBe(graph)
    expect(connectNodes(graph, 'trigger-1', 'condition-1')).toBe(graph)
  })
  it('removes attached edges with a deleted node', () => {
    const next = removeNode(createGraph('Heating'), 'condition-1')
    expect(next.nodes.map(n => n.id)).toEqual(['trigger-1', 'service-1'])
    expect(next.edges).toEqual([])
  })
  it('adds a configurable node and a valid connection', () => {
    const graph = addNode(createGraph('Heating'), 'delay', 'delay-new')
    const connected = connectNodes(graph, 'service-1', 'delay-new')
    expect(connected.nodes.at(-1)).toMatchObject({ id: 'delay-new', kind: 'delay', config: { seconds: '60' } })
    expect(connected.edges.at(-1)).toMatchObject({ source: 'service-1', target: 'delay-new' })
  })
})

it('undoes and redoes entire edits, clears redo on a new edit, and ignores no-ops', () => createRoot(dispose => {
  const history = createGraphHistory(createGraph('Heating'))
  history.commit(history.graph())
  expect(history.canUndo()).toBe(false)
  history.commit(updateNode(history.graph(), 'trigger-1', { position: { x: 90, y: 110 } }))
  history.undo()
  expect(history.graph().nodes[0].position).toEqual({ x: 60, y: 100 })
  expect(history.canRedo()).toBe(true)
  history.redo()
  expect(history.graph().nodes[0].position).toEqual({ x: 90, y: 110 })
  history.undo()
  history.commit(removeNode(history.graph(), 'service-1'))
  expect(history.canRedo()).toBe(false)
  dispose()
}))
