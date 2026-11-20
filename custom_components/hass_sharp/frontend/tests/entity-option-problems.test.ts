import { describe, expect, it } from 'vitest'
import shared from '../../../../tests/fixtures/entity-option-problems.json'
import { optionProblems, readNumber } from '../src/entities/definitionEdits'
import { editorGraph, type DefinitionBody, type DefinitionEntity } from '../src/entities/model'
import type { VisualDiagnostic } from '../src/visual/api'
import { nodeCatalog } from '../src/visual/contract'
import { visualFixture } from './visual-fixture'

// The problems of one Entity node's options, as the server answers them (tests/Runtime's EntityStoreTests reads the
// same cases): each on the node (`nodeId`), about `option:<option>`, in the labels' words.
type Expected = { code: string; option: string; message?: string; messageStartsWith?: string }
type Case = { what: string; local?: boolean; key: string; domain: string; name?: string; options?: unknown; problems: Expected[] }
const cases = shared.cases as Case[]

const entityOf = (c: Case) => ({ key: c.key, domain: c.domain, ...(c.name ? { name: c.name } : {}), ...(c.options !== undefined ? { options: c.options } : {}) }) as DefinitionEntity
const body = (c: Case): DefinitionBody => ({
  name: 'Options', settings: [],
  logic: { kind: 'graph', graph: { version: 5, name: 'Options', nodes: [{ id: 'e', kind: 'entity', config: entityOf(c) as unknown as Record<string, unknown> }], edges: [] } },
})
function same(got: VisualDiagnostic[], want: Expected[]) {
  expect(got.map(d => ({ code: d.code, inputId: d.inputId, portId: d.portId, nodeId: d.nodeId, severity: d.severity }))).toEqual(
    want.map(w => ({ code: w.code, inputId: `option:${w.option}`, portId: null, nodeId: 'e', severity: 3 })))
  got.forEach((d, i) => {
    if (want[i].message !== undefined) expect(d.message).toBe(want[i].message)
    else expect(d.message.startsWith(want[i].messageStartsWith!)).toBe(true)
  })
}

describe('the problems of an Entity node\'s options', () => {
  it('has cases to check', () => {
    expect(cases.length).toBeGreaterThan(20)
    expect(cases.filter(c => c.local).length).toBeGreaterThanOrEqual(4)
  })

  it.each(cases.filter(c => c.local).map(c => [c.what, c]))('the panel finds %s itself, in the server\'s words', (_what, c) => {
    same(optionProblems(editorGraph(body(c as Case)), nodeCatalog), (c as Case).problems)
  })

  it('finds nothing wrong with a camera\'s seconds between frames at either end of its range', () => {
    for (const frame_interval of [0.5, 1, 60])
      expect(optionProblems(editorGraph(body({ what: '', key: 'front_door', domain: 'camera', options: { frame_interval }, problems: [] })), nodeCatalog), String(frame_interval)).toEqual([])
  })

  it.each(cases.map(c => [c.what, c]))('the in-memory backend answers %s as the server does', async (_what, c) => {
    const answer = await visualFixture().callWS({ type: 'hass_sharp/entities', request: { operation: 'preview', definition: body(c as Case) } })
    expect(answer.ok).toBe(true)
    expect(answer.result.valid).toBe(false)
    same(answer.result.diagnostics, (c as Case).problems)
  })
})

describe('a number option as typed', () => {
  it('reads digits, a sign, a fraction and an exponent with a point; an empty text as none, and anything else as no number', () => {
    const typed = ['-0', '-0.', '-0.5', '.5', '1', '+2', '-3', '1.5', '1e1', '1e3', '1E-2', ' 7 ']
    expect(typed.map(text => readNumber(text))).toEqual([-0, -0, -0.5, 0.5, 1, 2, -3, 1.5, 10, 1000, 0.01, 7])
    expect(['', '   '].map(text => readNumber(text))).toEqual([null, null])
    // Unfinished, with a comma or a unit, or past what a number holds: not a number, so nothing is committed for it.
    for (const text of ['-', '.', '1e', '1e+', '1,5', '5 W', '0x10', 'Infinity', '1e999']) expect(readNumber(text), text).toBeUndefined()
  })
})
