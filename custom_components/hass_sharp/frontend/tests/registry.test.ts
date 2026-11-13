import { expect, it, vi } from 'vitest'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import type { EntityInfo } from '../src/entities'
import { createRegistry, entityFilter, registryQuery } from '../src/registry'
import type { ServicesSection } from '../src/visual/contract'

const entity = (entity_id: string, extra: Partial<EntityInfo> = {}): EntityInfo => ({
  entity_id, name: null, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null,
  supported_features: null, unit: null, icon: null, disabled: false, state_options: null, ...extra,
})
const entities = [
  entity('light.hall', { supported_features: 44 }),
  entity('light.porch', { supported_features: 0 }),
  entity('binary_sensor.door', { device_class: 'door' }),
  entity('binary_sensor.motion', { device_class: 'motion' }),
  entity('switch.fan'),
]
const ids = (input: unknown) => entities.filter(entityFilter(input)).map(e => e.entity_id)

it('filters entities by domain, device class and supported feature bits', () => {
  expect(ids({ entity: [{ domain: ['light'], deviceClass: [], supportedFeatures: [] }], device: [] })).toEqual(['light.hall', 'light.porch'])
  expect(ids({ entity: { domain: 'binary_sensor', device_class: 'door' } })).toEqual(['binary_sensor.door'])
  expect(ids({ entity: { device_class: ['door', 'motion'] } })).toEqual(['binary_sensor.door', 'binary_sensor.motion'])
  // 44 = 4 | 8 | 32: any listed bit matches, as in Home Assistant.
  expect(ids({ supportedFeatures: [16, 32] })).toEqual(['light.hall'])
  expect(ids({ supportedFeatures: [16] })).toEqual([])
  // A multi-bit value matches on any shared bit (Home Assistant frontend): 48 = 16 | 32 shares 32 with 44; 3 shares none.
  expect(ids({ supportedFeatures: [48] })).toEqual(['light.hall'])
  expect(ids({ supportedFeatures: [3] })).toEqual([])
  expect(ids({ supported_features: [3, 48] })).toEqual(['light.hall'])
  expect(ids({ target: { entity: { domain: 'light', supported_features: 4 } } })).toEqual(['light.hall'])
})
it('accepts any alternative and treats a missing or unrestricted filter as no filter', () => {
  expect(ids({ entity: { filter: [{ domain: 'switch' }, { device_class: 'motion' }] } })).toEqual(['binary_sensor.motion', 'switch.fan'])
  expect(ids({ entity: [{ domain: ['switch'] }, { domain: [] }] })).toHaveLength(entities.length)
  for (const input of [null, undefined, {}, { entity: [] }, { entity: {} }, { number: { min: 0 } }]) expect(ids(input)).toHaveLength(entities.length)
})
it('applies a service target from the services section', () => {
  const items = (servicesFixture as unknown as ServicesSection).items
  const service = Object.values(items).find(s => s.target?.entity.some(f => f.domain.length))!
  const domains = service.target!.entity.flatMap(f => f.domain)
  expect(entities.filter(entityFilter(service.target)).every(e => domains.includes(e.domain))).toBe(true)
})
it('normalises the registry reply and shares one get_entities request', async () => {
  const registry = createRegistry({ entities, areas: [{ area_id: 'hall', name: 'Hall', floor_id: 'ground' }], floors: [{ floor_id: 'ground', name: 'Ground' }] })
  expect(registry.area('hall')?.name).toBe('Hall')
  expect(registry.floor('ground')?.name).toBe('Ground')
  expect([registry.devices, registry.labels, registry.device('x'), registry.label('y')]).toEqual([[], [], undefined, undefined])
  const callWS = vi.fn(async () => ({ entities: [entity('light.hall')] }))
  const loaded = await registryQuery({ callWS } as any).queryFn()
  expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/get_entities' })
  expect(loaded.entities.map(e => e.entity_id)).toEqual(['light.hall'])
  expect(loaded.areas).toEqual([])
})
