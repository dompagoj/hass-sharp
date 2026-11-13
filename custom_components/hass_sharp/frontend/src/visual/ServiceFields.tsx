import { createMemo, createSignal, For, onMount, Show, type JSX } from 'solid-js'
import { useHass } from '../context'
import { ServicePicker } from '../components/ServicePicker'
import { dataInputs, type GraphNode, type InputSpec, type Operand, type ServiceFieldSchema, type ServiceSchema, type VisualCatalogSnapshot } from './contract'
import type { VisualGraph } from './graph'
import { serviceFieldLabel } from './nodeSummary'
import { domainWords } from './typeLabels'
import { SearchSelect } from './widgets/SearchSelect'

type Localize = (key: string, ...args: unknown[]) => string
type Translating = { localize?: Localize; loadBackendTranslation?: (category: string, integrations?: string | string[]) => Promise<unknown> }
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const split = (id: string) => { const dot = id.indexOf('.'); return dot > 0 ? [id.slice(0, dot), id.slice(dot + 1)] : [id, id] }

export interface ServiceText {
  serviceName(id: string, schema?: ServiceSchema): string
  serviceDescription(id: string, schema?: ServiceSchema): string
  fieldLabel(serviceId: string, field: string, schema?: ServiceSchema): string
  fieldDescription(serviceId: string, field: string, schema?: ServiceSchema): string
  sectionName(serviceId: string, section: string): string
  /** The field's input spec with translated select option labels (`selector.select.translation_key`). */
  localizeSpec(serviceId: string, spec: InputSpec): InputSpec
}

/**
 * Names of services, fields, sections and select options as Home Assistant shows them: `hass.localize` after
 * `loadBackendTranslation('services')` where the `hass` object offers it, else the services section's own text
 * (tests and the preview have no `localize`). Field labels fall back to `serviceFieldLabel`, as node cards do.
 */
export function useServiceText(): ServiceText {
  const hass = useHass() as unknown as Translating | undefined
  const [localize, setLocalize] = createSignal<Localize | undefined>(typeof hass?.localize === 'function' ? hass.localize.bind(hass) : undefined)
  onMount(() => {
    const load = hass?.loadBackendTranslation
    if (typeof load !== 'function') return
    try {
      // Home Assistant resolves to the localize function that includes the loaded category.
      Promise.resolve(load.call(hass, 'services')).then(fn => { if (typeof fn === 'function') setLocalize(() => fn as Localize) }, () => {})
    } catch {
      // Names fall back to the services section.
    }
  })
  const t = (key: string) => {
    const f = localize()
    if (!f) return ''
    try {
      const value = f(key)
      return typeof value === 'string' && value !== key ? value.trim() : ''
    } catch {
      return ''
    }
  }
  const base = (id: string) => { const [d, s] = split(id); return `component.${d}.services.${s}` }
  return {
    serviceName: (id, schema) => {
      const [, s] = split(id)
      const own = schema?.name?.trim()
      return t(`${base(id)}.name`) || (own && own !== s ? own : domainWords(s))
    },
    serviceDescription: (id, schema) => t(`${base(id)}.description`) || schema?.description?.trim() || '',
    fieldLabel: (serviceId, field, schema) =>
      t(`${base(serviceId)}.fields.${field}.name`) || serviceFieldLabel(serviceId, field, schema ? { servicesGeneration: '', items: { [serviceId]: schema } } : undefined),
    fieldDescription: (serviceId, field, schema) => {
      const own = schema && Object.hasOwn(schema.fields, field) ? schema.fields[field].description?.trim() : ''
      return t(`${base(serviceId)}.fields.${field}.description`) || own || ''
    },
    sectionName: (serviceId, section) => t(`${base(serviceId)}.sections.${section}.name`) || domainWords(section),
    localizeSpec: (serviceId, spec) => {
      const select = record(spec.selector) && record(spec.selector.select) ? spec.selector.select : undefined
      const key = typeof select?.translation_key === 'string' ? select.translation_key : ''
      if (!key || !Array.isArray(spec.enum)) return spec
      const [d] = split(serviceId)
      const labels = spec.enum.map((value, i) => t(`component.${d}.selector.${key}.options.${String(value)}`) || spec.enumLabels?.[i] || String(value))
      return { ...spec, enumLabels: labels }
    },
  }
}

/** Fields in Home Assistant's display order (C4 `order`), with advanced fields last. */
export function orderedFields(schema: ServiceSchema): [string, ServiceFieldSchema][] {
  return Object.entries(schema.fields ?? {}).filter(([, f]) => record(f))
    .sort(([, a], [, b]) => Number(!!a.advanced) - Number(!!b.advanced) || (a.order ?? 0) - (b.order ?? 0))
}

/** What the inspector's input row needs beyond the port: overrides for rows of service fields. */
export interface FieldRow {
  label?: string
  description?: string
  /** The spec to edit with, for a field that is not stored yet or a target filtered by the service. */
  spec?: InputSpec
  /** The value of a field that is not stored yet, and where its first change goes. */
  value?: Operand
  onChange?(operand: Operand, options?: { coalesce?: string }): void
  onRemove?(): void
  removeLabel?: string
  /** Why the field cannot be removed (it is connected); undefined when it can. */
  removeBlocked?: string
}

const unset: Operand = { kind: 'unset' }

/**
 * The main section of a Call service node: the service, its target (filtered to what the service accepts), its
 * fields (required ones always present, optional ones added with "+ Add field" and removable) and "Use the
 * response". Changing the service keeps the fields the new service also has and adds its required ones. The panel
 * never connects or disconnects anything: a connected field is kept and cannot be removed here.
 */
export function ServiceFields(props: {
  node: GraphNode
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  onChange(config: GraphNode['config'], options?: { coalesce?: string }): void
  /** The input row for a port, the same one every other input uses. */
  row(port: string, extra: FieldRow): JSX.Element
  /** Focus the row of a port once it is shown. */
  focus(port: string): void
}) {
  const text = useServiceText()
  const serviceId = () => (typeof props.node.config.service === 'string' ? props.node.config.service : '')
  const items = () => props.catalog.services?.items
  const schema = createMemo(() => { const all = items(), id = serviceId(); return all && id && Object.hasOwn(all, id) ? all[id] : undefined })
  const fields = () => (record(props.node.config.fields) ? props.node.config.fields : {})
  const connected = (port: string) => props.graph.edges.some(e => e.kind === 'data' && e.target === props.node.id && e.targetPort === port)
  const responseConnected = () => props.graph.edges.some(e => e.kind === 'data' && e.source === props.node.id && e.sourcePort === 'response')
  /** The input spec a field would have once stored (its constraints and default, C4/C5). */
  const specOf = (key: string, service = serviceId()): InputSpec | undefined => {
    const probe: GraphNode = { ...props.node, config: { ...props.node.config, service, fields: { ...fields(), [key]: unset } } }
    return dataInputs(probe, undefined, props.catalog)[`field:${key}`]
  }
  const startValue = (key: string, service?: string): Operand => {
    const fallback = specOf(key, service)?.default
    return record(fallback) && fallback.kind === 'literal' ? structuredClone(fallback) as Operand : unset
  }
  const write = (next: Record<string, unknown>, options?: { coalesce?: string }) => props.onChange({ ...props.node.config, fields: next }, options)

  const chooseService = (id: string) => {
    if (id === serviceId()) return
    const next: Record<string, unknown> = { ...props.node.config, service: id }
    const target = items() && Object.hasOwn(items()!, id) ? items()![id] : undefined
    if (target) {
      const kept: Record<string, unknown> = {}
      // A field the new service also has keeps its value; a connected one stays until its wire is removed on the canvas.
      for (const [key, value] of Object.entries(fields())) if (Object.hasOwn(target.fields, key) || connected(`field:${key}`)) kept[key] = value
      for (const [key, field] of orderedFields(target)) if (field.required && !Object.hasOwn(kept, key)) kept[key] = startValue(key, id)
      next.fields = kept
      if (target.response === 'only') next.returnResponse = true
      else if (target.response === 'none' && !responseConnected()) next.returnResponse = false
    }
    props.onChange(next)
  }

  const known = () => schema()
  const label = (key: string) => text.fieldLabel(serviceId(), key, schema())
  const stored = createMemo(() => {
    const keys = Object.keys(fields()), s = schema()
    if (!s) return keys
    const order = orderedFields(s).map(([key]) => key)
    return [...order.filter(key => keys.includes(key)), ...keys.filter(key => !order.includes(key))]
  }, undefined, { equals: (a, b) => a.length === b.length && a.every((v, i) => v === b[i]) })
  // Required fields of a known service that are not stored yet (a graph saved before the field became required).
  const missing = createMemo(() => { const s = schema(); return s ? orderedFields(s).filter(([key, f]) => f.required && !Object.hasOwn(fields(), key)).map(([key]) => key) : [] },
    undefined, { equals: (a, b) => a.length === b.length && a.every((v, i) => v === b[i]) })
  const addable = () => {
    const s = schema()
    if (!s) return []
    return orderedFields(s).filter(([key]) => !Object.hasOwn(fields(), key)).map(([key, field]) => ({
      value: key,
      label: label(key),
      detail: key,
      group: field.advanced ? 'Advanced' : field.section ? text.sectionName(serviceId(), field.section) : undefined,
    }))
  }
  const add = (key: string) => {
    write({ ...fields(), [key]: startValue(key) })
    props.focus(`field:${key}`)
  }
  const remove = (key: string) => {
    const next = { ...fields() }
    delete next[key]
    write(next)
  }
  const localized = (key: string) => { const spec = specOf(key); return spec && schema() ? text.localizeSpec(serviceId(), spec) : undefined }
  // Getters keep each row reactive: the same field of another service gets that service's label and constraints.
  const fieldRow = (key: string) => {
    const port = `field:${key}`, required = () => !!schema()?.fields[key]?.required
    return props.row(port, {
      get label() { return label(key) },
      get description() { return text.fieldDescription(serviceId(), key, schema()) },
      get spec() { return localized(key) },
      get onRemove() { return required() ? undefined : () => remove(key) },
      get removeLabel() { return `Remove ${label(key)}` },
      get removeBlocked() { return connected(port) ? 'Remove its connection on the canvas first' : undefined },
    })
  }
  const showTarget = () => {
    const s = schema(), target = props.node.config.target
    return !s || !!s.target || connected('target') || (record(target) && target.kind !== 'unset')
  }
  const targetSpec = (): InputSpec | undefined => {
    const spec = dataInputs(props.node, undefined, props.catalog).target
    const filter = schema()?.target
    // The service's own target filter (domains, device classes, features) narrows the entity choices.
    return spec && filter ? { ...spec, selector: { target: filter } } : spec
  }
  const response = () => schema()?.response
  const returning = () => props.node.config.returnResponse === true

  return (
    <div class="hs-insp-service">
      <div class="hs-insp-field" data-input-id="service">
        <ServicePicker value={serviceId()} services={props.catalog.services} onChange={chooseService} />
        <Show when={known() && text.serviceDescription(serviceId(), schema())}>{description => <small class="hs-field-hint">{description()}</small>}</Show>
      </div>
      <Show when={serviceId() && items() && !known()}>
        <p class="hs-insp-note hs-insp-note--warning" role="note">
          {serviceId()} is not available in this Home Assistant. Its fields are kept as they are and can still be edited.
        </p>
      </Show>
      <Show when={showTarget()}>{props.row('target', { get spec() { return targetSpec() } })}</Show>
      <For each={stored()}>{key => fieldRow(key)}</For>
      <For each={missing()}>
        {key => props.row(`field:${key}`, {
          get label() { return label(key) },
          get description() { return text.fieldDescription(serviceId(), key, schema()) },
          get spec() { return localized(key) },
          value: unset,
          onChange: (operand, options) => write({ ...fields(), [key]: operand }, options),
        })}
      </For>
      <Show when={addable().length}>
        <div class="hs-insp-add">
          <SearchSelect label="Add field" placeholder="+ Add field…" clearOnSelect options={addable()} onSelect={add} />
        </div>
      </Show>
      <Show when={response() === 'only' || response() === 'optional' || returning() || (serviceId() && !known())}>
        <label class="hs-insp-toggle">
          <input type="checkbox" role="switch" class="hs-switch" checked={response() === 'only' || returning()}
            disabled={response() === 'only' || (returning() && responseConnected())}
            onChange={event => {
              const next = { ...props.node.config }
              if (event.currentTarget.checked) next.returnResponse = true
              else next.returnResponse = false
              props.onChange(next)
            }} />
          <span>
            <strong>Use the response</strong>
            <small class="hs-field-hint">
              {response() === 'only'
                ? 'This service always answers, so its Response output is always there.'
                : returning() && responseConnected()
                  ? 'The Response output is connected; remove that connection on the canvas to turn this off.'
                  : 'Adds a Response output with what the service answers.'}
            </small>
          </span>
        </label>
      </Show>
    </div>
  )
}
