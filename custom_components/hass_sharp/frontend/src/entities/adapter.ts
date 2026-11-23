import { createComponent, type Accessor } from 'solid-js'
import type { HomeAssistant } from '../types'
import type { EditorAdapter, EditorTexts } from '../visual/adapter'
import type { VisualGraph } from '../visual/graph'
import { definitionDeviceKeys, definitionEntityKeys } from '../visual/contract'
import { definitionsQueryKey, EntityApiError, entityApi, type EntitySaveResult } from './api'
import { DefinitionOverview, liveEntitiesOf } from './DefinitionPanel'
import { DefinitionRunNow } from './DefinitionRunNow'
import { DevicePanel } from './DevicePanel'
import { attentionOf, nodeAttention } from './deviceCounts'
import type { DeviceCounts } from './deviceUi'
import { icons } from '../visual/icons'
import { copyNotice, entityRemoval, optionProblems, problemRow, renameDefinition, savedSecrets } from './definitionEdits'
import { EntityHelp } from './EntityHelp'
import { definitionBody, definitionId, deviceEntities, draftGraph, editorGraph, newGraphDefinition, type EntityDocument } from './model'

/** A saved definition as the editor holds it: the answer, with its editor graph (the whole definition in it). */
export type EntityEditorDocument = EntityDocument & { graph: VisualGraph }

/** The entity definition editor's words. */
export const entityTexts: EditorTexts = {
  editorLabel: 'Entity definition editor',
  canvasLabel: 'Entity definition canvas',
  untitled: 'Untitled entity definition',
  nameLabel: 'Entity definition name',
  back: 'Back to entities',
  overviewLabel: 'Entity definition',
  problemsHeading: 'This entity definition',
  runsEmpty: 'Save this entity definition to see its runs here.',
  exportFileName: 'entity-definition.graph.json',
  enabledOn: 'On: its entities are in Home Assistant and its steps run',
  enabledOff: 'Off: its entities are unavailable in Home Assistant and none of its steps run',
  conflictMessage: 'This entity definition changed elsewhere while you had unsaved changes here.',
  conflictSaveTitle: 'The saved entity definition changed elsewhere: choose what to keep',
  conflictTitle: 'This entity definition changed elsewhere',
  conflictElsewhere: 'It was saved again somewhere else while you had unsaved changes here. Saving a node library does this to every entity definition that uses it.',
  conflictDraft: time => `Your unsaved changes from ${time} were made before this entity definition was saved again somewhere else.`,
  copyNoun: 'a new entity definition',
  overviewPeek: 'Settings',
  runsNone: 'Runs appear here when one of its entities is commanded, when it starts, or when a trigger with run history fires.',
  runRevision: 'Revision',
  traceBack: 'Back to the entity definition’s runs',
  traceUntitled: 'Entity definition run',
  traceEarlier: 'The canvas shows the entity definition as it was for this run, not as it is now.',
  traceMissing: 'The entity definition as it was for this run is no longer stored. Its summary is below.',
  traceNoRecord: 'No step-by-step record was kept for this run (records are kept for the newest 1,000 runs). The canvas shows the entity definition as it was for this run.',
  traceForeign: 'This run is not one of this entity definition’s runs.',
  traceDeleted: 'This entity definition no longer exists.',
}

/**
 * A saved definition as the editor holds it. Off is said by `enabled`: an answer's "Not active" for a definition the
 * person turned off is not an activation problem to show, and its saved revision, unloaded while it is off, is no
 * reason to save it again (an automation that is off stays loaded; a save of an off definition would not start it).
 */
const held = (document: EntityDocument): EntityEditorDocument => ({
  ...document,
  activationError: document.enabled ? document.activationError : null,
  currentRevisionActive: document.enabled ? document.currentRevisionActive : true,
  graph: editorGraph(document),
})
/**
 * A save's answer as the editor takes it. A definition that is off is not started by a save (and a copy is saved off):
 * its answer's `active: false` is no failed activation to report.
 */
const result = (saved: EntitySaveResult) => ({ ...saved, active: saved.document.enabled ? saved.active : true, document: held(saved.document) })

/**
 * The editor of an entity definition: the `hass_sharp/entities` API, ids from the name (fixed at the
 * first save), a starter with one binary sensor named after the definition, `/entities` and `/entities/<id>`, drafts
 * and downloads without secret values, Run now for its Trigger steps, and the definition form as the inspector's
 * overview. Nothing else is run by hand: an entity's commands are tried from Home Assistant (ruling R1).
 *
 * The entity definition's page reads a definition before it opens this editor; `handed` gives this editor that read
 * for its first read of the definition (once), so it is not read twice. `counts`, the page's shared device page counts,
 * are what the Device tab's count and each C# node's badge show of the saved, running definition; without them, none.
 */
export function entityAdapter(
  hass: Pick<HomeAssistant, 'callWS'>,
  options: { handed?(id: string): EntityDocument | undefined; counts?: Accessor<DeviceCounts | undefined> } = {},
): EditorAdapter<EntityEditorDocument> {
  const api = entityApi(hass)
  return {
    document: 'entity',
    read: async id => held(options.handed?.(id) ?? await api.read(id)),
    // The entity API's check answers no source map: no line of the generated C# then selects a step.
    preview: async graph => { const preview = await api.preview(definitionBody(graph)); return { ...preview, sourceMap: preview.sourceMap ?? [] } },
    save: async (id, graph, expectedRevision, saveOptions) => result(await api.save(id, definitionBody(graph), expectedRevision, saveOptions?.enabled)),
    setEnabled: async (id, enabled, switchOptions = {}) => {
      const answer = await api.setEnabled(id, enabled)
      void switchOptions.queryClient?.invalidateQueries({ queryKey: [...definitionsQueryKey] })
      return answer.enabled
    },
    apiError: error => (error instanceof EntityApiError ? error : undefined),
    newId: async graph => definitionId(graph.name, (await api.list()).map(definition => definition.id)),
    starter: name => editorGraph(newGraphDefinition(name)),
    draft: draftGraph,
    savedGraph: (sent, document) => savedSecrets(sent, document),
    rename: (graph, name, state) => renameDefinition(graph, name, state.saved),
    listHref: '/entities',
    editorHref: id => `/entities/${encodeURIComponent(id)}`,
    runHref: (id, runId) => `/entities/${encodeURIComponent(id)}/runs/${encodeURIComponent(runId)}`,
    queryKeys: [definitionsQueryKey],
    texts: entityTexts,
    runGroup: context => createComponent(DefinitionRunNow, { context }),
    // The pages the running revision's C# nodes draw, read while the tab is shown; its count, the cards they wait on.
    drawerTabs: context => [{
      id: 'device', label: 'Device', lazy: true,
      get count() { const id = context.document?.id; return id ? attentionOf(options.counts?.(), id) : 0 },
      content: () => createComponent(DevicePanel, { get definitionId() { return context.document?.id }, get visible() { return context.drawerTab === 'device' } }),
    }],
    // By the key of a node of the graph being edited: the cards its page in the running revision waits on.
    attention: context => ({
      count: nodeKey => { const id = context.document?.id; return id ? nodeAttention(options.counts?.(), id, nodeKey) : 0 },
      tab: 'device',
    }),
    overview: context => createComponent(DefinitionOverview, { context }),
    // An Entity node's or a C# entity's key is fixed once saved; the line under an Entity node's gives the entity id Home
    // Assistant gave it. So is a Device node's or a C# device's: the saved entities and devices are those the nodes of
    // the graph as last read or saved declare, whatever the stored list of entities holds. A C# device's card lists the
    // entities its code added.
    savedEntities: context => {
      const document = context.document
      if (!document) return undefined
      const entityIds = Object.fromEntries(liveEntitiesOf(document).flatMap(e => (e.entityId ? [[e.key, e.entityId] as const] : [])))
      return {
        keys: definitionEntityKeys(document.graph, context.catalog), entityIds, deviceKeys: definitionDeviceKeys(document.graph, context.catalog),
        deviceEntities: deviceEntities(document),
      }
    },
    overviewIcon: icons.entity,
    // A problem about no step and no row of the form is listed at the form's top.
    overviewProblem: diagnostic => problemRow(diagnostic) ?? (diagnostic.nodeId || diagnostic.startLine != null ? undefined : 'problems'),
    check: optionProblems,
    // Deleting an Entity node that steps left behind still use asks first, and so does deleting a C# node that has stored
    // data in the saved definition (by the counts' `stored`; nothing of it before they are read).
    removal: (graph, ids, catalog, context) => {
      const document = context?.document, stored = options.counts?.()?.stored
      const keys = document && stored && Object.hasOwn(stored, document.id) ? stored[document.id] : undefined
      return entityRemoval(graph, ids, catalog, document && keys ? { saved: document.graph, keys } : undefined)
    },
    copyNotice,
    help: EntityHelp,
  }
}
