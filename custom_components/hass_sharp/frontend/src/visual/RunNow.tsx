import { createEffect, createSignal, createUniqueId, For, on, onCleanup, Show } from 'solid-js'
import { useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { dispatchRun, forgetAcceptedRun, runErrorMessage, type RunAction } from '../components/runDispatch'
import { useHass } from '../context'
import type { VisualDocument } from './api'
import type { VisualCatalogSnapshot } from './contract'
import { nodeTitle } from './flow/flowModel'
import { triggerMethod, visualActionLabels, visualRunClasses } from './graph'
import { nodeSummary, type SummaryNames } from './nodeSummary'

/** A step Run now can start: its card title (with its id when two share one) and its one-line summary, as on the card. */
export interface RunChoice {
  nodeId: string
  label: string
  summary: string
}
/** A trigger of the saved automation that can be started by hand. */
export interface ManualTrigger extends RunChoice {
  action: RunAction
}

/** The saved graph's triggers that allow manual runs, in the graph's order; summaries name entities as the cards do. */
export function manualTriggers(document: RunnableDocument | undefined, catalog: VisualCatalogSnapshot, names?: SummaryNames): ManualTrigger[] {
  if (!document) return []
  const graph = document.graph
  const labels = visualActionLabels(graph, catalog)
  // The actions the saved automation offers for manual runs (the same list the backend runs).
  const actions = new Map(visualRunClasses(graph).flatMap(klass => klass.methods.map(method => [method, klass.name] as const)))
  return graph.nodes.flatMap(node => {
    if (node.kind !== 'trigger') return []
    const method = triggerMethod(node), className = actions.get(method)
    if (!className) return []
    return [{
      nodeId: node.id,
      action: { className, method },
      label: labels[`${className}::${method}`] ?? nodeTitle(node, catalog),
      summary: nodeSummary(node, catalog, names, graph),
    }]
  })
}

export interface RunNotice { text: string; error?: boolean }
/** What Run now needs of a saved automation: its graph (or the automations page's outline of it) and identity. */
export type RunnableDocument = Pick<VisualDocument, 'graph' | 'automationId'>

/**
 * The editor's one place to start a run of the saved automation. With one trigger that allows manual runs it runs
 * that one; with several it opens a menu of them (label and summary). Disabled, its tooltip says why. A started run
 * is remembered so the Runs tab polls until it has finished.
 */
export function RunNowButton(props: {
  document?: RunnableDocument
  catalog: VisualCatalogSnapshot
  /** Registry names for the menu's summaries. */
  names?: SummaryNames
  /** Why the saved version cannot be run now (a conflict, the catalog failed, not activated), if so. */
  unavailable?: string
  dirty: boolean
  busy?: boolean
  onNotice(notice: RunNotice): void
}) {
  const hass = useHass()
  const queryClient = useQueryClient()
  const triggers = () => manualTriggers(props.document, props.catalog, props.names)
  const reason = () => {
    if (!props.document) return 'Save the automation to run it'
    if (props.unavailable) return props.unavailable
    if (!triggers().length) return 'No trigger allows manual runs'
  }
  const tip = () =>
    props.dirty
      ? 'Runs the last saved version; unsaved changes are not included'
      : triggers().length > 1
        ? 'Choose a trigger to run the saved automation'
        : 'Run the saved automation now'
  return (
    <RunNowControl automationId={props.document?.automationId} choices={triggers()} unavailable={reason()} tip={tip()} menuLabel="Run which trigger"
      busy={props.busy} onNotice={props.onNotice} start={choice => dispatchRun(hass, props.document!.automationId, choice.action, queryClient)} />
  )
}

/**
 * Run now over any saved document's steps: one choice runs at a press; several open a menu of them (label and
 * summary). Unavailable, it is disabled and its tooltip says why; while its request is in flight it takes no press
 * but keeps the focus. `start` sends the request (and remembers the run for polling, as `acceptRun` does); its answer
 * or its error in the server's words is said in a notice.
 */
export function RunNowControl<C extends RunChoice>(props: {
  /** The run history key of the saved document; the run it started is forgotten when this changes or the page closes. */
  automationId?: string
  choices: C[]
  /** Why nothing can be run now, if so (its tooltip then). */
  unavailable?: string
  /** The tooltip while it can run. */
  tip: string
  /** The menu's accessible name. */
  menuLabel: string
  busy?: boolean
  start(choice: C): Promise<unknown>
  onNotice(notice: RunNotice): void
}) {
  const menuId = createUniqueId()
  let root!: HTMLDivElement
  let trigger!: HTMLButtonElement
  let menu: HTMLDivElement | undefined
  const [open, setOpen] = createSignal(false)
  const [running, setRunning] = createSignal(false)
  const choices = () => props.choices
  const reason = () => props.unavailable
  const tip = () => reason() ?? props.tip
  // The run history whose run this control started (captured then, not read at cleanup): forgotten when the page
  // leaves it, for another document or a new draft, or closes.
  let started: string | undefined
  const forget = () => {
    if (started) forgetAcceptedRun(started)
    started = undefined
  }
  createEffect(on(() => props.automationId, id => id !== started && forget(), { defer: true }))
  onCleanup(forget)

  const items = () => [...(menu?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
  const close = (restore = true) => {
    setOpen(false)
    if (restore) trigger.focus()
  }
  const outside = (event: PointerEvent) => {
    if (open() && !event.composedPath().includes(root)) close(false)
  }
  document.addEventListener('pointerdown', outside)
  onCleanup(() => document.removeEventListener('pointerdown', outside))

  const run = async (choice: C) => {
    const automationId = props.automationId
    if (!automationId || running()) return
    setRunning(true)
    try {
      if (started !== automationId) forget()
      started = automationId
      await props.start(choice)
      props.onNotice({ text: choices().length > 1 ? `Run of ${choice.label} started. Its result is under Runs.` : 'Run started. Its result is under Runs.' })
    } catch (error) {
      props.onNotice({ text: `Run request failed: ${runErrorMessage(error)}`, error: true })
    } finally {
      setRunning(false)
    }
  }
  const press = () => {
    const list = choices()
    if (reason() || running() || !list.length) return
    if (list.length === 1) return void run(list[0])
    if (open()) return close()
    setOpen(true)
    items()[0]?.focus()
  }
  const keys = (event: KeyboardEvent) => {
    const buttons = items()
    const current = buttons.indexOf(event.target as HTMLButtonElement)
    if (event.key === 'Escape' || event.key === 'Tab') {
      if (event.key === 'Escape') event.preventDefault()
      event.stopPropagation()
      close(event.key === 'Escape')
      return
    }
    const next = { ArrowDown: (current + 1) % buttons.length, ArrowUp: (current - 1 + buttons.length) % buttons.length, Home: 0, End: buttons.length - 1 }[event.key]
    if (next === undefined) return
    event.preventDefault()
    buttons[next]?.focus()
  }

  return (
    <div class="hs-run-now" ref={root}>
      {/* In flight: marked, not disabled, so the focus stays on it; a press does nothing. */}
      <Button
        ref={trigger}
        class="hs-topbar__run"
        aria-busy={running() || undefined}
        aria-disabled={running() ? 'true' : undefined}
        disabled={!!reason() || !choices().length || props.busy}
        title={tip()}
        aria-haspopup={choices().length > 1 ? 'menu' : undefined}
        aria-expanded={choices().length > 1 ? open() : undefined}
        aria-controls={open() ? menuId : undefined}
        onClick={press}
        onKeyDown={event => {
          if (choices().length > 1 && (event.key === 'ArrowDown' || event.key === 'ArrowUp') && !reason() && !running()) {
            event.preventDefault()
            setOpen(true)
            ;(event.key === 'ArrowUp' ? items().at(-1) : items()[0])?.focus()
          }
        }}
      >
        Run now
      </Button>
      <Show when={open()}>
        <div ref={menu} id={menuId} role="menu" aria-label={props.menuLabel} class="hs-run-now__menu" on:keydown={keys}>
          <For each={choices()}>
            {choice => (
              <button type="button" role="menuitem" tabindex="-1" class="hs-run-now__item" onClick={() => { close(); void run(choice) }}>
                <strong>{choice.label}</strong>
                <small>{choice.summary}</small>
              </button>
            )}
          </For>
        </div>
      </Show>
    </div>
  )
}
