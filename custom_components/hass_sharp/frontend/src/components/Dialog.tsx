import { Show, type JSX } from 'solid-js'

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
  const close = () => {
    if (!props.preventClose) props.onClose()
  }

  return (
    <ha-dialog
      prop:open={props.open}
      header-title={props.title}
      width={props.width ?? 'medium'}
      prop:preventScrimClose={props.preventClose ?? false}
      on:closed={close}
    >
      <ha-icon-button
        slot="headerNavigationIcon"
        label="Close"
        prop:disabled={props.preventClose ?? false}
        onClick={close}
      >
        <ha-icon icon="mdi:close" />
      </ha-icon-button>
      {/* HA's dialog also defines --spacing; restore Tailwind's unit for slotted content. */}
      <div style="--spacing: 0.25rem;">{props.children}</div>
      <Show when={props.footer}>
        <ha-dialog-footer
          slot="footer"
          style="--spacing: 0.25rem; padding: 12px 24px 24px; box-sizing: border-box;"
        >
          {props.footer}
        </ha-dialog-footer>
      </Show>
    </ha-dialog>
  )
}
