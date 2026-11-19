import { describe, expect, it } from 'vitest'
import shared from '../../../../tests/fixtures/entity-option-problems.json'
import { optionProblems } from '../src/entities/definitionEdits'
import { editorGraph, type DefinitionBody, type DefinitionEntity } from '../src/entities/model'
import type { VisualDiagnostic } from '../src/visual/api'
import { nodeCatalog } from '../src/visual/contract'
import { visualFixture } from './visual-fixture'

// The problems of one entity's options, as the server answers them (tests/Runtime's EntityStoreTests reads the same
// cases): each about the entity's key and the option's, in the labels' words. A case marked node gives the entity as an
// Entity node, whose problems are on it (`nodeId`), about `option:<option>`.
type Expected = { code: string; inputId: string; portId: string | null; message?: string; messageStartsWith?: string }
type Case = { what: string; local?: boolean; node?: boolean; key: string; domain: string; name?: string; options?: unknown; problems: Expected[] }
const cases = shared.cases as Case[]

const entityOf = (c: Case) => ({ key: c.key, domain: c.domain, ...(c.name ? { name: c.name } : {}), ...(c.options !== undefined ? { options: c.options } : {}) }) as DefinitionEntity
const body = (c: Case): DefinitionBody => ({
  name: 'Options', device: null, settings: [], entities: c.node ? [] : [entityOf(c)],
  logic: { kind: 'graph', graph: { version: 5, name: 'Options', nodes: c.node ? [{ id: 'e', kind: 'entity', config: entityOf(c) as unknown as Record<string, unknown> }] : [], edges: [] } },
})
function same(got: VisualDiagnostic[], want: Expected[], node = false) {
  expect(got.map(d => ({ code: d.code, inputId: d.inputId, portId: d.portId, nodeId: d.nodeId, severity: d.severity }))).toEqual(
    want.map(w => node ? { code: w.code, inputId: `option:${w.portId}`, portId: null, nodeId: 'e', severity: 3 } : { code: w.code, inputId: w.inputId, portId: w.portId, nodeId: null, severity: 3 }))
  got.forEach((d, i) => {
    if (want[i].message !== undefined) expect(d.message).toBe(want[i].message)
    else expect(d.message.startsWith(want[i].messageStartsWith!)).toBe(true)
  })
}

describe('the problems of an entity\'s options', () => {
  it('has cases to check', () => {
    expect(cases.length).toBeGreaterThan(20)
    expect(cases.filter(c => c.local).length).toBeGreaterThanOrEqual(4)
    // Each case of a listed entity has its node form, but options that are not an object (the node's Options field says so).
    expect(cases.filter(c => c.node).map(c => c.what)).toEqual(cases.filter(c => !c.node && c.problems.every(p => p.portId !== null)).map(c => `${c.what} (on its Entity node)`))
  })

  it.each(cases.filter(c => c.local).map(c => [c.what, c]))('the panel finds %s itself, in the server\'s words', (_what, c) => {
    same(optionProblems(editorGraph(body(c as Case)), nodeCatalog), (c as Case).problems, (c as Case).node)
  })

  it('finds nothing wrong with a camera\'s seconds between frames at either end of its range', () => {
    for (const frame_interval of [0.5, 1, 60])
      for (const node of [false, true])
        expect(optionProblems(editorGraph(body({ what: '', node, key: 'front_door', domain: 'camera', options: { frame_interval }, problems: [] })), nodeCatalog), String(frame_interval)).toEqual([])
  })

  it.each(cases.map(c => [c.what, c]))('the in-memory backend answers %s as the server does', async (_what, c) => {
    const answer = await visualFixture().callWS({ type: 'hass_sharp/entities', request: { operation: 'preview', definition: body(c as Case) } })
    expect(answer.ok).toBe(true)
    expect(answer.result.valid).toBe(false)
    same(answer.result.diagnostics, (c as Case).problems, (c as Case).node)
  })
})
