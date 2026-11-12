import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/graphV4'
import type { VisualDocument, VisualPreview } from '../src/visual/api'
import type { VisualGraph } from '../src/visual/graph'

export function visualFixture(
  options: { catalog?: VisualCatalogSnapshot; libraries?: { id: string; name: string; revision: number; source: string }[] } = {},
) {
  const documents = new Map<string, VisualDocument>()
  const calls: any[] = []
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
        return { version: 1, ok: true, result: { documents: options.libraries ?? [], generation: controls.generation } }
      if (request.operation === 'read') {
        const document = options.libraries?.find(d => d.id === request.id)
        return document
          ? { version: 1, ok: true, result: document }
          : { version: 1, ok: false, error: { code: 'not_found', detail: 'Library missing' } }
      }
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
