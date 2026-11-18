import { createSignal, Show, type JSX } from 'solid-js'
import { useNavigate } from '@solidjs/router'
import { useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { useHass } from '../context'
import { definitionsQueryKey, entityApi, entityError } from './api'

/**
 * Delete at the bottom of a saved definition's form. It asks first, saying that its entities (and its device) go from
 * Home Assistant; then it deletes the saved revision and goes to the entities page. Not while there are unsaved
 * changes, a save, or a conflict to settle (`blocked`); `hint` says why, when the person can do something about it.
 */
export function DeleteDefinition(props: { document: { id: string; revision: number }; name: string; blocked: boolean; hint?: string; onError(text: string): void }) {
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
      <Button variant="danger" disabled={props.blocked} aria-describedby={props.blocked && props.hint ? 'hs-def-delete-hint' : undefined} onClick={() => setAsking(true)}>
        Delete entity definition
      </Button>
      <Show when={props.blocked && props.hint}>{hint => <small id="hs-def-delete-hint" class="hs-field-hint">{hint()}</small>}</Show>
      <DeleteDefinitionDialog open={asking()} name={props.name} deleting={deleting()} onCancel={() => setAsking(false)} onConfirm={() => void confirm()} />
    </div>
  )
}

/**
 * The confirmation before a definition is deleted, in the editor and on the entities page: what goes from Home
 * Assistant, Cancel and Delete. `error` is a refusal to show in it (with what to do about it), when the owner keeps it
 * open for one.
 */
export function DeleteDefinitionDialog(props: { open: boolean; name: string; deleting: boolean; error?: JSX.Element; onCancel(): void; onConfirm(): void }) {
  return (
    <Dialog open={props.open} title="Delete this entity definition?" width="small" onClose={() => { if (!props.deleting) props.onCancel() }} footer={<>
      <Button disabled={props.deleting} onClick={() => props.onCancel()}>Cancel</Button>
      <Button variant="danger" busy={props.deleting} onClick={() => props.onConfirm()}>Delete</Button>
    </>}>
      <p>{props.name} and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.</p>
      <Show when={props.error}>{message => <div class="hs-error" role="alert">{message()}</div>}</Show>
    </Dialog>
  )
}
