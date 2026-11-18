import { createEffect, createSignal, on, Show, untrack } from 'solid-js'
import { useNavigate, useParams, useSearchParams } from '@solidjs/router'
import { useHass } from '../context'
import { entityAdapter } from '../entities/adapter'
import { ClassDefinitionEditor } from '../entities/ClassDefinitionEditor'
import type { EntityDocument } from '../entities/model'
import { EditorView } from './VisualAutomationView'

/**
 * An entity definition's page: `/entities/new` (`?logic=graph`, the default, or `?logic=class`; `?name=` names it,
 * `?type=` chooses a class's) and `/entities/<id>`. One built from steps opens in the visual editor with the definition
 * form; one built from a class opens in {@link ClassDefinitionEditor}.
 */
export function EntityDefinitionView() {
  const route = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  // A saved definition is known to be a class's only once it is read: the editor's read says so (its adapter never
  // makes an editor graph of it), and this page then shows the class's page instead.
  const [classDocument, setClassDocument] = createSignal<EntityDocument>()
  const adapter = entityAdapter(useHass(), { onClassDefinition: setClassDocument })
  const newClass = () => route.id === 'new' && params.logic === 'class'
  const savedClass = () => { const document = classDocument(); return document && document.id === route.id ? document : undefined }
  const text = (value: unknown) => (typeof value === 'string' && value.trim() ? value : undefined)
  // The class page starts again for another definition, but not for the address its own first save goes to.
  const [page, setPage] = createSignal(1)
  let followed: string | undefined
  createEffect(on(() => route.id, id => { if (id !== followed) setPage(n => n + 1); followed = undefined }, { defer: true }))
  const saved = (document: EntityDocument) => {
    setClassDocument(document)
    if (route.id === document.id) return
    followed = document.id
    navigate(`/entities/${encodeURIComponent(document.id)}`, { replace: true })
  }
  return (
    <Show when={!newClass() && !savedClass()} fallback={
      <Show when={page()} keyed>
        {_page => <ClassDefinitionEditor document={untrack(savedClass)} name={untrack(() => text(params.name))} type={untrack(() => text(params.type))} onSaved={saved} />}
      </Show>
    }>
      <EditorView adapter={adapter} />
    </Show>
  )
}
