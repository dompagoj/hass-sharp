import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'

export interface SearchOption { value: string; label: string; detail?: string; group?: string }
/**
 * A searchable single choice without a dialog: a combobox that filters its options as you type. Arrow keys move,
 * Enter chooses, Escape closes and restores. `clearOnSelect` makes it an "add" control that stays empty.
 */
export function SearchSelect(props: {
  id?: string
  label: string
  options: SearchOption[]
  value?: string
  onSelect(value: string): void
  placeholder?: string
  describedBy?: string
  invalid?: boolean
  disabled?: boolean
  clearOnSelect?: boolean
}) {
  const listId = createUniqueId(), labelId = `${listId}-label`
  const selected = () => props.value === undefined ? '' : props.options.find(o => o.value === props.value)?.label ?? props.value
  const [query, setQuery] = createSignal<string>()
  const [open, setOpen] = createSignal(false)
  const [active, setActive] = createSignal(-1)
  const text = () => query() ?? (props.clearOnSelect ? '' : selected())
  const matches = createMemo(() => {
    const q = (query() ?? '').trim().toLocaleLowerCase()
    return q ? props.options.filter(o => `${o.label} ${o.value} ${o.detail ?? ''} ${o.group ?? ''}`.toLocaleLowerCase().includes(q)) : props.options
  })
  const optionId = (i: number) => `${listId}-${i}`
  const close = () => { setOpen(false); setActive(-1); setQuery() }
  const choose = (value: string) => { close(); props.onSelect(value) }
  const keydown = (event: KeyboardEvent & { currentTarget: HTMLInputElement }) => {
    const count = matches().length
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!open()) { setOpen(true); setActive(event.key === 'ArrowDown' ? 0 : count - 1); return }
      // From no active option, Down goes to the first and Up to the last.
      if (count) setActive(i => i < 0 ? (event.key === 'ArrowDown' ? 0 : count - 1) : (i + (event.key === 'ArrowDown' ? 1 : -1) + count) % count)
      ;(event.currentTarget.getRootNode() as Document | ShadowRoot).getElementById?.(optionId(active()))?.scrollIntoView?.({ block: 'nearest' })
    } else if (event.key === 'Enter' && !event.isComposing && open() && matches()[active()]) {
      event.preventDefault(); choose(matches()[active()].value)
    } else if (event.key === 'Escape' && (open() || query() !== undefined)) {
      event.preventDefault(); event.stopPropagation(); close()
    }
  }
  return <div class="hs-autocomplete hs-search-select" role="group" aria-labelledby={labelId}>
    <span id={labelId} class="hs-visually-hidden">{props.label}</span>
    <input id={props.id} class="hs-input" role="combobox" aria-label={props.label} aria-autocomplete="list" aria-expanded={open()} aria-controls={listId}
      aria-activedescendant={open() && active() >= 0 && active() < matches().length ? optionId(active()) : undefined}
      aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
      placeholder={props.placeholder ?? 'Search…'} value={text()} autocomplete="off"
      onInput={event => { setQuery(event.currentTarget.value); setOpen(true); setActive(matches().length ? 0 : -1) }}
      onKeyDown={keydown} onBlur={close}
      onClick={() => { if (!open()) { setOpen(true); setActive(-1) } }} />
    <Show when={open()}>
      <div class="hs-suggestions" role="listbox" id={listId} aria-label={`${props.label} options`} onMouseDown={event => event.preventDefault()}>
        <Show when={matches().length} fallback={<p class="hs-muted" role="status">No match</p>}>
          <For each={matches()}>{(option, i) => <>
            <Show when={option.group && option.group !== matches()[i() - 1]?.group}><div class="hs-search-group" role="presentation">{option.group}</div></Show>
            <div role="option" id={optionId(i())} aria-selected={active() === i()} classList={{ 'hs-search-current': option.value === props.value }}
              onPointerDown={event => event.preventDefault()} onClick={() => choose(option.value)}>
              <span>{option.label}</span><Show when={option.detail}><small>{option.detail}</small></Show>
            </div>
          </>}</For>
        </Show>
      </div>
    </Show>
  </div>
}
