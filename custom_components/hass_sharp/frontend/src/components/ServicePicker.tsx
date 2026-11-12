import { useQuery } from '@tanstack/solid-query'
import { useHass } from '../context'
import { CatalogPicker, type CatalogItem } from './CatalogPicker'

export type ServiceCatalog = Record<string, Record<string, { name?: string; description?: string }>>
export function serviceItems(catalog: ServiceCatalog): CatalogItem[] {
  return Object.entries(catalog).flatMap(([domain, services]) => Object.entries(services).map(([service, metadata]) => ({
    id: `${domain}.${service}`, name: metadata.name, description: metadata.description,
  })))
}
export function ServicePicker(props: { value: string; onChange: (value: string) => void }) {
  const hass = useHass()
  const services = useQuery(() => ({
    queryKey: ['hass-services'],
    queryFn: async () => serviceItems(await hass.callWS<ServiceCatalog>({ type: 'get_services' })),
    staleTime: Infinity,
    retry: false,
  }))
  return <CatalogPicker {...props} kind="services" items={services.data ?? []} loading={services.isFetching}
    error={services.isError} refresh={() => services.refetch()} />
}
