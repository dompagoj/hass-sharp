import { expect, it } from 'vitest'
import { visualFixture } from './visual-fixture'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import type { VisualCatalogSnapshot } from '../src/visual/contract'
it('provides realistic isolated library language envelopes and revisioned CRUD for browser acceptance', async () => {
  const fixture = visualFixture({ catalog: rich.catalog as unknown as VisualCatalogSnapshot, libraries: [{ id: 'shared', name: 'Shared.cs', source: rich.source, revision: 1 }] })
  const call = (operation: string, fields = {}) => fixture.callWS({ type: 'hass_sharp/node_library', request: { operation, ...fields } }) as Promise<any>
  const context = { id: 'shared', source: '// 😀\nentity.Sta', generation: 'saved-fixture', workspaceVersion: 'draft-1', sources: [] }
  const completion = await call('completion', { ...context, position: context.source.length })
  expect(completion.result.data[0]).toMatchObject({ displayText: 'State', textEdit: { start: context.source.length - 3, length: 3, newText: 'State' } })
  const signature = await call('signature', { ...context, source: 'Factory.Make(count: 1, label: null)', position: 21 })
  expect(signature.result.data.activeParameter).toBe(1)
  expect(signature.result).toMatchObject({ id: 'shared', generation: 'saved-fixture', workspaceVersion: 'draft-1', saved: false })
  const definition = await call('definition', { ...context, id: 'helper', source: 'Samples.Describe(entity)', position: 12 })
  expect(definition.result.data[0].id).toBe('shared')
  const saved = await call('save', { id: 'shared', name: 'Renamed.cs', source: rich.source, expectedRevision: 1 })
  expect(saved.result.document).toMatchObject({ id: 'shared', name: 'Renamed.cs', revision: 2 })
  expect((await call('save', { id: 'shared', name: 'Old.cs', source: rich.source, expectedRevision: 1 })).error.code).toBe('conflict')
  expect((await call('delete', { id: 'shared', expectedRevision: 2 })).result.deleted).toBe(true)
})
