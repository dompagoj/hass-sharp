import { batch, createEffect, createMemo, createSignal, on, onCleanup, onMount, Show, untrack } from 'solid-js'
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
import { CodeLeftBehind, CustomCodeEditor } from './CustomCodeEditor'
import { applyCustomCode } from './customCode'
import { libraryApi } from './library-api'
import { TypeDefinitions } from './typeDefinition'

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
  /** Receives what discards the unsaved changes' draft, for leaving without saving. */
  discardRef?: (discard: () => void) => void
  /** Opens a node library file at a line (a type's definition was declared there); not offered without it. */
  onOpenLibrary?: (id: string, line?: number) => void
}) {
  const hass = useHass()
  // Unapplied code counts as unsaved: a revision saved elsewhere is then never reloaded over it.
  const session = createVisualSession(hass, createGraph(props.name), { unsaved: () => codeDirty(), code: () => unappliedCode() })
  props.discardRef?.(session.discardDraft)
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

  // ---- The code editor of a Custom C# node; its unapplied changes count as unsaved.
  const [codeNodeId, setCodeNodeId] = createSignal<string>()
  const [codeDirty, setCodeDirty] = createSignal(false)
  const codeNode = createMemo(() => graph().nodes.find(node => node.id === codeNodeId()))
  /** The open editor's unapplied text, if any: kept in the draft, and for when its step disappears under it (a reload). */
  const [codeDraft, setCodeDraft] = createSignal<string>()
  /** Unapplied code taken back from a draft: the code editor opens with it. */
  const [codeRestore, setCodeRestore] = createSignal<string>()
  const unappliedCode = () => { const nodeId = codeNodeId(), text = codeDraft(); return codeDirty() && nodeId && text !== undefined ? { nodeId, text } : undefined }
  const [orphanCode, setOrphanCode] = createSignal<string>()
  const closeCode = () => batch(() => { setCodeNodeId(undefined); setCodeDirty(false); setCodeDraft(undefined); setCodeRestore(undefined) })
  createEffect(() => {
    if (!codeNodeId() || codeNode()) return
    const draft = untrack(codeDraft)
    closeCode()
    if (draft !== undefined) setOrphanCode(draft)
  })
  // A restored draft's unapplied code opens in its node's code editor, once the person has chosen what to keep.
  createEffect(() => {
    if (!session.restoredCode() || session.conflict() || session.restoreOffer() || session.loading()) return
    const code = session.takeRestoredCode()!
    untrack(() => {
      if (!graph().nodes.some(node => node.id === code.nodeId && node.kind === 'custom')) return setOrphanCode(code.text)
      batch(() => { setCodeRestore(code.text); setCodeNodeId(code.nodeId) })
    })
  })
  const dirty = () => session.dirty() || codeDirty()
  createEffect(() => props.onDirtyChange?.(dirty()))
  createEffect(
    on(
      () => [props.documentId, props.name] as const,
      ([id, name], previous) => {
        if (id && id !== session.document()?.id) void session.load(id)
        else if (!id && previous) session.newDraft(createGraph(name))
        else if (!id) session.openNew()
      },
    ),
  )
  // Saved, unchanged and active: Save would store the same revision again. A saved version that is not active can be
  // saved again (to activate it), and a new automation always can.
  const nothingToSave = () => { const doc = session.document(); return !!doc && !session.dirty() && doc.currentRevisionActive !== false }
  /** A save (or a copy, or a resolved conflict) went through: the lists show it, and the address follows a new id. */
  const stored = (result: Awaited<ReturnType<typeof session.save>>) => {
    if (!result) return
    void queryClient.invalidateQueries({ queryKey: ['visual-automations'] })
    if (!session.dirty()) props.onSaved?.(result.document)
  }
  const save = async () => {
    if (session.conflict()) return setConflictOpen(true)
    if (nothingToSave()) return
    stored(await session.save())
  }
  const [reloadDialog, setReloadDialog] = createSignal(false)
  const reload = () => {
    if (session.loading() || session.saving()) return
    if (session.dirty()) setReloadDialog(true)
    else if (props.documentId) void session.load(props.documentId)
  }
  // ---- A revision saved elsewhere while there are unsaved changes here: the person chooses what to keep.
  const [conflictOpen, setConflictOpen] = createSignal(false)
  createEffect(on(session.conflict, conflicted => setConflictOpen(conflicted)))
  const resolve = async (choice: 'keep' | 'copy' | 'discard') => {
    setConflictOpen(false)
    // An open code editor stays, or offers its unapplied code when its step is gone (as on any reload).
    if (choice === 'discard') return session.discardChanges()
    stored(await (choice === 'keep' ? session.keepMine() : session.saveCopy()))
  }
  const draftTime = (at: number | undefined) => (at === undefined ? '' : new Date(at).toLocaleString())
  // ---- A check of the graph that could not run is tried again when the page comes back or reconnects.
  const retryFailedPreview = () => { if (session.previewError()) session.retryPreview() }
  onMount(() => {
    window.addEventListener('focus', retryFailedPreview)
    hass.connection?.addEventListener?.('ready', retryFailedPreview)
  })
  onCleanup(() => {
    window.removeEventListener('focus', retryFailedPreview)
    hass.connection?.removeEventListener?.('ready', retryFailedPreview)
  })
  const [helpOpen, setHelpOpen] = createSignal(false)
  const beforeUnload = (event: BeforeUnloadEvent) => {
    if (dirty()) {
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
  /** Opens a Custom C# node's code (from its card or the panel), with the node selected. */
  const editCode = (id: string) => {
    if (!graph().nodes.some(n => n.id === id)) return
    if (selectedId() !== id) choose(id)
    setCodeNodeId(id)
  }
  /** Writes the code and its pins as one undo step, and says which values and wires no longer fit. */
  const applyCode = (id: string, source: string, signature: Parameters<typeof applyCustomCode>[3]) => {
    const result = applyCustomCode(graph(), id, source, signature, session.catalog())
    commit(result.graph)
    // What Apply removed stays on view until dismissed or the next change: the dialog closing moves focus away.
    if (result.notice) flow?.notify(result.notice, { keep: true })
  }

  // ---- Runs: Run now in the top bar (RunNow.tsx), the last run in the overview.
  const [runNotice, showRunNotice] = createNotice()
  /** Why the saved version cannot be run now, if so. */
  const runUnavailable = () => {
    const doc = session.document()
    if (!doc) return
    if (session.conflict()) return 'The saved automation changed elsewhere; choose what to keep first'
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
  // A save's reply may hold the on/off state from before a switch the server took (whichever was pressed or answered
  // first): the switch's own answer wins until a document says the same, another automation opens, or the saved one is
  // read again.
  createEffect(on(() => session.document(), doc => {
    const local = switched()
    if (!local) return
    if (!doc || doc.id !== local.id || (doc.enabled ?? true) === local.enabled) setSwitched(undefined)
  }, { defer: true }))
  createEffect(on(session.loading, loading => { if (loading) batch(() => { setSwitched(undefined); setSwitching(false) }) }, { defer: true }))
  const toggleEnabled = async (wanted: boolean) => {
    const doc = session.document()
    if (!doc || switching()) return
    const before = enabled() ?? true
    batch(() => { setSwitching(true); setSwitched({ id: doc.id, enabled: wanted }) })
    try {
      const now = await setAutomationEnabled(hass, queryClient, doc.id, wanted)
      setSwitched({ id: doc.id, enabled: now })
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
  /** Save's title: why it does nothing, or what it is waiting on (it saves anyway; the server checks a save itself). */
  const saveTitle = () => {
    if (session.conflict()) return 'The saved automation changed elsewhere: choose what to keep'
    if (nothingToSave()) return 'Nothing to save: every change is saved'
    if (!session.canSave()) return
    if (session.previewError()) return 'Could not check the graph'
    if (session.previewPending()) return 'Checking…'
  }

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
      <TypeDefinitions load={(type, code) => libraryApi(hass).typeDefinition(type, code)} graph={graph} catalog={session.catalog}
        onOpenLibrary={props.onOpenLibrary} onEditCode={editCode}>
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
        canSave={session.conflict() || (session.canSave() && !nothingToSave())}
        saveTitle={saveTitle()}
        onSave={() => void save()}
        run={
          admin() ? (
            <div class="hs-topbar__run-group">
              <Show when={session.document()}>
                <EnabledSwitch class="hs-topbar__enabled" name={graph().name} enabled={enabled() ?? true} disabled={busy()} busy={switching()} onChange={wanted => void toggleEnabled(wanted)} />
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
            <p>Your changes are still here. <Button onClick={() => setConflictOpen(true)}>Choose what to keep…</Button></p>
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
            onEditCode={editCode}
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
          onEditCode={editCode}
          onNotice={text => flow?.notify(text, { keep: true })}
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
                onSelectStep={id => { choose(id); focusCard(id) }} onShowLine={showLine} onGraphChange={next => commit(next)} />
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
                void session.discardChanges()
              }}
            >
              Reload and discard
            </Button>
          </>
        }
      >
        <p>Reloading replaces your unsaved changes with the saved version.</p>
      </Dialog>
      <Dialog open={session.conflict() && conflictOpen()} title="This automation changed elsewhere" width="medium" onClose={() => setConflictOpen(false)}
        footer={
          <>
            <Button variant="danger" disabled={busy()} onClick={() => void resolve('discard')}>Discard my changes</Button>
            <Button disabled={busy()} onClick={() => void resolve('copy')}>Save as a copy</Button>
            <Button variant="primary" disabled={busy()} onClick={() => void resolve('keep')}>Keep my version</Button>
          </>
        }
      >
        <Show when={session.conflictDraft()} fallback={
          <p>It was saved again somewhere else while you had unsaved changes here. Saving a node library does this to every automation that uses it.</p>
        }>
          {at => <p>Your unsaved changes from {draftTime(at())} were made before this automation was saved again somewhere else.</p>}
        </Show>
        <p><strong>Keep my version</strong> saves your changes over the saved one. <strong>Save as a copy</strong> saves them as a new automation, turned off, and leaves the saved one as it is. <strong>Discard my changes</strong> opens the saved version.</p>
        <Show when={codeDirty() || session.restoredCode()}>
          <p>Code you have not applied stays in the code editor whichever you choose: apply it there, then save.</p>
        </Show>
      </Dialog>
      <Dialog open={!!session.restoreOffer()} title="Restore unsaved changes?" width="small" preventClose onClose={() => {}}
        footer={
          <>
            <Button onClick={() => session.dismissDraft()}>Discard</Button>
            <Button variant="primary" autofocus onClick={() => session.restoreDraft()}>Restore</Button>
          </>
        }
      >
        <p>Restore unsaved changes from {draftTime(session.restoreOffer()?.at)}?</p>
        <p>They were left when this page closed before they were saved. Restoring them can be undone.</p>
      </Dialog>
      <HelpDialog open={helpOpen()} onClose={() => setHelpOpen(false)} />
      {/* Inside the page root, so it also covers the full-screen editor. */}
      <Show when={codeNode() && codeNodeId()} keyed>
        {id => (
          <CustomCodeEditor node={codeNode()!} catalog={session.catalog()} draft={untrack(codeRestore)}
            onDirtyChange={(changed, source) => {
              // Torn down with the page (its node still open in it, not closed): the unapplied code stays counted, so
              // the page's draft keeps it.
              if (!changed && source === undefined && untrack(codeNodeId) === id && untrack(codeNode)) return
              setCodeDirty(changed)
              // Closing reports no text: the draft is kept until it is known whether the step is still there.
              if (source !== undefined) setCodeDraft(changed ? source : undefined)
            }}
            onApply={(source, signature) => applyCode(id, source, signature)}
            onClose={closeCode} />
        )}
      </Show>
      <CodeLeftBehind source={orphanCode()} onClose={() => setOrphanCode(undefined)} />
      </TypeDefinitions>
    </section>
  )
}
