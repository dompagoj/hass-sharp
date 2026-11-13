import type { ServicesSection } from '../visual/contract'
import { useServiceText, type ServiceText } from '../visual/serviceText'
import { CatalogPicker, type CatalogItem } from './CatalogPicker'

/** The services of the C4 services section as picker items: translated name, id and description. */
export function serviceItems(services: ServicesSection | undefined, text?: Pick<ServiceText, 'serviceName' | 'serviceDescription'>): CatalogItem[] {
  const items = services?.items ?? {}
  return Object.keys(items).sort().map(id => ({
    id,
    name: text ? text.serviceName(id, items[id]) : items[id].name || undefined,
    description: text ? text.serviceDescription(id, items[id]) || undefined : items[id].description || undefined,
  }))
}

/**
 * Chooses a service from the services section the editor already holds (contract C4): name, id and domain icon.
 * Without a section (or for a service Home Assistant does not have) an exact id can still be typed.
 */
export function ServicePicker(props: {
  value: string
  onChange: (value: string) => void
  services?: ServicesSection
  /** Asks for a fresh services section when the dialog opens; optional. */
  onRefresh?: () => unknown
  id?: string
  describedBy?: string
  invalid?: boolean
  disabled?: boolean
}) {
  const text = useServiceText()
  const items = () => serviceItems(props.services, text)
  const current = () => {
    const schema = props.value && props.services?.items && Object.hasOwn(props.services.items, props.value) ? props.services.items[props.value] : undefined
    return schema ? `${text.serviceName(props.value, schema)} (${props.value})` : undefined
  }
  return <CatalogPicker value={props.value} onChange={props.onChange} kind="services" items={items()} loading={false} error={false}
    refresh={() => props.onRefresh?.()} buttonText={current()} id={props.id} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />
}
