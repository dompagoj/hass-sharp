import { createSignal, onMount } from 'solid-js'
import { useBeforeLeave, useSearchParams, useParams, useNavigate } from '@solidjs/router'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { VisualEditor } from '../visual/VisualEditor'
import { libraryHref } from '../visual/library-api'

export function VisualAutomationView() {
  const [params, setParams] = useSearchParams()
  const route = useParams()
  const navigate = useNavigate()
  const [dirty, setDirty] = createSignal(false)
  const [leave, setLeave] = createSignal<(() => void)>()
  // Leaving through Discard changes also drops the draft the editor keeps of them.
  let discardDraft: (() => void) | undefined
  // Leaving with unsaved changes (the back button, a link, the browser's back) asks first.
  useBeforeLeave(event => {
    if (!dirty()) return
    event.preventDefault()
    setLeave(() => () => event.retry(true))
  })
  // `?drawer=runs` only says which tab to open with: the editor has read it by now, so it leaves the address and a
  // reload opens the editor as usual.
  onMount(() => { if (params.drawer !== undefined) setParams({ drawer: undefined }, { replace: true }) })
  return <>
    <VisualEditor documentId={route.id && route.id !== 'new' ? route.id : undefined} onDirtyChange={setDirty}
      discardRef={discard => (discardDraft = discard)}
      // A first save, or a copy saved instead of a conflicting revision, has an id of its own.
      onSaved={doc => { if (route.id !== doc.id) navigate(`/visual/${doc.id}`, { replace: true }) }}
      onBack={() => navigate('/automations')}
      onOpenLibrary={(id, line) => navigate(libraryHref(id, line))}
      drawer={params.drawer === 'runs' ? 'runs' : undefined}
      name={typeof params.name === 'string' && params.name.trim() ? params.name : 'Untitled visual automation'} />
    <Dialog open={!!leave()} title="Unsaved changes" width="small" onClose={() => setLeave(undefined)} footer={<>
      <Button onClick={() => setLeave(undefined)}>Keep editing</Button>
      <Button variant="danger" onClick={() => { const retry = leave(); setLeave(undefined); discardDraft?.(); retry?.() }}>Discard changes</Button>
    </>}><p>Leave and discard your unsaved changes?</p></Dialog>
  </>
}
