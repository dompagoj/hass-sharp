import { createEffect, createMemo, createSignal, For, on, onCleanup, onMount, Show } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { Button, Field, Status } from '../components/Controls'
import { FlowAdapter } from './FlowAdapter'
import { HelpDialog } from '../components/HelpDialog'
import { Inspector } from './Inspector'
import { useEntities } from '../entities'
import { useHass } from '../context'
import { useQueryClient } from '@tanstack/solid-query'
import { AutomationRuns } from '../components/AutomationRuns'
import { Dialog } from '../components/Dialog'
import { createVisualSession } from './session'
import type { VisualDiagnostic, VisualDocument } from './api'
import {
  canCreateNode,
  createGraph,
  outputPort,
  nodeLabel,
  catalogCategories,
  catalogPalette,
  portLabel,
  removeEdge,
  removeNode,
  serializeGraph,
  visualRunClasses,
  visualActionLabels,
  type NodeKind,
} from './graph'

export function VisualEditor(props: {
  name: string
  documentId?: string
  onDirtyChange?: (dirty: boolean) => void
  onSaved?: (document: VisualDocument) => void
}) {
  const session = createVisualSession(useHass(), createGraph(props.name))
  const queryClient = useQueryClient()
  const entities = useEntities()
  onMount(() => {
    void entities.refetch()
  })
  const history = session.history
  const [advanced, setAdvanced] = createSignal(false)
  const [reloadDialog, setReloadDialog] = createSignal(false)
  const [sourceLine, setSourceLine] = createSignal<number>()
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
  const reload = () => {
    if (session.dirty()) setReloadDialog(true)
    else if (props.documentId) void session.load(props.documentId)
  }
  const locate = (diagnostic: VisualDiagnostic) => {
    const nodeId =
      diagnostic.nodeId ??
      session
        .preview()
        ?.sourceMap.find(
          range => diagnostic.startLine != null && diagnostic.startLine >= range.startLine && diagnostic.startLine <= range.endLine,
        )?.nodeId
    if (nodeId) {
      choose(nodeId)
      focusSection(nodeId)
    }
    if (diagnostic.inputId || diagnostic.portId) {
      queueMicrotask(() => {
        const row = [...editor.querySelectorAll<HTMLElement>('.hs-inspector [data-input-id]')].find(
          el => el.dataset.inputId === (diagnostic.inputId ?? diagnostic.portId),
        )
        const target =
          row?.querySelector<HTMLElement>('input,select,textarea,button') ??
          row ??
          [...editor.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.dataset.id === nodeId)
        target?.focus()
        target?.scrollIntoView?.({ block: 'nearest' })
      })
      return
    }
    queueMicrotask(() => [...editor.querySelectorAll<HTMLElement>('.solid-flow__node')].find(el => el.dataset.id === nodeId)?.focus())
    setPreview('code')
    setSourceLine(diagnostic.startLine ?? session.preview()?.sourceMap.find(range => range.nodeId === nodeId)?.startLine)
    queueMicrotask(() => editor.querySelector<HTMLElement>(`[data-source-line="${sourceLine()}"]`)?.scrollIntoView?.({ block: 'nearest' }))
  }
  let editor!: HTMLElement
  const { graph, commit } = history
  const [paletteKind, setPaletteKind] = createSignal<NodeKind>()
  const [addStepRequest, setAddStepRequest] = createSignal(0)
  const palette = () => catalogPalette(session.catalog())
  const categories = () => catalogCategories(session.catalog())
  const [librarySearch, setLibrarySearch] = createSignal('')
  const description = createMemo(() => palette().find(item => item.kind === paletteKind()))
  const [selectedId, setSelectedId] = createSignal<string | undefined>('trigger-1')
  const [preview, setPreview] = createSignal<'code' | 'json'>('code')
  const selected = createMemo(() => graph().nodes.find(node => node.id === selectedId()))
  const edited = session.dirty
  const beforeUnload = (event: BeforeUnloadEvent) => {
    if (edited()) {
      event.preventDefault()
      event.returnValue = ''
    }
  }
  onMount(() => window.addEventListener('beforeunload', beforeUnload))
  onCleanup(() => window.removeEventListener('beforeunload', beforeUnload))
  // Each finished load of a saved graph fits the canvas to it.
  const [loads, setLoads] = createSignal(0)
  createEffect(on(session.loading, (loading, before) => before && !loading && setLoads(count => count + 1), { defer: true }))
  const choose = (id?: string) => {
    setPaletteKind(undefined)
    setSelectedId(id)
    setSourceLine(undefined)
  }
  const focusSection = (id: string) => {
    if (id === '$imports' || id === '$members') {
      setAdvanced(true)
      queueMicrotask(() => editor.querySelector<HTMLTextAreaElement>(`[data-section-id="${id}"]`)?.focus())
    }
  }
  const keyboard = (event: KeyboardEvent) => {
    event.stopPropagation()
    // Preserve native text undo and do not leak editor shortcuts to Home Assistant.
    if ((event.target as HTMLElement).closest('input,textarea,select,[contenteditable="true"]')) return
    if ((event.ctrlKey || event.metaKey) && ['z', 'y'].includes(event.key.toLowerCase())) {
      event.preventDefault()
      event.stopPropagation()
      if (event.key.toLowerCase() === 'y' || event.shiftKey) history.redo()
      else history.undo()
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      const target = event.target as Element
      const nodeId = target.closest('.solid-flow__node')?.getAttribute('data-id')
      const edgeId = target.closest('.solid-flow__edge')?.getAttribute('data-id')
      if (nodeId) {
        event.preventDefault()
        commit(removeNode(graph(), nodeId, session.catalog()))
      } else if (edgeId) {
        event.preventDefault()
        commit(removeEdge(graph(), edgeId, session.catalog()))
      }
    }
  }
  const download = () => {
    const url = URL.createObjectURL(new Blob([serializeGraph(graph())], { type: 'application/json' }))
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = 'automation.graph.json'
    anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
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
      aria-label="Visual automation editor"
      on:keydown={keyboard}
      on:keyup={event => event.stopPropagation()}
    >
      <header class="hs-visual-heading">
        <div>
          <p class="hs-eyebrow">VISUAL AUTOMATION</p>
          <h1>{graph().name}</h1>
          <p class="hs-muted">Build a flow, one step at a time.</p>
        </div>
        <div class="hs-visual-actions">
          <HelpDialog mode="visual" />
          <Status>{session.document() ? `Saved · revision ${session.document()!.revision}` : 'Not saved'}</Status>
          <Show when={session.dirty()}>
            <Status>Unsaved changes</Status>
          </Show>
          <Button onClick={download}>Export graph</Button>
          <Show when={props.documentId}>
            <Button disabled={session.loading() || session.saving()} onClick={reload}>
              Reload saved graph
            </Button>
          </Show>
          <Button variant="primary" busy={session.saving()} disabled={!session.canSave()} onClick={() => void save()}>
            Save graph
          </Button>
        </div>
      </header>
      <p class="hs-visual-notice" role="note">
        Execution edges run steps; typed data edges pass their results. Use Parallel for joined branches and structured loops for bounded
        repetition. Saving activates the automation and may execute startup code.
      </p>
      <Field
        label="Automation name"
        aria-label="Automation name"
        value={graph().name}
        disabled={session.loading() || session.saving()}
        onInput={event => commit({ ...graph(), name: event.currentTarget.value })}
      />
      <Show when={session.loading()}>
        <p role="status">Loading saved graph…</p>
      </Show>
      <Show when={session.catalogError()}>
        <div class="hs-error" role="alert">
          Node catalog: {session.catalogError()} <Button onClick={() => void session.refreshCatalog()}>Reload node catalog</Button>
        </div>
      </Show>
      <Show when={session.error()}>
        <div class="hs-notice hs-notice--danger" role="alert">
          {session.error()}
          <Show when={session.conflict()}>
            <p>Your local graph is preserved. Export it before reloading the saved graph to resolve this conflict.</p>
          </Show>
        </div>
      </Show>
      <Show when={session.activationError()}>
        <div class="hs-notice hs-notice--danger" role="alert">
          <strong>Saved, but not activated.</strong>
          <p>{session.activationError()}</p>
          <p>The previous runtime may still be running. Edit and save again, or reload the HassSharp integration.</p>
        </div>
      </Show>
      <div inert={session.loading() || session.saving()} aria-busy={session.loading() || session.saving()}>
        <div class="hs-flow-toolbar" aria-label="Canvas controls">
          <Button onClick={() => setAddStepRequest(value => value + 1)}>Add step</Button>
          <Button aria-label="Undo" disabled={!history.canUndo()} onClick={history.undo}>
            ↶ Undo
          </Button>
          <Button aria-label="Redo" disabled={!history.canRedo()} onClick={history.redo}>
            ↷ Redo
          </Button>
          <span class="hs-muted">Drag to move · Tab to a node, Enter to select, arrows to move</span>
        </div>
        <div class="hs-visual-workspace">
          <aside class="hs-palette" aria-label="Node palette">
            <h2>Node library</h2>
            <Field
              label="Search nodes"
              aria-label="Search nodes"
              value={librarySearch()}
              onInput={e => setLibrarySearch(e.currentTarget.value)}
            />
            <p class="hs-muted">Click for details · Drag to add</p>
            <For each={categories()}>
              {category => (
                <section class="hs-palette-group">
                  <h3>{category}</h3>
                  <For
                    each={palette().filter(
                      item =>
                        item.category === category &&
                        `${item.label} ${item.description}`.toLowerCase().includes(librarySearch().toLowerCase()),
                    )}
                  >
                    {item => (
                      <Button
                        class="hs-palette-item"
                        aria-label={`About ${item.label}`}
                        draggable={canCreateNode(graph(), item.kind, undefined, session.catalog())}
                        onClick={() => setPaletteKind(item.kind)}
                        onDragStart={event => {
                          if (!canCreateNode(graph(), item.kind, undefined, session.catalog())) {
                            event.preventDefault()
                            return
                          }
                          event.dataTransfer?.setData('application/x-hass-sharp-node', item.kind)
                          if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy'
                        }}
                      >
                        <span class={`hs-node-dot hs-node-dot--${item.kind}`} />
                        <span>
                          <strong>{item.label}</strong>
                          <small>{item.description}</small>
                        </span>
                        <span aria-hidden="true">⠿</span>
                      </Button>
                    )}
                  </For>
                </section>
              )}
            </For>
          </aside>
          <div class="hs-canvas-column">
            <FlowAdapter
              catalog={session.catalog()}
              graph={graph()}
              addStepRequest={addStepRequest()}
              selectedId={selectedId()}
              onSelect={choose}
              onChange={commit}
              diagnostics={session.diagnostics()}
              fitViewKey={loads() || undefined}
            />
            <ul class="hs-diagnostics" aria-label="Graph diagnostics">
              <For each={session.diagnostics().filter(d => d.severity > 0)}>
                {diagnostic => (
                  <li classList={{ 'hs-error': diagnostic.severity === 3 }}>
                    <span>{diagnostic.severity === 3 ? 'Error' : diagnostic.severity === 2 ? 'Warning' : 'Info'}: </span>
                    <Button variant="ghost" onClick={() => locate(diagnostic)}>
                      {diagnostic.message}
                      {diagnostic.startLine != null ? ` · line ${diagnostic.startLine}` : ''}
                    </Button>
                    <Show when={diagnostic.startLine != null}>
                      <Button
                        variant="ghost"
                        aria-label={`View source for ${diagnostic.message}`}
                        onClick={() => {
                          setPreview('code')
                          setSourceLine(diagnostic.startLine ?? undefined)
                          queueMicrotask(() =>
                            editor
                              .querySelector<HTMLElement>(`[data-source-line="${diagnostic.startLine}"]`)
                              ?.scrollIntoView?.({ block: 'nearest' }),
                          )
                        }}
                      >
                        Source
                      </Button>
                    </Show>
                  </li>
                )}
              </For>
            </ul>
          </div>
          <Inspector
            description={description()}
            selected={selected()}
            graph={graph()}
            catalog={session.catalog()}
            diagnostics={session.diagnostics()}
            commit={commit}
          />
        </div>
        <details
          class="hs-graph-sections"
          aria-label="Graph C# sections"
          open={advanced()}
          onToggle={event => setAdvanced(event.currentTarget.open)}
        >
          <summary>Advanced C#</summary>
          <p class="hs-muted">
            Imports appear before the generated class. Members are fields, properties and helper methods inside it. Fields are shared across
            invocations; await child work and pass CancellationToken to delays and I/O. The generated class, Configure and action signatures
            are reserved.
          </p>
          <div class="hs-graph-section-fields">
            <For each={['imports', 'members'] as const}>
              {key => (
                <div class="hs-field" classList={{ 'hs-code-section--selected': selectedId() === `$${key}` }}>
                  <label for={`hs-graph-${key}`}>{key === 'imports' ? 'C# imports' : 'C# members'}</label>
                  <textarea
                    id={`hs-graph-${key}`}
                    data-section-id={`$${key}`}
                    aria-label={key === 'imports' ? 'C# imports' : 'C# members'}
                    aria-invalid={session.preview()?.diagnostics.some(d => d.nodeId === `$${key}` && d.severity === 3) || undefined}
                    class="hs-input hs-code-input"
                    rows={key === 'imports' ? 3 : 6}
                    value={graph()[key] ?? ''}
                    onInput={event => commit({ ...graph(), [key]: event.currentTarget.value })}
                  />
                </div>
              )}
            </For>
          </div>
        </details>
        <div class="hs-visual-bottom">
          <section class="hs-connections" aria-label="Connections">
            <h2>
              Connections <span class="hs-muted">({graph().edges.length})</span>
            </h2>
            <p class="hs-muted">Use the inspector to connect steps with a keyboard or touch.</p>
            <Show when={!graph().edges.length}>
              <p class="hs-muted">No connections yet.</p>
            </Show>
            <ul>
              <For each={graph().edges}>
                {edge => {
                  const label = () => {
                    const source = graph().nodes.find(n => n.id === edge.source)
                    const branch =
                      edge.kind === 'data'
                        ? ` · ${edge.sourcePort} → ${edge.targetPort}`
                        : source && outputPort(source.kind, edge) !== 'next'
                          ? ` · ${portLabel(outputPort(source.kind, edge))}`
                          : ''
                    return `${nodeLabel(source, session.catalog(), edge.source)}${branch} → ${nodeLabel(
                      graph().nodes.find(n => n.id === edge.target),
                      session.catalog(),
                      edge.target,
                    )}`
                  }
                  return (
                    <li>
                      <span>{label()}</span>
                      <Button
                        variant="ghost"
                        aria-label={`Remove ${label()}`}
                        onClick={() => commit(removeEdge(graph(), edge.id, session.catalog()))}
                      >
                        Remove
                      </Button>
                    </li>
                  )
                }}
              </For>
            </ul>
          </section>
          <section class="hs-graph-preview" aria-label="Code preview">
            <div class="hs-preview-toolbar">
              <Button aria-pressed={preview() === 'code'} onClick={() => setPreview('code')}>
                Generated C#
              </Button>
              <Button aria-pressed={preview() === 'json'} onClick={() => setPreview('json')}>
                Graph JSON
              </Button>
            </div>
            <p class="hs-muted">Generated by Home Assistant from this graph. Select a step or diagnostic to locate its source.</p>
            <Show when={session.previewError()}>
              <div role="alert" class="hs-error">
                Preview failed: {session.previewError()} <Button onClick={session.retryPreview}>Retry preview</Button>
              </div>
            </Show>
            <Show when={!session.preview() && !session.previewError()}>
              <p role="status">Updating preview…</p>
            </Show>
            <Show
              when={preview() === 'json'}
              fallback={
                <Show when={session.preview()}>
                  {result => (
                    <pre tabindex="0" aria-label="Generated C#">
                      <code>
                        <For each={result().source.split('\n')}>
                          {(line, index) => {
                            const range = () =>
                              result().sourceMap.find(range => index() + 1 >= range.startLine && index() + 1 <= range.endLine)
                            const selected = () =>
                              sourceLine() === index() + 1 || (sourceLine() == null && range()?.nodeId === selectedId())
                            return (
                              <span class="hs-source-line" data-source-line={index() + 1} aria-current={selected() ? 'true' : undefined}>
                                <button
                                  class="hs-source-number"
                                  aria-label={`Select source line ${index() + 1}`}
                                  disabled={!range()}
                                  onClick={() => {
                                    if (range()) {
                                      choose(range()!.nodeId)
                                      focusSection(range()!.nodeId)
                                    }
                                    setSourceLine(index() + 1)
                                  }}
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
                <code>{serializeGraph(graph())}</code>
              </pre>
            </Show>
          </section>
        </div>
      </div>
      <Show when={session.document()}>
        {doc => (
          <AutomationRuns
            automationId={doc().automationId}
            classes={doc().currentRevisionActive && !session.conflict() && !session.catalogError() ? visualRunClasses(doc().graph) : []}
            actionLabels={visualActionLabels(doc().graph, session.catalog())}
          />
        )}
      </Show>
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
    </section>
  )
}
