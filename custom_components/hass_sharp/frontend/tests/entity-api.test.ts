import { describe, expect, it } from 'vitest'
import { entityApi, EntityApiError, entityError } from '../src/entities/api'
import { VisualApiError } from '../src/visual/api'
import { definitionBody, editorGraph, newGraphDefinition, type DefinitionBody } from '../src/entities/model'
import { visualFixture, type FixtureEntityType } from './visual-fixture'

const ok = (result: unknown) => ({ version: 1, ok: true, result })
const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
function client(answer: (request: any) => unknown = () => ok({})) {
  const messages: any[] = []
  const hass = { callWS: async (message: any) => { messages.push(structuredClone(message)); return answer(message.request) } }
  return { api: entityApi(hass as any), messages }
}
const body = (): DefinitionBody => ({
  name: 'Any motion', device: null, settings: [{ key: 'offset', value: 4, label: 'Offset', type: 'number' }],
  entities: [{ key: 'any_motion', domain: 'binary_sensor', name: 'Any motion' }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Any motion', nodes: [], edges: [] } },
})

describe('entity API client', () => {
  it('sends each operation as the backend reads it', async () => {
    const { api, messages } = client()
    await api.types()
    await api.list()
    await api.home('2026-11-18T00:00:00.000Z')
    await api.read('motion')
    await api.preview(body())
    await api.save('motion', body(), 0)
    await api.save('motion', body(), 0, false)
    await api.save('motion', body(), 3, true)
    await api.setEnabled('motion', false)
    await api.remove('motion', 4)
    expect(messages.map(m => m.type)).toEqual(Array(10).fill('hass_sharp/entities'))
    expect(messages.map(m => m.request)).toEqual([
      { operation: 'types' },
      { operation: 'list' },
      { operation: 'list', view: 'home', todayUtc: '2026-11-18T00:00:00.000Z' },
      { operation: 'read', id: 'motion' },
      { operation: 'preview', definition: body() },
      { operation: 'save', id: 'motion', expectedRevision: 0, definition: body() },
      { operation: 'save', id: 'motion', expectedRevision: 0, definition: body(), enabled: false },
      { operation: 'save', id: 'motion', expectedRevision: 3, definition: body(), enabled: true },
      { operation: 'set_enabled', id: 'motion', enabled: false },
      { operation: 'delete', id: 'motion', expectedRevision: 4 },
    ])
    expect(Object.keys(messages[5].request)).not.toContain('enabled')
  })
  it('runs a Trigger step of a definition by its id and answers the run id', async () => {
    const { api, messages } = client(() => ok({ version: 1, runId: 'run-7' }))
    await expect(api.run('motion', 'poll')).resolves.toEqual({ version: 1, runId: 'run-7' })
    expect(messages).toEqual([{ type: 'hass_sharp/entities', request: { operation: 'run', id: 'motion', triggerId: 'poll' } }])
  })
  it('answers the result of the envelope', async () => {
    const { api } = client(() => ok({ deleted: true }))
    await expect(api.remove('motion', 1)).resolves.toEqual({ deleted: true })
  })
  it('throws validation with its diagnostics and a sentence of its own', async () => {
    const detail = { valid: false, diagnostics: [{ nodeId: null, message: 'Entity lamp has the domain light', severity: 3, startLine: null, startColumn: null }], source: '', sourceMap: [] }
    const { api } = client(() => fail('validation', detail))
    const error = await api.save('motion', body(), 0).catch(e => e)
    expect(error).toBeInstanceOf(EntityApiError)
    expect(error).toMatchObject({ code: 'validation', detail, message: 'Fix the problems before saving.' })
    expect(entityError(error)).toBe('Fix the problems before saving.')
  })
  it('throws conflict and not_found with the backend sentence', async () => {
    const { api } = client(request => request.operation === 'read'
      ? fail('not_found', 'Entity definition not found')
      : fail('conflict', 'Revision conflict; reload the current document'))
    await expect(api.read('gone')).rejects.toMatchObject({ code: 'not_found', message: 'Entity definition not found' })
    await expect(api.save('motion', body(), 2)).rejects.toMatchObject({ code: 'conflict', message: 'Revision conflict; reload the current document' })
  })
  it('names the code when the detail is not a sentence', async () => {
    const { api } = client(() => fail('internal', { trace: 1 }))
    await expect(api.list()).rejects.toMatchObject({ code: 'internal', message: 'Entity definition request failed (internal).' })
  })
  it("passes Home Assistant's own error on as an EntityApiError", async () => {
    const hass = { callWS: async () => { throw { code: 'unauthorized', message: 'Administrator required' } } }
    const error = await entityApi(hass as any).list().catch(e => e)
    expect(error).toBeInstanceOf(EntityApiError)
    expect(error).toMatchObject({ code: 'unauthorized', detail: 'Administrator required', message: 'Administrator required' })
    expect(entityError(error)).toBe('Administrator required')
  })
  it('is a VisualApiError, so the editor session handles its conflicts and validation', async () => {
    const error = new EntityApiError('conflict', 'Revision conflict; reload the current document')
    expect(error).toBeInstanceOf(VisualApiError)
    expect(error).toBeInstanceOf(EntityApiError)
    expect(new VisualApiError('conflict', 'x')).not.toBeInstanceOf(EntityApiError)
    expect(new EntityApiError('validation', {})).toMatchObject({ code: 'validation', detail: {}, message: 'Fix the problems before saving.' })
  })
  it('rethrows an Error as it is', async () => {
    const thrown = new Error('Socket closed')
    const hass = { callWS: async () => { throw thrown } }
    await expect(entityApi(hass as any).list()).rejects.toBe(thrown)
    expect(entityError(thrown)).toBe('Socket closed')
  })
  it('passes a lost connection on in both forms the websocket library rejects with', async () => {
    const rejecting = (value: unknown) => entityApi({ callWS: async () => { throw value } } as any).list().catch(e => e)
    const result = await rejecting({ type: 'result', success: false, error: { code: 3, message: 'Connection lost' } })
    expect(result).toBeInstanceOf(EntityApiError)
    expect(result).toMatchObject({ code: '3', detail: 'Connection lost', message: 'Connection lost' })
    expect(entityError(result)).toBe('Connection lost')
    const bare = await rejecting(3)
    expect(bare).toBeInstanceOf(EntityApiError)
    expect(bare).toMatchObject({ code: '3', message: 'The connection to Home Assistant was lost.' })
    expect(entityError(bare)).toBe('The connection to Home Assistant was lost.')
    expect(entityError(undefined)).toBe('Check your connection and try again.')
  })
})

describe('the in-memory entities backend', () => {
  const tokenType: FixtureEntityType = {
    id: 'library:shelly:NodeLib.FakePlug', sourceId: 'shelly', typeExpression: 'global::NodeLib.FakePlug', kind: 'device', domain: null,
    title: 'FakePlug', description: null, settings: [
      { key: 'Host', label: 'Host', description: null, type: 'string', secret: false, options: [] },
      { key: 'Password', label: 'Password', description: null, type: 'string', secret: true, options: [] },
    ],
  }
  const secretBody = (): DefinitionBody => ({ ...body(), settings: [...body().settings!, { key: 'token', value: 'hunter2', label: 'Token', type: 'string', secret: true }] })
  const setup = () => { const fixture = visualFixture({ entityTypes: [tokenType] }); return { fixture, api: entityApi(fixture as any) } }

  it('saves with revisions, answers a stale save with a conflict and reads not_found for an unknown id', async () => {
    const { fixture, api } = setup()
    const saved = await api.save('motion', body(), 0)
    expect(saved).toMatchObject({ id: 'motion', saved: true, active: true, activationError: null })
    expect(saved.document).toMatchObject({ id: 'motion', revision: 1, name: 'Any motion', enabled: true, status: 'running', automationId: 'entity:motion', currentRevisionActive: true })
    expect(saved.document.registeredEntities).toEqual([{ uniqueId: 'motion:any_motion', entityId: 'binary_sensor.motion_any_motion', domain: 'binary_sensor', name: 'Any motion', disabled: false }])
    expect(saved.document.liveEntities).toEqual([{ uniqueId: 'motion:any_motion', key: 'any_motion', domain: 'binary_sensor', name: 'Any motion', entityId: 'binary_sensor.motion_any_motion' }])
    expect(saved.document.logic).toEqual({ kind: 'graph', type: null, graph: body().logic.kind === 'graph' && body().logic.graph })
    await expect(api.save('motion', body(), 0)).rejects.toMatchObject({ code: 'conflict' })
    expect((await api.save('motion', body(), 1)).document.revision).toBe(2)
    await expect(api.read('gone')).rejects.toMatchObject({ code: 'not_found', message: 'Entity definition not found' })
    await expect(api.remove('motion', 1)).rejects.toMatchObject({ code: 'conflict' })
    await expect(api.remove('motion', 2)).resolves.toEqual({ deleted: true })
    expect(fixture.entities.documents.size).toBe(0)
    expect(fixture.entities.requests.map(r => r.operation)).toEqual(['save', 'save', 'save', 'read', 'delete', 'delete'])
  })
  it('masks a secret in every answer and keeps the stored value', async () => {
    const { fixture, api } = setup()
    const saved = await api.save('motion', secretBody(), 0)
    const masked = { key: 'token', value: null, label: 'Token', type: 'string', secret: true, hasValue: true }
    expect(saved.document.settings[1]).toEqual(masked)
    expect((await api.read('motion')).settings[1]).toEqual(masked)
    expect((await api.list())[0].settings[1]).toEqual(masked)
    expect(JSON.stringify(await api.home('2026-11-18T00:00:00.000Z'))).not.toContain('hunter2')
    expect(fixture.entities.documents.get('motion')!.settings[1].value).toBe('hunter2')
    // Sent back through the editor graph: keep.
    await api.save('motion', definitionBody(editorGraph(await api.read('motion'))), 1)
    expect(fixture.entities.requests.at(-1).definition.settings[1]).toEqual({ key: 'token', label: 'Token', type: 'string', secret: true, keep: true })
    expect(fixture.entities.documents.get('motion')!.settings[1]).toEqual({ key: 'token', value: 'hunter2', label: 'Token', type: 'string', secret: true })
    // Sent back as answered (hasValue, no value): kept too; a value replaces; null without hasValue clears.
    await api.save('motion', { ...body(), settings: [masked] }, 2)
    expect(fixture.entities.documents.get('motion')!.settings[0].value).toBe('hunter2')
    await api.save('motion', { ...body(), settings: [{ key: 'token', value: '', type: 'string', secret: true }] }, 3)
    expect(fixture.entities.documents.get('motion')!.settings[0].value).toBe('')
    expect((await api.read('motion')).settings[0]).toEqual({ key: 'token', value: null, type: 'string', secret: true, hasValue: true })
    await api.save('motion', { ...body(), settings: [{ key: 'token', value: null, type: 'string', secret: true, hasValue: false }] }, 4)
    expect(fixture.entities.documents.get('motion')!.settings[0].value).toBeNull()
    expect((await api.read('motion')).settings[0].hasValue).toBe(false)
  })
  it('masks every setting of a key that one of them marks secret, as the real answer does', async () => {
    const { fixture, api } = setup()
    // Only a file edited by hand holds a key twice.
    fixture.entities.seed('twin', { ...body(), settings: [{ key: 'token', type: 'string', secret: true, value: 'hunter2' }, { key: 'token', type: 'string', value: 'twin-hunter2' }] })
    expect((await api.read('twin')).settings).toEqual([
      { key: 'token', type: 'string', secret: true, value: null, hasValue: true },
      { key: 'token', type: 'string', secret: true, value: null, hasValue: true },
    ])
    expect(JSON.stringify(await api.list())).not.toContain('hunter2')
  })
  it('keeps a stored secret flag and refuses keep with a value', async () => {
    const { fixture, api } = setup()
    await api.save('motion', secretBody(), 0)
    await api.save('motion', { ...body(), settings: [{ key: 'token', keep: true }] }, 1)
    expect(fixture.entities.documents.get('motion')!.settings).toEqual([{ key: 'token', value: 'hunter2', secret: true }])
    await expect(api.save('motion', { ...body(), settings: [{ key: 'token', value: 'x', keep: true }] }, 2)).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(api.preview({ ...body(), settings: [{ key: 'token', value: 'x', keep: true }] })).rejects.toMatchObject({ code: 'invalid_request' })
  })
  it('masks the secret settings its class declares, or every setting when the class is gone', async () => {
    const { fixture, api } = setup()
    const plug: DefinitionBody = { name: 'Plug one', settings: [{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', value: 'pw' }], logic: { kind: 'class', type: tokenType.id } }
    await api.save('plug1', plug, 0)
    expect(fixture.entities.documents.get('plug1')!.settings).toEqual(plug.settings)
    expect((await api.read('plug1')).settings).toEqual([{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', value: null, secret: true, hasValue: true }])
    // A setting the class does not declare (renamed since, or a stray) is never answered in clear.
    await api.save('plug1', { ...plug, settings: [...plug.settings!, { key: 'Old', value: 'stray' }] }, 1)
    expect((await api.read('plug1')).settings[2]).toEqual({ key: 'Old', value: null, secret: true, hasValue: true })
    expect(JSON.stringify(await api.list())).not.toContain('stray')
    await api.save('plug1', plug, 2)
    fixture.entities.types.length = 0
    expect((await api.read('plug1')).settings).toEqual([{ key: 'Host', value: null, secret: true, hasValue: true }, { key: 'Password', value: null, secret: true, hasValue: true }])
    // Sent back as answered: every value kept.
    await api.save('plug1', { ...plug, settings: (await api.read('plug1')).settings }, 3)
    expect(fixture.entities.documents.get('plug1')!.settings.map(s => s.value)).toEqual(['10.0.0.1', 'pw'])
  })
  it('lists the home view with seeded runs and registry entries, and switches definitions on and off', async () => {
    const { fixture, api } = setup()
    fixture.entities.seed('motion', body())
    fixture.entities.seed('plug1', { name: 'Plug one', settings: [], logic: { kind: 'class', type: tokenType.id } }, { enabled: false })
    fixture.entities.registry.set('plug1', [{ uniqueId: 'plug1:relay', entityId: 'switch.plug1_relay', domain: 'switch', name: null, disabled: false }])
    fixture.entities.runs.set('motion', { runsToday: 2, lastRunUtc: '2026-11-18T08:00:00Z', recentRuns: [{ runId: 'r1', status: 'Succeeded', startUtc: '2026-11-18T08:00:00Z', durationMs: 4 }] })
    const home = await api.home('2026-11-18T00:00:00.000Z')
    expect(home.map(d => [d.id, d.status, d.runsToday, d.failedLastDay, d.recentRuns.length])).toEqual([['motion', 'running', 2, 0, 1], ['plug1', 'off', 0, 0, 0]])
    expect(home[1]).toMatchObject({ activationError: 'Not active', liveEntities: [], registeredEntities: [{ uniqueId: 'plug1:relay' }] })
    expect(home[0]).not.toHaveProperty('source')
    expect((await api.list())[0]).not.toHaveProperty('runsToday')
    await expect(api.setEnabled('plug1', true)).resolves.toEqual({ id: 'plug1', enabled: true, active: true, activationError: null })
    expect((await api.read('plug1')).status).toBe('running')
    await expect(api.save('plug1', { name: 'Plug one', settings: [], logic: { kind: 'class', type: tokenType.id } }, 1, false)).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(api.types()).resolves.toEqual({ types: [tokenType], generation: 'saved-fixture' })
  })
  it('previews valid unless a test seeds diagnostics, and refuses such a save', async () => {
    const { fixture, api } = setup()
    expect(await api.preview(body())).toMatchObject({ valid: true, diagnostics: [] })
    fixture.entities.controls.diagnostics = [{ nodeId: 's', message: 'On is required', severity: 3, startLine: null, startColumn: null, inputId: 'is_on' }]
    expect(await api.preview(body())).toMatchObject({ valid: false, diagnostics: fixture.entities.controls.diagnostics, source: '', sourceMap: [] })
    await expect(api.save('motion', body(), 0)).rejects.toMatchObject({ code: 'validation', detail: { valid: false, diagnostics: fixture.entities.controls.diagnostics } })
    expect(fixture.entities.documents.size).toBe(0)
  })
  it('refuses an id outside the definition id rule on every operation that takes one', async () => {
    const { fixture, api } = setup()
    for (const id of ['Any motion', '_motion', 'new:1', 'a'.repeat(81), '']) {
      await expect(api.save(id, { ...body(), logic: { kind: 'class', type: tokenType.id }, entities: null }, 0)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
      await expect(api.save(id, body(), 0)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
      await expect(api.read(id)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
      await expect(api.setEnabled(id, true)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
      await expect(api.remove(id, 1)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
    }
    expect(fixture.entities.documents.size).toBe(0)
    await expect(api.save('a'.repeat(80), body(), 0)).resolves.toMatchObject({ saved: true })
    await expect(api.save('0-motion_2', body(), 0)).resolves.toMatchObject({ saved: true })
  })
  it('refuses to save the id new, which is the new-definition page, as the real save does', async () => {
    const { fixture, api } = setup()
    await expect(api.save('new', body(), 0)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID: new is reserved' })
    await expect(api.save('new', { ...body(), logic: { kind: 'class', type: tokenType.id }, entities: null }, 0)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID: new is reserved' })
    expect(fixture.entities.documents.has('new')).toBe(false)
    await expect(api.read('new')).rejects.toMatchObject({ code: 'not_found' })
  })
  it('refuses a save in the order the real API does', async () => {
    const { fixture, api } = setup()
    await api.save('motion', body(), 0)
    const errors = [{ nodeId: 's', message: 'On is required', severity: 3, startLine: null, startColumn: null, inputId: 'is_on' }]
    fixture.entities.controls.diagnostics = errors
    // A graph is previewed first: an invalid one is refused as such, stale or not, with or without a revision.
    await expect(api.save('motion', body(), 0)).rejects.toMatchObject({ code: 'validation' })
    await expect(fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'save', id: 'motion', definition: body() } })).resolves.toMatchObject({ ok: false, error: { code: 'validation' } })
    await expect(api.save('Bad id', body(), 0)).rejects.toMatchObject({ code: 'validation' })
    await expect(api.save('motion', { ...body(), settings: [{ key: 'token', value: 'x', keep: true }] }, 0)).rejects.toMatchObject({ code: 'invalid_request' })
    // A class is not: its revision is checked before its validation.
    const plug: DefinitionBody = { name: '', settings: [], logic: { kind: 'class', type: tokenType.id } }
    await expect(api.save('motion', plug, 0)).rejects.toMatchObject({ code: 'conflict' })
    await expect(api.save('plug', plug, 0)).rejects.toMatchObject({ code: 'validation', detail: { diagnostics: [{ code: 'definition.name' }] } })
    fixture.entities.controls.diagnostics = []
    // A valid graph: a missing revision, then a stale one, then enabled for a saved definition.
    await expect(fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'save', id: 'motion', definition: body() } })).resolves.toEqual({ version: 1, ok: false, error: { code: 'invalid_request', detail: 'expectedRevision is required: the revision this replaces, 0 for a new definition' } })
    await expect(api.save('motion', body(), 0, false)).rejects.toMatchObject({ code: 'conflict' })
    await expect(api.save('motion', body(), 1, false)).rejects.toMatchObject({ code: 'invalid_request', message: /enabled can only be given/ })
    await expect(fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'delete', id: 'motion' } })).resolves.toMatchObject({ ok: false, error: { code: 'invalid_request' } })
  })
  it('checks the document as the real API does, before the graph', async () => {
    const { fixture, api } = setup()
    fixture.entities.controls.diagnostics = [{ nodeId: 's', message: 'On is required', severity: 3, startLine: null, startColumn: null }]
    const codes = async (definition: DefinitionBody) => (await api.preview(definition)).diagnostics.map(d => d.code)
    expect(await codes({ ...body(), name: '' })).toEqual(['definition.name'])
    expect(await codes({ ...body(), name: 'x'.repeat(121) })).toEqual(['definition.name'])
    expect(await codes({ ...body(), entities: [] })).toEqual(['definition.entitiesRequired'])
    expect(await codes({ ...body(), entities: [{ key: 'Door', domain: 'binary_sensor' }, { key: 'door', domain: 'binary_sensor' }, { key: 'door', domain: 'binary_sensor' }, { key: 'door', domain: 'binary_sensor' }] }))
      .toEqual(['definition.entityKey', 'definition.duplicateEntity'])
    const keyProblem = (await api.preview({ ...body(), entities: [{ key: 'Door', domain: 'binary_sensor' }] })).diagnostics[0]
    expect(keyProblem).toEqual({ nodeId: null, message: 'Entity key Door is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters', severity: 3, startLine: null, startColumn: null, inputId: 'Door', portId: null, code: 'definition.entityKey' })
    // With no document problem, the seeded graph diagnostics.
    expect(await api.preview(body())).toMatchObject({ valid: false, diagnostics: [{ message: 'On is required' }] })
    expect(await codes(newGraphDefinition('x'.repeat(120)))).toEqual([undefined])
  })
  it('answers a valid save with its warnings', async () => {
    const { fixture, api } = setup()
    const warning = { nodeId: 'a', message: 'Home Assistant has no entity binary_sensor.motion_a', severity: 2, startLine: null, startColumn: null, inputId: 'entity' }
    fixture.entities.controls.diagnostics = [warning]
    expect(await api.preview(body())).toMatchObject({ valid: true, diagnostics: [warning] })
    expect((await api.save('motion', body(), 0)).diagnostics).toEqual([warning])
    fixture.entities.controls.diagnostics = []
    expect(await api.save('motion', body(), 1)).not.toHaveProperty('diagnostics')
  })
  it('runs a definition as the test says: running, failed in its start, not loaded or off', async () => {
    const { fixture, api } = setup()
    const state = (d: { status: string; active: boolean; currentRevisionActive: boolean; activationError: string | null }) => [d.status, d.active, d.currentRevisionActive, d.activationError]
    let saved = await api.save('motion', body(), 0)
    expect([saved.active, saved.activationError, ...state(saved.document)]).toEqual([true, null, 'running', true, true, null])
    fixture.entities.controls.activation = 'failed'
    fixture.entities.controls.activationError = 'Start threw: no network'
    saved = await api.save('motion', body(), 1)
    expect([saved.active, saved.activationError, ...state(saved.document)]).toEqual([true, 'Start threw: no network', 'failed', true, true, 'Start threw: no network'])
    // Its start threw after its entities were made: they are listed (and shown unavailable in Home Assistant).
    expect(saved.document.liveEntities.map(e => e.uniqueId)).toEqual(['motion:any_motion'])
    fixture.entities.controls.activation = 'notLoaded'
    fixture.entities.controls.activationError = 'The node libraries have errors; fix them first'
    saved = await api.save('motion', body(), 2)
    // On and not loaded, for a reason: failed, as EntityApi.EntityJson answers it.
    expect([saved.active, saved.activationError, ...state(saved.document)]).toEqual([false, 'The node libraries have errors; fix them first', 'failed', false, false, 'The node libraries have errors; fix them first'])
    expect(saved.document.enabled).toBe(true)
    // Off by the user: "Not active" in the document, no error in the switch's answer.
    await expect(api.setEnabled('motion', false)).resolves.toEqual({ id: 'motion', enabled: false, active: false, activationError: null })
    const off = await api.read('motion')
    expect([off.enabled, ...state(off)]).toEqual([false, 'off', false, false, 'Not active'])
    fixture.entities.controls.activation = 'running'
    await expect(api.setEnabled('motion', true)).resolves.toEqual({ id: 'motion', enabled: true, active: true, activationError: null })
    const seeded = fixture.entities.seed('seeded', body(), { enabled: false })
    expect(state(seeded)).toEqual(['off', false, false, 'Not active'])
    const fresh = await api.save('fresh', body(), 0, false)
    expect([fresh.active, fresh.activationError, fresh.document.status]).toEqual([false, null, 'off'])
  })
  it('answers a host that is setting up or stopping as EntityApi does: setup as it is, a stopped one as off', async () => {
    const { fixture, api } = setup()
    const state = (d: { status: string; active: boolean; currentRevisionActive: boolean; activationError: string | null; liveEntities: unknown[] }) =>
      [d.status, d.active, d.currentRevisionActive, d.activationError, d.liveEntities.length]
    fixture.entities.controls.activation = 'setup'
    // Its Start() still running: its entities are made and registered, no error.
    const starting = fixture.entities.seed('starting', body())
    expect(state(starting)).toEqual(['setup', true, true, null, 1])
    expect(state(await api.read('starting'))).toEqual(['setup', true, true, null, 1])
    expect((await api.read('starting')).registeredEntities.map(e => e.uniqueId)).toEqual(['starting:any_motion'])
    const saved = await api.save('starting', body(), 1)
    expect([saved.active, saved.activationError, saved.document.status]).toEqual([true, null, 'setup'])
    // A host that is stopping is no host: off, with no live entities, though its script is still loaded.
    fixture.entities.controls.activation = 'stopped'
    expect(state(await api.save('starting', body(), 2).then(s => s.document))).toEqual(['off', true, true, null, 0])
    expect(state(fixture.entities.seed('stopping', body()))).toEqual(['off', true, true, null, 0])
    // Off by the user it is off as ever.
    await api.setEnabled('stopping', false)
    expect(state(await api.read('stopping'))).toEqual(['off', false, false, 'Not active', 0])
  })
  it("answers an entity's disabled flag from the registry entry a test seeded for it", async () => {
    const { fixture, api } = setup()
    const two: DefinitionBody = { ...body(), entities: [...body().entities!, { key: 'tamper', domain: 'binary_sensor', name: 'Tamper' }] }
    fixture.entities.registry.set('motion', [{ uniqueId: 'motion:tamper', entityId: 'binary_sensor.motion_tamper', domain: 'binary_sensor', name: 'Tamper', disabled: true }])
    // Seeded entries are its registry while seeded.
    expect((await api.save('motion', two, 0)).document.registeredEntities.map(e => [e.uniqueId, e.disabled])).toEqual([['motion:tamper', true]])
    // What its load registered keeps the flag: Home Assistant keeps an entry's disabled_by when it registers it again.
    fixture.entities.registry.delete('motion')
    expect((await api.read('motion')).registeredEntities.map(e => [e.uniqueId, e.disabled])).toEqual([['motion:any_motion', false], ['motion:tamper', true]])
    await api.save('motion', two, 1)
    expect((await api.read('motion')).registeredEntities.map(e => [e.uniqueId, e.disabled])).toEqual([['motion:any_motion', false], ['motion:tamper', true]])
  })
  it("lists a class definition's entities from what its class reports, and keeps them registered while it is off", async () => {
    const plugType: FixtureEntityType = { ...tokenType, id: 'library:shelly:NodeLib.Plug', entities: [{ key: 'relay', domain: 'switch', name: null }, { key: 'power', domain: 'sensor', name: 'Power' }] }
    const fixture = visualFixture({ entityTypes: [plugType] }), api = entityApi(fixture as any)
    // The types answer is the real descriptor: no entities.
    const { entities: _entities, ...descriptor } = plugType
    expect((await api.types()).types).toStrictEqual([descriptor])
    const plug: DefinitionBody = { name: 'Plug one', settings: [], logic: { kind: 'class', type: plugType.id } }
    const saved = await api.save('plug1', plug, 0)
    expect(saved.document.liveEntities).toEqual([
      { uniqueId: 'plug1:relay', key: 'relay', domain: 'switch', name: null, entityId: 'switch.plug1_relay' },
      { uniqueId: 'plug1:power', key: 'power', domain: 'sensor', name: 'Power', entityId: 'sensor.plug1_power' },
    ])
    const registered = [
      { uniqueId: 'plug1:power', entityId: 'sensor.plug1_power', domain: 'sensor', name: 'Power', disabled: false },
      { uniqueId: 'plug1:relay', entityId: 'switch.plug1_relay', domain: 'switch', name: null, disabled: false },
    ]
    expect(saved.document.registeredEntities).toEqual(registered)
    // Failed in its start: still listed.
    fixture.entities.controls.activation = 'failed'
    expect((await api.save('plug1', plug, 1)).document.liveEntities).toHaveLength(2)
    // Off, or not loaded: no live entities, the registry's as they were.
    await api.setEnabled('plug1', false)
    expect(await api.read('plug1')).toMatchObject({ status: 'off', liveEntities: [], registeredEntities: registered })
    fixture.entities.controls.activation = 'notLoaded'
    await api.setEnabled('plug1', true)
    expect(await api.read('plug1')).toMatchObject({ status: 'failed', liveEntities: [], registeredEntities: registered })
    // Entries a test seeds win; a delete forgets them all.
    fixture.entities.registry.set('plug1', [{ ...registered[1], disabled: true }])
    expect((await api.read('plug1')).registeredEntities).toEqual([{ ...registered[1], disabled: true }])
    await api.remove('plug1', 2)
    fixture.entities.controls.activation = 'notLoaded'
    expect((await api.save('plug1', plug, 0)).document.registeredEntities).toEqual([])
    // A seeded running class definition reports its entities too.
    fixture.entities.controls.activation = 'running'
    expect(fixture.entities.seed('plug2', plug).liveEntities.map(e => e.entityId)).toEqual(['switch.plug2_relay', 'sensor.plug2_power'])
    expect((await api.read('plug2')).registeredEntities.map(e => e.uniqueId)).toEqual(['plug2:power', 'plug2:relay'])
  })
  it('refuses a field of the wrong type with the real messages', async () => {
    const { fixture, api } = setup()
    const send = (request: Record<string, unknown>) => fixture.callWS({ type: 'hass_sharp/entities', request })
    const refused = (detail: string) => ({ version: 1, ok: false, error: { code: 'invalid_request', detail } })
    const classBody = { name: 'Plug one', settings: [], logic: { kind: 'class', type: tokenType.id } }
    expect(await send({ operation: 'save', id: 7, expectedRevision: 0, definition: classBody })).toEqual(refused('id must be a string'))
    expect(await send({ operation: 'save', id: 'plug', expectedRevision: 1.5, definition: classBody })).toEqual(refused('expectedRevision must be a whole number'))
    expect(await send({ operation: 'save', id: 'plug', expectedRevision: '0', definition: classBody })).toEqual(refused('expectedRevision must be a whole number'))
    expect(await send({ operation: 'save', id: 'plug', expectedRevision: 0, definition: [] })).toEqual(refused('definition must be an object'))
    expect(await send({ operation: 'save', id: 'plug', expectedRevision: 0, definition: classBody, enabled: 'no' })).toEqual(refused('enabled must be true or false'))
    // Read before the id's form: a bad enabled is refused first.
    expect(await send({ operation: 'save', id: 'Bad id', expectedRevision: 0, definition: classBody, enabled: 1 })).toEqual(refused('enabled must be true or false'))
    expect(await send({ operation: 'save', id: 'plug', expectedRevision: 0, definition: classBody, enabled: null })).toMatchObject({ ok: true })
    expect(await send({ operation: 'read', id: ['plug'] })).toEqual(refused('id must be a string'))
    expect(await send({ operation: 'read' })).toEqual(refused('id is required'))
    expect(await send({ operation: 'set_enabled', id: 'plug', enabled: 'true' })).toEqual(refused('enabled must be true or false'))
    expect(await send({ operation: 'set_enabled', id: 'plug' })).toEqual(refused('enabled is required'))
    expect(await send({ operation: 'delete', id: 'plug', expectedRevision: 'one' })).toEqual(refused('expectedRevision must be a whole number'))
    expect(await send({ operation: 'list', view: 3 })).toEqual(refused('view must be a string'))
    expect(await send({ operation: 'list', view: 'all' })).toEqual(refused('Unknown list view; the entity definitions list has the view home'))
    expect(await send({ operation: 'run' })).toEqual(refused('id is required'))
    expect(await send({ operation: 'run', id: 'plug', triggerId: 3 })).toEqual(refused('triggerId must be a string'))
    expect(await send({ operation: 'start' })).toEqual(refused('Unknown entity operation'))
    expect(fixture.entities.documents.size).toBe(1)
  })
  it("answers a graph's Trigger steps as Python does, each runnable only while its saved revision runs", async () => {
    const { fixture, api } = setup()
    const trigger = (id: string, config: Record<string, unknown> = {}) => ({ id, kind: 'trigger', config: { type: 'time_pattern', ...config } })
    const steps = (): DefinitionBody => ({ ...body(), logic: { kind: 'graph', graph: { version: 5, name: 'Any motion', edges: [],
      nodes: [trigger('poll'), trigger('quiet', { manual: false }), { id: 'log', kind: 'log', config: {} }] } } })
    const saved = await api.save('motion', steps(), 0)
    const answer = (runnable: boolean) => [
      { triggerId: 'poll', method: 'Run_706F6C6C', manual: true, runnable },
      { triggerId: 'quiet', method: 'Run_7175696574', manual: false, runnable: false },
    ]
    expect(saved.document.triggers).toEqual(answer(true))
    expect((await api.read('motion')).triggers).toEqual(answer(true))
    expect((await api.list())[0].triggers).toEqual(answer(true))
    await api.setEnabled('motion', false)
    expect((await api.read('motion')).triggers).toEqual(answer(false))
    fixture.entities.controls.activation = 'setup'
    await api.setEnabled('motion', true)
    expect((await api.read('motion')).triggers).toEqual(answer(false))
    // A class has no steps.
    fixture.entities.seed('plug', { name: 'Plug', settings: [], logic: { kind: 'class', type: tokenType.id } })
    expect((await api.read('plug')).triggers).toEqual([])
  })
  it('runs a Trigger step of a running definition, and refuses the rest in the words EntityApi does', async () => {
    const { fixture, api } = setup()
    const steps = (): DefinitionBody => ({ ...body(), logic: { kind: 'graph', graph: { version: 5, name: 'Any motion', edges: [], nodes: [
      { id: 'poll', kind: 'trigger', config: { type: 'time_pattern' } }, { id: 'quiet', kind: 'trigger', config: { manual: false } }, { id: 'log', kind: 'log', label: 'Write it down', config: {} }] } } })
    await api.save('motion', steps(), 0)
    await expect(api.run('motion', 'poll')).resolves.toEqual({ version: 1, runId: 'manual-entity-run' })
    expect(fixture.entities.controls.runs).toEqual([{ id: 'motion', triggerId: 'poll' }])
    const refused = async (id: string, triggerId: string, message: string) =>
      expect(api.run(id, triggerId)).rejects.toMatchObject({ code: 'invalid_request', message })
    await refused('motion', 'gone', 'Trigger not found')
    await refused('motion', 'log', 'Only a Trigger step can be run by hand; Write it down is not one')
    await refused('motion', 'quiet', 'Trigger does not permit manual execution')
    await expect(api.run('other', 'poll')).rejects.toMatchObject({ code: 'not_found', message: 'Entity definition not found' })
    fixture.entities.seed('plug', { name: 'Plug', settings: [], logic: { kind: 'class', type: tokenType.id } })
    await refused('plug', 'poll', 'A class definition has no steps to run')
    fixture.entities.controls.activation = 'failed'
    fixture.entities.controls.activationError = 'Start threw: no network'
    await api.save('motion', steps(), 1)
    await refused('motion', 'poll', 'The saved revision of this entity definition is not running: Start threw: no network')
    await api.setEnabled('motion', false)
    await refused('motion', 'poll', 'The entity definition is off')
    expect(fixture.entities.controls.runs).toHaveLength(1)
  })
  it('previews first only a graph Python reads as one: an object', async () => {
    const { fixture, api } = setup()
    fixture.entities.controls.diagnostics = [{ nodeId: 's', message: 'On is required', severity: 3, startLine: null, startColumn: null }]
    const arrayGraph = { ...body(), logic: { kind: 'graph', graph: [] } } as unknown as DefinitionBody
    // Not previewed first: the id's form is refused before the validation.
    await expect(api.save('Bad id', arrayGraph, 0)).rejects.toMatchObject({ code: 'invalid_request', message: 'Invalid definition ID' })
    await expect(api.save('Bad id', body(), 0)).rejects.toMatchObject({ code: 'validation' })
    await expect(api.save('motion', arrayGraph, 0)).rejects.toMatchObject({ code: 'validation' })
  })
})
