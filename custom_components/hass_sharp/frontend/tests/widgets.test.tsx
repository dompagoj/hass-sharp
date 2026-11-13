import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import { HassCtx } from '../src/context'
import type { EntityInfo } from '../src/entities'
import { EntityAutocomplete } from '../src/components/EntityAutocomplete'
import { CatalogPicker } from '../src/components/CatalogPicker'
import { dataInputs, nodeCatalog, validLiteral, type GraphNode, type InputSpec, type Operand, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { InputEditor } from '../src/visual/InputEditor'
import { entityPredicate, literalFor, targetValue, widgetFor } from '../src/visual/widgets'

const catalog: VisualCatalogSnapshot = { ...nodeCatalog, services: servicesFixture as unknown as ServicesSection }
const serviceInputs = (service: string, fields: string[]) => {
  const node: GraphNode = { id: 's', kind: 'service', config: { service, fields: Object.fromEntries(fields.map(f => [f, { kind: 'unset' }])), returnResponse: false } }
  return dataInputs(node, undefined, catalog)
}
const field = (service: string, name: string) => serviceInputs(service, [name])[`field:${name}`]
const spec = (types: string[], extra: Partial<InputSpec> = {}): InputSpec => ({ types, required: false, description: '', ...extra })
const selector = (types: string[], value: Record<string, unknown>) => spec(types, { selector: value })

const entity = (entity_id: string, extra: Partial<EntityInfo> = {}): EntityInfo => ({
  entity_id, name: null, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null,
  supported_features: 0, unit: null, icon: null, disabled: false, state_options: null, ...extra,
})
const registry = {
  entities: [
    entity('light.hall', { name: 'Hall lamp' }), entity('light.porch', { name: 'Porch' }), entity('switch.fan', { name: 'Fan' }),
    entity('climate.outside_bathroom_vtrv', { name: 'Bathroom TRV', state_options: ['off', 'heat', 'auto'] }),
    entity('climate.living', { state_options: ['off', 'cool'] }),
  ],
  areas: [{ area_id: 'hall', name: 'Hall', floor_id: 'ground' }], devices: [{ id: 'dev1', name: 'Hub', area_id: null }],
  floors: [{ floor_id: 'ground', name: 'Ground' }], labels: [{ label_id: 'night', name: 'Night' }],
}
let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(() => { dispose?.(); dispose = undefined; client?.clear(); document.body.innerHTML = '' })
function mount(view: () => any) {
  const host = document.createElement('div'); document.body.append(host)
  client = new QueryClient()
  const callWS = vi.fn(async () => registry)
  dispose = render(() => <QueryClientProvider client={client!}><HassCtx.Provider value={{ callWS } as any}>{view()}</HassCtx.Provider></QueryClientProvider>, host)
  return { host, callWS }
}

describe('widgetFor', () => {
  it('maps every C3 selector row of the services fixture', () => {
    expect(widgetFor(field('hass_sharp_test.odd_texts', 'odd_label'), catalog)).toMatchObject({ kind: 'toggle' })
    expect(widgetFor(field('light.turn_on', 'brightness_pct'), catalog)).toEqual({ kind: 'number', multiple: false, slider: true, min: 0, max: 100, step: 1, unit: '%' })
    expect(widgetFor(field('light.turn_off', 'transition'), catalog)).toMatchObject({ kind: 'number', slider: true, unit: 'seconds' })
    expect(widgetFor(field('hass_sharp_test.sectioned', 'level'), catalog)).toMatchObject({ kind: 'number', slider: false, min: 1, max: 10, step: 1 })
    expect(widgetFor(field('hass_sharp_test.sectioned', 'note'), catalog)).toMatchObject({ kind: 'text', multiline: true })
    expect(widgetFor(field('notify.notify', 'message'), catalog)).toMatchObject({ kind: 'text', multiline: false })
    expect(widgetFor(field('hass_sharp_test.sectioned', 'speed'), catalog)).toMatchObject({ kind: 'select', options: [{ value: 'slow', label: 'Slow' }, { value: 'fast', label: 'fast' }] })
    expect(widgetFor(field('climate.set_hvac_mode', 'hvac_mode'), catalog)).toMatchObject({ kind: 'state', hideStates: ['unavailable', 'unknown'] })
    expect(widgetFor(field('light.turn_on', 'effect'), catalog)).toMatchObject({ kind: 'state', stateAttribute: 'effect' })
    expect(widgetFor(field('light.turn_on', 'rgb_color'), catalog)).toMatchObject({ kind: 'color' })
    expect(widgetFor(field('light.turn_on', 'color_temp_kelvin'), catalog)).toMatchObject({ kind: 'number', slider: true, min: 2000, max: 6500, unit: 'K' })
    expect(widgetFor(field('light.turn_on', 'white'), catalog)).toMatchObject({ kind: 'constant', options: [{ value: true, label: 'Enabled' }] })
    expect(widgetFor(field('light.turn_on', 'hs_color'), catalog)).toMatchObject({ kind: 'json' })
    expect(widgetFor(serviceInputs('light.turn_on', []).target, catalog)).toMatchObject({ kind: 'target', domains: ['light'] })
  })
  it('maps the C3 rows the fixture has no field for', () => {
    expect(widgetFor(selector(['entity_id:light'], { entity: { domain: 'light' } }))).toMatchObject({ kind: 'entity', multiple: false, domains: ['light'] })
    expect(widgetFor(selector(['entity_id', 'array'], { entity: { domain: ['light', 'switch'], multiple: true } }))).toMatchObject({ kind: 'entity', multiple: true, domains: [] })
    for (const name of ['device', 'area', 'floor', 'label']) {
      expect(widgetFor(selector(['string'], { [name]: {} }))).toEqual({ kind: name, multiple: false })
      expect(widgetFor(selector(['string', 'array'], { [name]: { multiple: true } }))).toEqual({ kind: name, multiple: true })
    }
    expect(widgetFor(selector(['target'], { target: { entity: { domain: 'cover' } } }))).toMatchObject({ kind: 'target' })
    expect(widgetFor(selector(['number', 'object'], { duration: { enable_day: true } }))).toEqual({ kind: 'duration', multiple: false, days: true })
    for (const name of ['time', 'date', 'datetime']) expect(widgetFor(selector(['string'], { [name]: {} }))).toEqual({ kind: name, multiple: false })
    expect(widgetFor(selector(['array'], { text: { multiple: true } }))).toMatchObject({ kind: 'text', multiple: true })
    expect(widgetFor(selector(['string'], { select: { custom_value: true, options: ['a', 'b'] } }))).toMatchObject({ kind: 'text', options: [{ value: 'a' }, { value: 'b' }] })
    expect(widgetFor(selector(['number'], { color_temp: { unit: 'mired' } }))).toMatchObject({ kind: 'number', slider: true, min: 153, max: 500, unit: 'mired' })
    for (const name of ['object', 'template', 'media', 'action', 'unknown_thing']) expect(widgetFor(selector(['object', 'array', 'string', 'number', 'boolean'], { [name]: {} })).kind).toBe('json')
  })
  it('falls back to constraints and types without a selector', () => {
    expect(widgetFor(spec(['string'], { enum: ['a', 'b'], enumLabels: ['Alpha', 'Beta'] }))).toEqual({ kind: 'select', multiple: false, options: [{ value: 'a', label: 'Alpha' }, { value: 'b', label: 'Beta' }] })
    expect(widgetFor(spec(['boolean'])).kind).toBe('toggle')
    expect(widgetFor(spec(['number'], { min: 0, max: 5, integer: true }))).toEqual({ kind: 'number', multiple: false, slider: false, min: 0, max: 5, step: 1 })
    expect(widgetFor(spec(['string'])).kind).toBe('text')
    expect(widgetFor(spec(['entity_id']))).toMatchObject({ kind: 'entity', domains: [] })
    expect(widgetFor(spec(['entity_id:light']))).toMatchObject({ kind: 'entity', domains: ['light'] })
    expect(widgetFor(spec(['target', 'entity_id:light']))).toMatchObject({ kind: 'target', domains: ['light'] })
    expect(widgetFor(spec(['target', 'entity_id']))).toMatchObject({ kind: 'target', domains: [] })
    expect(widgetFor(spec(['object', 'array'])).kind).toBe('json')
    expect(widgetFor(spec(Object.keys(nodeCatalog.types))).kind).toBe('json')
    expect(widgetFor(spec(['global::HassSharp.EntityStateSnapshot'])).kind).toBe('none')
  })
})

describe('literalFor', () => {
  const valid = (input: InputSpec, operand: Operand) => operand.kind === 'literal' && validLiteral(operand.type, operand.value) && input.types.includes(operand.type)
  it('authors an entity_id:light input with that exact type', () => {
    const input = spec(['entity_id:light'])
    expect(literalFor(input, 'light.hall')).toEqual({ kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
    expect(valid(input, literalFor(input, 'light.hall'))).toBe(true)
    // No accepted type takes a switch: keep the shaped type so the validator can explain it.
    expect(literalFor(input, 'switch.fan').type).toBe('entity_id:light')
  })
  it('chooses the first accepted type the value is valid for', () => {
    const input = spec(['target', 'entity_id:light'])
    expect(literalFor(input, 'light.hall')).toEqual({ kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
    expect(literalFor(input, { entity_id: ['light.hall'], area_id: 'hall' })).toMatchObject({ type: 'target' })
    expect(literalFor(spec(['target', 'entity_id:light', 'entity_id:switch']), 'switch.fan').type).toBe('entity_id:switch')
    expect(literalFor(spec(['entity_id:light', 'array']), ['light.hall', 'light.porch']).type).toBe('array')
    expect(literalFor(spec(['number', 'object']), 90).type).toBe('number')
    expect(literalFor(spec(['number', 'object']), { minutes: 1 }).type).toBe('object')
    expect(literalFor(spec(Object.keys(nodeCatalog.types)), 'light.hall').type).toBe('string')
  })
})

describe('target values', () => {
  it('keeps the shape a target already has and uses the object form for new values', () => {
    const ids = (entity_id: string[], extra: Record<string, string[]> = {}) => ({ entity_id, device_id: [], area_id: [], floor_id: [], label_id: [], ...extra })
    expect(targetValue('light.hall', ids(['light.porch']))).toBe('light.porch')
    expect(targetValue('light.hall', ids(['light.hall', 'light.porch']))).toEqual({ entity_id: ['light.hall', 'light.porch'] })
    expect(targetValue(undefined, ids(['light.hall']))).toEqual({ entity_id: ['light.hall'] })
    expect(targetValue({ entity_id: 'light.hall' }, ids(['light.hall'], { area_id: ['hall'] }))).toEqual({ entity_id: 'light.hall', area_id: ['hall'] })
    expect(targetValue({ entity_id: ['light.hall'] }, ids([]))).toBeUndefined()
  })
})

describe('pickers', () => {
  it('offers only entities passing the filter, and keeps the default picker unchanged', async () => {
    const [value, setValue] = createSignal('')
    const { host } = mount(() => <>
      <EntityAutocomplete value={value()} onChange={setValue} label="Lamp" filter={entityPredicate(widgetFor(spec(['entity_id:light'])))} />
      <EntityAutocomplete value="" onChange={() => {}} />
    </>)
    const [filtered, plain] = host.querySelectorAll<HTMLButtonElement>('.hs-picker-field')
    expect(filtered.getAttribute('aria-label')).toBe('Lamp')
    expect(plain.getAttribute('aria-label')).toBe('Entity ID')
    filtered.click()
    await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]')).toHaveLength(2))
    expect([...host.querySelectorAll('dialog[open] [role="option"] small')].map(e => e.textContent)).toEqual(['light.hall', 'light.porch'])
    host.querySelector<HTMLButtonElement>('dialog[open] [role="option"]')!.click()
    expect(value()).toBe('light.hall')
    plain.click()
    await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]')).toHaveLength(registry.entities.length))
  })
  it('respects the selector filter (device class, features) as well as domains', () => {
    const accepts = entityPredicate(widgetFor(selector(['entity_id'], { entity: { domain: 'light', supported_features: 4 } })))
    expect([entity('light.a', { supported_features: 4 }), entity('light.b'), entity('switch.c', { supported_features: 4 })].filter(accepts).map(e => e.entity_id)).toEqual(['light.a'])
  })
  it('selects several items when multiple, keeping the dialog open', async () => {
    const [values, setValues] = createSignal<string[]>(['light.porch'])
    const items = registry.entities.map(e => ({ id: e.entity_id, name: e.name ?? undefined }))
    const { host } = mount(() => <CatalogPicker kind="entities" items={items} loading={false} error={false} refresh={() => {}} multiple values={values()} onChangeValues={setValues}
      filter={item => item.id.startsWith('light.')} />)
    host.querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
    expect(host.querySelector('[role="listbox"]')!.getAttribute('aria-multiselectable')).toBe('true')
    const options = () => [...host.querySelectorAll<HTMLButtonElement>('dialog[open] [role="option"]')]
    expect(options()).toHaveLength(2)
    options()[0].click()
    expect(values()).toEqual(['light.porch', 'light.hall'])
    expect(host.querySelector('dialog[open]')).not.toBeNull()
    options()[1].click()
    expect(values()).toEqual(['light.hall'])
    expect(options().map(o => o.getAttribute('aria-selected'))).toEqual(['true', 'false'])
  })
  it('state select offers the target entity state options (climate.set_hvac_mode)', async () => {
    const changes: Operand[] = []
    const input = field('climate.set_hvac_mode', 'hvac_mode')
    const { host } = mount(() => <InputEditor input={input} value={undefined} onChange={o => changes.push(o)} variant="panel" label="HVAC mode"
      catalog={catalog} context={{ targetEntityIds: ['climate.outside_bathroom_vtrv'] }} />)
    await vi.waitFor(() => expect(host.querySelector('select[aria-label="HVAC mode"]')).not.toBeNull())
    const select = host.querySelector<HTMLSelectElement>('select[aria-label="HVAC mode"]')!
    expect([...select.options].filter(o => !o.disabled).map(o => o.textContent)).toEqual(['off', 'heat', 'auto'])
    select.value = '1'; select.dispatchEvent(new Event('change', { bubbles: true }))
    expect(changes).toEqual([{ kind: 'literal', type: 'string', value: 'heat' }])
  })
  it('state widget is free text without target entities or options', async () => {
    const { host } = mount(() => <InputEditor input={field('climate.set_hvac_mode', 'hvac_mode')} value={undefined} onChange={() => {}} variant="panel" label="HVAC mode" catalog={catalog} />)
    await Promise.resolve()
    expect(host.querySelector('input[aria-label="HVAC mode"]')).not.toBeNull()
    expect(host.querySelector('select[aria-label="HVAC mode"]')).toBeNull()
  })
})
