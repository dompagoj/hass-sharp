import { createMemo, For, Show } from 'solid-js'
import type { VisualDiagnostic } from '../visual/api'
import type { EditorContext } from '../visual/adapter'
import type { VisualCatalogSnapshot } from '../visual/contract'
import { Glyph } from '../visual/Glyph'
import type { VisualGraph } from '../visual/graph'
import { icons } from '../visual/icons'
import type { EntityEditorDocument } from './adapter'
import { DeleteDefinition } from './DeleteDefinition'
import { definitionOf, definitionProblems } from './definitionEdits'
import { DeviceForm } from './DeviceForm'
import { EntityRows } from './EntityRows'
import { SettingRows } from './SettingRows'

/** An entity of the saved definition with the entity id Home Assistant gave it. */
export interface DefinitionLiveEntity { key: string; entityId: string | null }

export interface DefinitionPanelProps {
  /** The editor graph: the steps, and the whole definition under `definition`. */
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics: VisualDiagnostic[]
  /** Saved at least once: entity keys are part of the entities' identity in Home Assistant and no longer change. */
  saved: boolean
  /** Changes the graph as one undo step. */
  commit(graph: VisualGraph, options?: { coalesce?: string }): void
  /** The saved definition's entities as Home Assistant has them (running, else as its entity registry keeps them). */
  liveEntities?: DefinitionLiveEntity[]
  /** Says what a change also did (the steps an entity's new type no longer fits). */
  onNotice?(text: string): void
}

/**
 * The definition form, where the inspector shows an overview with no step selected: the entities, the device and the
 * settings of an entity definition built from steps. It edits `graph.definition` through `commit` only, so undo,
 * unsaved changes, drafts and conflicts cover it as they cover the steps. The problems about no step are shown here:
 * an entity's or a setting's on its row, the rest above the form.
 */
export function DefinitionPanel(props: DefinitionPanelProps) {
  const definition = () => definitionOf(props.graph)
  const problems = createMemo(() => definitionProblems(props.diagnostics, definition()))
  const entityIds = createMemo(() => new Map((props.liveEntities ?? []).flatMap(e => (e.entityId ? [[e.key, e.entityId] as const] : []))))
  const commit = (graph: VisualGraph) => props.commit(graph)
  return (
    <div class="hs-def">
      <Show when={problems().others.length}>
        <ul class="hs-def-problems" aria-label="Problems of this entity definition">
          <For each={problems().others}>
            {problem => (
              <li classList={{ 'hs-def-problems__item': true, 'hs-def-problems__item--warning': problem.severity < 3 }}>
                <Glyph paths={problem.severity === 3 ? icons.problem : icons.warning} />
                <span>{problem.message}</span>
              </li>
            )}
          </For>
        </ul>
      </Show>
      <EntityRows graph={props.graph} catalog={props.catalog} problems={problems().entities} saved={props.saved} commit={commit}
        entityIds={entityIds()} onNotice={props.onNotice} />
      <DeviceForm graph={props.graph} commit={commit} />
      <SettingRows graph={props.graph} catalog={props.catalog} problems={problems().settings} commit={commit} />
    </div>
  )
}

/** The saved definition's entities with their Home Assistant ids: the running ones, else the entity registry's. */
export function liveEntitiesOf(document: EntityEditorDocument | undefined): DefinitionLiveEntity[] {
  if (!document) return []
  if (document.liveEntities.length) return document.liveEntities.map(e => ({ key: e.key, entityId: e.entityId }))
  const prefix = `${document.id}:`
  return document.registeredEntities.filter(e => e.uniqueId.startsWith(prefix)).map(e => ({ key: e.uniqueId.slice(prefix.length), entityId: e.entityId }))
}

/** The entity adapter's overview: whether the saved definition is off, the definition form, and Delete. */
export function DefinitionOverview(props: { context: EditorContext<EntityEditorDocument> }) {
  const context = props.context
  return (
    <>
      <Show when={context.enabled === false}>
        <p class="hs-insp-off" role="note"><strong>Off:</strong> its entities are unavailable in Home Assistant and none of its steps run.</p>
      </Show>
      <DefinitionPanel graph={context.graph()} catalog={context.catalog} diagnostics={context.diagnostics} saved={context.saved}
        commit={context.commit} liveEntities={liveEntitiesOf(context.document)} onNotice={text => context.notice({ text })} />
      <Show when={context.document}>
        {document => <DeleteDefinition document={document()} name={context.graph().name} blocked={context.dirty || context.busy || context.conflict} onError={text => context.notice({ text, error: true })} />}
      </Show>
    </>
  )
}
