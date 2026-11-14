import { afterEach, expect, it, vi } from 'vitest'
import { createGraph, updateNode } from '../src/visual/graph'
import { draftLimit, listDrafts, readDraft, writeDraft } from '../src/visual/drafts'

const draft = (at: number, label = 'x') => ({ graph: updateNode(createGraph('Motion'), 'trigger-1', { label }), revision: 1, name: 'Motion', at })
const keys = () => Object.keys(localStorage).filter(k => k.startsWith('hass-sharp:draft:')).sort()
afterEach(() => vi.restoreAllMocks())

it(`keeps at most ${draftLimit} drafts, dropping the oldest`, () => {
  for (let i = 0; i < draftLimit + 5; i++) expect(writeDraft(`a${String(i).padStart(2, '0')}`, draft(1000 + i))).toBe(true)
  expect(keys()).toHaveLength(draftLimit)
  // The five oldest went; the newest is there.
  expect(readDraft('a00')).toBeUndefined()
  expect(readDraft('a04')).toBeUndefined()
  expect(readDraft('a05')).toBeDefined()
  expect(readDraft(`a${draftLimit + 4}`)).toBeDefined()
  // Rewriting one that is kept drops nothing.
  writeDraft('a10', draft(5000, 'again'))
  expect(keys()).toHaveLength(draftLimit)
  localStorage.setItem('someone-else', 'kept')
  writeDraft('b', draft(6000))
  expect(localStorage.getItem('someone-else')).toBe('kept')
})

it('keeps the time of a draft whose changes are the same, and takes the new time when they change', () => {
  writeDraft('k', draft(1000, 'one'))
  writeDraft('k', draft(2000, 'one'))
  expect(readDraft('k')?.at).toBe(1000)
  writeDraft('k', { ...draft(3000, 'one'), code: { nodeId: 'n', text: 'typed' } })
  expect(readDraft('k')?.at).toBe(3000)
  writeDraft('k', draft(4000, 'two'))
  expect(readDraft('k')?.at).toBe(4000)
})

it('prunes and tries once more when the storage is full', () => {
  for (let i = 0; i < 15; i++) writeDraft(`a${String(i).padStart(2, '0')}`, draft(1000 + i))
  const setItem = localStorage.setItem.bind(localStorage)
  let refused = 0, full = false
  const spy = vi.spyOn(localStorage, 'setItem').mockImplementation((key: string, value: string) => {
    // Full until older drafts are removed (or, with `full`, always).
    if (key.startsWith('hass-sharp:draft:') && (full || (keys().length > 9 && refused < 5))) { refused++; throw new DOMException('full', 'QuotaExceededError') }
    return setItem(key, value)
  })
  try {
    expect(writeDraft('new', draft(9000))).toBe(true)
    expect(refused).toBe(1)
    expect(readDraft('new')?.at).toBe(9000)
    expect(keys().length).toBeLessThanOrEqual(draftLimit / 2)
    // Still full after pruning: no draft, and no error.
    full = true
    expect(writeDraft('another', draft(9500))).toBe(false)
  } finally {
    spy.mockRestore()
  }
})

it('lists the drafts under a key prefix, newest first', () => {
  writeDraft('ann/new:1', draft(1000))
  writeDraft('ann/new:2', draft(3000))
  writeDraft('bob/new:3', draft(5000))
  writeDraft('ann/motion', draft(6000))
  expect(listDrafts('ann/new:').map(d => d.key)).toEqual(['ann/new:2', 'ann/new:1'])
})
