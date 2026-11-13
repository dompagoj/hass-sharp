import { createSignal } from 'solid-js'
import { useBeforeLeave, useSearchParams, useParams, useNavigate } from '@solidjs/router'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { VisualEditor } from '../visual/VisualEditor'

export function VisualAutomationView() {
  const [params] = useSearchParams()
  const route = useParams()
  const navigate = useNavigate()
  const [dirty, setDirty] = createSignal(false)
  const [leave, setLeave] = createSignal<(() => void)>()
  // Leaving with unsaved changes (the back button, a link, the browser's back) asks first.
  useBeforeLeave(event => {
    if (!dirty()) return
    event.preventDefault()
    setLeave(() => () => event.retry(true))
  })
  return <>
    <VisualEditor documentId={route.id && route.id !== 'new' ? route.id : undefined} onDirtyChange={setDirty}
      onSaved={doc => { if (route.id === 'new') navigate(`/visual/${doc.id}`, { replace: true }) }}
      onBack={() => navigate('/automations')}
      name={typeof params.name === 'string' && params.name.trim() ? params.name : 'Untitled visual automation'} />
    <Dialog open={!!leave()} title="Unsaved changes" width="small" onClose={() => setLeave(undefined)} footer={<>
      <Button onClick={() => setLeave(undefined)}>Keep editing</Button>
      <Button variant="danger" onClick={() => { const retry = leave(); setLeave(undefined); retry?.() }}>Discard changes</Button>
    </>}><p>Leave and discard your unsaved changes? Export the graph from the "⋯" menu first if you want to keep a copy.</p></Dialog>
  </>
}
