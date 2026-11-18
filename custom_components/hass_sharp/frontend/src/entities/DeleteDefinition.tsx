import { createSignal, Show } from 'solid-js'
import { useNavigate } from '@solidjs/router'
import { useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { useHass } from '../context'
import { definitionsQueryKey, entityApi, entityError } from './api'

/**
 * Delete at the bottom of a saved definition's form. It asks first, saying that its entities (and its device) go from
 * Home Assistant; then it deletes the saved revision and goes to the entities page. Not while there are unsaved
 * changes, a save, or a conflict to settle.
 */
export function DeleteDefinition(props: { document: { id: string; revision: number }; name: string; blocked: boolean; onError(text: string): void }) {
  const hass = useHass()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [asking, setAsking] = createSignal(false)
  const [deleting, setDeleting] = createSignal(false)
  const confirm = async () => {
    setDeleting(true)
    try {
      await entityApi(hass).remove(props.document.id, props.document.revision)
      setAsking(false)
      void queryClient.invalidateQueries({ queryKey: [...definitionsQueryKey] })
      navigate('/entities')
    } catch (error) {
      setAsking(false)
      props.onError(`Could not delete it: ${entityError(error)}`)
    } finally {
      setDeleting(false)
    }
  }
  return (
    <div class="hs-def-delete">
      <Button variant="danger" disabled={props.blocked} aria-describedby={props.blocked ? 'hs-def-delete-hint' : undefined} onClick={() => setAsking(true)}>
        Delete entity definition
      </Button>
      <Show when={props.blocked}><small id="hs-def-delete-hint" class="hs-field-hint">Save or undo your changes before deleting it.</small></Show>
      <Dialog open={asking()} title="Delete this entity definition?" width="small" onClose={() => { if (!deleting()) setAsking(false) }} footer={<>
        <Button disabled={deleting()} onClick={() => setAsking(false)}>Cancel</Button>
        <Button variant="danger" busy={deleting()} onClick={() => void confirm()}>Delete</Button>
      </>}>
        <p>{props.name} and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.</p>
      </Dialog>
    </div>
  )
}
