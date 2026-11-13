import { createSignal, onMount } from 'solid-js'
import { useHass } from '../context'
import type { InputSpec, ServiceSchema } from './contract'
import { serviceFieldLabel } from './nodeSummary'
import { domainWords } from './typeLabels'

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
