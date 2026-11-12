import { createMemo, createSignal, For, onCleanup, onMount, Show } from 'solid-js'
import { Button, Field, Status } from '../components/Controls'
import { FlowAdapter } from './FlowAdapter'
import { addNode, canConnect, connectNodes, createGraph, createGraphHistory, palette, removeNode, serializeGraph, updateNode } from './graph'

const sampleCode = `// SAMPLE ONLY — not generated from your graph.
// This editor does not compile, save or run automations yet.
public class ExampleAutomation : ManagedAutomation
{
    public override void Configure()
    {
        OnStartup();
    }

    public override async Task ExecuteAsync(AutomationContext context)
    {
        await Task.Delay(1000, context.CancellationToken);
    }
}`
const configLabels: Record<string, string> = { entity: 'Entity ID', state: 'Required state', service: 'Service', seconds: 'Seconds', code: 'Custom C# section' }

export function VisualEditor(props: { name: string }) {
  const history = createGraphHistory(createGraph(props.name))
  const { graph, commit } = history
  const [selectedId, setSelectedId] = createSignal<string>('trigger-1')
  const [target, setTarget] = createSignal('')
  const [preview, setPreview] = createSignal<'code' | 'json'>('code')
  const selected = createMemo(() => graph().nodes.find(node => node.id === selectedId()))
  const edited = () => history.canUndo() || history.canRedo()
  const beforeUnload = (event: BeforeUnloadEvent) => { if (edited()) { event.preventDefault(); event.returnValue = '' } }
  onMount(() => window.addEventListener('beforeunload', beforeUnload))
  onCleanup(() => window.removeEventListener('beforeunload', beforeUnload))
  const choose = (id: string) => { setSelectedId(id); setTarget('') }
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
  return <section class="hs-visual" aria-label="Visual automation editor" on:keydown={keyboard} on:keyup={event => event.stopPropagation()}>
    <header class="hs-visual-heading">
      <div><p class="hs-eyebrow">VISUAL DRAFT</p><h1>{props.name}</h1><p class="hs-muted">Build a flow, one step at a time.</p></div>
      <div class="hs-visual-actions"><Status>Not saved</Status><Button onClick={download}>Export graph</Button></div>
    </header>
    <div class="hs-visual-notice" role="note"><strong>Draft editor preview.</strong> Changes stay on this page. Export JSON to keep a copy. Graphs cannot be saved to Home Assistant or run yet.</div>
    <div class="hs-flow-toolbar" aria-label="Canvas controls">
      <Button aria-label="Undo" disabled={!history.canUndo()} onClick={history.undo}>↶ Undo</Button>
      <Button aria-label="Redo" disabled={!history.canRedo()} onClick={history.redo}>↷ Redo</Button>
      <span class="hs-muted">Drag to move · Tab to a node, Enter to select, arrows to move</span>
    </div>
    <div class="hs-visual-workspace">
      <aside class="hs-palette" aria-label="Node palette"><h2>Add a step</h2><p class="hs-muted">Choose a building block</p>
        <For each={palette}>{item => <Button class="hs-palette-item" aria-label={`Add ${item.label}`} onClick={() => {
          const next = addNode(graph(), item.kind); commit(next); choose(next.nodes.at(-1)!.id)
        }}><span class={`hs-node-dot hs-node-dot--${item.kind}`} /><span><strong>{item.label}</strong><small>{item.description}</small></span><span aria-hidden="true">+</span></Button>}</For>
      </aside>
      <FlowAdapter graph={graph()} selectedId={selectedId()} onSelect={choose} onChange={commit} />
      <aside class="hs-inspector" aria-label="Node inspector"><h2>Step details</h2>
        <Show when={selected()} fallback={<p class="hs-muted">Select a step on the canvas to edit it.</p>}>{node => <div class="hs-stack">
          <Status>{node().kind}</Status>
          <Field label="Label" aria-label="Node label" value={node().label} onInput={event => commit(updateNode(graph(), node().id, { label: event.currentTarget.value }))} />
          <For each={Object.keys(node().config)}>{key => key === 'code' ? <div class="hs-field"><label for="hs-custom-code">Custom C# section</label><textarea id="hs-custom-code" class="hs-input" rows="6" value={node().config[key]} onInput={event => commit(updateNode(graph(), node().id, { config: { ...node().config, [key]: event.currentTarget.value } }))} /></div>
            : <Field label={configLabels[key] ?? key} aria-label={configLabels[key] ?? key} value={node().config[key]} onInput={event => commit(updateNode(graph(), node().id, { config: { ...node().config, [key]: event.currentTarget.value } }))} />}</For>
          <div class="hs-size-fields"><For each={['width', 'height'] as const}>{dimension => <Field
            label={dimension === 'width' ? 'Width' : 'Height'} aria-label={dimension === 'width' ? 'Width' : 'Height'}
            type="number" min={dimension === 'width' ? 180 : 100} value={node().size[dimension]}
            onChange={event => {
              const value = Number(event.currentTarget.value)
              if (Number.isFinite(value) && value >= (dimension === 'width' ? 180 : 100)) commit(updateNode(graph(), node().id, { size: { ...node().size, [dimension]: value } }))
              else event.currentTarget.value = String(node().size[dimension])
            }} />}</For></div>
          <div class="hs-field"><label for="hs-connect">Connect to</label><select id="hs-connect" class="hs-input" aria-label="Connect to" value={target()} onChange={event => setTarget(event.currentTarget.value)}>
            <option value="">Choose next step</option><For each={graph().nodes.filter(n => canConnect(graph(), node().id, n.id))}>{next => <option value={next.id}>{next.label}</option>}</For>
          </select></div>
          <Button disabled={!canConnect(graph(), node().id, target())} onClick={() => { commit(connectNodes(graph(), node().id, target())); setTarget('') }}>Connect</Button>
          <Button variant="danger" onClick={() => commit(removeNode(graph(), node().id))}>Delete step</Button>
        </div>}</Show>
      </aside>
    </div>
    <div class="hs-visual-bottom">
      <section class="hs-connections" aria-label="Connections"><h2>Connections <span class="hs-muted">({graph().edges.length})</span></h2>
        <p class="hs-muted">Use the inspector to connect steps with a keyboard or touch.</p>
        <Show when={!graph().edges.length}><p class="hs-muted">No connections yet.</p></Show>
        <ul><For each={graph().edges}>{edge => {
          const label = () => `${graph().nodes.find(n => n.id === edge.source)?.label} → ${graph().nodes.find(n => n.id === edge.target)?.label}`
          return <li><span>{label()}</span><Button variant="ghost" aria-label={`Remove ${label()}`} onClick={() => commit({ ...graph(), edges: graph().edges.filter(e => e.id !== edge.id) })}>Remove</Button></li>
        }}</For></ul>
      </section>
      <section class="hs-graph-preview" aria-label="Code preview">
        <div class="hs-preview-toolbar"><Button aria-pressed={preview() === 'code'} onClick={() => setPreview('code')}>Sample C#</Button><Button aria-pressed={preview() === 'json'} onClick={() => setPreview('json')}>Graph JSON</Button></div>
        <p class="hs-muted">Sample C# is not generated from your graph. Code generation, validation and source mapping come later.</p>
        <pre tabindex="0" aria-label={preview() === 'code' ? 'Sample C#' : 'Graph JSON'}><code>{preview() === 'code' ? sampleCode : serializeGraph(graph())}</code></pre>
      </section>
    </div>
  </section>
}
