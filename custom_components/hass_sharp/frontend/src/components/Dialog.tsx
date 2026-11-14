import { createEffect, createUniqueId, onCleanup, Show, type JSX } from 'solid-js'
import { Button, Icon } from './Controls'

export interface DialogProps {
  open: boolean
  title: string
  children: JSX.Element
  footer?: JSX.Element
  width?: 'small' | 'medium' | 'large' | 'full'
  preventClose?: boolean
  onClose: () => void
}

const focusableSelector = 'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'
/** What Tab reaches in `dialog`: its own controls, not those of a dialog open inside it. */
export const focusableIn = (dialog: HTMLDialogElement) =>
  Array.from(dialog.querySelectorAll<HTMLElement>(focusableSelector)).filter(el => el.closest('dialog') === dialog)

/**
 * Opens `dialog` as a modal (kept in the panel's theme tree; native modality makes the rest of HA inert) and returns
 * what closes it and gives focus back to where it was.
 */
export function openModal(dialog: HTMLDialogElement): () => void {
  const previous = (dialog.getRootNode() as Document | ShadowRoot).activeElement as HTMLElement | null
  dialog.showModal()
  return () => { dialog.close(); previous?.focus() }
}

/** Keeps Tab and Shift+Tab inside `dialog`: from its last control to its first, and back. */
export function trapTab(dialog: HTMLDialogElement, event: KeyboardEvent) {
  if (event.key !== 'Tab') return
  const items = focusableIn(dialog)
  const first = items[0], last = items.at(-1)
  const active = (dialog.getRootNode() as Document | ShadowRoot).activeElement
  if (!first) { event.preventDefault(); dialog.focus() }
  else if (event.shiftKey && (active === first || active === dialog)) { event.preventDefault(); last?.focus() }
  else if (!event.shiftKey && active === last) { event.preventDefault(); first.focus() }
}

export const Dialog = (props: DialogProps) => {
  const titleId = createUniqueId()
  let dialog!: HTMLDialogElement
  const close = () => { if (!props.preventClose) props.onClose() }
  createEffect(() => {
    if (!props.open) return
    const restore = openModal(dialog)
    queueMicrotask(() => {
      if (dialog.open) (dialog.querySelector<HTMLElement>('[autofocus]') ?? focusableIn(dialog)[0] ?? dialog).focus()
    })
    onCleanup(restore)
  })

  return <dialog ref={dialog} role="dialog" aria-modal="true" aria-labelledby={titleId}
    class={`hs-dialog hs-dialog-backdrop hs-dialog--${props.width ?? 'medium'}`}
    onCancel={event => { event.preventDefault(); close() }}
    onClick={event => {
      if (event.target !== dialog) return
      const rect = dialog.getBoundingClientRect()
      if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close()
    }}
    on:keydown={event => {
      // HA's global shortcuts must not intercept modal interactions.
      event.stopPropagation()
      if (event.key === 'Escape') { event.preventDefault(); close() }
      trapTab(dialog, event)
    }}>
    <header class="hs-dialog-header"><h2 id={titleId}>{props.title}</h2>
      <Button variant="ghost" class="hs-icon-button" aria-label="Close dialog" disabled={props.preventClose} onClick={close}><Icon name="close" /></Button>
    </header>
    <div class="hs-dialog-body">{props.children}</div>
    <Show when={props.footer}><footer class="hs-dialog-footer">{props.footer}</footer></Show>
  </dialog>
}
