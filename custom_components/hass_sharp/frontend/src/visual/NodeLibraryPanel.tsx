import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Glyph } from './Glyph'
import { catalogPalette, type NodeKind } from './graph'
import { nodeIdentity } from './icons'
import { nodeCatalog, type VisualCatalogSnapshot } from './contract'
import { NODE_DRAG_TYPE } from './nodeDrag'

const chevron = { left: ['M15 5l-7 7 7 7'], right: ['M9 5l7 7-7 7'] }
const plus = ['M12 5v14', 'M5 12h14']

export interface NodeLibraryPanelProps {
  catalog?: VisualCatalogSnapshot
  /** Narrowed to a rail: an expand button and "Add step". */
  collapsed: boolean
  onCollapsedChange(collapsed: boolean): void
  /** A step clicked (or chosen with Enter): the page adds it at the centre of the view. `label` is its library name. */
  onAdd(kind: NodeKind, label: string): void
  /** "Add step": the canvas's quick-add palette (the library's stand-in on narrow screens and in the rail). */
  onQuickAdd(): void
  disabled?: boolean
}

/**
 * The step library at the left of the editor: a search over every step (by name, kind id, description and
 * category), the steps grouped by category with the category's icon and colour, each with its name and one line of
 * description. Click adds a step at the centre of the view, drag places it on the canvas. It narrows to a rail; on
 * narrow screens only its "Add step" button shows (CSS), which opens quick-add.
 */
export function NodeLibraryPanel(props: NodeLibraryPanelProps) {
  const id = createUniqueId()
  const [search, setSearch] = createSignal('')
  let searchInput: HTMLInputElement | undefined
  let expandButton: HTMLButtonElement | undefined
  const catalog = () => props.catalog ?? nodeCatalog
  const items = createMemo(() => {
    const words = search().toLocaleLowerCase().split(/\s+/).filter(Boolean)
    return catalogPalette(catalog()).filter(item => {
      const text = `${item.label} ${item.kind} ${item.description} ${nodeIdentity(item.kind, catalog()).label}`.toLocaleLowerCase()
      return words.every(word => text.includes(word))
    })
  })
  // Categories in catalog order; the heading shows the category's look, not the trigger's own, when it has others.
  const groups = createMemo(() => {
    const byCategory = new Map<string, ReturnType<typeof items>>()
    for (const item of items()) byCategory.set(item.category, [...(byCategory.get(item.category) ?? []), item])
    return [...byCategory].map(([category, entries]) => {
      const sample = entries.find(entry => entry.kind !== 'trigger') ?? entries[0]
      return { category, entries, identity: nodeIdentity(sample.kind, catalog()) }
    })
  })
  const collapse = () => {
    props.onCollapsedChange(true)
    // The button that had focus goes away with the panel: the rail's expand button takes it.
    queueMicrotask(() => expandButton?.focus())
  }
  const expand = () => {
    props.onCollapsedChange(false)
    queueMicrotask(() => searchInput?.focus())
  }
  return (
    <aside class="hs-lib" classList={{ 'hs-lib--collapsed': props.collapsed }} aria-label="Step library" inert={props.disabled}>
      <div class="hs-lib__rail">
        <button ref={expandButton} type="button" class="hs-lib__icon-button hs-lib__expand" aria-label="Show step library" aria-expanded="false"
          aria-controls={`${id}-panel`} title="Show step library" onClick={expand}>
          <Glyph paths={chevron.right} />
        </button>
        <button type="button" class="hs-lib__add" aria-label="Add step" title="Add step (A)" aria-keyshortcuts="A" onClick={() => props.onQuickAdd()}>
          <Glyph paths={plus} />
          <span class="hs-lib__add-text" aria-hidden="true">Add step</span>
        </button>
      </div>
      <Show when={!props.collapsed}>
        <div class="hs-lib__panel" id={`${id}-panel`}>
          <div class="hs-lib__head">
            <h2 class="hs-lib__title">Steps</h2>
            <button type="button" class="hs-lib__icon-button" aria-label="Hide step library" aria-expanded="true" aria-controls={`${id}-panel`}
              title="Hide step library" onClick={collapse}>
              <Glyph paths={chevron.left} />
            </button>
          </div>
          <input
            ref={searchInput}
            type="search"
            class="hs-input hs-lib__search"
            aria-label="Search the step library"
            placeholder="Search steps…"
            autocomplete="off"
            spellcheck={false}
            value={search()}
            onInput={event => setSearch(event.currentTarget.value)}
          />
          <p class="hs-lib__hint">Click to add · drag onto the canvas</p>
          <div class="hs-lib__list">
            <For each={groups()}>
              {(group, index) => (
                <section class="hs-lib__group" aria-labelledby={`${id}-group-${index()}`} style={{ '--hs-cat': group.identity.color }}>
                  <h3 class="hs-lib__category" id={`${id}-group-${index()}`}>
                    <Glyph paths={group.identity.icon} />
                    {group.identity.label}
                  </h3>
                  <ul class="hs-lib__items">
                    <For each={group.entries}>
                      {item => {
                        const identity = nodeIdentity(item.kind, catalog())
                        const describedBy = `${id}-${item.kind.replace(/[^A-Za-z0-9_-]/g, '_')}`
                        return (
                          <li>
                            <button
                              type="button"
                              class="hs-lib__item"
                              style={{ '--hs-cat': identity.color }}
                              aria-label={`Add ${item.label}`}
                              aria-describedby={describedBy}
                              draggable={true}
                              onClick={() => props.onAdd(item.kind, item.label)}
                              onDragStart={event => {
                                event.dataTransfer?.setData(NODE_DRAG_TYPE, item.kind)
                                if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy'
                              }}
                            >
                              <span class="hs-lib__icon"><Glyph paths={identity.icon} /></span>
                              <span class="hs-lib__text">
                                <strong>{item.label}</strong>
                                <small id={describedBy}>{item.description}</small>
                              </span>
                            </button>
                          </li>
                        )
                      }}
                    </For>
                  </ul>
                </section>
              )}
            </For>
            <Show when={!groups().length}>
              <p class="hs-lib__empty" role="status">No step matches that search.</p>
            </Show>
          </div>
        </div>
      </Show>
    </aside>
  )
}
