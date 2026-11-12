import { createMemo, createSignal, createUniqueId, For, onCleanup, onMount, Show } from 'solid-js'
import { useEntities } from '../entities'
import { Button } from './Controls'

/** Suggestions never constrain the value: unknown and unavailable IDs remain editable. */
export function EntityAutocomplete(props: { value: string; onChange: (value: string) => void }) {
  const entities = useEntities()
  const id = createUniqueId()
  const [open, setOpen] = createSignal(false)
  const [active, setActive] = createSignal(-1)
  let root!: HTMLDivElement
  const matches = createMemo(() => {
    const term = props.value.toLocaleLowerCase()
    return (entities.data ?? []).filter(entity => entity.entity_id.toLocaleLowerCase().includes(term) || entity.friendly_name?.toLocaleLowerCase().includes(term))
  })
  const show = () => {
    if (open()) return
    setOpen(true); setActive(-1); void entities.refetch()
  }
  const close = () => { setOpen(false); setActive(-1) }
  const choose = (value: string) => { props.onChange(value); close() }
  const outside = (event: PointerEvent) => { if (!event.composedPath().includes(root)) close() }
  onMount(() => document.addEventListener('pointerdown', outside))
  onCleanup(() => document.removeEventListener('pointerdown', outside))
  return <div ref={root} class="hs-field hs-autocomplete" onFocusOut={event => { if (!root.contains(event.relatedTarget as Node)) close() }}>
    <label for={id}>Entity ID</label>
    <input id={id} class="hs-input" aria-label="Entity ID" role="combobox" autocomplete="off" aria-autocomplete="list"
      aria-expanded={open()} aria-controls={`${id}-options`} aria-activedescendant={open() && active() >= 0 && active() < matches().length ? `${id}-option-${active()}` : undefined}
      value={props.value} onFocus={show} onClick={show}
      onInput={event => { props.onChange(event.currentTarget.value); setActive(-1); show() }}
      on:keydown={event => {
        if (event.key === 'Escape') { event.preventDefault(); close() }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault(); show()
          const count = matches().length
          if (count) {
            setActive(index => (index + (event.key === 'ArrowDown' ? 1 : index < 0 ? 0 : -1) + count) % count)
            root.querySelector(`#${id}-option-${active()}`)?.scrollIntoView?.({ block: 'nearest' })
          }
        }
        if (event.key === 'Enter' && open() && matches()[active()]) { event.preventDefault(); choose(matches()[active()].entity_id) }
      }} />
    <Show when={open()}><div class="hs-suggestions">
      <Show when={entities.isFetching}><p role="status">Loading entities…</p></Show>
      <Show when={entities.isError}><div role="alert">Could not load entities. <Button onClick={() => void entities.refetch()}>Retry</Button></div></Show>
      <Show when={!entities.isFetching && !entities.isError && !matches().length}><p role="status">No matching entities. You can keep this ID.</p></Show>
      <div role="listbox" id={`${id}-options`} aria-label="Entity suggestions"><For each={matches()}>{(entity, index) =>
        <button type="button" role="option" id={`${id}-option-${index()}`} aria-selected={active() === index()} tabindex="-1"
          onPointerDown={event => event.preventDefault()} onClick={() => choose(entity.entity_id)}>
          <strong>{entity.entity_id}</strong><Show when={entity.friendly_name}><small>{entity.friendly_name}</small></Show>
        </button>
      }</For></div>
    </div></Show>
  </div>
}
