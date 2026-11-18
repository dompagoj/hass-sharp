import { createSignal, onMount } from 'solid-js'
import { useSearchParams, useParams, useNavigate } from '@solidjs/router'
import { LeaveGuard } from '../components/LeaveGuard'
import { useHass } from '../context'
import { VisualEditor } from '../visual/VisualEditor'
import { automationAdapter, type EditorAdapter, type EditorDocument } from '../visual/adapter'
import { libraryHref } from '../visual/library-api'

export function VisualAutomationView() {
  const adapter = automationAdapter(useHass())
  return <EditorView adapter={adapter} />
}

/**
 * The editor page of one kind of document, at the adapter's `editorHref` (`:id`, or `new` for a new one, named by
 * `?name=`): it asks before leaving unsaved changes, and goes to a new document's own address once it is saved.
 */
export function EditorView<D extends EditorDocument>(props: { adapter: EditorAdapter<D> }) {
  const adapter = props.adapter
  const [params, setParams] = useSearchParams()
  const route = useParams()
  const navigate = useNavigate()
  const [dirty, setDirty] = createSignal(false)
  // Leaving through Discard changes also drops the draft the editor keeps of them.
  let discardDraft: (() => void) | undefined
  // `?drawer=runs` only says which tab to open with: the editor has read it by now, so it leaves the address and a
  // reload opens the editor as usual.
  onMount(() => { if (params.drawer !== undefined) setParams({ drawer: undefined }, { replace: true }) })
  return <>
    <VisualEditor adapter={adapter} documentId={route.id && route.id !== 'new' ? route.id : undefined} onDirtyChange={setDirty}
      discardRef={discard => (discardDraft = discard)}
      // A first save, or a copy saved instead of a conflicting revision, has an id of its own.
      onSaved={doc => { if (route.id !== doc.id) navigate(adapter.editorHref(doc.id), { replace: true }) }}
      onBack={() => navigate(adapter.listHref)}
      onOpenLibrary={(id, line) => navigate(libraryHref(id, line))}
      drawer={params.drawer === 'runs' ? 'runs' : undefined}
      name={typeof params.name === 'string' && params.name.trim() ? params.name : adapter.texts.untitled} />
    <LeaveGuard dirty={dirty()} onDiscard={() => discardDraft?.()} />
  </>
}
