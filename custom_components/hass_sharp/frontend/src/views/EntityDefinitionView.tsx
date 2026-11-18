import { createSignal, Show } from 'solid-js'
import { A, useParams, useSearchParams } from '@solidjs/router'
import { useHass } from '../context'
import { entityAdapter } from '../entities/adapter'
import type { EntityDocument } from '../entities/model'
import { EditorView } from './VisualAutomationView'

/**
 * An entity definition's page: `/entities/new` (`?logic=graph`, the default, or `?logic=class`; `?name=` names it)
 * and `/entities/<id>`. One built from steps opens in the visual editor with the definition form; one built from a
 * class opens in {@link ClassDefinitionPlaceholder}.
 */
export function EntityDefinitionView() {
  const route = useParams()
  const [params] = useSearchParams()
  // A saved definition is known to be a class's only once it is read: the editor's read says so, and this page then
  // shows the class's page instead.
  const [classDocument, setClassDocument] = createSignal<EntityDocument>()
  const adapter = entityAdapter(useHass(), { onClassDefinition: setClassDocument })
  const newClass = () => route.id === 'new' && params.logic === 'class'
  const savedClass = () => { const document = classDocument(); return document && document.id === route.id ? document : undefined }
  const name = () => (typeof params.name === 'string' && params.name.trim() ? params.name : undefined)
  return (
    <Show when={!newClass() && !savedClass()} fallback={<ClassDefinitionPlaceholder document={savedClass()} name={name()} />}>
      <EditorView adapter={adapter} />
    </Show>
  )
}

/**
 * THE SEAM FOR TASK 8: where an entity definition built from a class is edited, a new one (`/entities/new?logic=class`)
 * or a saved one (`document`). For now it only says so; Task 8 replaces this component with its `ClassDefinitionEditor`.
 */
function ClassDefinitionPlaceholder(props: { document?: EntityDocument; name?: string }) {
  return (
    <section class="hs-page hs-def-class" aria-label="Entity definition">
      <h1 class="hs-page-heading">{props.document?.name ?? props.name ?? 'New entity definition'}</h1>
      <p>Entity definitions built from a class in a node library cannot be edited here yet.</p>
      <p><A href="/entities">Back to entities</A></p>
    </section>
  )
}
