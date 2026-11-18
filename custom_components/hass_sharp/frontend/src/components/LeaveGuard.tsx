import { createSignal } from 'solid-js'
import { useBeforeLeave } from '@solidjs/router'
import { Button } from './Controls'
import { Dialog } from './Dialog'

/**
 * Leaving an editor page with unsaved changes (the back button, a link, the browser's back) asks first: Keep editing,
 * or Discard changes (`onDiscard`, then the page is left).
 */
export function LeaveGuard(props: { dirty: boolean; onDiscard?(): void }) {
  const [leave, setLeave] = createSignal<(() => void)>()
  useBeforeLeave(event => {
    if (!props.dirty) return
    event.preventDefault()
    setLeave(() => () => event.retry(true))
  })
  return (
    <Dialog open={!!leave()} title="Unsaved changes" width="small" onClose={() => setLeave(undefined)} footer={<>
      <Button onClick={() => setLeave(undefined)}>Keep editing</Button>
      <Button variant="danger" onClick={() => { const retry = leave(); setLeave(undefined); props.onDiscard?.(); retry?.() }}>Discard changes</Button>
    </>}><p>Leave and discard your unsaved changes?</p></Dialog>
  )
}
