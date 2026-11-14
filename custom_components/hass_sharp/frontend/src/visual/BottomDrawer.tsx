import { createEffect, createMemo, createSignal, createUniqueId, For, Show, type JSX } from 'solid-js'
import { Glyph } from './Glyph'
import type { VisualDiagnostic, VisualPreview } from './api'
import type { VisualCatalogSnapshot } from './contract'
import { nodeTitle } from './flow/flowModel'
import { serializeGraph, type VisualGraph } from './graph'
import { icons, nodeIdentity } from './icons'
import { RemoveWire } from './RemoveWire'

export interface DrawerTab {
  id: string
  label: string
  /** Shown after the label ("Problems 2"). */
  count?: number
  tone?: 'error' | 'warning'
  /** Rendered the first time the tab is shown, then kept (with its state) while other tabs are shown. */
  lazy?: boolean
  content: () => JSX.Element
}

const chevron = { up: ['M6 15l6-6 6 6'], down: ['M6 9l6 6 6-6'] }

/**
 * The editor's bottom drawer. Closed, it is a one-line bar ("Problems 2 · Generated C# · Runs"); each name opens it
 * at that tab. Open, the same names are its tabs. Every tab's panel stays mounted once shown, so its scroll position,
 * filters and expanded rows survive switching tabs and closing the drawer. Escape inside a panel closes it.
 */
export function BottomDrawer(props: {
  tabs: DrawerTab[]
  open: boolean
  tab: string
  onOpenChange(open: boolean): void
  onTabChange(id: string): void
  /** The drawer's accessible name (the editor's: "Problems, code and runs"). */
  label?: string
}) {
  const id = createUniqueId()
  let bar!: HTMLDivElement
  const [seen, setSeen] = createSignal<ReadonlySet<string>>(new Set(props.tabs.filter(tab => !tab.lazy).map(tab => tab.id)))
  createEffect(() => {
    if (props.open && !seen().has(props.tab)) setSeen(current => new Set([...current, props.tab]))
  })
  const tabButton = (tab: string) => bar.querySelector<HTMLButtonElement>(`[id="${id}-${tab}"]`)
  const choose = (tab: string) => {
    props.onTabChange(tab)
    if (!props.open) props.onOpenChange(true)
  }
  const keys = (event: KeyboardEvent) => {
    const list = props.tabs.map(tab => tab.id)
    const current = list.indexOf(props.tab)
    const next = { ArrowRight: current + 1, ArrowLeft: current - 1, Home: 0, End: list.length - 1 }[event.key]
    if (next === undefined) return
    event.preventDefault()
    const tab = list[(next + list.length) % list.length]
    props.onTabChange(tab)
    tabButton(tab)?.focus()
  }
  return (
    <section class="hs-drawer" classList={{ 'hs-drawer--open': props.open }} aria-label={props.label ?? 'Problems, code and runs'}>
      <div ref={bar} class="hs-drawer__bar">
        <div class="hs-drawer__tabs" role="tablist" aria-label="Details" on:keydown={keys}>
          <For each={props.tabs}>
            {(tab, index) => (
              <>
                <Show when={index() > 0}>
                  <span class="hs-drawer__dot" aria-hidden="true">·</span>
                </Show>
                <button
                  type="button"
                  role="tab"
                  id={`${id}-${tab.id}`}
                  class="hs-drawer__tab"
                  classList={{ [`hs-drawer__tab--${tab.tone}`]: !!tab.tone && !!tab.count }}
                  aria-selected={props.open && props.tab === tab.id}
                  aria-controls={`${id}-${tab.id}-panel`}
                  tabindex={props.tab === tab.id ? 0 : -1}
                  onClick={() => (props.open && props.tab === tab.id ? props.onOpenChange(false) : choose(tab.id))}
                >
                  {tab.label}
                  <Show when={tab.count}>
                    <span class="hs-drawer__count">{tab.count}</span>
                  </Show>
                </button>
              </>
            )}
          </For>
        </div>
        <button type="button" class="hs-drawer__toggle" aria-expanded={props.open} aria-label={props.open ? 'Hide details' : 'Show details'}
          title={props.open ? 'Hide details' : 'Show details'} onClick={() => props.onOpenChange(!props.open)}>
          <Glyph paths={props.open ? chevron.down : chevron.up} />
        </button>
      </div>
      <For each={props.tabs}>
        {tab => (
          <div
            role="tabpanel"
            id={`${id}-${tab.id}-panel`}
            aria-labelledby={`${id}-${tab.id}`}
            class="hs-drawer__panel"
            hidden={!props.open || props.tab !== tab.id}
            on:keydown={(event: KeyboardEvent) => {
              if (event.key !== 'Escape' || event.defaultPrevented) return
              event.preventDefault()
              props.onOpenChange(false)
              tabButton(tab.id)?.focus()
            }}
          >
            <Show when={seen().has(tab.id)}>{tab.content()}</Show>
          </div>
        )}
      </For>
    </section>
  )
}

const severityName = (severity: number) => (severity === 3 ? 'Error' : severity === 2 ? 'Warning' : 'Info')

/**
 * The Problems tab: every diagnostic as its plain sentence, grouped by the step it is about (then the shared C# and
 * the automation itself). A message selects its step and focuses the field it is about; "Line N" shows it in the
 * generated C#.
 */
export function ProblemsPanel(props: {
  diagnostics: VisualDiagnostic[]
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  onLocate(diagnostic: VisualDiagnostic): void
  onSelectStep(id: string): void
  onShowLine(line: number): void
  /** Applies a graph change (the editor's: one undo step); without it (read only) nothing is offered that edits. */
  onGraphChange?(graph: VisualGraph): void
}) {
  let root!: HTMLDivElement
  const shown = () => props.diagnostics.filter(d => d.severity > 0)
  const groups = createMemo(() => {
    const byKey = new Map<string, VisualDiagnostic[]>()
    for (const diagnostic of shown()) {
      const key = diagnostic.nodeId ?? ''
      byKey.set(key, [...(byKey.get(key) ?? []), diagnostic])
    }
    // The automation's own problems first, then node libraries, then steps in the graph's order, then anything about a missing step.
    const order = (key: string) => (key === '' ? -2 : key.startsWith('$') ? -1 : props.graph.nodes.findIndex(n => n.id === key) + 1 || 1e9)
    return [...byKey].sort(([a], [b]) => order(a) - order(b))
  })
  return (
    <div class="hs-problems" ref={root} tabindex="-1">
      <Show when={shown().length} fallback={<p class="hs-problems__empty">No problems found.</p>}>
        <For each={groups()}>
          {([key, list]) => {
            const node = () => props.graph.nodes.find(n => n.id === key)
            const heading = () => (key === '' ? 'This automation' : key.startsWith('$') ? 'Node libraries' : nodeTitle(node(), props.catalog, 'Missing step'))
            return (
              <section class="hs-problems__group" style={{ '--hs-cat': node() ? nodeIdentity(node()!.kind, props.catalog).color : undefined }}>
                <h3 class="hs-problems__step">
                  <Show when={node()} fallback={<span>{heading()}</span>}>
                    {current => (
                      <button type="button" class="hs-problems__step-button" onClick={() => props.onSelectStep(current().id)}>
                        <Glyph paths={nodeIdentity(current().kind, props.catalog).icon} />
                        <span>{heading()}</span>
                      </button>
                    )}
                  </Show>
                </h3>
                <ul class="hs-problems__list" aria-label={`Problems of ${heading()}`}>
                  <For each={list}>
                    {diagnostic => (
                      <li class={`hs-problem hs-problem--${severityName(diagnostic.severity).toLowerCase()}`}>
                        <span class="hs-problem__icon" role="img" aria-label={severityName(diagnostic.severity)}>
                          <Glyph paths={diagnostic.severity === 3 ? icons.problem : icons.warning} />
                        </span>
                        <button type="button" class="hs-problem__message" onClick={() => props.onLocate(diagnostic)}>{diagnostic.message}</button>
                        <span class="hs-problem__actions">
                          <Show when={props.onGraphChange}>
                            {change => <RemoveWire graph={props.graph} diagnostic={diagnostic} catalog={props.catalog} class="hs-problem__line"
                              onRemove={next => change()(next)} focusAfter={() => root} />}
                          </Show>
                          <Show when={diagnostic.startLine != null}>
                            <button type="button" class="hs-problem__line" aria-label={`Show line ${diagnostic.startLine} of the generated C#`}
                              onClick={() => props.onShowLine(diagnostic.startLine!)}>
                              Line {diagnostic.startLine}
                            </button>
                          </Show>
                        </span>
                      </li>
                    )}
                  </For>
                </ul>
              </section>
            )
          }}
        </For>
      </Show>
    </div>
  )
}

/**
 * The Generated C# tab: the C# Home Assistant generated from this graph, one numbered line per row (a line's number
 * selects the step it came from), or the graph as JSON.
 */
export function CodePanel(props: {
  graph: VisualGraph
  preview?: VisualPreview
  previewError?: string
  onRetry(): void
  view: 'code' | 'json'
  onViewChange(view: 'code' | 'json'): void
  /** The line picked from a problem or a line number; else the selected step's lines are marked. */
  sourceLine?: number
  selectedId?: string
  onSelectLine(line: number, nodeId?: string): void
}) {
  return (
    <div class="hs-code-panel">
      <div class="hs-code-panel__bar">
        <div class="hs-segmented" role="group" aria-label="Show">
          <button type="button" aria-pressed={props.view === 'code'} onClick={() => props.onViewChange('code')}>C#</button>
          <button type="button" aria-pressed={props.view === 'json'} onClick={() => props.onViewChange('json')}>Graph JSON</button>
        </div>
        <p class="hs-code-panel__note">
          {props.view === 'code' ? 'Generated by Home Assistant from this graph. A line number selects its step.' : 'The graph as it is saved.'}
        </p>
      </div>
      <Show when={props.previewError}>
        <div role="alert" class="hs-error hs-code-panel__error">
          Preview failed: {props.previewError}{' '}
          <button type="button" class="hs-button hs-button--secondary" onClick={() => props.onRetry()}>Retry preview</button>
        </div>
      </Show>
      <Show
        when={props.view === 'json'}
        fallback={
          <Show when={props.preview} fallback={<Show when={!props.previewError}><p role="status" class="hs-code-panel__note">Updating preview…</p></Show>}>
            {result => (
              <pre tabindex="0" aria-label="Generated C#">
                <code>
                  <For each={result().source.split('\n')}>
                    {(line, index) => {
                      const range = () => result().sourceMap.find(range => index() + 1 >= range.startLine && index() + 1 <= range.endLine)
                      const current = () => props.sourceLine === index() + 1 || (props.sourceLine == null && !!range() && range()!.nodeId === props.selectedId)
                      return (
                        <span class="hs-source-line" data-source-line={index() + 1} aria-current={current() ? 'true' : undefined}>
                          <button
                            type="button"
                            class="hs-source-number"
                            aria-label={`Select source line ${index() + 1}`}
                            disabled={!range()}
                            onClick={() => props.onSelectLine(index() + 1, range()?.nodeId)}
                          >
                            {index() + 1}
                          </button>
                          {line}
                          {'\n'}
                        </span>
                      )
                    }}
                  </For>
                </code>
              </pre>
            )}
          </Show>
        }
      >
        <pre tabindex="0" aria-label="Graph JSON">
          <code>{serializeGraph(props.graph)}</code>
        </pre>
      </Show>
    </div>
  )
}
