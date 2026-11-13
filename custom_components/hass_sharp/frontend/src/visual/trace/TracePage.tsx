import { batch, createEffect, createMemo, createSignal, on, onCleanup, Show } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { A } from '@solidjs/router'
import { Icon } from '../../components/Controls'
import { Menu, type MenuItem } from '../../components/Menu'
import { summarizeTrigger } from '../../components/TriggerSummary'
import { BottomDrawer } from '../BottomDrawer'
import { FlowAdapter, type FlowControls } from '../flow/FlowAdapter'
import { createFullscreen, fullscreenIcons, FullscreenButton } from '../fullscreen'
import { Glyph } from '../Glyph'
import { icons } from '../icons'
import { nodeCatalog } from '../contract'
import type { RunStatus, RunTraceResponse, TraceEvent } from '../../types'
import { Timeline } from './Timeline'
import { runDuration, runTime, TraceInspector } from './TraceInspector'
import { flowTrace, nextFrame, traceModel } from './traceModel'

/** Focus is where keys mean text or a choice in a list: the page's shortcuts leave those keys alone. */
const typing = (target: Element) =>
  !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="menu"], [role="listbox"], dialog')
/** The run status chip's tone, from the save-state chip's tones. */
const tones: Record<RunStatus, string> = {
  Succeeded: 'saved', Failed: 'danger', Interrupted: 'danger', Cancelled: 'dirty', Stopped: 'dirty', Skipped: 'dirty', Running: 'busy', Pending: 'busy',
}

/** A media query as a signal (false where `matchMedia` is missing). */
function media(query: string) {
  const list = typeof window.matchMedia === 'function' ? window.matchMedia(query) : undefined
  const [matches, setMatches] = createSignal(!!list?.matches)
  const update = () => setMatches(!!list?.matches)
  list?.addEventListener?.('change', update)
  onCleanup(() => list?.removeEventListener?.('change', update))
  return matches
}

/**
 * A run on view, read-only: the editor page's shape with the run in place of editing. The top bar has back, the
 * automation's name, the run's status, "Open in editor" and a "⋯" menu; the canvas shows the graph as it was for this
 * run with the path it took; the value panel sits where the inspector does; the timeline is in the bottom drawer.
 * The page root is the keyboard boundary, as in the editor; `F` toggles full screen.
 */
export function TracePage(props: {
  response: RunTraceResponse
  /** The automation's revision now, when known: an older run says it used an earlier version. */
  currentRevision?: number
  editorHref: string
  onBack(): void
}) {
  let root!: HTMLElement
  let flow: FlowControls | undefined
  const run = () => props.response.run
  const snapshot = () => props.response.snapshot ?? undefined
  const graph = () => snapshot()?.graph
  const catalog = () => snapshot()?.catalog ?? nodeCatalog
  const model = createMemo(() => {
    const trace = props.response.trace, current = graph()
    return trace && current ? traceModel(trace, current, catalog()) : undefined
  })
  const name = () => graph()?.name || run().actionLabel || 'Automation run'
  const activeElement = () => (root.getRootNode() as Document | ShadowRoot).activeElement as HTMLElement | null

  // ---- Selection: a step, the run of it on view, and the frame (iteration or branch) the canvas shows.
  const [nodeId, setNodeId] = createSignal<string>()
  const [seq, setSeq] = createSignal<number>()
  const [frame, setFrame] = createSignal('')
  const [focusId, setFocusId] = createSignal<string>()
  const event = () => {
    const current = model()?.event(seq())
    return current && current.node === nodeId() ? current : undefined
  }
  const show = (picked: TraceEvent) => batch(() => {
    setNodeId(picked.node)
    setSeq(picked.seq)
    setFrame(current => nextFrame(current, picked.frame))
  })
  /** A step chosen on the canvas: its run in the frame on view (else its latest); the view stays where it is. */
  const selectNode = (id?: string) => {
    const runs = model()
    const picked = id ? (runs?.eventIn(id, frame()) ?? runs?.eventsOf(id).at(-1)) : undefined
    batch(() => {
      setFocusId(undefined)
      if (picked) show(picked)
      else {
        setNodeId(id)
        setSeq(undefined)
      }
    })
  }
  /** A run chosen in the timeline: its step is selected and centred. */
  const selectEvent = (picked: TraceEvent) => {
    const again = focusId() === picked.node
    show(picked)
    setFocusId(picked.node)
    // Already the focus node: the canvas centres only on a change, so bring it back into view if it was panned away.
    if (again) flow?.reveal(picked.node, root.querySelector('.hs-canvas-area')?.getBoundingClientRect().bottom ?? innerHeight)
  }
  // A failed run opens on the step that failed.
  createEffect(
    on(model, (current, before) => {
      if (before || !current) return
      const failed = current.events.find(e => e.status === 'failed')
      if (failed) {
        show(failed)
        setFocusId(failed.node)
      }
    }),
  )
  const trace = createMemo(() => {
    const current = model(), shown = graph()
    return current && shown ? flowTrace(current, shown, frame(), focusId()) : undefined
  })

  // ---- The value panel: docked beside the canvas, or a sheet (below 900px) / floating panel (full screen).
  const narrow = media('(max-width: 899px)')
  const fullscreen = createFullscreen(() => root)
  const floating = () => narrow() || fullscreen.active()
  const [panelOpen, setPanelOpen] = createSignal(!narrow())
  // A selection made on the canvas or in the timeline comes back as the sheet's peek bar (a full-screen panel opens
  // for it); one made inside the open panel (another run, a "From …" source) keeps it open.
  let selectingInPanel = false
  const inPanel = (action: () => void) => {
    selectingInPanel = true
    setTimeout(() => (selectingInPanel = false))
    action()
  }
  createEffect(on(() => [nodeId(), seq()], () => { if (!(selectingInPanel && panelOpen())) setPanelOpen(!narrow()) }, { defer: true }))
  createEffect(on(narrow, wide => setPanelOpen(!wide), { defer: true }))
  const openPanel = () => {
    setPanelOpen(true)
    // The step's title, else (the run's overview) the close button: focus never stays on the peek bar that goes away.
    queueMicrotask(() => (root.querySelector<HTMLElement>('.hs-trace-panel .hs-trace-node__title') ?? root.querySelector<HTMLElement>('.hs-trace-panel .hs-insp-close'))?.focus())
  }

  // ---- The timeline drawer: open by default where there is room.
  const [drawerOpen, setDrawerOpen] = createSignal(!narrow())

  // ---- Full screen: the drawer folds away; a compact bar keeps the name, the status and "Exit full screen".
  let opener: HTMLElement | null = null
  const toggleFullscreen = () => {
    if (fullscreen.active()) return void fullscreen.exit()
    opener = activeElement()
    setDrawerOpen(false)
    void fullscreen.enter()
  }
  createEffect(
    on(
      fullscreen.active,
      active => {
        const focused = activeElement()
        const lost = !focused || focused === root.ownerDocument.body
        if (active) {
          if (lost || focused.closest('.hs-fs-hide, .hs-drawer__panel')) queueMicrotask(() => root.querySelector<HTMLElement>('.hs-topbar__exit')?.focus())
          return
        }
        if (lost || focused.closest('.hs-topbar__exit'))
          queueMicrotask(() => (opener?.isConnected ? opener : root.querySelector<HTMLElement>('.hs-flow'))?.focus({ preventScroll: true }))
      },
      { defer: true },
    ),
  )
  const keyboard = (event: KeyboardEvent) => {
    event.stopPropagation()
    if (typing(event.target as Element)) return
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key
    if (!event.ctrlKey && !event.metaKey && !event.altKey && key === 'f') {
      event.preventDefault()
      if (!event.repeat) toggleFullscreen()
      return
    }
    // Escape leaves the maximised fallback only when nothing on the way (a menu, the drawer, the canvas) used it.
    if (event.key === 'Escape' && fullscreen.maximised()) setTimeout(() => !event.defaultPrevented && fullscreen.maximised() && void fullscreen.exit())
  }
  const menu = (): MenuItem[] => (fullscreen.active() ? [] : [{ label: 'Full screen', onSelect: toggleFullscreen }])

  const earlier = () => props.currentRevision !== undefined && snapshot() !== undefined && snapshot()!.visualRevision < props.currentRevision
  const triggerLine = () => [run().actionLabel, run().trigger && summarizeTrigger(run().trigger!)].filter(Boolean).join(' · ')
  const reason = () => (run().error && run().status !== 'Succeeded' ? run().error!.split('\n', 1)[0] : undefined)
  const dropped = () => props.response.trace?.dropped ?? 0

  return (
    <section
      ref={element => {
        root = element
        delegateEvents(['keydown', 'keyup'], element as unknown as Document)
      }}
      class="hs-visual hs-trace"
      classList={{ 'hs-visual--fullscreen': fullscreen.active(), 'hs-visual--maximised': fullscreen.maximised(), 'hs-visual--floating': floating() }}
      aria-label="Run trace"
      on:keydown={keyboard}
      on:keyup={event => event.stopPropagation()}
    >
      <header class="hs-topbar">
        <button type="button" class="hs-topbar__icon hs-fs-hide" aria-label="Back" title="Back" onClick={() => props.onBack()}>
          <Icon name="arrow" />
        </button>
        <div class="hs-topbar__title hs-trace-title">
          <h1 class="hs-trace-title__name">{name()}</h1>
        </div>
        <span class={`hs-save-chip hs-save-chip--${tones[run().status] ?? 'busy'}`} role="status">
          <span class="hs-visually-hidden">Run status: </span>
          {run().status}
        </span>
        <div class="hs-topbar__actions">
          <A class="hs-button hs-button--secondary hs-fs-hide hs-trace-open" href={props.editorHref}>Open in editor</A>
          <Show when={fullscreen.active()}>
            <button type="button" class="hs-button hs-button--secondary hs-topbar__exit" aria-label="Exit full screen" title="Exit full screen (F)" onClick={() => void fullscreen.exit()}>
              <Glyph paths={fullscreenIcons.exit} class="hs-topbar__exit-icon" />
              <span class="hs-topbar__exit-text">Exit full screen</span>
            </button>
          </Show>
          <div class="hs-fs-hide hs-topbar__more">
            <Menu label="More actions" items={menu()} />
          </div>
        </div>
      </header>
      <div class="hs-trace-strip hs-fs-hide">
        <p class="hs-trace-strip__line">
          <span class="hs-trace-strip__trigger">{triggerLine()}</span>
          <span>Started {runTime(run().startUtc ?? run().receivedUtc)}</span>
          <span>{props.response.live ? 'Running…' : `Took ${runDuration(run().durationMs)}`}</span>
        </p>
        <Show when={reason()}>
          {text => <p class="hs-trace-strip__note" classList={{ 'hs-trace-strip__note--danger': run().status === 'Failed' || run().status === 'Interrupted' }}>
            {run().status === 'Cancelled' ? `Cancelled: ${text()}` : text()}
          </p>}
        </Show>
        <Show when={earlier()}>
          <p class="hs-trace-strip__note" title="The canvas shows the automation as it was for this run, not as it is now.">This run used an earlier version.</p>
        </Show>
        <Show when={props.response.trace?.truncated}>
          <p class="hs-trace-strip__note hs-trace-strip__note--warning">
            <Glyph paths={icons.warning} />
            {dropped()
              ? `Only part of this run was recorded: ${dropped().toLocaleString()} ${dropped() === 1 ? 'step is' : 'steps are'} missing at the end.`
              : 'Only part of this run was recorded.'}
          </p>
        </Show>
      </div>
      <Show
        when={graph()}
        fallback={
          <div class="hs-trace-missing" role="note">
            <h2>This run can no longer be shown on the canvas</h2>
            <p>The automation as it was for this run is no longer stored. Its summary is below.</p>
            <div class="hs-trace-missing__panel">
              <TraceInspector run={run()} catalog={catalog()} onSelectNode={() => {}} onSelectEvent={() => {}} docked open onOpen={() => {}} onClose={() => {}} />
            </div>
          </div>
        }
      >
        {current => (
          <>
            <Show when={!props.response.trace}>
              <p class="hs-visual-alert hs-visual-alert--quiet hs-trace-notrace" role="note">
                No step-by-step record was kept for this run (records are kept for the newest 1,000 runs). The canvas shows the automation as it was for this run.
              </p>
            </Show>
            <div class="hs-visual-workspace">
              <div class="hs-canvas-area">
                <FlowAdapter
                  graph={current()}
                  catalog={catalog()}
                  readOnly
                  trace={trace()}
                  selectedId={nodeId()}
                  onSelect={selectNode}
                  toolbarExtra={<FullscreenButton fullscreen={fullscreen} onToggle={toggleFullscreen} />}
                  controls={controls => (flow = controls)}
                />
              </div>
              <TraceInspector
                run={run()}
                graph={current()}
                catalog={catalog()}
                model={model()}
                selectedNodeId={nodeId()}
                event={event()}
                onSelectNode={id => inPanel(() => selectNode(id))}
                onSelectEvent={value => {
                  const picked = model()?.event(value)
                  if (picked) inPanel(() => batch(() => { setNodeId(picked.node); setSeq(picked.seq); setFrame(picked.frame) }))
                }}
                docked={!floating()}
                open={panelOpen()}
                onOpen={openPanel}
                onClose={() => setPanelOpen(false)}
              />
            </div>
            <Show when={model()}>
              {runs => (
                <BottomDrawer
                  label="Timeline"
                  open={drawerOpen()}
                  tab="timeline"
                  onOpenChange={setDrawerOpen}
                  onTabChange={() => {}}
                  tabs={[
                    {
                      id: 'timeline',
                      label: 'Timeline',
                      get count() { return runs().events.length },
                      content: () => (
                        <Timeline model={runs()} graph={current()} catalog={catalog()} selectedNodeId={nodeId()} selectedSeq={seq()} live={props.response.live} onSelect={selectEvent} />
                      ),
                    },
                  ]}
                />
              )}
            </Show>
          </>
        )}
      </Show>
    </section>
  )
}
