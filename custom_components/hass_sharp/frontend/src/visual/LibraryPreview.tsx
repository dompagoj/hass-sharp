import { For, Show } from 'solid-js'
import { dataInputs, dataOutputs, executionPorts, type VisualCatalogSnapshot } from './graphV4'
import { portLabel, typeCategory, typeColor, typeLabel } from './graph'

export const LibraryPreview = (props: { catalog?: VisualCatalogSnapshot; sourceId: string; pending: boolean }) => {
  const nodes = () => Object.entries(props.catalog?.nodes ?? {}).filter(([, spec]) => spec.sourceId === props.sourceId)
  return <aside class="hs-library-previews" aria-label="Node previews" aria-busy={props.pending}>
    <h2>Nodes in this file</h2>
    <p class="hs-muted">{props.pending ? 'Checking workspace…' : `${nodes().length} nodes · port types from C#`}</p>
    <p class="hs-muted">Previews compile signatures and documentation. Methods run only through execution connections.</p>
    <Show when={!props.pending && !nodes().length}><p class="hs-notice">Add a public static method with <code>[Node("Title")]</code> inside a public class.</p></Show>
    <For each={nodes()}>{([kind, spec]) => {
      const node = { id: kind, kind, config: {} }
      const catalog = () => props.catalog!
      const inputs = () => Object.entries(dataInputs(node, undefined, catalog()))
      const outputs = () => Object.entries(dataOutputs(node, catalog()))
      return <article class="hs-library-preview">
        <div class="hs-library-node hs-flow-card"><header><strong>{spec.label}</strong></header>
          <div class="hs-library-execution"><span>▷ Execution</span><span>{executionPorts(node, catalog()).map(portLabel).join(' · ')} ▷</span></div>
          <For each={inputs()}>{([id, input]) => <div class="hs-library-port" title={input.description} data-port-id={id}>
            <span class="hs-library-port-dot" style={{ 'border-color': typeColor(input.types[0], catalog()) }} />
            <span>{input.label ?? id}<small>{input.required ? 'Required' : 'Optional'}</small></span>
            <code data-type-category={typeCategory(input.types[0], catalog())}>{input.types.map(t => typeLabel(t, catalog())).join(' / ')}</code>
          </div>}</For>
          <For each={outputs()}>{([id, output]) => <div class="hs-library-port hs-library-port--output" title={output.description} data-port-id={id}>
            <span>{output.label ?? id}</span><code data-type-category={typeCategory(output.type, catalog())}>{typeLabel(output.type, catalog())}</code>
            <span class="hs-library-port-dot" style={{ 'border-color': typeColor(output.type, catalog()) }} />
          </div>}</For>
        </div>
        <span class="hs-badge">{spec.category}</span><p>{spec.description}</p>
        <dl><For each={inputs()}>{([id, input]) => <><dt>{input.label ?? id}</dt><dd>{input.description}<Show when={Object.hasOwn(input, 'default')}> Default: <code>{JSON.stringify((input.default as { value?: unknown })?.value)}</code>.</Show>
          <Show when={input.types.some(t => !catalog().types[t]?.literalKind)}> Construct this C# value with a factory node and connect its result to this input.<Show when={input.types.some(t => catalog().types[t]?.nullable)}> Null is also allowed.</Show></Show>
        </dd></>}</For>
        <For each={outputs()}>{([id, output]) => <><dt>{output.label ?? id}</dt><dd>{output.description}</dd></>}</For></dl>
      </article>
    }}</For>
  </aside>
}
