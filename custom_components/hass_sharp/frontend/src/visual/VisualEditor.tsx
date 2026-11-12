import { createEffect, createMemo, createSignal, For, on, onCleanup, onMount, Show } from 'solid-js'
import { Button, Field, Status } from '../components/Controls'
import { FlowAdapter } from './FlowAdapter'
import { TriggerConfig } from './TriggerConfig'
import { useHass } from '../context'
import { useQueryClient } from '@tanstack/solid-query'
import { AutomationRuns } from '../components/AutomationRuns'
import { Dialog } from '../components/Dialog'
import { createVisualSession } from './session'
import type { VisualDiagnostic, VisualDocument } from './api'
import { addNode, canConnect, connectNodes, createGraph, palette, removeNode, serializeGraph, updateNode, visualRunClasses, visualActionLabels } from './graph'

const configLabels: Record<string, string> = { entity: 'Entity ID', state: 'Required state', service: 'Service', seconds: 'Seconds', code: 'Custom C# section' }

export function VisualEditor(props: { name: string; documentId?: string; onDirtyChange?: (dirty: boolean) => void; onSaved?: (document: VisualDocument) => void }) {
  const session = createVisualSession(useHass(), createGraph(props.name))
  const queryClient = useQueryClient()
  const history = session.history
  const [reloadDialog, setReloadDialog] = createSignal(false)
  const [sourceLine, setSourceLine] = createSignal<number>()
  createEffect(() => props.onDirtyChange?.(session.dirty()))
  createEffect(on(() => [props.documentId, props.name] as const, ([id, name], previous) => {
    if (id && id !== session.document()?.id) void session.load(id)
    else if (!id && previous) session.newDraft(createGraph(name))
  }))
  const save = async () => {
    const result = await session.save()
    if (result) {
      void queryClient.invalidateQueries({ queryKey: ['visual-automations'] })
      if (!session.dirty()) props.onSaved?.(result.document)
    }
  }
  const reload = () => { if (session.dirty()) setReloadDialog(true); else if (props.documentId) void session.load(props.documentId) }
  const locate = (diagnostic: VisualDiagnostic) => {
    const nodeId = diagnostic.nodeId ?? session.preview()?.sourceMap.find(range => diagnostic.startLine != null && diagnostic.startLine >= range.startLine && diagnostic.startLine <= range.endLine)?.nodeId
    if (nodeId) { choose(nodeId); focusSection(nodeId) }
    setPreview('code')
    setSourceLine(diagnostic.startLine ?? session.preview()?.sourceMap.find(range => range.nodeId === nodeId)?.startLine)
    queueMicrotask(() => editor.querySelector<HTMLElement>(`[data-source-line="${sourceLine()}"]`)?.scrollIntoView?.({ block: 'nearest' }))
  }
  let editor!: HTMLElement
  const { graph, commit } = history
  const [selectedId, setSelectedId] = createSignal<string>('trigger-1')
  const [target, setTarget] = createSignal('')
  const [preview, setPreview] = createSignal<'code' | 'json'>('code')
  const selected = createMemo(() => graph().nodes.find(node => node.id === selectedId()))
  const edited = session.dirty
  const beforeUnload = (event: BeforeUnloadEvent) => { if (edited()) { event.preventDefault(); event.returnValue = '' } }
  onMount(() => window.addEventListener('beforeunload', beforeUnload))
  onCleanup(() => window.removeEventListener('beforeunload', beforeUnload))
  const choose = (id: string) => { setSelectedId(id); setTarget(''); setSourceLine(undefined) }
  const focusSection = (id: string) => {
    if (id === '$imports' || id === '$members') queueMicrotask(() => editor.querySelector<HTMLTextAreaElement>(`[data-section-id="${id}"]`)?.focus())
  }
  const keyboard = (event: KeyboardEvent) => {
    event.stopPropagation()
    // Preserve native text undo and do not leak editor shortcuts to Home Assistant.
    if ((event.target as HTMLElement).closest('input,textarea,select,[contenteditable="true"]')) return
    if ((event.ctrlKey || event.metaKey) && ['z', 'y'].includes(event.key.toLowerCase())) {
      event.preventDefault(); event.stopPropagation()
      if (event.key.toLowerCase() === 'y' || event.shiftKey) history.redo(); else history.undo()
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      const target = event.target as Element
      const nodeId = target.closest('.solid-flow__node')?.getAttribute('data-id')
      const edgeId = target.closest('.solid-flow__edge')?.getAttribute('data-id')
      if (nodeId) { event.preventDefault(); commit(removeNode(graph(), nodeId)) }
      else if (edgeId) { event.preventDefault(); commit({ ...graph(), edges: graph().edges.filter(edge => edge.id !== edgeId) }) }
    }
  }
  const download = () => {
    const url = URL.createObjectURL(new Blob([serializeGraph(graph())], { type: 'application/json' }))
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'automation.graph.json'; anchor.click()
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  return <section ref={editor} class="hs-visual" aria-label="Visual automation editor" on:keydown={keyboard} on:keyup={event => event.stopPropagation()}>
    <header class="hs-visual-heading">
      <div><p class="hs-eyebrow">VISUAL AUTOMATION</p><h1>{graph().name}</h1><p class="hs-muted">Build a flow, one step at a time.</p></div>
      <div class="hs-visual-actions"><Status>{session.document() ? `Saved · revision ${session.document()!.revision}` : 'Not saved'}</Status>
        <Show when={session.dirty()}><Status>Unsaved changes</Status></Show><Button onClick={download}>Export graph</Button>
        <Show when={props.documentId}><Button disabled={session.loading() || session.saving()} onClick={reload}>Reload saved graph</Button></Show>
        <Button variant="primary" busy={session.saving()} disabled={!session.canSave()} onClick={() => void save()}>Save graph</Button></div>
    </header>
    <p class="hs-visual-notice" role="note">{graph().version === 2 ? 'Multiple independent triggers can share downstream steps.' : 'Graph v1 supports one trigger.'} Graph edges cannot loop or fork in parallel. Use custom C# and helpers for loops, concurrent work and custom schedules. Saving activates the automation and may execute startup code.</p>
    <Show when={graph().version === 1}><div class="hs-visual-notice"><p>Upgrade to v2 to add triggers and C# imports or members. Trigger actions change from Run to individual actions after saving.</p><Button disabled={session.loading() || session.saving()} onClick={() => commit({ ...graph(), version: 2 })}>Upgrade to graph v2</Button></div></Show>
    <Field label="Automation name" aria-label="Automation name" value={graph().name} disabled={session.loading() || session.saving()} onInput={event => commit({ ...graph(), name: event.currentTarget.value })} />
    <Show when={session.loading()}><p role="status">Loading saved graph…</p></Show>
    <Show when={session.error()}><div class="hs-notice hs-notice--danger" role="alert">{session.error()}
      <Show when={session.conflict()}><p>Your local graph is preserved. Export it before reloading the saved graph to resolve this conflict.</p></Show>
    </div></Show>
    <Show when={session.activationError()}><div class="hs-notice hs-notice--danger" role="alert"><strong>Saved, but not activated.</strong><p>{session.activationError()}</p><p>The previous runtime may still be running. Edit and save again, or reload the HassSharp integration.</p></div></Show>
    <div inert={session.loading() || session.saving()} aria-busy={session.loading() || session.saving()}>
    <div class="hs-flow-toolbar" aria-label="Canvas controls">
      <Button aria-label="Undo" disabled={!history.canUndo()} onClick={history.undo}>↶ Undo</Button>
      <Button aria-label="Redo" disabled={!history.canRedo()} onClick={history.redo}>↷ Redo</Button>
      <span class="hs-muted">Drag to move · Tab to a node, Enter to select, arrows to move</span>
    </div>
    <div class="hs-visual-workspace">
      <aside class="hs-palette" aria-label="Node palette"><h2>Add a step</h2><p class="hs-muted">Choose a building block</p>
        <For each={palette}>{item => <Button class="hs-palette-item" disabled={item.kind === 'trigger' && graph().version === 1 && graph().nodes.some(node => node.kind === 'trigger')} aria-label={`Add ${item.label}`} onClick={() => {
          const next = addNode(graph(), item.kind); commit(next); choose(next.nodes.at(-1)!.id)
        }}><span class={`hs-node-dot hs-node-dot--${item.kind}`} /><span><strong>{item.label}</strong><small>{item.description}</small></span><span aria-hidden="true">+</span></Button>}</For>
      </aside>
      <FlowAdapter graph={graph()} selectedId={selectedId()} onSelect={choose} onChange={commit} diagnostics={session.preview()?.diagnostics} />
      <aside class="hs-inspector" aria-label="Node inspector"><h2>Step details</h2>
        <Show when={selected()} fallback={<p class="hs-muted">Select a step on the canvas to edit it.</p>}>{node => <div class="hs-stack">
          <Status>{node().kind}</Status>
          <Field label="Label" aria-label="Node label" value={node().label ?? node().kind} onInput={event => commit(updateNode(graph(), node().id, { label: event.currentTarget.value }))} />
          <Show when={node().kind === 'trigger'} fallback={<For each={Object.keys(node().config)}>{key => key === 'code' ? <div class="hs-field"><label for="hs-custom-code">Custom C# section</label><textarea id="hs-custom-code" aria-label="Custom C# section" class="hs-input hs-code-input" rows="6" value={String(node().config[key])} onInput={event => commit(updateNode(graph(), node().id, { config: { ...node().config, [key]: event.currentTarget.value } }))} /></div>
            : <Field label={configLabels[key] ?? key} aria-label={configLabels[key] ?? key} value={String(node().config[key])} onInput={event => commit(updateNode(graph(), node().id, { config: { ...node().config, [key]: event.currentTarget.value } }))} />}</For>}>
            <TriggerConfig node={node()} version={graph().version} onChange={config => commit(updateNode(graph(), node().id, { config }))} />
          </Show>
          <div class="hs-size-fields"><For each={['width', 'height'] as const}>{dimension => <Field
            label={dimension === 'width' ? 'Width' : 'Height'} aria-label={dimension === 'width' ? 'Width' : 'Height'}
            type="number" min={dimension === 'width' ? 180 : 100} value={node().size?.[dimension] ?? (dimension === 'width' ? 220 : 112)}
            onChange={event => {
              const value = Number(event.currentTarget.value)
              if (Number.isFinite(value) && value >= (dimension === 'width' ? 180 : 100)) commit(updateNode(graph(), node().id, { size: { width: node().size?.width ?? 220, height: node().size?.height ?? 112, [dimension]: value } }))
              else event.currentTarget.value = String(node().size?.[dimension] ?? (dimension === 'width' ? 220 : 112))
            }} />}</For></div>
          <div class="hs-field"><label for="hs-connect">Connect to</label><select id="hs-connect" class="hs-input" aria-label="Connect to" value={target()} onChange={event => setTarget(event.currentTarget.value)}>
            <option value="">Choose next step</option><For each={graph().nodes.filter(n => canConnect(graph(), node().id, n.id))}>{next => <option value={next.id}>{next.label}</option>}</For>
          </select></div>
          <Button disabled={!canConnect(graph(), node().id, target())} onClick={() => { commit(connectNodes(graph(), node().id, target())); setTarget('') }}>Connect</Button>
          <Button variant="danger" onClick={() => commit(removeNode(graph(), node().id))}>Delete step</Button>
        </div>}</Show>
      </aside>
    </div>
    <Show when={graph().version === 2}><section class="hs-graph-sections" aria-label="Graph C# sections">
      <h2>C# imports and members</h2>
      <p class="hs-muted">Imports appear before the generated class. Members are fields, properties and helper methods inside it. Fields are shared across invocations; await child work and pass CancellationToken to delays and I/O. The generated class, Configure and action signatures are reserved.</p>
      <div class="hs-graph-section-fields"><For each={['imports', 'members'] as const}>{key => <div class="hs-field" classList={{ 'hs-code-section--selected': selectedId() === `$${key}` }}>
        <label for={`hs-graph-${key}`}>{key === 'imports' ? 'C# imports' : 'C# members'}</label>
        <textarea id={`hs-graph-${key}`} data-section-id={`$${key}`} aria-label={key === 'imports' ? 'C# imports' : 'C# members'} aria-invalid={session.preview()?.diagnostics.some(d => d.nodeId === `$${key}` && d.severity === 3) || undefined}
          class="hs-input hs-code-input" rows={key === 'imports' ? 3 : 6} value={graph()[key] ?? ''}
          onInput={event => commit({ ...graph(), [key]: event.currentTarget.value })} />
      </div>}</For></div>
    </section></Show>
    <div class="hs-visual-bottom">
      <section class="hs-connections" aria-label="Connections"><h2>Connections <span class="hs-muted">({graph().edges.length})</span></h2>
        <p class="hs-muted">Use the inspector to connect steps with a keyboard or touch.</p>
        <Show when={!graph().edges.length}><p class="hs-muted">No connections yet.</p></Show>
        <ul><For each={graph().edges}>{edge => {
          const label = () => `${graph().nodes.find(n => n.id === edge.source)?.label ?? edge.source} → ${graph().nodes.find(n => n.id === edge.target)?.label ?? edge.target}`
          return <li><span>{label()}</span><Button variant="ghost" aria-label={`Remove ${label()}`} onClick={() => commit({ ...graph(), edges: graph().edges.filter(e => e.id !== edge.id) })}>Remove</Button></li>
        }}</For></ul>
      </section>
      <section class="hs-graph-preview" aria-label="Code preview">
        <div class="hs-preview-toolbar"><Button aria-pressed={preview() === 'code'} onClick={() => setPreview('code')}>Generated C#</Button><Button aria-pressed={preview() === 'json'} onClick={() => setPreview('json')}>Graph JSON</Button></div>
        <p class="hs-muted">Generated by Home Assistant from this graph. Select a step or diagnostic to locate its source.</p>
        <Show when={session.previewError()}><div role="alert" class="hs-error">Preview failed: {session.previewError()} <Button onClick={session.retryPreview}>Retry preview</Button></div></Show>
        <Show when={!session.preview() && !session.previewError()}><p role="status">Updating preview…</p></Show>
        <ul class="hs-diagnostics" aria-label="Graph diagnostics"><For each={session.preview()?.diagnostics.filter(d => d.severity > 0)}>{diagnostic => <li classList={{ 'hs-error': diagnostic.severity === 3 }}>
          <span>{diagnostic.severity === 3 ? 'Error' : diagnostic.severity === 2 ? 'Warning' : 'Info'}: </span><Button variant="ghost" onClick={() => locate(diagnostic)}>{diagnostic.message}{diagnostic.startLine != null ? ` · line ${diagnostic.startLine}` : ''}</Button>
        </li>}</For></ul>
        <Show when={preview() === 'json'} fallback={<Show when={session.preview()}>{result => <pre tabindex="0" aria-label="Generated C#"><code><For each={result().source.split('\n')}>{(line, index) => {
          const range = () => result().sourceMap.find(range => index() + 1 >= range.startLine && index() + 1 <= range.endLine)
          const selected = () => sourceLine() === index() + 1 || (sourceLine() == null && range()?.nodeId === selectedId())
          return <span class="hs-source-line" data-source-line={index() + 1} aria-current={selected() ? 'true' : undefined}>
            <button class="hs-source-number" aria-label={`Select source line ${index() + 1}`} disabled={!range()} onClick={() => { if (range()) { choose(range()!.nodeId); focusSection(range()!.nodeId) } setSourceLine(index() + 1) }}>{index() + 1}</button>{line}{'\n'}</span>
        }}</For></code></pre>}</Show>}><pre tabindex="0" aria-label="Graph JSON"><code>{serializeGraph(graph())}</code></pre></Show>
      </section>
    </div>
    </div>
    <Show when={session.document()}>{doc => <AutomationRuns automationId={doc().automationId} classes={visualRunClasses(doc().graph)} actionLabels={visualActionLabels(doc().graph)} />}</Show>
    <Dialog open={reloadDialog()} title="Discard local changes?" width="small" onClose={() => setReloadDialog(false)} footer={<>
      <Button onClick={() => setReloadDialog(false)}>Keep editing</Button><Button variant="danger" onClick={() => { setReloadDialog(false); if (props.documentId) void session.load(props.documentId) }}>Reload and discard</Button>
    </>}><p>Reloading replaces your local graph with the saved revision. Export your graph first to keep a copy.</p></Dialog>
  </section>
}
