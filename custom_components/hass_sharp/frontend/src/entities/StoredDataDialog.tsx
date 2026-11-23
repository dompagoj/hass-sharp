import { createEffect, createSignal, For, on, Show } from 'solid-js'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { useHass } from '../context'
import { focusSoon } from '../visual/soon'
import { EntityApiError, entityError } from './api'
import { deviceUiApi, type SharedStore } from './deviceUi'

/** A size as `SharpFiles.Size` words it: in bytes below 1 KiB, else in KiB, MiB or GiB to one decimal. */
export function storeSize(bytes: number): string {
  if (bytes < 1024) return bytes === 1 ? '1 byte' : `${bytes} bytes`
  const units = ['KiB', 'MiB', 'GiB']
  let value = bytes / 1024, unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return `${Math.round(value * 10) / 10} ${units[unit]}`
}

const and = (names: string[]) => (names.length < 2 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`)

/**
 * The Entities page's Stored data (administrators only): each shared store C# devices keep, with its size and the
 * running definitions that have it open, named by `definitionName` (by its id when the page lists none of that id).
 * The list is read each time the dialog opens, and only then. Delete asks first; a store in use is refused, and the
 * refusal is shown here; once deleted, or found already gone, the list is read again.
 */
export function StoredDataDialog(props: { open: boolean; definitionName(id: string): string | undefined; onClose(): void }) {
  const hass = useHass()
  let body!: HTMLDivElement
  const [stores, setStores] = createSignal<SharedStore[]>()
  const [loadError, setLoadError] = createSignal<string>()
  const [asking, setAsking] = createSignal<string>()
  const [deleting, setDeleting] = createSignal(false)
  const [refusal, setRefusal] = createSignal<string>()

  // Each opening reads the list; an answer to an earlier read, or to a dialog closed since, is dropped.
  let generation = 0
  const load = async () => {
    const mine = ++generation
    setLoadError(undefined)
    try {
      const listed = await deviceUiApi(hass).stores()
      if (mine === generation) setStores(listed)
    } catch (error) {
      if (mine === generation) { setStores(undefined); setLoadError(`Could not load the stored data: ${entityError(error)}`) }
    }
  }
  createEffect(on(() => props.open, open => {
    generation++
    setStores(undefined); setLoadError(undefined); setAsking(undefined); setRefusal(undefined)
    if (open) void load()
  }))

  const close = () => { if (!deleting()) props.onClose() }
  const cancelDelete = () => { if (!deleting()) setAsking(undefined) }
  const usedBy = (store: SharedStore) =>
    store.users.length ? `Used by ${and(store.users.map(id => props.definitionName(id) ?? id))}` : 'Not in use'

  // After a delete, focus goes to the next store's Delete, else the previous one's, else the dialog's Close.
  const focusAfter = (name: string) => {
    const names = (stores() ?? []).map(store => store.name), at = names.indexOf(name)
    const neighbour = at < 0 ? undefined : names[at + 1] ?? names[at - 1]
    return () => focusSoon(() => {
      const dialog = body.closest('dialog')
      const row = neighbour === undefined ? undefined : [...body.querySelectorAll<HTMLElement>('[data-store]')].find(found => found.getAttribute('data-store') === neighbour)
      return row?.querySelector<HTMLElement>('button') ?? dialog?.querySelector<HTMLElement>('.hs-dialog-header button')
    }, body)
  }
  const confirmDelete = async () => {
    const name = asking()
    if (name === undefined || deleting()) return
    setDeleting(true)
    setRefusal(undefined)
    const focusNext = focusAfter(name)
    try {
      await deviceUiApi(hass).deleteStore(name)
    } catch (error) {
      // Already gone is deleted, as asked.
      if (!(error instanceof EntityApiError && error.code === 'not_found')) {
        setRefusal(error instanceof EntityApiError && error.code === 'in_use' ? error.message : `Could not delete it: ${entityError(error)}`)
        setDeleting(false)
        setAsking(undefined)
        return
      }
    }
    await load()
    setDeleting(false)
    setAsking(undefined)
    focusNext()
  }

  return (
    <>
      <Dialog open={props.open} title="Stored data" width="medium" onClose={close} footer={<Button disabled={deleting()} onClick={close}>Close</Button>}>
        <div class="hs-stored hs-stack" ref={body}>
          <Show when={loadError()}>
            {message => <p class="hs-stored__failed" role="alert"><span class="hs-error">{message()}</span> <Button onClick={() => void load()}>Try again</Button></p>}
          </Show>
          <Show when={refusal()}>{message => <p class="hs-error" role="alert">{message()}</p>}</Show>
          <Show when={stores()}>
            {listed => (
              <Show when={listed().length} fallback={<p role="status">No device has stored shared data.</p>}>
                <ul class="hs-stored__list" aria-label="Shared stores">
                  <For each={listed()}>
                    {store => (
                      <li class="hs-stored__row" data-store={store.name}>
                        <span class="hs-stored__text">
                          <span class="hs-stored__name">{store.name}</span>
                          <span class="hs-stored__detail"><span class="hs-stored__size">{storeSize(store.bytes)}</span> · <span class="hs-stored__users">{usedBy(store)}</span></span>
                        </span>
                        <Button variant="danger" disabled={deleting()} aria-label={`Delete ${store.name}`} onClick={() => { setRefusal(undefined); setAsking(store.name) }}>Delete</Button>
                      </li>
                    )}
                  </For>
                </ul>
              </Show>
            )}
          </Show>
          <Show when={!stores() && !loadError()}><p class="hs-muted" role="status">Loading stored data…</p></Show>
          <p class="hs-muted">Data a single device keeps for itself is deleted with its entity definition, or when its node is removed and the definition is saved.</p>
        </div>
      </Dialog>
      <Dialog open={asking() !== undefined} title={`Delete ${asking() ?? ''}?`} width="small" onClose={cancelDelete} footer={<>
        <Button disabled={deleting()} onClick={cancelDelete}>Cancel</Button>
        <Button variant="danger" busy={deleting()} onClick={() => void confirmDelete()}>Delete</Button>
      </>}>
        <p>Everything devices stored in it is removed and cannot be brought back.</p>
      </Dialog>
    </>
  )
}
