import { createComponent } from 'solid-js'
import type { HomeAssistant } from '../types'
import type { EditorAdapter, EditorTexts } from '../visual/adapter'
import type { VisualGraph } from '../visual/graph'
import { definitionsQueryKey, EntityApiError, entityApi, type EntitySaveResult } from './api'
import { DefinitionOverview } from './DefinitionPanel'
import { renameDefinition, savedSecrets } from './definitionEdits'
import { EntityHelp } from './EntityHelp'
import { definitionBody, definitionId, draftGraph, editorGraph, newGraphDefinition, type EntityDocument } from './model'

/** A saved graph definition as the editor holds it: the answer, with its editor graph (the whole definition in it). */
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
  overviewPeek: 'Entities, device and settings',
}

/** The code of the error `read` throws for a definition built from a class, which this editor does not edit. */
export const classDefinitionCode = 'class_definition'

/**
 * A saved definition as the editor holds it. Off is said by `enabled`: an answer's "Not active" for a definition the
 * person turned off is not an activation problem to show.
 */
const held = (document: EntityDocument): EntityEditorDocument => ({
  ...document,
  activationError: document.enabled ? document.activationError : null,
  graph: editorGraph(document),
})
const result = (saved: EntitySaveResult) => ({ ...saved, document: held(saved.document) })

/**
 * The editor of an entity definition built from steps: the `hass_sharp/entities` API, ids from the name (fixed at the
 * first save), a starter with one binary sensor named after the definition, `/entities` and `/entities/<id>`, drafts
 * and downloads without secret values, and the definition form as the inspector's overview. It has no Run now: an
 * entity's commands are tried from Home Assistant (ruling R1).
 *
 * `onClassDefinition` hears of a definition built from a class when `read` meets one; the read then fails with
 * {@link classDefinitionCode}, and the page shows that definition's own editor instead.
 */
export function entityAdapter(
  hass: Pick<HomeAssistant, 'callWS'>,
  options: { onClassDefinition?(document: EntityDocument): void } = {},
): EditorAdapter<EntityEditorDocument> {
  const api = entityApi(hass)
  return {
    document: 'entity',
    read: async id => {
      const document = await api.read(id)
      if (document.logic.kind !== 'graph') {
        options.onClassDefinition?.(document)
        throw new EntityApiError(classDefinitionCode, 'This entity definition is built from a class in a node library, not from steps.')
      }
      return held(document)
    },
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
    overview: context => createComponent(DefinitionOverview, { context }),
    help: EntityHelp,
  }
}
