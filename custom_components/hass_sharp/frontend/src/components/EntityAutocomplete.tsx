import { splitProps } from 'solid-js'
import { useEntities, type EntityInfo } from '../entities'
import { CatalogPicker, type CatalogPickerProps } from './CatalogPicker'
type PickerProps = Omit<CatalogPickerProps, 'kind' | 'items' | 'loading' | 'error' | 'refresh' | 'filter' | 'clearLabel'>
/** `filter` limits the offered entities (for example `entityFilter(selector)`); `multiple` selects a list. */
export function EntityAutocomplete(props: PickerProps & { optional?: boolean; filter?: (entity: EntityInfo) => boolean }) {
  const [local, rest] = splitProps(props, ['optional', 'filter'])
  const entities = useEntities()
  const items = () => (entities.data ?? []).filter(item => !local.filter || local.filter(item)).map(item => ({ id: item.entity_id, name: item.name ?? undefined }))
  return <CatalogPicker {...rest} clearLabel={local.optional ? 'No target' : undefined} kind="entities" items={items()} loading={entities.isFetching} error={entities.isError} refresh={() => entities.refetch()} />
}
