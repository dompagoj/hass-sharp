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

export const Dialog = (props: DialogProps) => {
  const titleId = createUniqueId()
  let dialog!: HTMLDialogElement
  const close = () => { if (!props.preventClose) props.onClose() }
  const focusable = () => Array.from(dialog.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'))
  createEffect(() => {
    if (!props.open) return
    // Keep the dialog in the panel's theme tree; native modality makes the rest of HA inert.
    const root = dialog.getRootNode() as Document | ShadowRoot
    const previous = root.activeElement as HTMLElement | null
    dialog.showModal()
    queueMicrotask(() => {
      if (dialog.open) (dialog.querySelector<HTMLElement>('[autofocus]') ?? focusable()[0] ?? dialog).focus()
    })
    onCleanup(() => { dialog.close(); previous?.focus() })
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
      if (event.key !== 'Tab') return
      const items = focusable()
      const first = items[0], last = items.at(-1)
      const active = (dialog.getRootNode() as Document | ShadowRoot).activeElement
      if (!first) { event.preventDefault(); dialog.focus() }
      else if (event.shiftKey && (active === first || active === dialog)) { event.preventDefault(); last?.focus() }
      else if (!event.shiftKey && active === last) { event.preventDefault(); first.focus() }
    }}>
    <header class="hs-dialog-header"><h2 id={titleId}>{props.title}</h2>
      <Button variant="ghost" class="hs-icon-button" aria-label="Close dialog" disabled={props.preventClose} onClick={close}><Icon name="close" /></Button>
    </header>
    <div class="hs-dialog-body">{props.children}</div>
    <Show when={props.footer}><footer class="hs-dialog-footer">{props.footer}</footer></Show>
  </dialog>
}
