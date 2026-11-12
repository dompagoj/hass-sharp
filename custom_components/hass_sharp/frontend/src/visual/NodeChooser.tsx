import { createMemo, createSignal, createUniqueId, For, onCleanup, onMount, Show } from 'solid-js'
import { categories, palette, portLabels, type NodeKind, type PendingConnection } from './graph'

export function NodeChooser(props: { x: number; y: number; pending?: PendingConnection; allowed: (kind: NodeKind) => boolean; onChoose: (kind: NodeKind) => void; onClose: () => void }) {
  const [search, setSearch] = createSignal('')
  const [category, setCategory] = createSignal('All')
  const [active, setActive] = createSignal(0)
  const id = createUniqueId()
  let root!: HTMLDivElement
  let input!: HTMLInputElement
  const items = createMemo(() => palette.filter(item => props.allowed(item.kind) && (category() === 'All' || item.category === category()) && `${item.category} ${item.label} ${item.description}`.toLocaleLowerCase().includes(search().toLocaleLowerCase())))
  const outside = (event: PointerEvent) => { if (!event.composedPath().includes(root)) props.onClose() }
  onMount(() => { input.focus(); document.addEventListener('pointerdown', outside) })
  onCleanup(() => document.removeEventListener('pointerdown', outside))
  return <div ref={root} class="hs-node-chooser" style={{ left: `${props.x}px`, top: `${props.y}px` }}
    onFocusOut={event => { if (event.relatedTarget && !root.contains(event.relatedTarget as Node)) props.onClose() }}
    on:keydown={event => {
      if (event.key === 'Escape') { event.preventDefault(); props.onClose() }
      if (event.target === input && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
        event.preventDefault()
        if (items().length) setActive(index => (index + (event.key === 'ArrowDown' ? 1 : -1) + items().length) % items().length)
      }
      if (event.key === 'Enter' && event.target === input) { event.preventDefault(); if (items()[active()]) props.onChoose(items()[active()].kind) }
    }}>
    <strong>{props.pending ? `Connect ${portLabels[props.pending.port]}` : 'Add a step'}</strong>
    <input ref={input} class="hs-input" aria-label="Search steps" role="combobox" aria-expanded="true" aria-controls={`${id}-choices`}
      aria-activedescendant={items()[active()] ? `${id}-${items()[active()].kind}` : undefined} autocomplete="off"
      placeholder="Search steps…" value={search()} onInput={event => { setSearch(event.currentTarget.value); setActive(0) }} />
    <label class="hs-field">Category<select class="hs-input" aria-label="Step category" value={category()} onChange={event => { setCategory(event.currentTarget.value); setActive(0) }}><option value="All">All categories</option><For each={categories}>{category => <option value={category}>{category}</option>}</For></select></label>
    <div id={`${id}-choices`} role="listbox" aria-label="Step choices"><For each={items()}>{(item, index) =>
      <button type="button" id={`${id}-${item.kind}`} role="option" tabindex="-1" aria-selected={active() === index()} aria-label={`Create ${item.label}`}
        onPointerDown={event => event.preventDefault()} onClick={() => props.onChoose(item.kind)}>
        <strong>{item.label}</strong><small>{item.category} · {item.description}</small>
      </button>
    }</For></div>
    <Show when={!items().length}><p role="status">No available steps.</p></Show>
  </div>
}
