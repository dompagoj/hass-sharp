import { createEffect, createMemo, For, on, Show } from 'solid-js'
import type { VisualDiagnostic } from '../visual/api'
import type { EditorContext } from '../visual/adapter'
import type { VisualCatalogSnapshot } from '../visual/contract'
import { Glyph } from '../visual/Glyph'
import type { VisualGraph } from '../visual/graph'
import { icons } from '../visual/icons'
import type { EntityEditorDocument } from './adapter'
import { DeleteDefinition } from './DeleteDefinition'
import { definitionOf, definitionProblems } from './definitionEdits'
import { SettingRows } from './SettingRows'

/** An entity of the saved definition with the entity id Home Assistant gave it. */
export interface DefinitionLiveEntity { key: string; entityId: string | null }

export interface DefinitionPanelProps {
  /** The editor graph: the steps, and the whole definition under `definition`. */
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics: VisualDiagnostic[]
  /**
   * A problem chosen in the Problems tab: its row opens, and focus goes into it (`setting:<key>`), or the list of
   * problems no row shows takes it (`problems`). A problem about an entity is on its Entity node, which it selects.
   */
  located?: { target: string }
  /** Changes the graph as one undo step. */
  commit(graph: VisualGraph, options?: { coalesce?: string }): void
  /** Says what a change also did (a setting's value a new type removed). */
  onNotice?(text: string): void
}

/**
 * The definition form, where the inspector shows an overview with no step selected: the settings of an entity
 * definition, whose entities are its Entity nodes and whose devices are its Device nodes. It edits
 * `graph.definition` through `commit` only, so undo, unsaved changes, drafts and conflicts cover it as they cover the
 * steps. The problems about no step are shown here: a setting's on its row, the rest above the form.
 */
export function DefinitionPanel(props: DefinitionPanelProps) {
  const definition = () => definitionOf(props.graph)
  const problems = createMemo(() => definitionProblems(props.diagnostics, definition()))
  const commit = (graph: VisualGraph) => props.commit(graph)
  // A problem chosen in the Problems tab that no row shows: the list at the top takes the focus.
  let list: HTMLUListElement | undefined
  createEffect(on(() => props.located, located => { if (located?.target === 'problems') queueMicrotask(() => list?.focus()) }))
  return (
    <div class="hs-def">
      <Show when={problems().others.length}>
        <ul ref={list} class="hs-def-problems" aria-label="Problems of this entity definition" tabindex="-1">
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
      <SettingRows graph={props.graph} catalog={props.catalog} problems={problems().settings} commit={commit} onNotice={props.onNotice}
        located={props.located} />
    </div>
  )
}

/**
 * The saved definition's entities with their Home Assistant ids: the running ones, else the entity registry's. Only those
 * it declares, whose unique id is `<id>:<key>`: an entity a C# device's code added (`<id>:<device key>:<key>`) is no
 * step's, and is never taken for the Entity node of its key.
 */
export function liveEntitiesOf(document: EntityEditorDocument | undefined): DefinitionLiveEntity[] {
  if (!document) return []
  const prefix = `${document.id}:`
  if (document.liveEntities.length) return document.liveEntities.filter(e => e.uniqueId === `${prefix}${e.key}`).map(e => ({ key: e.key, entityId: e.entityId }))
  return document.registeredEntities.filter(e => e.uniqueId.startsWith(prefix) && !e.uniqueId.slice(prefix.length).includes(':'))
    .map(e => ({ key: e.uniqueId.slice(prefix.length), entityId: e.entityId }))
}

/** The entity adapter's overview: whether the saved definition is off, the definition form, and Delete. */
export function DefinitionOverview(props: { context: EditorContext<EntityEditorDocument> }) {
  const context = props.context
  return (
    <>
      <Show when={context.enabled === false}>
        <p class="hs-insp-off" role="note"><strong>Off:</strong> its entities are unavailable in Home Assistant and none of its steps run.</p>
      </Show>
      <DefinitionPanel graph={context.graph()} catalog={context.catalog} diagnostics={context.diagnostics} located={context.located}
        commit={context.commit} onNotice={text => context.notice({ text })} />
      <Show when={context.document}>
        {document => (
          <DeleteDefinition document={document()} name={context.graph().name} blocked={context.dirty || context.busy || context.conflict}
            hint={context.dirty ? 'Save or undo your changes before deleting it.' : undefined} onError={text => context.notice({ text, error: true })} />
        )}
      </Show>
    </>
  )
}
