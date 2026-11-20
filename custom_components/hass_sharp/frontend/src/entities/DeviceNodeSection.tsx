import { For, Show, type JSX } from 'solid-js'
import { declaredDevices, entityNodeName, type GraphNode } from '../visual/contract'
import type { VisualGraph } from '../visual/graph'
import { keyProblem, keyRule } from './definitionEdits'
import { deviceFields, PartOf, type PartOfParents, type PartOfVia } from './DeviceForm'
import { ReadOnlyField, TextField } from './formFields'
import type { StepPickerContext } from './NodePickers'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown) => (typeof value === 'string' ? value : '')
/** Under a key Home Assistant has. */
const savedKeyHint = "The key is part of the device's identity in Home Assistant and cannot change."
/** Part of's choice of another Device node of the definition, picked from the list under it. */
const localChoice = 'local'

/** A field of the section, under the config key that a problem names (`data-input-id`), so the problem's row focuses it. */
const At = (props: { id: string; children: JSX.Element }) => <div data-input-id={props.id}>{props.children}</div>

/** The graph with the device key `from` renamed `to` on the Device node `nodeId`, and on each other Device node that is part of it. */
function renameDeviceKey(graph: VisualGraph, nodeId: string, from: string, to: string, devices: readonly string[]): VisualGraph {
  return {
    ...graph,
    nodes: graph.nodes.map((node: GraphNode) => {
      if (node.id === nodeId) return { ...node, config: { ...node.config, key: to } }
      const via = node.config.via
      return from && devices.includes(node.id) && record(via) && via.device === from ? { ...node, config: { ...node.config, via: { ...via, device: to } } } : node
    }),
  }
}

/**
 * The right panel's section of a Device node: its key (read only once the saved document has that device), the details
 * Home Assistant's device page shows, and what it is part of: nothing, another Device node of this definition (by its
 * key, so one without a key cannot be chosen), or a Home Assistant device other than the ones this definition's own
 * entities are on. Its name is the inspector's header. A field cleared is left out of the node's config, never stored blank.
 */
export function DeviceNodeSection(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const config = () => ctx.node().config
  const key = () => text(config().key)
  const set = (field: string, value: unknown) => {
    const next = { ...config() }
    if (value === undefined) delete next[field]
    else next[field] = value
    ctx.onChange(next)
  }
  // The key no longer changes once Home Assistant has the device.
  const fixed = () => key() !== '' && !!ctx.saved?.()?.deviceKeys.includes(key())
  const others = () => declaredDevices(ctx.graph(), ctx.catalog()).filter(d => d.nodeId !== ctx.node().id)
  const via = (): PartOfVia | null => (record(config().via) ? config().via as PartOfVia : null)
  const nodeName = (nodeId: string) => { const node = ctx.graph().nodes.find(n => n.id === nodeId); return (node && entityNodeName(node, ctx.catalog())) ?? '' }
  /** The other Device nodes that have a key, each by its name (else its key), in node order. */
  const keyed = () => others().flatMap(d => (text(d.device.key) ? [{ key: text(d.device.key), name: nodeName(d.nodeId) }] : []))
  /** The device a key names, as the validators read it (the first Device node of that key): its name, else the key. */
  const deviceName = (key: string) => keyed().find(d => d.key === key)?.name || key
  const options = () => keyed().map(d => ({ value: d.key, label: d.name || d.key, detail: d.key }))
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: 'base' }) || a.value.localeCompare(b.value))
  const parents: PartOfParents = {
    choices: () => [{ value: localChoice, label: 'Another device of this definition' }],
    chosen: current => (typeof current?.device === 'string' ? localChoice : undefined),
    named: current => deviceName(text(current.device)),
    list: {
      choice: localChoice, field: 'viaLocal', label: 'Device of this definition',
      options: () => {
        // A key no other device has stays shown as chosen, so the problem about it can be acted on.
        const current = text(via()?.device)
        return current && !options().some(o => o.value === current) ? [{ value: current, label: `Missing device: ${current}` }, ...options()] : options()
      },
      value: current => (typeof current?.device === 'string' ? current.device : undefined),
      via: device => ({ device }),
    },
  }
  const ownEntityIds = () => Object.values(ctx.saved?.()?.entityIds ?? {})
  return (
    <>
      <At id="key">
        <Show when={!fixed()} fallback={<ReadOnlyField name="key" label="Key" text={key()} hint={savedKeyHint} />}>
          <TextField name="key" label="Key" text={key()} hint={`${keyRule} Devices that are part of it follow it. It cannot change once saved.`} errors={ctx.errorsFor('key')}
            commit={value => {
              if (value === key()) return
              const problem = keyProblem(value, others().map(d => text(d.device.key)))
              if (problem) return problem
              ctx.onGraphChange(renameDeviceKey(ctx.graph(), ctx.node().id, key(), value, others().map(d => d.nodeId)))
            }} />
        </Show>
      </At>
      <For each={deviceFields}>
        {field => (
          <At id={field.member}>
            <TextField name={field.member} label={field.label} hint={field.hint} type={field.type} placeholder="Not set" text={text(config()[field.member])}
              errors={ctx.errorsFor(field.member)} commit={value => set(field.member, value.trim() || undefined)} />
          </At>
        )}
      </For>
      <At id="via">
        <PartOf via={via()} ownEntityIds={ownEntityIds()} parents={parents} errors={ctx.errorsFor('via')} onChange={next => set('via', next ?? undefined)} />
      </At>
    </>
  )
}
