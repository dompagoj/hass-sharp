import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/graphV4'
import type { VisualDocument, VisualPreview } from '../src/visual/api'
import type { VisualGraph } from '../src/visual/graph'

export function visualFixture(
  options: { catalog?: VisualCatalogSnapshot; libraries?: { id: string; name: string; revision: number; source: string }[] } = {},
) {
  const documents = new Map<string, VisualDocument>()
  const calls: any[] = []
  const libraries = new Map((options.libraries ?? []).map(d => [d.id, { version: 1, ...d }]))
  let libraryRevision = 0
  const controls = {
    conflict: false,
    active: true,
    previewError: false,
    invalid: false,
    deleteConflict: false,
    preview: undefined as VisualPreview | undefined,
    catalog: options.catalog ?? nodeCatalog,
    generation: 'saved-fixture',
  }
  const makePreview = (_graph: VisualGraph): VisualPreview => ({
    valid: !controls.invalid,
    source: 'class VisualAutomation\n{\n  // generated on server\n}',
    sourceMap: [{ nodeId: 'trigger-1', startLine: 1, endLine: 4 }],
    diagnostics: controls.invalid ? [{ nodeId: 'trigger-1', message: 'Invalid entity', severity: 3, startLine: 3, startColumn: 2 }] : [],
  })
  const callWS = async (message: any) => {
    calls.push(structuredClone(message))
    if (message.type === 'hass_sharp/node_library') {
      const request = message.request
      if (request.operation === 'catalog')
        return {
          version: 1,
          ok: true,
          result: { catalog: controls.catalog, generation: controls.generation, saved: true, valid: true, diagnostics: [] },
        }
      if (request.operation === 'list')
        return { version: 1, ok: true, result: { documents: [...libraries.values()], generation: controls.generation } }
      if (request.operation === 'read') {
        const document = libraries.get(request.id)
        return document
          ? { version: 1, ok: true, result: document }
          : { version: 1, ok: false, error: { code: 'not_found', detail: 'Library missing' } }
      }
      const ok = (result: unknown) => ({ version: 1, ok: true, result })
      const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
      if (request.operation === 'save' || request.operation === 'delete') {
        const existing = libraries.get(request.id)
        if (controls.conflict || (existing?.revision ?? 0) !== request.expectedRevision) return fail('conflict', 'Library changed elsewhere')
        if (controls.invalid) return fail('validation', { diagnostics: [{ sourceId: request.id, startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, message: 'Fixture library validation error', severity: 3 }], automations: [] })
        if (request.operation === 'delete' && !existing) return fail('not_found', 'Library missing')
        const document = request.operation === 'save' ? { version: 1, id: request.id, name: request.name, source: request.source, revision: request.expectedRevision + 1 } : null
        if (document) libraries.set(document.id, document); else libraries.delete(request.id)
        controls.generation = `saved-fixture-${++libraryRevision}`
        const automations = [...documents.values()].map(prior => {
          const updated = { ...prior, revision: prior.revision + 1, catalogGeneration: controls.generation, currentRevisionActive: controls.active, activeRevision: controls.active ? prior.revision + 1 : prior.activeRevision }
          documents.set(prior.id, updated)
          return { id: prior.id, saved: true, document: updated, active: controls.active, activationError: controls.active ? null : 'Fixture activation failed' }
        })
        return ok({ saved: true, deleted: !document, document, generation: controls.generation, catalog: controls.catalog, automations })
      }
      if (request.generation && request.generation !== controls.generation) return fail('conflict', 'Saved library generation changed')
      if (controls.previewError) throw new Error('Library fixture offline')
      const sources = new Map([...libraries.values()].map(d => [d.id, d.source]))
      for (const other of request.sources ?? []) sources.set(other.id, other.source)
      sources.set(request.id, request.source)
      const identity = JSON.stringify([...sources].sort()) + controls.generation
      let hash = 2166136261
      for (let i = 0; i < identity.length; i++) hash = Math.imul(hash ^ identity.charCodeAt(i), 16777619)
      const wrap = (data: unknown) => ok({ id: request.id, workspaceVersion: request.workspaceVersion, generation: controls.generation, sourceGeneration: `fixture-source-${hash >>> 0}`, saved: false, data })
      const range = (source: string, start: number, length: number) => {
        const position = (offset: number) => { const lines = source.slice(0, offset).split('\n'); return { line: lines.length, column: lines.at(-1)!.length + 1 } }
        const a = position(start), b = position(start + length)
        return { start, length, startLine: a.line, startColumn: a.column, endLine: b.line, endColumn: b.column }
      }
      if (request.operation === 'preview') {
        const diagnostics = [...sources].flatMap(([id, source]) => {
          const start = source.indexOf('MissingName')
          return start < 0 ? [] : [{ sourceId: id, ...range(source, start, 11), severity: 3, message: "The name 'MissingName' does not exist in the current context (fixture)" }]
        })
        return wrap({ valid: !diagnostics.length, diagnostics, catalog: controls.catalog })
      }
      const before = request.source.slice(0, request.position ?? 0)
      const word = /[A-Za-z_][A-Za-z_0-9]*$/.exec(before)?.[0] ?? ''
      if (request.operation === 'completion') {
        const members = before.includes('entity.') || before.includes('state.') ? ['State', 'Attributes', 'EntityId', 'LastChanged', 'LastUpdated', 'LastReported'] : ['State', 'Describe', 'Make', 'Reading', 'EntityStateSnapshot']
        return wrap(members.filter(name => name.toLowerCase().startsWith(word.toLowerCase())).map(name => ({ displayText: name, displayTextPrefix: '', displayTextSuffix: '', filterText: name, tags: ['Property'], textEdit: { start: before.length - word.length, length: word.length, newText: name }, additionalTextEdits: [] })))
      }
      if (request.operation === 'hover') return wrap({ text: 'EntityStateSnapshot.State : string\nThe detached Home Assistant entity state (fixture).', range: range(request.source, before.length - word.length, Math.max(1, word.length)) })
      if (request.operation === 'signature') {
        const argumentsText = before.slice(before.lastIndexOf('(') + 1)
        const argument = argumentsText.split(',').at(-1) ?? ''
        const activeParameter = /count\s*:/.test(argument) ? 1 : /label\s*:/.test(argument) ? 0 : argumentsText.split(',').length - 1
        return wrap({ signatures: [{ label: 'Reading Factory.Make(string? label, int count = 1)', documentation: '<summary>Create a reading (fixture).</summary>', parameters: ['string? label', 'int count = 1'] }], activeSignature: 0, activeParameter })
      }
      if (request.operation === 'definition') {
        const target = [...sources].find(([, source]) => source.includes('Reading Describe'))
        const start = target?.[1].indexOf('Describe') ?? -1
        return wrap(target && start >= 0 ? [{ id: target[0], range: range(target[1], start, 'Describe'.length) }] : [])
      }
      if (request.operation === 'format') return wrap(request.source.replace(/\s*=>\s*/g, ' => '))
      throw new Error(`Unsupported library fixture operation ${request.operation}`)
    }
    if (message.type === 'get_services') return { light: { turn_on: { name: 'Turn on', description: 'Turn on a light' } } }
    if (message.type === 'hass_sharp/run_history') return { version: 1, offset: 0, limit: 20, runs: [] }
    if (message.type === 'hass_sharp/run_now') return { version: 1, runId: 'manual-visual-run' }
    if (message.type === 'hass_sharp/get_entities') return { entities: [] }
    if (message.type === 'hass_sharp/reload_entities') return { success: true }
    const req = message.request
    let result: unknown
    const fail = (code: string, detail: unknown) => ({ version: 1, ok: false, error: { code, detail } })
    if (req.operation === 'preview') {
      if (controls.previewError) throw new Error('Preview offline')
      result = controls.preview ?? makePreview(req.graph)
    } else if (req.operation === 'list') result = [...documents.values()]
    else if (req.operation === 'read') {
      if (!documents.has(req.id)) return fail('not_found', 'Document missing')
      result = documents.get(req.id)
    } else if (req.operation === 'save') {
      if (controls.conflict || (documents.get(req.id)?.revision ?? 0) !== req.expectedRevision) return fail('conflict', 'Changed elsewhere')
      const preview = makePreview(req.graph)
      if (!preview.valid) return fail('validation', preview)
      const document: VisualDocument = {
        version: 1,
        id: req.id,
        revision: req.expectedRevision + 1,
        graph: structuredClone(req.graph),
        source: preview.source,
        sourceMap: preview.sourceMap,
        currentRevisionActive: controls.active,
        active: controls.active,
        catalogGeneration: 'saved-fixture',
        activeRevision: controls.active ? req.expectedRevision + 1 : null,
        automationId: `visual:${req.id}`,
      }
      documents.set(req.id, document)
      result = { document, active: controls.active, activationError: controls.active ? null : 'Compilation failed during activation' }
    } else if (req.operation === 'delete') {
      if (controls.deleteConflict || documents.get(req.id)?.revision !== req.expectedRevision) return fail('conflict', 'Changed elsewhere')
      documents.delete(req.id)
      result = { deleted: true }
    } else throw new Error(`Unexpected request ${JSON.stringify(message)}`)
    return { version: 1, ok: true, result }
  }
  return {
    documents,
    libraries,
    calls,
    controls,
    callWS,
    connection: {
      subscribeMessage: async (callback: (value: unknown) => void) => {
        callback({
          'update.became_available': { fields: {}, target: { entity: { domain: 'update' } } },
          'battery.started_charging': { fields: {}, target: { entity: { domain: 'sensor' } } },
          'temperature.changed': { fields: {}, target: { entity: { domain: 'sensor' } } },
        })
        return () => {}
      },
    },
  }
}
