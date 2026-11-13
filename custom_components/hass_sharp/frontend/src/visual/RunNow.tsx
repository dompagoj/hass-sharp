import { createSignal, createUniqueId, For, onCleanup, Show } from 'solid-js'
import { useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { dispatchRun, forgetAcceptedRun, runErrorMessage, type RunAction } from '../components/runDispatch'
import { useHass } from '../context'
import type { VisualDocument } from './api'
import type { VisualCatalogSnapshot } from './contract'
import { nodeTitle } from './flow/flowModel'
import { triggerMethod, visualActionLabels, visualRunClasses } from './graph'
import { nodeSummary } from './nodeSummary'

/** A trigger of the saved automation that can be started by hand. */
export interface ManualTrigger {
  nodeId: string
  action: RunAction
  /** The trigger's card title (with its id when two share one). */
  label: string
  /** Its one-line summary, as on the card. */
  summary: string
}

/** The saved graph's triggers that allow manual runs, in the graph's order. */
export function manualTriggers(document: VisualDocument | undefined, catalog: VisualCatalogSnapshot): ManualTrigger[] {
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
      summary: nodeSummary(node, catalog, undefined, graph),
    }]
  })
}

export interface RunNotice { text: string; error?: boolean }

/**
 * The editor's one place to start a run of the saved automation. With one trigger that allows manual runs it runs
 * that one; with several it opens a menu of them (label and summary). Disabled, its tooltip says why. A started run
 * is remembered so the Runs tab polls until it has finished.
 */
export function RunNowButton(props: {
  document?: VisualDocument
  catalog: VisualCatalogSnapshot
  /** Why the saved version cannot be run now (a conflict, the catalog failed, not activated), if so. */
  unavailable?: string
  dirty: boolean
  busy?: boolean
  onNotice(notice: RunNotice): void
}) {
  const hass = useHass()
  const queryClient = useQueryClient()
  const menuId = createUniqueId()
  let root!: HTMLDivElement
  let trigger!: HTMLButtonElement
  let menu: HTMLDivElement | undefined
  const [open, setOpen] = createSignal(false)
  const [running, setRunning] = createSignal(false)
  const triggers = () => manualTriggers(props.document, props.catalog)
  const reason = () => {
    if (!props.document) return 'Save the automation to run it'
    if (props.unavailable) return props.unavailable
    if (!triggers().length) return 'No trigger allows manual runs'
  }
  const tip = () =>
    reason() ??
    (props.dirty
      ? 'Runs the last saved version; unsaved changes are not included'
      : triggers().length > 1
        ? 'Choose a trigger to run the saved automation'
        : 'Run the saved automation now')
  onCleanup(() => props.document && forgetAcceptedRun(props.document.automationId))

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

  const run = async (choice: ManualTrigger) => {
    const doc = props.document
    if (!doc || running()) return
    setRunning(true)
    try {
      await dispatchRun(hass, doc.automationId, choice.action, queryClient)
      props.onNotice({ text: triggers().length > 1 ? `Run of ${choice.label} started. Its result is under Runs.` : 'Run started. Its result is under Runs.' })
    } catch (error) {
      props.onNotice({ text: `Run request failed: ${runErrorMessage(error)}`, error: true })
    } finally {
      setRunning(false)
    }
  }
  const press = () => {
    const list = triggers()
    if (reason() || !list.length) return
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
      <Button
        ref={trigger}
        class="hs-topbar__run"
        busy={running()}
        disabled={!!reason() || props.busy}
        title={tip()}
        aria-haspopup={triggers().length > 1 ? 'menu' : undefined}
        aria-expanded={triggers().length > 1 ? open() : undefined}
        aria-controls={open() ? menuId : undefined}
        onClick={press}
        onKeyDown={event => {
          if (triggers().length > 1 && (event.key === 'ArrowDown' || event.key === 'ArrowUp') && !reason()) {
            event.preventDefault()
            setOpen(true)
            ;(event.key === 'ArrowUp' ? items().at(-1) : items()[0])?.focus()
          }
        }}
      >
        Run now
      </Button>
      <Show when={open()}>
        <div ref={menu} id={menuId} role="menu" aria-label="Run which trigger" class="hs-run-now__menu" on:keydown={keys}>
          <For each={triggers()}>
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
