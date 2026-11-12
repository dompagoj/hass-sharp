import { useEntities } from '../entities'
import { CatalogPicker } from './CatalogPicker'
export function EntityAutocomplete(props: { value: string; onChange: (value: string) => void; optional?: boolean }) {
  const entities = useEntities()
  return <CatalogPicker {...props} clearLabel={props.optional ? 'No target' : undefined} kind="entities" items={(entities.data ?? []).map(item => ({ id: item.entity_id, name: item.friendly_name }))} loading={entities.isFetching} error={entities.isError} refresh={() => entities.refetch()} />
}
