import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import { HassCtx } from '../src/context'
import type { EntityInfo } from '../src/entities'
import { createRegistry } from '../src/registry'
import { EntityAutocomplete } from '../src/components/EntityAutocomplete'
import { CatalogPicker } from '../src/components/CatalogPicker'
import { createGraph, updateNode } from '../src/visual/graph'
import { dataInputs, nodeCatalog, validateGraph, validLiteral, type GraphNode, type InputSpec, type JsonValue, type Operand, type ServiceFieldSchema, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { InputEditor } from '../src/visual/InputEditor'
import { typeLabel } from '../src/visual/typeLabels'
import {
  commitLiteral, durationValue, entityPredicate, initialValue, inlineWidget, jsonEqual, literalFor, manualEntityError, registryNames, targetValue, valueSummary, widgetFor,
  type ValueKind, type Widget,
} from '../src/visual/widgets'

const catalog: VisualCatalogSnapshot = { ...nodeCatalog, services: servicesFixture as unknown as ServicesSection }
const serviceInputs = (service: string, fields: string[], snapshot = catalog) => {
  const node: GraphNode = { id: 's', kind: 'service', config: { service, fields: Object.fromEntries(fields.map(f => [f, { kind: 'unset' }])), returnResponse: false } }
  return dataInputs(node, undefined, snapshot)
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
  areas: [{ area_id: 'hall', name: 'Hall', floor_id: 'ground' }, { area_id: 'attic', name: 'Attic', floor_id: null }], devices: [{ id: 'dev1', name: 'Hub', area_id: null }],
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
    // An object selector accepts every simple kind: a kind switch that starts at "List or object".
    expect(widgetFor(field('light.turn_on', 'hs_color'), catalog)).toEqual({ kind: 'mixed', multiple: false, kinds: ['text', 'number', 'boolean', 'json'], defaultKind: 'json' })
    expect(widgetFor(serviceInputs('light.turn_on', []).target, catalog)).toMatchObject({ kind: 'target', domains: ['light'], entityString: true })
  })
  it('maps the C3 rows the fixture has no field for', () => {
    expect(widgetFor(selector(['entity_id:light'], { entity: { domain: 'light' } }))).toMatchObject({ kind: 'entity', multiple: false, domains: ['light'] })
    expect(widgetFor(selector(['entity_id', 'array'], { entity: { domain: ['light', 'switch'], multiple: true } }))).toMatchObject({ kind: 'entity', multiple: true, domains: [] })
    for (const name of ['device', 'area', 'floor', 'label']) {
      expect(widgetFor(selector(['string'], { [name]: {} }))).toEqual({ kind: name, multiple: false })
      expect(widgetFor(selector(['string', 'array'], { [name]: { multiple: true } }))).toEqual({ kind: name, multiple: true })
    }
    expect(widgetFor(selector(['target'], { target: { entity: { domain: 'cover' } } }))).toMatchObject({ kind: 'target', entityString: false })
    expect(widgetFor(selector(['number', 'object'], { duration: { enable_day: true } }))).toEqual({ kind: 'duration', multiple: false, days: true, objectForm: true })
    expect(widgetFor(selector(['number'], { duration: {} }))).toEqual({ kind: 'duration', multiple: false, days: false, objectForm: false })
    for (const name of ['time', 'date', 'datetime']) expect(widgetFor(selector(['string'], { [name]: {} }))).toEqual({ kind: name, multiple: false })
    expect(widgetFor(selector(['array'], { text: { multiple: true } }))).toMatchObject({ kind: 'text', multiple: true })
    expect(widgetFor(selector(['string'], { select: { custom_value: true, options: ['a', 'b'] } }))).toMatchObject({ kind: 'text', options: [{ value: 'a' }, { value: 'b' }] })
    const any = ['object', 'array', 'string', 'number', 'boolean']
    expect(widgetFor(selector(any, { template: {} }))).toEqual({ kind: 'text', multiple: false, multiline: true })
    expect(literalFor(selector(any, { template: {} }), '{{ states("sun.sun") }}').type).toBe('string')
    for (const name of ['object', 'media', 'action', 'unknown_thing']) expect(widgetFor(selector(any, { [name]: {} }))).toMatchObject({ kind: 'mixed', defaultKind: 'json' })
  })
  it('defaults a colour temperature to mired, as Home Assistant does', () => {
    expect(widgetFor(selector(['number'], { color_temp: {} }))).toMatchObject({ kind: 'number', slider: true, min: 153, max: 500, unit: 'mired' })
    expect(widgetFor(selector(['number'], { color_temp: { min_mireds: 200, max_mireds: 400 } }))).toMatchObject({ min: 200, max: 400, unit: 'mired' })
    expect(widgetFor(selector(['number'], { color_temp: { unit: 'kelvin', min_mireds: 200 } }))).toMatchObject({ min: 2000, max: 6500, unit: 'K' })
  })
  it('falls back to constraints and types without a selector', () => {
    expect(widgetFor(spec(['string'], { enum: ['a', 'b'], enumLabels: ['Alpha', 'Beta'] }))).toEqual({ kind: 'select', multiple: false, options: [{ value: 'a', label: 'Alpha' }, { value: 'b', label: 'Beta' }] })
    expect(widgetFor(spec(['boolean'])).kind).toBe('toggle')
    expect(widgetFor(spec(['number'], { min: 0, max: 5, integer: true }))).toEqual({ kind: 'number', multiple: false, slider: false, min: 0, max: 5, step: 1 })
    expect(widgetFor(spec(['string'])).kind).toBe('text')
    expect(widgetFor(spec(['entity_id']))).toMatchObject({ kind: 'entity', domains: [] })
    expect(widgetFor(spec(['entity_id:light']))).toMatchObject({ kind: 'entity', domains: ['light'] })
    expect(widgetFor(spec(['target', 'entity_id:light']))).toMatchObject({ kind: 'target', domains: ['light'], entityString: true })
    expect(widgetFor(spec(['target']))).toMatchObject({ kind: 'target', entityString: false })
    expect(widgetFor(spec(['object', 'array'])).kind).toBe('json')
    expect(widgetFor(spec(['string', 'number']))).toEqual({ kind: 'mixed', multiple: false, kinds: ['text', 'number'], defaultKind: 'text' })
    expect(widgetFor(spec(Object.keys(nodeCatalog.types)))).toMatchObject({ kind: 'mixed', kinds: ['text', 'number', 'boolean', 'json'], defaultKind: 'text' })
    expect(widgetFor(spec(['global::HassSharp.EntityStateSnapshot'])).kind).toBe('none')
  })
  it('says which widgets are light enough for a node card', () => {
    const light: Widget[] = [{ kind: 'toggle', multiple: false }, { kind: 'number', multiple: false, slider: true }, { kind: 'text', multiple: false },
      { kind: 'select', multiple: false }, { kind: 'entity', multiple: false }, { kind: 'area', multiple: false }, { kind: 'state', multiple: false },
      { kind: 'duration', multiple: false, days: false }, { kind: 'mixed', multiple: false }]
    const heavy: Widget[] = [{ kind: 'target', multiple: false }, { kind: 'duration', multiple: false, days: true }, { kind: 'text', multiple: false, multiline: true },
      { kind: 'json', multiple: false }, { kind: 'color', multiple: false }, { kind: 'entity', multiple: true }, { kind: 'select', multiple: true }]
    expect(light.filter(inlineWidget)).toEqual(light)
    expect(heavy.filter(inlineWidget)).toEqual([])
  })
})

describe('values', () => {
  it('authors an entity_id:light input with that exact type', () => {
    const input = spec(['entity_id:light'])
    expect(literalFor(input, 'light.hall')).toEqual({ kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
    expect(validLiteral('entity_id:light', 'light.hall')).toBe(true)
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
  })
  it('types a mixed value by its chosen kind, not by its text', () => {
    const any = spec(Object.keys(nodeCatalog.types))
    expect(commitLiteral(any, '01', { kind: 'text' })).toEqual({ kind: 'literal', type: 'string', value: '01' })
    expect(commitLiteral(any, 1, { kind: 'number' })).toEqual({ kind: 'literal', type: 'number', value: 1 })
    expect(commitLiteral(any, true, { kind: 'boolean' })).toEqual({ kind: 'literal', type: 'boolean', value: true })
    expect(commitLiteral(any, [1], { kind: 'json' })).toEqual({ kind: 'literal', type: 'array', value: [1] })
  })
  it('keeps the shape a target already has and uses the object form for new values', () => {
    const ids = (entity_id: string[], extra: Record<string, string[]> = {}) => ({ entity_id, device_id: [], area_id: [], floor_id: [], label_id: [], ...extra })
    expect(targetValue('light.hall', ids(['light.porch']))).toBe('light.porch')
    // A target-only input never keeps the string form: the validator would reject it.
    expect(targetValue('light.hall', ids(['light.porch']), false)).toEqual({ entity_id: ['light.porch'] })
    expect(targetValue('light.hall', ids(['light.hall', 'light.porch']))).toEqual({ entity_id: ['light.hall', 'light.porch'] })
    expect(targetValue(undefined, ids(['light.hall']))).toEqual({ entity_id: ['light.hall'] })
    expect(targetValue({ entity_id: 'light.hall' }, ids(['light.hall'], { area_id: ['hall'] }))).toEqual({ entity_id: 'light.hall', area_id: ['hall'] })
    expect(targetValue({ entity_id: ['light.hall'] }, ids([]))).toBeUndefined()
  })
  it('keeps the stored duration shape and writes new values as Home Assistant does', () => {
    const both = widgetFor(selector(['number', 'object'], { duration: {} })), days = widgetFor(selector(['number', 'object'], { duration: { enable_day: true } }))
    const seconds = widgetFor(selector(['number'], { duration: {} }))
    expect(durationValue(both, 30, 3690)).toBe(3690)
    expect(durationValue(both, { minutes: 1 }, 3690)).toEqual({ hours: 1, minutes: 1, seconds: 30 })
    expect(durationValue(both, undefined, 90)).toEqual({ hours: 0, minutes: 1, seconds: 30 })
    expect(durationValue(days, undefined, 90061)).toEqual({ days: 1, hours: 1, minutes: 1, seconds: 1 })
    expect(durationValue(seconds, undefined, 90)).toBe(90)
    expect(durationValue(both, { seconds: 1 }, 0.1 + 0.2)).toEqual({ hours: 0, minutes: 0, seconds: 0.3 })
  })
  it('starts from a valid service default and ignores an invalid one', () => {
    const level = field('hass_sharp_test.sectioned', 'level')
    expect(initialValue(widgetFor(level), { ...level, default: 3 })).toBe(3)
    expect(initialValue(widgetFor(level), { ...level, default: 30 })).toBe(1)
    expect(initialValue(widgetFor(spec(['boolean'])), spec(['boolean'], { default: { kind: 'literal', type: 'boolean', value: true } }))).toBe(true)
  })
  it('summarises values too large for a node card', () => {
    const target = widgetFor(spec(['target', 'entity_id']))
    expect(valueSummary(target, { entity_id: ['light.a', 'light.b'], area_id: 'hall' })).toBe('light.a, light.b, 1 area')
    expect(valueSummary(target, { entity_id: ['light.a', 'light.b', 'light.c'], area_id: 'hall' })).toBe('3 entities, 1 area')
    expect(valueSummary(target, 'light.a')).toBe('light.a')
    expect(valueSummary(target, {})).toBe('No target')
    expect(valueSummary(widgetFor(selector(['number', 'object'], { duration: { enable_day: true } })), { days: 1, hours: 2, minutes: 0, seconds: 5 })).toBe('1 d 2 h 5 s')
    expect(valueSummary(widgetFor(selector(['string'], { text: { multiline: true } })), 'first\nsecond')).toBe('first …')
    expect(valueSummary({ kind: 'color', multiple: false }, [255, 0, 0])).toBe('RGB 255, 0, 0')
    expect(valueSummary({ kind: 'json', multiple: false }, undefined)).toBe('Not set')
    expect(valueSummary({ kind: 'json', multiple: false }, [30, 60])).toBe('30, 60')
    expect(valueSummary({ kind: 'json', multiple: false }, { a: 1, b: 2 })).toBe('Object, 2 fields')
    expect(valueSummary({ kind: 'json', multiple: false }, { a: 1 })).toBe('Object, 1 field')
    // List items that are objects or lists are described, never printed as JSON.
    expect(valueSummary({ kind: 'json', multiple: false }, [{ a: 1, b: 2 }, [1, 2, 3], 'x'])).toBe('Object, 2 fields, List, 3 items, x')
    expect(valueSummary({ kind: 'json', multiple: false }, [[1]])).toBe('List, 1 item')
    expect(valueSummary({ kind: 'select', multiple: true, options: [{ value: 'a', label: 'Apple' }, { value: 'b', label: 'Banana' }] }, ['a', 'b'])).toBe('Apple, Banana')
  })
  it('names registry items in a summary: an unnamed entity shows its id, any other unnamed item is only counted', () => {
    const names = registryNames(createRegistry(registry))
    const target = widgetFor(spec(['target', 'entity_id']))
    expect(valueSummary(target, { entity_id: ['light.hall', 'light.gone'], area_id: 'hall' }, names)).toBe('Hall lamp, light.gone, Hall')
    expect(valueSummary(target, { device_id: 'dev1', floor_id: 'ground', label_id: ['night'] }, names)).toBe('Hub, Ground, Night')
    expect(valueSummary({ kind: 'area', multiple: true }, ['hall', 'attic'], names)).toBe('Hall, Attic')
    expect(valueSummary({ kind: 'entity', multiple: false }, 'switch.fan', names)).toBe('Fan')
    expect(valueSummary({ kind: 'device', multiple: false }, 'dev2', names)).toBe('1 device')
    expect(valueSummary({ kind: 'label', multiple: false }, 'night', registryNames(undefined))).toBe('1 label')
    // A device id is a 32-character GUID: never on screen, with or without a resolver.
    const guid = '0123456789abcdef0123456789abcdef'
    expect(valueSummary({ kind: 'device', multiple: true }, ['dev1', guid, 'other'], names)).toBe('Hub, 2 devices')
    expect(valueSummary(target, { device_id: guid })).toBe('1 device')
    expect(valueSummary(target, { entity_id: 'light.hall', device_id: [guid, 'dev1'] }, names)).toBe('Hall lamp, Hub, 1 device')
    expect(valueSummary(target, { area_id: ['nowhere', 'gone'], floor_id: 'up', label_id: 'x' })).toBe('2 areas, 1 floor, 1 label')
    expect(valueSummary({ kind: 'area', multiple: false }, 'nowhere')).toBe('1 area')
    expect(valueSummary({ kind: 'floor', multiple: false }, 'up', names)).toBe('1 floor')
    expect(valueSummary({ kind: 'entity', multiple: false }, 'light.gone')).toBe('light.gone')
  })
  it('names types for people', () => {
    expect(['string', 'number', 'boolean', 'object', 'array', 'entity_id', 'entity_id:light', 'entity_id:binary_sensor', 'target', 'global::HassSharp.EntityStateSnapshot']
      .map(t => typeLabel(t))).toEqual(['Text', 'Number', 'Yes/No', 'Object', 'List', 'Entity', 'Light entity', 'Binary sensor entity', 'Target', 'Entity state'])
    expect(typeLabel('global::Library.Things.Gadget')).toBe('Gadget')
    expect(typeLabel('global::My.Thing', { ...nodeCatalog, types: { ...nodeCatalog.types, 'global::My.Thing': { description: 'x', label: 'My thing' } } })).toBe('My thing')
  })
})

// Item: every service field of the fixture, with the value its widget emits, passes the validator.
const multiSelect: ServiceFieldSchema = {
  label: 'Choices', description: '', types: ['array'], required: false, advanced: false, section: null, order: 0, min: null, max: null, integer: false,
  step: null, unit: null, enum: ['a', 'b'], enumLabels: null, pattern: null, minItems: null, maxItems: null, default: null, example: null,
  selector: { select: { multiple: true, options: ['a', 'b'] } }, filter: null,
}
const fixtureServices = (servicesFixture as unknown as ServicesSection).items
// The fixture has no multi-select field, so one is added here, shaped as the C# normaliser writes it.
const services: ServicesSection = { ...(servicesFixture as unknown as ServicesSection), items: { ...fixtureServices,
  'hass_sharp_test.multi_select': { domain: 'hass_sharp_test', service: 'multi_select', name: 'Multi select', description: '', target: null, response: 'none', fields: { choices: multiSelect } } } }
/** Fields whose widget value the validator still rejects: none, since both validators apply `enum` to each item of a list. */
const expectedRejections: string[] = []
function sample(widget: Widget): { value: JsonValue; kind?: ValueKind } {
  const one = (value: JsonValue) => ({ value: widget.multiple ? [value] : value })
  switch (widget.kind) {
    case 'toggle': return { value: true }
    case 'number': return { value: widget.min !== undefined && widget.max !== undefined ? (widget.min + widget.max) / 2 : widget.min ?? 1 }
    case 'text': return one('hello')
    case 'select': case 'constant': return one(widget.options![0].value)
    case 'entity': return one(`${widget.domains?.[0] ?? 'light'}.kitchen`)
    case 'state': return one('heat')
    case 'device': case 'area': case 'floor': case 'label': return one('abc')
    case 'target': return { value: { entity_id: [`${widget.domains?.[0] ?? 'light'}.kitchen`] } }
    case 'duration': return { value: durationValue(widget, undefined, 90) }
    case 'time': return { value: '10:30:00' }
    case 'date': return { value: '2026-11-13' }
    case 'datetime': return { value: '2026-11-13 10:30:00' }
    case 'color': return { value: [255, 0, 0] }
    case 'mixed': return widget.defaultKind === 'json' ? { value: {}, kind: 'json' } : { value: 'hello', kind: widget.defaultKind }
    case 'json': return { value: {} }
    case 'none': throw new Error('a service field always has a value widget')
  }
}
it('every fixture service field accepts the value its widget emits', () => {
  const snapshot: VisualCatalogSnapshot = { ...nodeCatalog, services }
  const rejected: string[] = [], checked: string[] = []
  for (const [id, service] of Object.entries(services.items)) for (const name of Object.keys(service.fields)) {
    const input = serviceInputs(id, [name], snapshot)[`field:${name}`]
    const widget = widgetFor(input, snapshot), choice = sample(widget)
    const start = initialValue(widget, input, snapshot)
    const cases: [string, Operand][] = [['sample', commitLiteral(input, choice.value, choice, snapshot)], ...(start === undefined ? [] : [['initial', literalFor(input, start, snapshot)] as [string, Operand]])]
    for (const [label, operand] of cases) {
      const graph = updateNode(createGraph('Fixture'), 'service-1', { config: { service: id, fields: { [name]: operand }, returnResponse: service.response === 'only' } })
      const errors = validateGraph(graph, snapshot).filter(d => d.severity === 3 && d.nodeId === 'service-1' && d.inputId === `field:${name}`)
      const key = `${id}.${name}: ${label}`
      checked.push(key)
      if (errors.length) rejected.push(key)
      if (errors.length && !expectedRejections.includes(key)) expect.fail(`${key} ${JSON.stringify(operand)} was rejected: ${errors.map(e => e.message).join('; ')}`)
    }
  }
  expect(checked.length).toBeGreaterThan(30)
  // An exception that starts passing must be removed from the list.
  expect(rejected).toEqual(expectedRejections)
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
  it('applies the domain rule to typed entity IDs too', async () => {
    const [value, setValue] = createSignal('')
    const widget = widgetFor(spec(['entity_id:light']))
    expect(manualEntityError(widget)('light.attic')).toBeUndefined()
    expect(manualEntityError(widgetFor(selector(['entity_id'], { entity: { domain: ['switch', 'binary_sensor'] } })))('light.x')).toBe('Only switch or binary sensor entities fit here.')
    const { host } = mount(() => <EntityAutocomplete value={value()} onChange={setValue} label="Lamp" filter={entityPredicate(widget)} manualError={manualEntityError(widget)} />)
    host.querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
    const status = host.querySelector('.hs-picker-rejected')!
    await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]').length).toBeGreaterThan(0))
    // One polite region, present before anything is typed, so a message is announced when it changes, not per keystroke.
    expect(status.getAttribute('role')).toBe('status')
    expect(status.textContent).toBe('')
    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search entities"]')!
    search.value = 'switch.unlisted'; search.dispatchEvent(new Event('input', { bubbles: true }))
    expect(status.textContent).toBe('Only light entities fit here.')
    expect(host.querySelector('[role="alert"]')).toBeNull()
    // The rejection replaces the "enter an exact ID" advice it would contradict.
    expect(host.textContent).not.toContain('No matching')
    search.value = 'switch.unlisted2'; search.dispatchEvent(new Event('input', { bubbles: true }))
    expect(host.querySelector('.hs-picker-rejected')).toBe(status)
    expect(status.textContent).toBe('Only light entities fit here.')
    expect([...host.querySelectorAll('button')].some(b => b.textContent?.startsWith('Use ID'))).toBe(false)
    search.value = 'light.unlisted'; search.dispatchEvent(new Event('input', { bubbles: true }))
    expect(status.textContent).toBe('')
    expect(host.textContent).toContain('No matching entities. Enter an exact ID to retain an unavailable item.')
    ;[...host.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Use ID: light.unlisted')!.click()
    expect(value()).toBe('light.unlisted')
  })
  it('judges a typed ID of a listed entity by the whole selector filter, as the picker does', () => {
    const widget = widgetFor(selector(['entity_id:light'], { entity: { domain: 'light', supported_features: 4 } }))
    const known = [entity('light.a', { supported_features: 4 }), entity('light.b')]
    const error = manualEntityError(widget, known), accepts = entityPredicate(widget)
    expect(known.map(e => [accepts(e), error(e.entity_id)])).toEqual([[true, undefined], [false, 'This entity does not fit here.']])
    // An unlisted entity's features are unknown, so only its domain is judged.
    expect(error('light.unlisted')).toBeUndefined()
    expect(error('switch.unlisted')).toBe('Only light entities fit here.')
    // The type's domains and the selector's must both allow it.
    const both = manualEntityError(widgetFor(selector(['entity_id:light', 'entity_id:switch'], { entity: { domain: ['switch', 'fan'] } })))
    expect([both('light.x'), both('switch.x'), both('fan.x')]).toEqual(['Only switch entities fit here.', undefined, 'Only switch entities fit here.'])
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
  it('searches registry items as you type and chooses with the keyboard', async () => {
    const changes: Operand[] = []
    const { host } = mount(() => <InputEditor input={selector(['string'], { area: {} })} value={undefined} onChange={o => changes.push(o)} variant="panel" label="Area" />)
    const box = host.querySelector<HTMLInputElement>('input[role="combobox"][aria-label="Area"]')!
    expect(box.placeholder).toBe('Not set')
    await vi.waitFor(() => { box.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })); expect(host.querySelectorAll('[role="option"]')).toHaveLength(2) })
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    expect(host.querySelector('[role="listbox"]')).toBeNull()
    box.value = 'att'; box.dispatchEvent(new Event('input', { bubbles: true }))
    expect([...host.querySelectorAll('[role="option"]')].map(o => o.textContent)).toEqual(['Attic'])
    expect(box.getAttribute('aria-activedescendant')).toBe(host.querySelector('[role="option"]')!.id)
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(changes).toEqual([{ kind: 'literal', type: 'string', value: 'attic' }])
    expect(jsonEqual({ a: 1, b: 2 }, { b: 2, a: 1 })).toBe(true)
  })
  it('moves up from no active option to the last one, names its group and keeps focus on a listbox press', async () => {
    const { host } = mount(() => <InputEditor input={selector(['string'], { area: {} })} value={undefined} onChange={() => {}} variant="panel" label="Area" />)
    const box = host.querySelector<HTMLInputElement>('input[role="combobox"][aria-label="Area"]')!
    const group = box.closest('.hs-search-select')!
    expect(group.getAttribute('role')).toBe('group')
    expect(host.querySelector(`[id="${group.getAttribute('aria-labelledby')}"]`)!.textContent).toBe('Area')
    await vi.waitFor(() => { box.click(); expect(host.querySelectorAll('[role="option"]')).toHaveLength(2) })
    expect(box.hasAttribute('aria-activedescendant')).toBe(false)
    box.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true, cancelable: true }))
    const options = [...host.querySelectorAll('[role="option"]')]
    expect(box.getAttribute('aria-activedescendant')).toBe(options.at(-1)!.id)
    expect(options.at(-1)!.textContent).toBe('Attic')
    const press = new MouseEvent('mousedown', { bubbles: true, cancelable: true })
    host.querySelector('[role="listbox"]')!.dispatchEvent(press)
    expect(press.defaultPrevented).toBe(true)
  })
})
