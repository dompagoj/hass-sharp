import { createSignal } from 'solid-js'
import { useBeforeLeave, useSearchParams } from '@solidjs/router'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { VisualEditor } from '../visual/VisualEditor'

export function VisualAutomationView() {
  const [params] = useSearchParams()
  const [leave, setLeave] = createSignal<(() => void)>()
  useBeforeLeave(event => {
    event.preventDefault()
    setLeave(() => () => event.retry(true))
  })
  return <>
    <VisualEditor name={typeof params.name === 'string' && params.name.trim() ? params.name : 'Untitled visual automation'} />
    <Dialog open={!!leave()} title="Leave visual draft?" width="small" onClose={() => setLeave(undefined)} footer={<>
      <Button onClick={() => setLeave(undefined)}>Keep editing</Button>
      <Button variant="danger" onClick={() => { const retry = leave(); setLeave(undefined); retry?.() }}>Discard draft</Button>
    </>}><p>This graph has not been saved. Export the graph before leaving if you want to keep a copy.</p></Dialog>
  </>
}
