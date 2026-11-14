import { batch, createEffect, createMemo, createSignal, on, Show } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { A } from '@solidjs/router'
import type { MenuItem } from '../../components/Menu'
import { summarizeTrigger } from '../../components/TriggerSummary'
import { BottomDrawer } from '../BottomDrawer'
import { FlowAdapter, type FlowControls } from '../flow/FlowAdapter'
import { createFullscreen, createFullscreenPage, FullscreenButton } from '../fullscreen'
import { Glyph } from '../Glyph'
import { icons } from '../icons'
import { media } from '../media'
import { nodeCatalog } from '../contract'
import { TopBar } from '../TopBar'
import { CustomCodeEditor } from '../CustomCodeEditor'
import type { RunStatus, RunTraceResponse, TraceEvent } from '../../types'
import { Timeline } from './Timeline'
import { runDuration, runTime, TraceInspector } from './TraceInspector'
import { flowTrace, inChosenFrame, traceModel } from './traceModel'

/** Focus is where keys mean text or a choice in a list: the page's shortcuts leave those keys alone. */
const typing = (target: Element) =>
  !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="menu"], [role="listbox"], dialog')
/** The run status chip's tone, from the save-state chip's tones. */
const tones: Record<RunStatus, string> = {
  Succeeded: 'saved', Failed: 'danger', Interrupted: 'danger', Cancelled: 'dirty', Stopped: 'dirty', Skipped: 'dirty', Running: 'busy', Pending: 'busy',
}

/**
 * A run on view, read-only: the editor page's shape with the run in place of editing. The top bar ({@link TopBar},
 * read-only) has back, the automation's name, the run's status, "Open in editor" and a "⋯" menu; the canvas shows the
 * graph as it was for this run with the path it took; the value panel sits where the inspector does; the timeline is
 * in the bottom drawer. The page root is the keyboard boundary, as in the editor; `F` toggles full screen.
 *
 * The canvas shows the whole run until an iteration or branch is chosen (a timeline row, the panel's "Which run"):
 * then the steps and wires of that loop or parallel show that iteration or branch only.
 */
export function TracePage(props: {
  response: RunTraceResponse
  /** The automation's revision now, when known: a run of another revision says it used an earlier version. */
  currentRevision?: number
  editorHref: string
  onBack(): void
  /** The last read of a live run failed, in words ("could not reach Home Assistant"): updates are paused until one succeeds. */
  paused?: string
  /** A read after the failed one is under way. */
  retrying?: boolean
  onRetry?(): void
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

  // ---- Selection: a step, the run of it on view, and the frame (iteration or branch; "" the whole run) the canvas shows.
  const [nodeId, setNodeId] = createSignal<string>()
  const [seq, setSeq] = createSignal<number>()
  const [frame, setFrame] = createSignal('')
  const [focusId, setFocusId] = createSignal<string>()
  /** A Custom C# node whose code is on view (read only). */
  const [codeNodeId, setCodeNodeId] = createSignal<string>()
  const codeNode = () => graph()?.nodes.find(n => n.id === codeNodeId())
  createEffect(() => { if (codeNodeId() && !codeNode()) setCodeNodeId(undefined) })
  const event = () => {
    const current = model()?.event(seq())
    return current && current.node === nodeId() ? current : undefined
  }
  /** One run chosen (a timeline row, the panel's chooser): its step, and its iteration or branch on the canvas. */
  const show = (picked: TraceEvent) => batch(() => {
    setNodeId(picked.node)
    setSeq(picked.seq)
    setFrame(picked.frame)
  })
  /**
   * A step chosen on the canvas or through a "From …" source; the view stays where it is. Inside the loop or parallel
   * of the chosen iteration or branch it shows its run there (none: it did not run there); anywhere else, or with
   * nothing chosen, the canvas shows the whole run and the panel the step's last run.
   */
  const selectNode = (id?: string) => {
    const runs = model()
    batch(() => {
      setFocusId(undefined)
      setNodeId(id)
      if (!id || !runs) return void setSeq(undefined)
      if (inChosenFrame(id, runs.eventsOf(id).map(e => e.frame), frame())) return void setSeq(runs.eventIn(id, frame())?.seq)
      setFrame('')
      setSeq(runs.eventsOf(id).at(-1)?.seq)
    })
  }
  /** The whole run again (the chooser's first option): the step's last run in the panel. */
  const showWholeRun = () => batch(() => {
    setFrame('')
    setSeq(nodeId() ? model()?.eventsOf(nodeId()!).at(-1)?.seq : undefined)
  })
  /** A run chosen in the timeline: its step is selected and centred. */
  const selectEvent = (picked: TraceEvent) => {
    const again = focusId() === picked.node
    show(picked)
    setFocusId(picked.node)
    // Already the focus node: the canvas centres only on a change, so bring it back into view if it was panned away.
    if (again) flow?.reveal(picked.node, root.querySelector('.hs-canvas-area')?.getBoundingClientRect().bottom ?? innerHeight)
  }
  // A failed run opens on the step that failed (in the iteration or branch it failed in).
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
  createEffect(on(narrow, isNarrow => setPanelOpen(!isNarrow), { defer: true }))
  const openPanel = () => {
    setPanelOpen(true)
    // The step's title, else (the run's overview) the close button: focus never stays on the peek bar that goes away.
    queueMicrotask(() => (root.querySelector<HTMLElement>('.hs-trace-panel .hs-trace-node__title') ?? root.querySelector<HTMLElement>('.hs-trace-panel .hs-insp-close'))?.focus())
  }

  // ---- The timeline drawer: open by default where there is room.
  const [drawerOpen, setDrawerOpen] = createSignal(!narrow())

  // ---- Full screen: the drawer folds away; a compact bar keeps the name, the status and "Exit full screen".
  const fullscreenPage = createFullscreenPage({
    root: () => root,
    fullscreen,
    onEnter: () => setDrawerOpen(false),
    fallback: () => root.querySelector<HTMLElement>('.hs-flow'),
  })
  // The paused note goes once a read succeeds: focus that was on its button moves to the page's title, never to the
  // page.
  let retryFocused = false
  createEffect(
    on(
      () => !!props.paused,
      (paused, was) => {
        if (paused || !was) return
        const focused = (root.getRootNode() as Document | ShadowRoot).activeElement
        const lost = !focused || focused === root.ownerDocument.body || !focused.isConnected
        if (retryFocused && lost)
          queueMicrotask(() => {
            const title = root.querySelector<HTMLElement>('.hs-topbar__heading')
            if (!title) return
            title.tabIndex = -1
            title.focus()
          })
      },
      { defer: true },
    ),
  )
  const keyboard = (event: KeyboardEvent) => {
    event.stopPropagation()
    if (typing(event.target as Element)) return
    fullscreenPage.keydown(event)
  }
  const menu = (): MenuItem[] => (fullscreen.active() ? [] : [{ label: 'Full screen', onSelect: fullscreenPage.enter }])

  const earlier = () => props.currentRevision !== undefined && snapshot() !== undefined && snapshot()!.visualRevision !== props.currentRevision
  const triggerLine = () => [run().actionLabel, run().trigger && summarizeTrigger(run().trigger!)].filter(Boolean).join(' · ')
  const reason = () => (run().error && run().status !== 'Succeeded' ? run().error!.split('\n', 1)[0] : undefined)
  const dropped = () => props.response.trace?.dropped ?? 0
  const status = () => (
    <span class={`hs-save-chip hs-save-chip--${tones[run().status] ?? 'busy'}`}>
      <span class="hs-save-chip__text" role="status"><span class="hs-visually-hidden">Run status: </span>{run().status}</span>
      <span class="hs-save-chip__short" aria-hidden="true">{run().status}</span>
    </span>
  )

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
      on:focusin={event => (retryFocused = !!(event.target as Element).closest?.('.hs-trace-paused__retry'))}
    >
      <TopBar
        name={name()}
        onBack={() => props.onBack()}
        backLabel="Back to the automation’s runs"
        status={status()}
        actions={<A class="hs-button hs-button--secondary hs-fs-hide hs-trace-open" href={props.editorHref}>Open in editor</A>}
        fullscreen={fullscreen.active()}
        onExitFullscreen={() => void fullscreen.exit()}
        menu={menu()}
      />
      <div class="hs-trace-strip hs-fs-hide">
        <p class="hs-trace-strip__line">
          <span class="hs-trace-strip__trigger">{triggerLine()}</span>
          <span>Started {runTime(run().startUtc ?? run().receivedUtc)}</span>
          <span>{props.response.live ? 'Running…' : `Took ${runDuration(run().durationMs)}`}</span>
        </p>
        {/* Mounted from the first failed read until one succeeds: a retry changes its button, never removes it. */}
        <Show when={props.paused}>
          {reason => (
            <p class="hs-trace-strip__note hs-trace-strip__note--warning hs-trace-paused">
              <Glyph paths={icons.warning} />
              <span role="status">Updates paused: {reason().replace(/\.$/, '')}.</span>
              <button type="button" class="hs-button hs-button--secondary hs-trace-paused__retry" aria-busy={props.retrying || undefined}
                aria-disabled={props.retrying || undefined} onClick={() => !props.retrying && props.onRetry?.()}>
                {props.retrying ? 'Trying…' : 'Try again'}
              </button>
            </p>
          )}
        </Show>
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
              <TraceInspector run={run()} catalog={catalog()} frame="" onSelectNode={() => {}} onSelectEvent={() => {}} onSelectWholeRun={() => {}} docked open onOpen={() => {}} onClose={() => {}} />
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
                  toolbarExtra={<FullscreenButton fullscreen={fullscreen} onToggle={fullscreenPage.toggle} />}
                  controls={controls => (flow = controls)}
                  onEditCode={id => { selectNode(id); setCodeNodeId(id) }}
                />
              </div>
              <TraceInspector
                run={run()}
                graph={current()}
                catalog={catalog()}
                model={model()}
                frame={frame()}
                selectedNodeId={nodeId()}
                event={event()}
                onSelectNode={id => inPanel(() => selectNode(id))}
                onSelectEvent={value => {
                  const picked = model()?.event(value)
                  if (picked) inPanel(() => show(picked))
                }}
                onSelectWholeRun={() => inPanel(showWholeRun)}
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
                        <Timeline model={runs()} graph={current()} catalog={catalog()} selectedNodeId={nodeId()} selectedSeq={seq()} live={props.response.live}
                          paused={!!props.paused} onSelect={selectEvent} />
                      ),
                    },
                  ]}
                />
              )}
            </Show>
          </>
        )}
      </Show>
      <Show when={codeNode() && codeNodeId()} keyed>
        {_id => <CustomCodeEditor node={codeNode()!} catalog={catalog()} readOnly onClose={() => setCodeNodeId(undefined)} />}
      </Show>
    </section>
  )
}
