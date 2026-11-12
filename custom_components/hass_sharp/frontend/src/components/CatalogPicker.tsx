import { createEffect, createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Button } from './Controls'
import { Dialog } from './Dialog'

export interface CatalogItem { id: string; name?: string; description?: string }

const domainPaths: Record<string, string> = {
  light: 'M9 18h6m-5 3h4M8 14a6 6 0 1 1 8 0l-1 2H9Z',
  sensor: 'M4 18a9 9 0 1 1 16 0M12 12l4-4M6 18h12',
  binary_sensor: 'M5 12a7 7 0 1 0 14 0 7 7 0 1 0-14 0M9 12h6',
  switch: 'M12 3v9M6 6a9 9 0 1 0 12 0',
  climate: 'M10 4a2 2 0 0 1 4 0v10a4 4 0 1 1-4 0ZM12 8v9',
  cover: 'M4 4h16v16H4ZM4 8h16M4 12h16M4 16h16',
  media_player: 'M3 4h18v14H3ZM9 21h6M10 8l5 3-5 3Z',
  automation: 'm13 2-8 12h6l-1 8 9-13h-7Z',
  script: 'M6 3h9l4 4v14H6ZM14 3v5h5M9 12h7M9 16h7',
  fan: 'M12 12c-9-9 3-13 3-5 9-3 10 9 2 7 0 9-12 8-8 1-8 0-6-11 3-3',
  lock: 'M7 10V7a5 5 0 0 1 10 0v3M5 10h14v11H5ZM12 14v3',
  camera: 'M3 7h5l2-3h4l2 3h5v13H3ZM8 13a4 4 0 1 0 8 0 4 4 0 1 0-8 0',
  scene: 'm12 3 3 6 6 3-6 3-3 6-3-6-6-3 6-3Z',
  notify: 'M6 17h12l-2-3V9a4 4 0 0 0-8 0v5ZM10 21h4',
  input_boolean: 'M8 6h8a6 6 0 0 1 0 12H8A6 6 0 0 1 8 6ZM5 12a3 3 0 1 0 6 0 3 3 0 1 0-6 0',
}
const DomainIcon = (props: { domain: string }) => <svg class="hs-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
  <path d={domainPaths[props.domain] ?? 'M5 5h14v14H5ZM9 9h6v6H9Z'} />
</svg>

export function CatalogPicker(props: {
  value: string
  onChange: (id: string) => void
  clearLabel?: string
  kind: 'entities' | 'services'
  items: CatalogItem[]
  loading: boolean
  error: boolean
  refresh: () => unknown
}) {
  const [open, setOpen] = createSignal(false)
  const [search, setSearch] = createSignal('')
  const [domain, setDomain] = createSignal('')
  const [active, setActive] = createSignal(-1)
  const id = createUniqueId()
  const domains = createMemo(() => [...new Set(props.items.map(item => item.id.split('.')[0]))].sort())
  const matches = createMemo(() => props.items.filter(item =>
    (!domain() || item.id.startsWith(domain() + '.')) &&
    `${item.id} ${item.name ?? ''} ${item.description ?? ''}`.toLocaleLowerCase().includes(search().toLocaleLowerCase())))
  // A refreshed catalog can reorder results while the dialog remains open.
  createEffect(() => { matches(); setActive(-1) })
  const activeId = () => active() >= 0 && active() < matches().length ? `${id}-${active()}` : undefined
  const choose = (value: string) => { props.onChange(value); setOpen(false) }
  const label = () => props.kind === 'entities' ? 'Entity ID' : 'Service'
  const show = () => {
    setSearch(''); setDomain(''); setActive(-1); setOpen(true)
    void props.refresh()
  }
  const manual = () => search().trim()
  const keyboard = (event: KeyboardEvent) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      const count = matches().length
      if (count) {
        setActive(index => (index + (event.key === 'ArrowDown' ? 1 : index < 0 ? 0 : -1) + count) % count)
        const root = (event.currentTarget as HTMLElement).getRootNode() as Document | ShadowRoot
        root.querySelector(`#${activeId()}`)?.scrollIntoView?.({ block: 'nearest' })
      }
    }
    if (event.key === 'Enter' && matches()[active()]) {
      event.preventDefault()
      choose(matches()[active()].id)
    }
  }

  return <div class="hs-field">
    <label id={`${id}-label`}>{label()}</label>
    <Button class="hs-picker-field" aria-label={label()} aria-haspopup="dialog" aria-expanded={open()} onClick={show}>
      {props.value || `Choose ${label().toLowerCase()}…`}
    </Button>
    <Dialog open={open()} title={`Choose ${props.kind === 'entities' ? 'entity' : 'service'}`} width="large" onClose={() => setOpen(false)} footer={<>
      <Button onClick={() => setOpen(false)}>Cancel</Button>
      <Show when={props.clearLabel}><Button onClick={() => choose('')}>{props.clearLabel}</Button></Show>
      <Show when={manual() && /^[a-z_][a-z0-9_]*\.[a-z0-9_]+$/.test(manual())}>
        <Button onClick={() => choose(manual())}>Use ID: {manual()}</Button>
      </Show>
    </>}>
      <p class="hs-muted">Current selection: {props.value || 'None'}. Search without changing it; select a result to apply.</p>
      <div class="hs-picker-filters">
        <input autofocus class="hs-input" aria-label={`Search ${props.kind}`} placeholder="Search by name, ID or description"
          value={search()} onInput={event => setSearch(event.currentTarget.value)} on:keydown={keyboard}
          role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls={`${id}-list`} aria-activedescendant={activeId()} />
        <select class="hs-input" aria-label="Domain filter" value={domain()} onChange={event => setDomain(event.currentTarget.value)}>
          <option value="">All domains</option>
          <For each={domains()}>{item => <option value={item}>{item}</option>}</For>
        </select>
      </div>
      <Show when={props.loading}><p role="status">Loading {props.kind}…</p></Show>
      <Show when={props.error}><p role="alert">Could not load {props.kind}. <Button onClick={() => void props.refresh()}>Retry</Button></p></Show>
      <Show when={!props.loading && !props.error && !matches().length}>
        <p role="status">No matching {props.kind}. Enter an exact ID to retain an unavailable item.</p>
      </Show>
      <div class="hs-picker-results" role="listbox" id={`${id}-list`} aria-label={`${label()} results`}>
        <For each={matches()}>{(item, index) => <button type="button" tabindex="-1" role="option" id={`${id}-${index()}`}
          aria-selected={props.value === item.id} classList={{ 'hs-picker-active': active() === index() }} onClick={() => choose(item.id)}>
          <DomainIcon domain={item.id.split('.')[0]} />
          <span><strong>{item.name || item.id}</strong><small>{item.id}</small><Show when={item.description}><small>{item.description}</small></Show></span>
          <Show when={props.value === item.id}><span aria-label="Current selection">Selected</span></Show>
        </button>}</For>
      </div>
    </Dialog>
  </div>
}
