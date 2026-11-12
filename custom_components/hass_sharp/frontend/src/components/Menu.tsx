import { createSignal, createUniqueId, For, onCleanup, Show } from 'solid-js'
import { Button, Icon } from './Controls'

export interface MenuItem { label: string; onSelect: () => void; danger?: boolean }
export const Menu = (props: { label: string; items: MenuItem[]; disabled?: boolean }) => {
  const [open, setOpen] = createSignal(false)
  const id = createUniqueId()
  let root!: HTMLDivElement
  let trigger!: HTMLButtonElement
  let menu!: HTMLDivElement
  const items = () => Array.from(menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'))
  const close = (restore = true) => { if (restore) trigger.focus(); setOpen(false) }
  const show = (last = false) => {
    setOpen(true)
    const buttons = items()
    ;(last ? buttons.at(-1) : buttons[0])?.focus()
  }
  const outside = (event: PointerEvent) => { if (!event.composedPath().includes(root)) close(false) }
  document.addEventListener('pointerdown', outside)
  onCleanup(() => document.removeEventListener('pointerdown', outside))
  return <div class="hs-menu" ref={root} onFocusOut={event => {
    if (event.relatedTarget && !root.contains(event.relatedTarget as Node)) close(false)
  }}>
    <Button ref={trigger} variant="ghost" class="hs-icon-button" aria-label={props.label} aria-haspopup="menu" aria-expanded={open()} aria-controls={open() ? id : undefined} disabled={props.disabled}
      onClick={() => open() ? close() : show()}
      onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); show(event.key === 'ArrowUp') }
      }}><Icon name="more" /></Button>
    <Show when={open()}><div ref={menu} id={id} role="menu" aria-label={props.label} class="hs-menu-items"
      onKeyDown={event => {
        const buttons = items()
        const current = buttons.indexOf((menu.getRootNode() as Document | ShadowRoot).activeElement as HTMLButtonElement)
        if (event.key === 'Escape' || event.key === 'Tab') { if (event.key === 'Escape') event.preventDefault(); event.stopPropagation(); close(); return }
        const target = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : event.key === 'ArrowDown' ? (current + 1) % buttons.length : event.key === 'ArrowUp' ? (current - 1 + buttons.length) % buttons.length : -1
        if (target >= 0) { event.preventDefault(); buttons[target]?.focus() }
      }}>
      <For each={props.items}>{item => <button type="button" role="menuitem" tabindex="-1" class={item.danger ? 'hs-menu-danger' : ''}
        onClick={() => { close(); item.onSelect() }}>{item.label}</button>}</For>
    </div></Show>
  </div>
}
