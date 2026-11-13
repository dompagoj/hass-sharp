import { createEffect, createMemo, createSignal, createUniqueId, For, onCleanup, onMount, Show } from 'solid-js'
import { catalogPalette, type NodeKind } from './graph'
import { nodeCatalog, type VisualCatalogSnapshot } from './contract'
import { nodeIdentity } from './icons'

const Glyph = (props: { paths: readonly string[] }) => (
  <svg class="hs-quick-add__glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <For each={props.paths}>{d => <path d={d} />}</For>
  </svg>
)

/**
 * The quick-add palette: a search field over every step it may add, grouped by category with the category's icon
 * and colour, each step with its name and one line of description. Arrow keys move through the list, Enter adds,
 * Escape (or Tab) closes and hands focus back to whoever opened it. Opened from a pin, it lists only steps that can
 * connect to that pin and its title says so.
 */
export function QuickAdd(props: {
  catalog?: VisualCatalogSnapshot
  /** Top-left corner inside the canvas, in pixels. */
  x: number
  y: number
  /** "Add a step", or what choosing will connect ("Connect Entity to…"). */
  title: string
  /** Asked once per kind while open (not per keystroke). */
  allowed: (kind: NodeKind) => boolean
  onChoose: (kind: NodeKind) => void
  /** `restore`: closed from the keyboard, so focus goes back to where it was; a click elsewhere keeps its own focus. */
  onClose: (restore: boolean) => void
}) {
  const [search, setSearch] = createSignal('')
  const [active, setActive] = createSignal(0)
  const id = createUniqueId()
  let root!: HTMLDivElement
  let input!: HTMLInputElement
  let list!: HTMLDivElement
  const catalog = () => props.catalog ?? nodeCatalog
  const offered = createMemo(() => catalogPalette(catalog()).filter(item => props.allowed(item.kind)))
  const items = createMemo(() => {
    const words = search().toLocaleLowerCase().split(/\s+/).filter(Boolean)
    return offered().filter(item => {
      const text = `${item.label} ${item.category} ${item.description}`.toLocaleLowerCase()
      return words.every(word => text.includes(word))
    })
  })
  // Categories in catalog order, each with the steps that match; the flat order is what the arrow keys walk.
  const groups = createMemo(() => {
    const byCategory = new Map<string, ReturnType<typeof items>>()
    for (const item of items()) byCategory.set(item.category, [...(byCategory.get(item.category) ?? []), item])
    return [...byCategory].map(([category, entries]) => {
      // The header shows the category, not the trigger's own look, when the category has other steps.
      const sample = entries.find(entry => entry.kind !== 'trigger') ?? entries[0]
      return { category, entries, identity: nodeIdentity(sample.kind, catalog()) }
    })
  })
  const flat = createMemo(() => groups().flatMap(group => group.entries))
  const optionId = (kind: string) => `${id}-option-${kind.replace(/[^A-Za-z0-9_-]/g, '_')}`
  const current = () => flat()[Math.min(active(), flat().length - 1)]
  createEffect(() => {
    const kind = current()?.kind
    if (kind) list?.querySelector(`[id="${optionId(kind)}"]`)?.scrollIntoView?.({ block: 'nearest' })
  })
  const outside = (event: PointerEvent) => {
    if (!event.composedPath().includes(root)) props.onClose(false)
  }
  onMount(() => {
    input.focus({ preventScroll: true })
    document.addEventListener('pointerdown', outside, true)
  })
  onCleanup(() => document.removeEventListener('pointerdown', outside, true))
  const move = (to: number) => {
    const count = flat().length
    if (count) setActive((to + count) % count)
  }
  return (
    <div
      ref={root}
      class="hs-quick-add nodrag nopan nowheel"
      role="dialog"
      aria-label={props.title}
      style={{ left: `${props.x}px`, top: `${props.y}px` }}
      on:focusout={(event: FocusEvent) => {
        if (event.relatedTarget && !root.contains(event.relatedTarget as Node)) props.onClose(false)
      }}
      on:keydown={(event: KeyboardEvent) => {
        const keys: Record<string, () => void> = {
          Escape: () => props.onClose(true),
          Tab: () => props.onClose(true),
          ArrowDown: () => move(active() + 1),
          ArrowUp: () => move(active() - 1),
          PageDown: () => move(Math.min(active() + 8, flat().length - 1)),
          PageUp: () => move(Math.max(active() - 8, 0)),
          Enter: () => {
            const item = current()
            if (item) props.onChoose(item.kind)
          },
        }
        const action = keys[event.key]
        if (!action) return
        event.preventDefault()
        event.stopPropagation()
        action()
      }}
    >
      <p class="hs-quick-add__title" id={`${id}-title`}>{props.title}</p>
      <input
        ref={input}
        class="hs-input"
        aria-label="Search steps"
        role="combobox"
        aria-expanded="true"
        aria-autocomplete="list"
        aria-controls={`${id}-choices`}
        aria-activedescendant={current() ? optionId(current()!.kind) : undefined}
        autocomplete="off"
        spellcheck={false}
        placeholder="Search steps…"
        value={search()}
        on:input={(event: InputEvent) => {
          setSearch((event.currentTarget as HTMLInputElement).value)
          setActive(0)
        }}
      />
      <div ref={list} id={`${id}-choices`} class="hs-quick-add__list" role="listbox" aria-label="Step choices">
        <For each={groups()}>
          {(group, index) => (
            <div role="group" aria-labelledby={`${id}-group-${index()}`} class="hs-quick-add__group" style={{ '--hs-cat': group.identity.color }}>
              <div id={`${id}-group-${index()}`} class="hs-quick-add__category">
                <Glyph paths={group.identity.icon} />
                {group.category}
              </div>
              <For each={group.entries}>
                {item => {
                  const identity = nodeIdentity(item.kind, catalog())
                  const selected = () => current()?.kind === item.kind
                  return (
                    <button
                      type="button"
                      id={optionId(item.kind)}
                      role="option"
                      tabindex="-1"
                      class="hs-quick-add__option"
                      classList={{ 'hs-quick-add__option--active': selected() }}
                      style={{ '--hs-cat': identity.color }}
                      aria-selected={selected()}
                      aria-label={`Create ${item.label}`}
                      aria-describedby={`${optionId(item.kind)}-description`}
                      on:pointerdown={(event: PointerEvent) => event.preventDefault()}
                      on:pointermove={() => setActive(flat().indexOf(item))}
                      on:click={() => props.onChoose(item.kind)}
                    >
                      <span class="hs-quick-add__icon">
                        <Glyph paths={identity.icon} />
                      </span>
                      <span class="hs-quick-add__text">
                        <strong>{item.label}</strong>
                        <small id={`${optionId(item.kind)}-description`}>{item.description}</small>
                      </span>
                    </button>
                  )
                }}
              </For>
            </div>
          )}
        </For>
      </div>
      <Show when={!flat().length}>
        <p class="hs-quick-add__empty" role="status">
          {search() ? 'No step matches that search.' : 'No step can connect here.'}
        </p>
      </Show>
    </div>
  )
}
