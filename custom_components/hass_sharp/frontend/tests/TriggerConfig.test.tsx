import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { HassCtx } from '../src/context'
import { TriggerConfig } from '../src/visual/TriggerConfig'
let dispose: (() => void) | undefined
afterEach(() => {
  dispose?.()
  document.body.innerHTML = ''
})
it('subscribes to HA trigger metadata, merges additions, preserves unknown platform keys and cleans up', async () => {
  const host = document.createElement('div')
  document.body.append(host)
  let callback!: (value: unknown) => void
  const unsubscribe = vi.fn()
  const subscribeMessage = vi.fn(async (cb: any, message: any) => {
    expect(message).toEqual({ type: 'trigger_platforms/subscribe' })
    callback = cb
    return unsubscribe
  })
  const [config, setConfig] = createSignal<Record<string, unknown>>({
    type: 'ha',
    trigger: { trigger: 'unlisted.platform', unknown: { keep: true } },
  })
  dispose = render(
    () => (
      <HassCtx.Provider value={{ connection: { subscribeMessage } } as any}>
        <TriggerConfig node={{ id: 'start', kind: 'trigger', config: config() }} onChange={setConfig} />
      </HassCtx.Provider>
    ),
    host,
  )
  await Promise.resolve()
  callback({ 'update.became_available': { fields: {}, target: { entity: { domain: 'update' } } } })
  callback({
    'battery.started_charging': { fields: {}, target: { entity: { domain: 'sensor' } } },
    'temperature.changed': { fields: {}, target: { entity: { domain: 'sensor' } } },
  })
  const picker = host.querySelector<HTMLSelectElement>('[aria-label="Native trigger platform"]')!
  expect([...picker.options].map(o => o.value)).toEqual(
    expect.arrayContaining(['update.became_available', 'battery.started_charging', 'temperature.changed', 'unlisted.platform', 'state']),
  )
  picker.value = 'state'
  picker.dispatchEvent(new Event('change', { bubbles: true }))
  expect(config().trigger).toMatchObject({ trigger: 'state', unknown: { keep: true } })
  const entity = host.querySelector<HTMLInputElement>('[aria-label="Native entity ID"]')!
  entity.value = 'binary_sensor.door'
  entity.dispatchEvent(new Event('input', { bubbles: true }))
  expect(config().trigger).toMatchObject({ entity_id: 'binary_sensor.door', unknown: { keep: true } })
  dispose()
  dispose = undefined
  expect(unsubscribe).toHaveBeenCalledTimes(1)
})
it('preserves entity-based numeric thresholds and structured unknown trigger fields while editing', () => {
  const host = document.createElement('div')
  document.body.append(host)
  const [config, setConfig] = createSignal<Record<string, unknown>>({
    type: 'ha',
    trigger: {
      trigger: 'numeric_state',
      entity_id: ['sensor.temperature'],
      above: 'input_number.threshold',
      for: { minutes: 2 },
      extension: { keep: 1 },
    },
  })
  dispose = render(
    () => (
      <HassCtx.Provider value={{} as any}>
        <TriggerConfig node={{ id: 'start', kind: 'trigger', config: config() }} onChange={setConfig} />
      </HassCtx.Provider>
    ),
    host,
  )
  const above = host.querySelector<HTMLInputElement>('[aria-label="Above"]')!
  above.value = 'input_number.other'
  above.dispatchEvent(new Event('input', { bubbles: true }))
  expect(config().trigger).toEqual({
    trigger: 'numeric_state',
    entity_id: ['sensor.temperature'],
    above: 'input_number.other',
    for: { minutes: 2 },
    extension: { keep: 1 },
  })
})
it('persists displayed native platform defaults when switching from a scheduled trigger', () => {
  const host = document.createElement('div')
  document.body.append(host)
  const [config, setConfig] = createSignal<Record<string, unknown>>({ type: 'daily', time: '07:30', timeZone: 'Europe/Zagreb' })
  dispose = render(
    () => (
      <HassCtx.Provider value={{} as any}>
        <TriggerConfig node={{ id: 'start', kind: 'trigger', config: config() }} onChange={setConfig} />
      </HassCtx.Provider>
    ),
    host,
  )
  const type = host.querySelector<HTMLSelectElement>('[aria-label="Trigger type"]')!
  type.value = 'ha'
  type.dispatchEvent(new Event('change', { bubbles: true }))
  const entity = host.querySelector<HTMLInputElement>('[aria-label="Native entity ID"]')!
  entity.value = 'binary_sensor.door'
  entity.dispatchEvent(new Event('input', { bubbles: true }))
  expect(config().trigger).toEqual({ trigger: 'state', entity_id: 'binary_sensor.door' })
  expect(config()).toMatchObject({ time: '07:30', timeZone: 'Europe/Zagreb' })
  const platform = host.querySelector<HTMLSelectElement>('[aria-label="Native trigger platform"]')!
  platform.value = 'sun'
  platform.dispatchEvent(new Event('change', { bubbles: true }))
  expect(config().trigger).toMatchObject({ trigger: 'sun', event: 'sunrise' })
})
