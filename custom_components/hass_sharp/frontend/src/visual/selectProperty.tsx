import { createEffect, createMemo, createUniqueId, For, Show } from 'solid-js'
import { analyzeGraph, connectable, dataInputs, dataOutputs, typeProperties, wireTypeError, words, type GraphNode, type PropertySpec, type VisualCatalogSnapshot } from './contract'
import { graphTypes } from './connectionIndex'
import { csharpType } from './csharpType'
import { removeEdge, updateNode, type VisualGraph } from './graph'
import { typeLabel } from './typeLabels'
import { SearchSelect } from './widgets/SearchSelect'

/** Above this many properties the choice is a searchable list rather than a plain select. */
export const SEARCH_ABOVE = 12
/** The input a Select property node reads its value from. */
const VALUE = 'value'

/**
 * What a Select property node can choose from: the type of the value wired into it (the graph's catalog resolves
 * chained nodes), and that type's properties. No wire, or a value of a type without properties, offers none.
 */
export function propertyChoices(graph: VisualGraph, node: GraphNode, catalog: VisualCatalogSnapshot): { connected: boolean; type?: string; properties: PropertySpec[] } {
  const types = graphTypes(graph, catalog)
  const wire = graph.edges.find(e => e.kind === 'data' && e.target === node.id && e.targetPort === VALUE)
  const source = wire?.kind === 'data' ? graph.nodes.find(n => n.id === wire.source) : undefined
  if (!wire || wire.kind !== 'data' || !source) return { connected: false, properties: [] }
  const outputs = dataOutputs(source, types)
  const type = Object.hasOwn(outputs, wire.sourcePort) ? outputs[wire.sourcePort].type || undefined : undefined
  return { connected: true, type, properties: type ? typeProperties(types, type) : [] }
}

/** A property as the choice lists it: its name, then its friendly type and C# type ("UserName · Text (string)"). */
export function propertyText(property: PropertySpec, catalog: VisualCatalogSnapshot): string {
  const csharp = csharpType(property.type, catalog)
  return `${property.name} · ${typeLabel(property.type, catalog)}${csharp ? ` (${csharp})` : ''}`
}

/**
 * The graph with the node's property chosen, as one change: each value wire from the node (and from a Select property
 * node reading it in turn) whose value no longer fits where it goes is removed, and `notice` says which. A wire that
 * already did not fit for another reason is left to the validator, and so is one whose problem is a property to choose.
 */
export function chooseProperty(graph: VisualGraph, nodeId: string, name: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node || node.config.property === name) return { graph }
  let next = updateNode(graph, nodeId, { config: { ...node.config, property: name } })
  const removed: string[] = []
  // A Loop result's carry and export inputs come from the loop it is in: each graph's owners, from one analysis of it.
  const analyses = new Map<VisualGraph, ReturnType<typeof analyzeGraph>>()
  const owner = (value: VisualGraph, node: GraphNode) => {
    let analysis = analyses.get(value)
    if (!analysis) analyses.set(value, (analysis = analyzeGraph(value, catalog)))
    return analysis.ownerOf(node)
  }
  const inputs = (value: VisualGraph, node: GraphNode) => dataInputs(node, owner(value, node), graphTypes(value, catalog))
  const misfit = (value: VisualGraph, edge: VisualGraph['edges'][number]) => {
    if (edge.kind !== 'data') return
    const types = graphTypes(value, catalog)
    const from = value.nodes.find(n => n.id === edge.source), to = value.nodes.find(n => n.id === edge.target)
    const output = from ? dataOutputs(from, types)[edge.sourcePort] : undefined
    const input = to ? connectable(inputs(value, to)[edge.targetPort]) : undefined
    const wrong = from && to && output && input ? wireTypeError(from, output, to, input, edge.targetPort, types) : undefined
    return wrong && !wrong.code?.startsWith('property.') ? to : undefined
  }
  // The nodes whose output the choice retypes: this one, and a Select property node reading one of them.
  const retyped = new Set([nodeId])
  for (let grew = true; grew; ) {
    grew = false
    for (const e of next.edges) if (e.kind === 'data' && retyped.has(e.source) && e.targetPort === VALUE && !retyped.has(e.target) && next.nodes.find(n => n.id === e.target)?.kind === node.kind) { retyped.add(e.target); grew = true }
  }
  // A wire that already did not fit stays: removing it is not what choosing a property is about.
  const before = new Set(graph.edges.filter(e => misfit(graph, e)).map(e => e.id))
  for (let found = true; found; ) {
    found = false
    for (const edge of next.edges) {
      if (edge.kind !== 'data' || !retyped.has(edge.source) || before.has(edge.id)) continue
      const to = misfit(next, edge)
      if (!to) continue
      removed.push(`${words.stepName(to, graphTypes(next, catalog))} · ${words.portLabel(inputs(next, to)[edge.targetPort], edge.targetPort)}`)
      next = removeEdge(next, edge.id, catalog)
      found = true
      break
    }
  }
  const count = removed.length === 1 ? '1 connection that no longer fits' : `${removed.length} connections that no longer fit`
  return { graph: next, ...(removed.length ? { notice: `Removed ${count}: ${[...new Set(removed)].join(', ')}.` } : {}) }
}

/**
 * The property choice of a Select property node, on its card (`compact`) and in the panel. Disabled until a value is
 * connected; a stored property the connected type does not have stays shown, marked missing. More than
 * {@link SEARCH_ABOVE} properties are searched. In the panel, the chosen property's type (friendly and C#) and its
 * description follow.
 */
export function PropertyPicker(props: {
  node: GraphNode
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  compact?: boolean
  disabled?: boolean
  errors?: string[]
  id?: string
  describedBy?: string
  onChoose(name: string): void
}) {
  const own = createUniqueId()
  const id = () => props.id ?? own
  const types = createMemo(() => graphTypes(props.graph, props.catalog))
  const choices = createMemo(() => propertyChoices(props.graph, props.node, props.catalog))
  const current = () => (typeof props.node.config.property === 'string' ? props.node.config.property : '')
  const chosen = () => choices().properties.find(p => p.name === current())
  // A stored name the connected type lacks: shown, and said to be missing (the validator's message says what to do).
  const missing = () => current() !== '' && choices().properties.length > 0 && !chosen()
  const empty = () => !choices().connected ? 'Connect a value to choose a property' : !choices().properties.length ? 'The connected value has no properties' : 'Choose a property'
  const disabled = () => props.disabled || !choices().properties.length
  const invalid = () => (props.errors?.length ?? 0) > 0 || missing()
  const label = () => (chosen() ? `Property, ${chosen()!.name}` : 'Property')
  let select: HTMLSelectElement | undefined
  // The chosen option follows the property once the options are in place (and again on undo or a new wire).
  createEffect(() => { choices(); const value = current(); if (select) select.value = value })
  const option = (p: PropertySpec) => propertyText(p, types())
  return (
    <div class="hs-select-property" classList={{ 'hs-select-property--compact': props.compact }}>
      <Show when={choices().properties.length > SEARCH_ABOVE && !disabled()} fallback={
        <select ref={select} id={id()} class="hs-input hs-select-property__select nodrag nopan" aria-label={props.compact ? label() : undefined}
          aria-describedby={props.describedBy} aria-invalid={invalid() || undefined} disabled={disabled()} title={chosen() ? option(chosen()!) : missing() ? `${current()} · missing` : empty()}
          onChange={event => { if (event.currentTarget.value) props.onChoose(event.currentTarget.value) }}>
          <Show when={current() === '' || !chosen()}>
            <option value={current()} selected>{current() === '' ? empty() : missing() ? `${current()} · missing` : current()}</option>
          </Show>
          <For each={choices().properties}>{p => <option value={p.name} selected={p.name === current()}>{option(p)}</option>}</For>
        </select>
      }>
        <SearchSelect id={id()} label="Property" value={current() || undefined} invalid={invalid()} describedBy={props.describedBy}
          placeholder={missing() ? `${current()} · missing` : 'Search properties…'}
          options={[...(missing() ? [{ value: current(), label: `${current()} · missing` }] : []),
            ...choices().properties.map(p => ({ value: p.name, label: p.name, detail: option(p).slice(p.name.length + 3) }))]}
          onSelect={value => { if (value && value !== current()) props.onChoose(value) }} />
      </Show>
      <Show when={!props.compact && chosen()}>
        {property => {
          const csharp = () => csharpType(property().type, types())
          return (
            <p class="hs-select-property__type">
              <span>{typeLabel(property().type, types())}{property().nullable ? ', may be empty' : ''}</span>
              <Show when={csharp()}>{text => <><span class="hs-visually-hidden">, C# type </span><code class="hs-csharp-type">{text()}</code></>}</Show>
              <Show when={property().description}>{text => <small class="hs-field-hint">{text()}</small>}</Show>
            </p>
          )
        }}
      </Show>
    </div>
  )
}
