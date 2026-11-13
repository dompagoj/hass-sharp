import { createEffect, createMemo, createSignal, on, For, Show } from 'solid-js'
import { EntityAutocomplete } from '../components/EntityAutocomplete'
import { ServicePicker } from '../components/ServicePicker'
import { Button, Field } from '../components/Controls'
import { ConfigSelect, JsonField, ValueOperand } from './ValueOperand'
import { TriggerConfig } from './TriggerConfig'
import {
  nodeInputs,
  nodeLabel,
  inputType,
  dataConnectionError,
  typeLabel,
  typeColor,
  updateNode,
  canConnect,
  connectNodes,
  portLabel,
  type GraphNode,
  type VisualGraph,
} from './graph'
import {
  connectData,
  isAssignable,
  dataOutputs,
  disconnectData,
  executionPorts,
  inputValue,
  setInput,
  type ConfigSpec,
  type VisualCatalogSnapshot,
  type Operand,
  type DataEdge,
} from './contract'
import type { VisualDiagnostic } from './api'

export function NodeConfig(props: {
  node: GraphNode
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  diagnostics?: VisualDiagnostic[]
  onChange: (config: GraphNode['config']) => void
  onGraphChange: (graph: VisualGraph) => void
}) {
  const nodeKind = createMemo(() => props.node.kind)
  const spec = createMemo(() => props.catalog.nodes[nodeKind()])
  const configEntries = createMemo(() =>
    Object.entries(spec()?.config ?? {}).filter(
      ([key]) => !(spec()?.argumentStorage === 'arguments' && key === 'arguments') && !(nodeKind() === 'loop_result' && key === 'values'),
    ),
  )
  const inputs = createMemo(() => nodeInputs(props.graph, props.node, props.catalog), undefined, {
    equals: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  })
  const inputEntries = createMemo(() => Object.entries(inputs()))
  const outputs = createMemo(() => dataOutputs(props.node, props.catalog), undefined, {
    equals: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  })
  const outputEntries = createMemo(() => Object.entries(outputs()))
  const execution = createMemo(() => executionPorts(props.node, props.catalog), undefined, {
    equals: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  })
  const [source, setSource] = createSignal<Record<string, string>>({})
  const [error, setError] = createSignal('')
  const [rejectedInput, setRejectedInput] = createSignal<string>()
  createEffect(
    on(
      () => [props.graph, props.catalog, props.node.id] as const,
      () => {
        setSource({})
        setError('')
        setRejectedInput(undefined)
      },
      { defer: true },
    ),
  )
  const set = (key: string, value: unknown) => props.onChange({ ...props.node.config, [key]: value })
  const setOperand = (key: string, value: Operand) => {
    const node = structuredClone(props.node)
    setInput(node, key, value, props.catalog)
    props.onChange(node.config)
  }
  const diagnostics = (key: string) =>
    props.diagnostics?.filter(d => d.nodeId === props.node.id && (d.inputId === key || d.portId === key)) ?? []
  const configField = (key: string, rule: ConfigSpec) => {
    const label = rule.label ?? key,
      value = () => props.node.config[key]
    const options = rule.enumSource === 'types' ? Object.keys(props.catalog.types) : rule.enum
    return (
      <div data-input-id={key} tabindex="-1" aria-invalid={diagnostics(key).some(d => d.severity === 3) || undefined}>
        <Show when={key === 'service'}>
          <ServicePicker value={String(value() ?? '')} onChange={v => set(key, v)} />
        </Show>
        <Show when={key === 'entity'}>
          <EntityAutocomplete optional={!rule.required} value={String(value() ?? '')} onChange={v => set(key, v)} />
        </Show>
        <Show when={key === 'code'}>
          <div class="hs-field">
            <label>
              Custom C# section
              <textarea
                aria-label="Custom C# section"
                class="hs-input hs-code-input"
                rows="6"
                value={String(value() ?? '')}
                onInput={e => set(key, e.currentTarget.value)}
              />
            </label>
          </div>
        </Show>
        <Show when={!['service', 'entity', 'code'].includes(key)}>
          <Show
            when={options}
            fallback={
              <Show
                when={rule.type === 'object' || rule.type === 'array'}
                fallback={
                  <Show
                    when={rule.type === 'boolean'}
                    fallback={
                      <Field
                        label={label}
                        aria-label={label}
                        hint={rule.description}
                        type={rule.type === 'integer' || rule.type === 'number' ? 'number' : 'text'}
                        value={String(value() ?? '')}
                        onInput={e =>
                          set(
                            key,
                            rule.type === 'integer' || rule.type === 'number' ? Number(e.currentTarget.value) : e.currentTarget.value,
                          )
                        }
                      />
                    }
                  >
                    <ConfigSelect
                      label={label}
                      value={value() === undefined ? '' : String(value())}
                      options={[
                        ['', 'Unset'],
                        ['true', 'True'],
                        ['false', 'False'],
                      ]}
                      onChange={v => {
                        const next = { ...props.node.config }
                        if (v === '') delete next[key]
                        else next[key] = v === 'true'
                        props.onChange(next)
                      }}
                    />
                  </Show>
                }
              >
                <JsonField
                  label={label}
                  value={value() ?? (rule.type === 'array' ? [] : {})}
                  hint={rule.description}
                  onChange={v => set(key, v)}
                />
              </Show>
            }
          >
            <ConfigSelect
              label={label}
              value={String(value() ?? '')}
              options={[
                ['', 'Unset'],
                ...(options ?? []).map(
                  v => [String(v), rule.enumSource === 'types' ? typeLabel(String(v), props.catalog) : String(v)] as const,
                ),
              ]}
              onChange={v => {
                const next = { ...props.node.config }
                if (v === '') delete next[key]
                else next[key] = v
                props.onChange(next)
              }}
            />
          </Show>
        </Show>
        <For each={diagnostics(key)}>{d => <small class="hs-error">{d.message}</small>}</For>
      </div>
    )
  }
  const labelFor = (id: string) =>
    nodeLabel(
      props.graph.nodes.find(node => node.id === id),
      props.catalog,
      id,
    )
  const destinations = (port: string) =>
    props.graph.edges.filter(e => e.kind === 'data' && e.source === props.node.id && e.sourcePort === port)
  return (
    <div class="hs-stack hs-typed-inspector">
      <header>
        <h2>{nodeLabel(props.node, props.catalog)}</h2>
        <small class="hs-muted">{props.node.id}</small>
        <p>{spec()?.description ?? 'Unknown node kind'}</p>
      </header>
      <Field
        label="Label"
        aria-label="Node label"
        value={nodeLabel(props.node, props.catalog)}
        onInput={e => props.onGraphChange(updateNode(props.graph, props.node.id, { label: e.currentTarget.value }))}
      />
      <Show when={props.node.kind === 'trigger'} fallback={<For each={configEntries()}>{([key, rule]) => configField(key, rule)}</For>}>
        <TriggerConfig node={props.node} onChange={props.onChange} />
      </Show>
      <section>
        <h3>Inputs</h3>
        <Show when={props.node.kind !== 'trigger'}>
          <p class="hs-port-row hs-port-row--execution">Execution · Receives the previous step</p>
        </Show>
        <For each={inputEntries()}>
          {([id, input]) => {
            const label = input.label ?? id
            const actualType = () => inputType(props.graph, props.node, id, input, props.catalog)
            const connected = () =>
              props.graph.edges.find((e): e is DataEdge => e.kind === 'data' && e.target === props.node.id && e.targetPort === id)
            const producers = () =>
              props.graph.nodes
                .flatMap(node => Object.entries(dataOutputs(node, props.catalog)).map(([port, output]) => ({ node, port, output })))
                .filter(item => item.node.id !== props.node.id)
            return (
              <div
                class="hs-typed-input"
                data-input-id={id}
                tabindex="-1"
                aria-invalid={diagnostics(id).some(d => d.severity === 3) || rejectedInput() === id || undefined}
                style={{ '--hs-value-color': typeColor(actualType() ?? 'object', props.catalog) }}
              >
                <strong>
                  {label} · {actualType() ? typeLabel(actualType()!, props.catalog) : 'Any registered type'} ·{' '}
                  {input.required ? 'Required' : 'Optional'}
                </strong>
                <p>{input.description}</p>
                <Show
                  when={connected()}
                  fallback={
                    <ValueOperand
                      label={label}
                      value={inputValue(props.node, id, props.catalog)}
                      spec={input}
                      variables={props.graph.nodes
                        .filter(
                          n =>
                            typeof n.config.output === 'string' &&
                            dataOutputs(n, props.catalog).value &&
                            input.types.some(t => isAssignable(dataOutputs(n, props.catalog).value.type, t, props.catalog)),
                        )
                        .map(n => String(n.config.output))}
                      catalog={props.catalog}
                      onChange={v => setOperand(id, v)}
                    />
                  }
                >
                  {edge => (
                    <div>
                      <p>
                        From {labelFor(edge().source)} · {edge().sourcePort}
                      </p>
                      <Button onClick={() => props.onGraphChange(disconnectData(props.graph, props.node.id, id, props.catalog))}>
                        Disconnect {label}
                      </Button>
                    </div>
                  )}
                </Show>
                <ConfigSelect
                  label={`${label} connection`}
                  value={source()[id] ?? ''}
                  options={[
                    ['', 'Choose typed output'],
                    ...producers().map(
                      item =>
                        [
                          JSON.stringify([item.node.id, item.port]),
                          `${nodeLabel(item.node, props.catalog)} · ${item.output.label ?? item.port} · ${typeLabel(item.output.type, props.catalog)}`,
                        ] as const,
                    ),
                  ]}
                  onChange={v => {
                    setSource({ ...source(), [id]: v })
                    setError('')
                    setRejectedInput(undefined)
                  }}
                />
                <Button
                  disabled={!source()[id]}
                  onClick={() => {
                    try {
                      const [node, port] = JSON.parse(source()[id])
                      const message = dataConnectionError(props.graph, node, port, props.node.id, id, props.catalog)
                      if (message) throw new Error(message)
                      props.onGraphChange(connectData(props.graph, node, port, props.node.id, id, props.catalog))
                      setError('')
                    } catch (e) {
                      setRejectedInput(id)
                      setError(e instanceof Error ? e.message : String(e))
                    }
                  }}
                >
                  Connect {label}
                </Button>
                <For each={diagnostics(id)}>{d => <small class="hs-error">{d.message}</small>}</For>
              </div>
            )
          }}
        </For>
        <Show when={!Object.keys(inputs()).length && props.node.kind === 'trigger'}>
          <p class="hs-muted">No inputs. Triggers start execution.</p>
        </Show>
      </section>
      <section>
        <h3>Outputs</h3>
        <For each={execution()}>
          {port => {
            const [target, setTarget] = createSignal('')
            createEffect(
              on(
                () => props.node.id,
                () => setTarget(''),
                { defer: true },
              ),
            )
            const edges = () =>
              props.graph.edges.filter(e => e.kind !== 'data' && e.source === props.node.id && (e.port ?? 'next') === port)
            return (
              <div class="hs-port-row hs-port-row--execution" style={{ '--hs-value-color': port === 'error' ? 'var(--hs-port-error)' : 'var(--hs-port-execution)' }}>
                <strong>{portLabel(port)} · Execution</strong>
                <p>
                  {port === 'next' && props.node.kind === 'parallel'
                    ? 'Joined continuation after all branches complete.'
                    : port === 'body'
                      ? 'Executes inside the loop body.'
                      : port === 'condition'
                        ? 'Compute the loop condition, ending with a Boolean condition result.'
                        : 'Execution branch.'}
                </p>
                <For each={edges()}>{edge => <p>To {labelFor(edge.target)}</p>}</For>
                <Show when={!edges().length}>
                  <ConfigSelect
                    label={`${portLabel(port)} destination`}
                    value={target()}
                    options={[
                      ['', 'Choose next step'],
                      ...props.graph.nodes
                        .filter(n => canConnect(props.graph, props.node.id, n.id, port, props.catalog))
                        .map(n => [n.id, nodeLabel(n, props.catalog)] as const),
                    ]}
                    onChange={setTarget}
                  />
                  <Button
                    disabled={!target()}
                    onClick={() => props.onGraphChange(connectNodes(props.graph, props.node.id, target(), port, props.catalog))}
                  >
                    Connect {portLabel(port)}
                  </Button>
                </Show>
              </div>
            )
          }}
        </For>
        <For each={outputEntries()}>
          {([id, output]) => (
            <div class="hs-port-row" style={{ '--hs-value-color': typeColor(output.type, props.catalog) }}>
              <strong>
                {output.label ?? id} · {typeLabel(output.type, props.catalog)}
              </strong>
              <p>{output.description}</p>
              <Show when={output.visibility}>
                <small>Visible: {output.visibility}</small>
              </Show>
              <For each={destinations(id)}>
                {edge => (
                  <p>
                    To {labelFor(edge.target)} · {edge.kind === 'data' ? edge.targetPort : ''}
                  </p>
                )}
              </For>
              <Show when={!destinations(id).length}>
                <p class="hs-muted">No destinations</p>
              </Show>
            </div>
          )}
        </For>
      </section>
      <Show when={error()}>
        <p role="alert" class="hs-error">
          {error()}
        </p>
      </Show>
      <details aria-label="Appearance">
        <summary>Appearance</summary>
        <div class="hs-size-fields">
          <For each={['width', 'height'] as const}>
            {dimension => (
              <Field
                label={dimension === 'width' ? 'Width' : 'Height'}
                aria-label={dimension === 'width' ? 'Width' : 'Height'}
                type="number"
                min={dimension === 'width' ? 180 : 100}
                value={props.node.size?.[dimension] ?? (dimension === 'width' ? 220 : 112)}
                onChange={e => {
                  const value = Number(e.currentTarget.value)
                  if (Number.isFinite(value) && value >= (dimension === 'width' ? 180 : 100))
                    props.onGraphChange(
                      updateNode(props.graph, props.node.id, {
                        size: { width: props.node.size?.width ?? 220, height: props.node.size?.height ?? 112, [dimension]: value },
                      }),
                    )
                  else e.currentTarget.value = String(props.node.size?.[dimension] ?? (dimension === 'width' ? 220 : 112))
                }}
              />
            )}
          </For>
        </div>
      </details>
    </div>
  )
}
