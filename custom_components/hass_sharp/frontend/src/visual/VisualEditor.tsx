import { batch, createEffect, createMemo, createSignal, on, onCleanup, onMount, Show } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import type { MenuItem } from '../components/Menu'
import { HelpDialog } from '../components/HelpDialog'
import { AutomationRuns } from '../components/AutomationRuns'
import { Dialog } from '../components/Dialog'
import { useEntities } from '../entities'
import { useHass } from '../context'
import { useRegistry } from '../registry'
import type { RunHistoryResponse } from '../types'
import { FlowAdapter, type FlowControls } from './flow/FlowAdapter'
import { Inspector, type InspectorLastRun } from './Inspector'
import { focusRow } from './InputRow'
import { NodeLibraryPanel } from './NodeLibraryPanel'
import { BottomDrawer, CodePanel, ProblemsPanel } from './BottomDrawer'
import { createFullscreen, createFullscreenPage, FullscreenButton } from './fullscreen'
import { media } from './media'
import { createVisualSession } from './session'
import { visualError, type VisualDiagnostic, type VisualDocument } from './api'
import { createGraph, createNodeAt, serializeGraph, type NodeKind } from './graph'
import { RunNowButton } from './RunNow'
import { EnabledSwitch } from '../automations/EnabledSwitch'
import { setAutomationEnabled } from '../automations/enabled'
import { soon } from './soon'
import { createNotice, TopBar } from './TopBar'

export type DrawerTab = 'problems' | 'code' | 'runs'
/** Focus is where keys mean text or a choice in a list: the page's shortcuts leave those keys alone. */
const typing = (target: Element) =>
  !!target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="menu"], [role="listbox"], .hs-card-editor, .hs-quick-add, dialog')
const failedStatuses = new Set(['Failed', 'Interrupted'])

/**
 * The visual editor page: the top bar ({@link TopBar}), the step library, the canvas, the inspector and a bottom
 * drawer with Problems, Generated C# and Runs.
 * The page root is the keyboard boundary: keys never leave it for Home Assistant's shortcuts, and everything
 * focusable (menus, dialogs, the floating inspector) is rendered inside it. The canvas can go full screen.
 */
export function VisualEditor(props: {
  name: string
  documentId?: string
  onDirtyChange?: (dirty: boolean) => void
  onSaved?: (document: VisualDocument) => void
  /** Back to the list; the page shows no back button without it. */
  onBack?: () => void
  /** Opens with the bottom drawer on this tab (a run's trace goes back to the Runs tab). */
  drawer?: DrawerTab
}) {
  const hass = useHass()
  const session = createVisualSession(hass, createGraph(props.name))
  const queryClient = useQueryClient()
  const entities = useEntities()
  onMount(() => {
    void entities.refetch()
  })
  const history = session.history
  const { graph, commit } = history
  const admin = () => !!hass.user?.is_admin
  let editor!: HTMLElement
  let flow: FlowControls | undefined
  const activeElement = () => (editor.getRootNode() as Document | ShadowRoot).activeElement as HTMLElement | null
  const canvas = () => editor.querySelector<HTMLElement>('.hs-flow')
  const inspector = () => editor.querySelector<HTMLElement>('.hs-inspector')
  const card = (id: string) => [...editor.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.dataset.id === id)

  createEffect(() => props.onDirtyChange?.(session.dirty()))
  createEffect(
    on(
      () => [props.documentId, props.name] as const,
      ([id, name], previous) => {
        if (id && id !== session.document()?.id) void session.load(id)
        else if (!id && previous) session.newDraft(createGraph(name))
      },
    ),
  )
  const save = async () => {
    const result = await session.save()
    if (result) {
      void queryClient.invalidateQueries({ queryKey: ['visual-automations'] })
      if (!session.dirty()) props.onSaved?.(result.document)
    }
  }
  const [reloadDialog, setReloadDialog] = createSignal(false)
  const reload = () => {
    if (session.loading() || session.saving()) return
    if (session.dirty()) setReloadDialog(true)
    else if (props.documentId) void session.load(props.documentId)
  }
  const [helpOpen, setHelpOpen] = createSignal(false)
  const beforeUnload = (event: BeforeUnloadEvent) => {
    if (session.dirty()) {
      event.preventDefault()
      event.returnValue = ''
    }
  }
  onMount(() => window.addEventListener('beforeunload', beforeUnload))
  onCleanup(() => window.removeEventListener('beforeunload', beforeUnload))
  // Each finished load of a saved graph fits the canvas to it.
  const [loads, setLoads] = createSignal(0)
  createEffect(on(session.loading, (loading, before) => before && !loading && setLoads(count => count + 1), { defer: true }))

  // ---- Selection, and the inspector's sheet (below 900 px) or floating panel (full screen).
  const [selectedId, setSelectedId] = createSignal<string | undefined>('trigger-1')
  const [selection, setSelection] = createSignal<{ ids: string[]; edge?: string }>({ ids: [] })
  const selected = createMemo(() => graph().nodes.find(node => node.id === selectedId()))
  const [sourceLine, setSourceLine] = createSignal<number>()
  const choose = (id?: string) => {
    setSelectedId(id)
    setSourceLine(undefined)
  }
  const narrow = media('(max-width: 899px)')
  const fullscreen = createFullscreen(() => editor)
  /**
   * The inspector is a sheet (below 900px) or floats over a full-screen canvas: it opens and closes. Otherwise it is
   * the side panel, always shown.
   */
  const floating = () => narrow() || fullscreen.active()
  // A sheet starts as its peek bar (never open on load); a floating panel opens when a step is selected.
  const [panelOpen, setPanelOpen] = createSignal(!narrow())
  // A selection made inside the panel (a "From …" pill, a wire end, a listed step, Duplicate) keeps it open, and
  // the panel focuses the new step's title; one made on the canvas folds a sheet back to its peek bar.
  let selectingInPanel = false
  const inPanel = <T,>(action: () => T): T => {
    selectingInPanel = true
    setTimeout(() => (selectingInPanel = false))
    return action()
  }
  createEffect(
    on(
      () => [selectedId(), selection().ids.join(' '), selection().edge].join('|'),
      () => {
        if (!(selectingInPanel && panelOpen())) setPanelOpen(!narrow())
      },
      { defer: true },
    ),
  )
  // Crossing 900px: the sheet comes back as its peek bar (never expanded); wider, the side panel shows.
  createEffect(on(narrow, isNarrow => setPanelOpen(!isNarrow), { defer: true }))
  /** Opens the sheet or panel; a sheet moves the canvas so the step stays visible above it, and takes focus. */
  const openPanel = (focus = true) => {
    const wasOpen = panelOpen()
    setPanelOpen(true)
    if (!floating()) return
    if (narrow() && !wasOpen)
      requestAnimationFrame(() => {
        const id = selectedId(), sheet = inspector(), area = editor.querySelector('.hs-visual-workspace')
        if (id && sheet && area) flow?.reveal(id, area.getBoundingClientRect().bottom - sheet.offsetHeight)
      })
    if (focus)
      soon(() => {
        const panel = inspector()
        const target = panel?.querySelector<HTMLElement>('.hs-insp-node input[aria-label="Node label"]') ?? panel?.querySelector<HTMLElement>('.hs-insp-sheet-bar ~ * button, .hs-insp-sheet-bar ~ * input')
        target?.focus()
        return !!target && activeElement() === target
      }, editor)
  }

  // ---- Step library, and adding a step.
  const [libraryCollapsed, setLibraryCollapsed] = createSignal(false)
  const [quickAddRequest, setQuickAddRequest] = createSignal(0)
  const addStep = (kind: NodeKind, label?: string) => {
    const next = createNodeAt(graph(), kind, flow?.placeAtCentre() ?? { x: 60, y: 100 }, undefined, session.catalog())
    if (next === graph()) {
      flow?.notify(`Cannot add ${label ?? 'that step'}: this Home Assistant does not offer it.`)
      return
    }
    commit(next)
    choose(next.nodes.at(-1)!.id)
  }

  // ---- The bottom drawer.
  const [drawerOpen, setDrawerOpen] = createSignal(!!props.drawer)
  const [drawerTab, setDrawerTab] = createSignal<DrawerTab>(props.drawer ?? 'problems')
  const [codeView, setCodeView] = createSignal<'code' | 'json'>('code')
  const shownProblems = () => session.diagnostics().filter(d => d.severity > 0)
  const errorCount = () => session.diagnostics().filter(d => d.severity === 3).length
  const openDrawer = (tab: DrawerTab) => batch(() => {
    setDrawerTab(tab)
    setDrawerOpen(true)
  })
  const showLine = (line: number) => {
    batch(() => {
      openDrawer('code')
      setCodeView('code')
      setSourceLine(line)
    })
    soon(() => {
      const row = editor.querySelector<HTMLElement>(`[data-source-line="${line}"]`)
      row?.scrollIntoView?.({ block: 'nearest' })
      return !!row
    })
  }
  const showProblems = () => {
    openDrawer('problems')
    soon(() => {
      const first = editor.querySelector<HTMLElement>('.hs-problem__message') ?? editor.querySelector<HTMLElement>('.hs-drawer__tab[aria-selected="true"]')
      first?.focus()
      first?.scrollIntoView?.({ block: 'nearest' })
      return !!first
    }, editor)
  }
  /** A problem: select its step and focus the field it is about (else its card); the shared C# shows its line. */
  const locate = (diagnostic: VisualDiagnostic) => {
    const nodeId =
      diagnostic.nodeId ??
      session.preview()?.sourceMap.find(range => diagnostic.startLine != null && diagnostic.startLine >= range.startLine && diagnostic.startLine <= range.endLine)?.nodeId
    if (!nodeId || !graph().nodes.some(n => n.id === nodeId)) {
      if (diagnostic.startLine != null) showLine(diagnostic.startLine)
      return
    }
    choose(nodeId)
    setSourceLine(diagnostic.startLine ?? undefined)
    const port = diagnostic.inputId ?? diagnostic.portId
    if (port) {
      openPanel(false)
      soon(() => focusRow(inspector() ?? undefined, port), editor)
    } else focusCard(nodeId)
  }
  const focusCard = (id: string) => soon(() => { const target = card(id); target?.focus(); return !!target }, editor)

  // ---- Runs: Run now in the top bar (RunNow.tsx), the last run in the overview.
  const [runNotice, showRunNotice] = createNotice()
  /** Why the saved version cannot be run now, if so. */
  const runUnavailable = () => {
    const doc = session.document()
    if (!doc) return
    if (session.conflict()) return 'The saved automation changed elsewhere; reload it first'
    if (session.catalogError()) return 'The node catalog could not be loaded'
    if (!doc.currentRevisionActive) return 'The saved version is not active'
  }
  const automationId = () => session.document()?.automationId
  // On/off of the saved automation: stored beside the graph, so it is not an edit (no undo step, no unsaved changes).
  // Shown at once; put back with a message when the request fails.
  const [switched, setSwitched] = createSignal<{ id: string; enabled: boolean }>()
  const [switching, setSwitching] = createSignal(false)
  const enabled = () => {
    const doc = session.document()
    if (!doc) return
    const local = switched()
    return local?.id === doc.id ? local.enabled : doc.enabled ?? true
  }
  createEffect(on(() => session.document(), () => setSwitched(undefined), { defer: true }))
  const toggleEnabled = async (wanted: boolean) => {
    const doc = session.document()
    if (!doc || switching()) return
    const before = enabled() ?? true
    batch(() => { setSwitching(true); setSwitched({ id: doc.id, enabled: wanted }) })
    try {
      await setAutomationEnabled(hass, queryClient, doc.id, wanted)
    } catch (error) {
      setSwitched({ id: doc.id, enabled: before })
      showRunNotice({ text: `Could not turn it ${wanted ? 'on' : 'off'}: ${visualError(error)}`, error: true })
    } finally {
      setSwitching(false)
    }
  }
  // Registry names for summaries outside the canvas (the canvas reads the same cached query).
  const registry = useRegistry()
  const names = () => (registry.isSuccess ? registry.data : undefined)
  // The same query (and cache) as the first page of the Runs tab.
  const lastRuns = useQuery(() => ({
    queryKey: ['run-history', automationId(), 0, ''],
    queryFn: () => hass.callWS<RunHistoryResponse>({ type: 'hass_sharp/run_history', automationId: automationId(), offset: 0, limit: 20 }),
    enabled: !!automationId() && admin(),
    staleTime: 30_000,
  }))
  const lastRun = createMemo<InspectorLastRun | undefined>(() => {
    const run = lastRuns.data?.runs?.[0]
    if (!run) return
    return { status: run.status, when: new Date(run.receivedUtc).toLocaleString(), ok: !failedStatuses.has(run.status) }
  })

  // ---- Full screen: the library and the drawer fold away; the inspector floats; a compact bar stays.
  let libraryBefore = false
  const fullscreenPage = createFullscreenPage({
    root: () => editor,
    fullscreen,
    onEnter: () => {
      libraryBefore = libraryCollapsed()
      batch(() => {
        setLibraryCollapsed(true)
        setDrawerOpen(false)
      })
    },
    onExit: () => setLibraryCollapsed(libraryBefore),
    fallback: canvas,
  })
  const enterFullscreen = fullscreenPage.enter
  const toggleFullscreen = fullscreenPage.toggle

  // ---- Keys at the page root.
  const keyboard = (event: KeyboardEvent) => {
    event.stopPropagation()
    const target = event.target as HTMLElement
    // Preserve native text undo and keys typed in fields, menus and lists.
    if (typing(target)) return
    const command = event.ctrlKey || event.metaKey
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key
    if (command && (key === 'z' || key === 'y')) {
      event.preventDefault()
      if (key === 'y' || event.shiftKey) history.redo()
      else history.undo()
      return
    }
    fullscreenPage.keydown(event)
  }
  // Enter on a step's card (the card itself, not an editor inside it) opens the sheet; the canvas selects the step.
  const cardEnter = (event: KeyboardEvent) => {
    if (event.key !== 'Enter' || event.ctrlKey || event.metaKey || event.altKey) return
    const target = event.target as Element
    if (target.classList?.contains('solid-flow__node')) queueMicrotask(() => openPanel())
  }
  onMount(() => editor.addEventListener('keydown', cardEnter, true))
  onCleanup(() => editor.removeEventListener('keydown', cardEnter, true))

  const download = () => {
    const url = URL.createObjectURL(new Blob([serializeGraph(graph())], { type: 'application/json' }))
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = 'automation.graph.json'
    anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  const menuItems = (): MenuItem[] => [
    ...(fullscreen.active() ? [] : [{ label: 'Full screen', onSelect: enterFullscreen }]),
    { label: 'Export graph', onSelect: download },
    ...(props.documentId ? [{ label: 'Reload saved graph', onSelect: reload }] : []),
    { label: 'Help', onSelect: () => setHelpOpen(true) },
  ]

  const busy = () => session.loading() || session.saving()

  return (
    <section
      // The editor stops keys at its root so they never reach Home Assistant's shortcuts. Solid runs delegated
      // handlers (menus, the canvas library) at this root instead of the document, so they still see those keys.
      ref={element => {
        editor = element
        delegateEvents(['keydown', 'keyup'], element as unknown as Document)
      }}
      class="hs-visual"
      classList={{ 'hs-visual--fullscreen': fullscreen.active(), 'hs-visual--maximised': fullscreen.maximised(), 'hs-visual--floating': floating() }}
      aria-label="Visual automation editor"
      on:keydown={keyboard}
      on:keyup={event => event.stopPropagation()}
    >
      <TopBar
        name={graph().name}
        onRename={(name, options) => commit({ ...graph(), name }, options)}
        onBack={props.onBack}
        save={{ saving: session.saving(), dirty: session.dirty(), errors: errorCount(), revision: session.document()?.revision }}
        busy={busy()}
        canUndo={history.canUndo()}
        canRedo={history.canRedo()}
        onUndo={history.undo}
        onRedo={history.redo}
        canSave={session.canSave()}
        onSave={() => void save()}
        run={
          admin() ? (
            <div class="hs-topbar__run-group">
              <Show when={session.document()}>
                <EnabledSwitch class="hs-topbar__enabled" name={graph().name} enabled={enabled() ?? true} disabled={busy() || switching()} onChange={wanted => void toggleEnabled(wanted)} />
              </Show>
              <RunNowButton document={session.document()} catalog={session.catalog()} names={names()} unavailable={runUnavailable()} dirty={session.dirty()} busy={busy()}
                onNotice={showRunNotice} />
            </div>
          ) : undefined
        }
        notice={runNotice()}
        fullscreen={fullscreen.active()}
        onExitFullscreen={() => void fullscreen.exit()}
        menu={menuItems()}
      />
      <Show when={session.loading()}>
        <p class="hs-visual-alert hs-visual-alert--quiet" role="status">Loading saved graph…</p>
      </Show>
      <Show when={session.catalogError()}>
        <div class="hs-visual-alert hs-error" role="alert">
          Node catalog: {session.catalogError()}{' '}
          <Button onClick={() => void session.refreshCatalog()}>Reload node catalog</Button>
        </div>
      </Show>
      <Show when={session.error()}>
        <div class="hs-visual-alert hs-notice--danger" role="alert">
          {session.error()}
          <Show when={session.conflict()}>
            <p>Your local graph is preserved. Export it from the "⋯" menu before reloading the saved graph to resolve this conflict.</p>
          </Show>
        </div>
      </Show>
      <Show when={session.activationError()}>
        <div class="hs-visual-alert hs-notice--danger" role="alert">
          <strong>Saved, but not activated.</strong>
          <p>{session.activationError()}</p>
          <p>The previous runtime may still be running. Edit and save again, or reload the HassSharp integration.</p>
        </div>
      </Show>
      <div class="hs-visual-workspace" inert={busy()} aria-busy={busy()}>
        <NodeLibraryPanel
          catalog={session.catalog()}
          collapsed={libraryCollapsed()}
          onCollapsedChange={setLibraryCollapsed}
          onAdd={addStep}
          onQuickAdd={() => setQuickAddRequest(value => value + 1)}
        />
        <div class="hs-canvas-area">
          <FlowAdapter
            catalog={session.catalog()}
            graph={graph()}
            addStepRequest={quickAddRequest()}
            selectedId={selectedId()}
            onSelect={choose}
            onSelectionChange={(ids, edge) => setSelection({ ids, edge })}
            onChange={commit}
            diagnostics={session.diagnostics()}
            fitViewKey={loads() || undefined}
            toolbarExtra={<FullscreenButton fullscreen={fullscreen} onToggle={toggleFullscreen} />}
            controls={controls => (flow = controls)}
          />
        </div>
        <Inspector
          selected={selected()}
          selectedIds={selection().ids}
          selectedEdgeId={selection().edge}
          onSelect={id => inPanel(() => choose(id))}
          graph={graph()}
          catalog={session.catalog()}
          names={names()}
          diagnostics={session.diagnostics()}
          commit={commit}
          onShowProblems={showProblems}
          lastRun={lastRun()}
          enabled={enabled()}
          docked={!floating()}
          open={panelOpen()}
          onClose={() => setPanelOpen(false)}
          onOpen={() => openPanel()}
          onDuplicate={ids => inPanel(() => flow?.duplicate(ids) ?? [])}
        />
      </div>
      <BottomDrawer
        open={drawerOpen()}
        tab={drawerTab()}
        onOpenChange={setDrawerOpen}
        onTabChange={tab => setDrawerTab(tab as DrawerTab)}
        tabs={[
          {
            id: 'problems',
            label: 'Problems',
            get count() { return shownProblems().length },
            get tone() { return errorCount() ? 'error' as const : 'warning' as const },
            content: () => (
              <ProblemsPanel diagnostics={session.diagnostics()} graph={graph()} catalog={session.catalog()} onLocate={locate}
                onSelectStep={id => { choose(id); focusCard(id) }} onShowLine={showLine} />
            ),
          },
          {
            id: 'code',
            label: 'Generated C#',
            content: () => (
              <CodePanel graph={graph()} preview={session.preview()} previewError={session.previewError()} onRetry={session.retryPreview}
                view={codeView()} onViewChange={setCodeView} sourceLine={sourceLine()} selectedId={selectedId()}
                onSelectLine={(line, nodeId) => {
                  // The line's step; a line of the shared C# belongs to none.
                  choose(nodeId && graph().nodes.some(n => n.id === nodeId) ? nodeId : undefined)
                  setSourceLine(line)
                }} />
            ),
          },
          {
            id: 'runs',
            label: 'Runs',
            lazy: true,
            content: () => (
              <Show when={session.document()} fallback={<p class="hs-drawer__empty">Save this automation to see its runs here.</p>}>
                {doc => (
                  <AutomationRuns automationId={doc().automationId} />
                )}
              </Show>
            ),
          },
        ]}
      />
      <Dialog
        open={reloadDialog()}
        title="Discard local changes?"
        width="small"
        onClose={() => setReloadDialog(false)}
        footer={
          <>
            <Button onClick={() => setReloadDialog(false)}>Keep editing</Button>
            <Button
              variant="danger"
              onClick={() => {
                setReloadDialog(false)
                if (props.documentId) void session.load(props.documentId)
              }}
            >
              Reload and discard
            </Button>
          </>
        }
      >
        <p>Reloading replaces your local graph with the saved revision. Export your graph first to keep a copy.</p>
      </Dialog>
      <HelpDialog open={helpOpen()} onClose={() => setHelpOpen(false)} />
    </section>
  )
}
