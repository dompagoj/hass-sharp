import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { TriggerAdvanced, TriggerConfig, triggerChoices } from '../src/visual/TriggerConfig'

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(() => {
  dispose?.()
  dispose = undefined
  client?.clear()
  document.body.innerHTML = ''
})
function mount(initial: Record<string, unknown>, hass: Record<string, unknown> = {}) {
  const host = document.createElement('div')
  host.className = 'hs-app'
  document.body.append(host)
  const [config, setConfig] = createSignal<Record<string, unknown>>(initial)
  client = new QueryClient()
  const value = { callWS: async () => ({ entities: [], areas: [], devices: [], floors: [], labels: [] }), ...hass }
  dispose = render(
    () => (
      <QueryClientProvider client={client!}>
        <HassCtx.Provider value={value as any}>
          <TriggerConfig node={{ id: 'start', kind: 'trigger', config: config() }} onChange={next => setConfig(next)} />
          <TriggerAdvanced node={{ id: 'start', kind: 'trigger', config: config() }} onChange={next => setConfig(next)} />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    host,
  )
  return { host, config }
}
/** The field a visible <label> names. */
const field = <T extends HTMLElement = HTMLInputElement>(host: Element, label: string) => {
  const tag = [...host.querySelectorAll<HTMLLabelElement>('label[for]')].find(l => l.textContent?.trim() === label)
  expect(tag, label).toBeDefined()
  return host.querySelector<T>(`[id="${tag!.htmlFor}"]`)!
}
function type(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  input.value = value
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new FocusEvent('blur'))
}
function choose(host: Element, id: string) {
  if (!host.querySelector('[role="radiogroup"][aria-label="Trigger type"]')) host.querySelector<HTMLButtonElement>('button[aria-label="Change trigger type"]')!.click()
  host.querySelector<HTMLInputElement>(`[aria-label="Trigger type"] input[value="${id}"]`)!.click()
}

it('shows "When" as a visible choice of trigger types, each with an icon and a one-line explanation', () => {
  const { host, config } = mount({ type: 'entity', entity: 'binary_sensor.motion', mode: 'Single' })
  const current = host.querySelector('.hs-when__current')!
  expect(current.textContent).toContain('Entity changes')
  expect(current.querySelector('svg')).not.toBeNull()
  expect(host.querySelector('select[aria-label="Trigger type"]')).toBeNull()
  const change = host.querySelector<HTMLButtonElement>('button[aria-label="Change trigger type"]')!
  expect(change.getAttribute('aria-expanded')).toBe('false')
  change.click()
  const choices = [...host.querySelectorAll<HTMLLabelElement>('[aria-label="Trigger type"] .hs-when__choice')]
  expect(choices).toHaveLength(triggerChoices.length)
  for (const choice of choices) {
    expect(choice.querySelector('svg')).not.toBeNull()
    expect(choice.querySelector('small')?.textContent?.length).toBeGreaterThan(0)
  }
  expect(host.querySelector<HTMLInputElement>('[aria-label="Trigger type"] input[value="entity"]')!.checked).toBe(true)
  choose(host, 'manual')
  expect(config()).toMatchObject({ type: 'manual', entity: 'binary_sensor.motion' })
  expect(host.querySelector('[role="radiogroup"][aria-label="Trigger type"]')).toBeNull()
  expect(host.querySelector('.hs-when__current')!.textContent).toContain('Only manually')
})

it('offers run modes in plain words', () => {
  const { host, config } = mount({ type: 'manual', mode: 'Single' })
  const modes = host.querySelector('.hs-insp-modes')!
  expect(modes.querySelector('legend')!.textContent).toBe('If it triggers again while running')
  expect(modes.textContent).toContain('Ignore it')
  expect(modes.textContent).toContain('Start over')
  modes.querySelector<HTMLInputElement>('input[value="Queued"]')!.click()
  expect(config().mode).toBe('Queued')
})

it('subscribes to HA trigger metadata, merges additions, preserves unknown platform keys and cleans up', async () => {
  let callback!: (value: unknown) => void
  const unsubscribe = vi.fn()
  const subscribeMessage = vi.fn(async (cb: any, message: any) => {
    expect(message).toEqual({ type: 'trigger_platforms/subscribe' })
    callback = cb
    return unsubscribe
  })
  const { host, config } = mount({ type: 'ha', trigger: { trigger: 'unlisted.platform', unknown: { keep: true } } }, { connection: { subscribeMessage } })
  await Promise.resolve()
  callback({ 'update.became_available': { fields: {}, target: { entity: { domain: 'update' } } } })
  callback({
    'battery.started_charging': { fields: {}, target: { entity: { domain: 'sensor' } } },
    'temperature.changed': { fields: {}, target: { entity: { domain: 'sensor' } } },
  })
  expect(host.querySelector('.hs-when__current')!.textContent).toContain('Other trigger')
  const platform = field(host, 'Trigger platform')
  expect(platform.value).toBe('unlisted.platform')
  const suggestions = [...host.querySelectorAll('datalist option, [role="option"]')].map(o => o.getAttribute('value') ?? o.textContent)
  expect(suggestions).toEqual(expect.arrayContaining(['update.became_available', 'battery.started_charging', 'temperature.changed']))
  choose(host, 'state')
  expect(config().trigger).toMatchObject({ trigger: 'state', unknown: { keep: true } })
  expect(host.querySelector('[data-input-id="trigger"]')).not.toBeNull()
  dispose!()
  dispose = undefined
  expect(unsubscribe).toHaveBeenCalledTimes(1)
})

it('preserves entity-based numeric thresholds and structured unknown trigger fields while editing', () => {
  const { host, config } = mount({
    type: 'ha',
    trigger: { trigger: 'numeric_state', entity_id: ['sensor.temperature'], above: 'input_number.threshold', for: { minutes: 2 }, extension: { keep: 1 } },
  })
  type(field(host, 'Above'), 'input_number.other')
  expect(config().trigger).toEqual({
    trigger: 'numeric_state', entity_id: ['sensor.temperature'], above: 'input_number.other', for: { minutes: 2 }, extension: { keep: 1 },
  })
  type(field(host, 'Below'), '30')
  expect((config().trigger as any).below).toBe(30)
})

it('keeps a scheduled trigger\'s settings when switching to a Home Assistant trigger, and gives sun a default event', () => {
  const { host, config } = mount({ type: 'daily', time: '07:30', timeZone: 'Europe/Zagreb' })
  expect(field(host, 'Time of day').value).toBe('07:30')
  choose(host, 'state')
  expect(config()).toMatchObject({ type: 'ha', trigger: { trigger: 'state' }, time: '07:30', timeZone: 'Europe/Zagreb' })
  type(field(host, 'Attribute'), 'brightness')
  expect(config().trigger).toEqual({ trigger: 'state', attribute: 'brightness' })
  choose(host, 'sun')
  expect(config().trigger).toMatchObject({ trigger: 'sun', event: 'sunrise' })
  choose(host, 'daily')
  expect(config()).toMatchObject({ type: 'daily', time: '07:30' })
})

it('keeps trigger subscriptions required', () => {
  const { host } = mount({ type: 'entity', entity: 'binary_sensor.motion' })
  host.querySelector<HTMLButtonElement>('[data-input-id="entity"] button[aria-label="Entity"]')!.click()
  expect(host.textContent).not.toContain('No target')
})

it('shows the stored startup choice and manual runs in Advanced', () => {
  const { host, config } = mount({ type: 'manual', startup: false, manual: true })
  const startup = field<HTMLSelectElement>(host, 'Run when Home Assistant starts')
  expect(startup.selectedOptions[0].textContent).toBe('No, never at startup')
  startup.value = 'true'
  startup.dispatchEvent(new Event('change', { bubbles: true }))
  expect(config().startup).toBe(true)
  const manual = [...host.querySelectorAll('label')].find(l => l.textContent?.includes('Can be started with Run now'))!.querySelector('input')!
  expect(manual.checked).toBe(true)
  manual.click()
  expect(config().manual).toBe(false)
})

it('chooses the trigger type from the keyboard: arrows move without choosing, Enter chooses, Escape closes unchanged', async () => {
  const { host, config } = mount({ type: 'entity', entity: 'binary_sensor.motion', mode: 'Single' })
  const change = host.querySelector<HTMLButtonElement>('button[aria-label="Change trigger type"]')!
  const group = () => host.querySelector<HTMLElement>('[role="radiogroup"][aria-label="Trigger type"]')
  const radio = (id: string) => host.querySelector<HTMLInputElement>(`[aria-label="Trigger type"] input[value="${id}"]`)!
  const key = (key: string) => document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  change.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(radio('entity')))
  key('ArrowDown')
  key('ArrowDown')
  // The third type is focused and checked, but nothing is chosen yet.
  expect(document.activeElement).toBe(radio(triggerChoices[2].id))
  expect(radio(triggerChoices[2].id).checked).toBe(true)
  expect(config().type).toBe('entity')
  expect(group()).not.toBeNull()
  key('Enter')
  expect(config()).toMatchObject({ type: 'ha', trigger: { trigger: triggerChoices[2].id } })
  expect(group()).toBeNull()
  await vi.waitFor(() => expect(document.activeElement).toBe(change))
  // Escape closes without changing.
  change.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(radio(triggerChoices[2].id)))
  key('ArrowUp')
  key('Escape')
  expect(group()).toBeNull()
  expect(config()).toMatchObject({ type: 'ha', trigger: { trigger: triggerChoices[2].id } })
  await vi.waitFor(() => expect(document.activeElement).toBe(change))
  // Space chooses too.
  change.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(radio(triggerChoices[2].id)))
  key('End')
  key(' ')
  expect(config().type).toBe(triggerChoices.at(-1)!.id === 'other' ? 'ha' : triggerChoices.at(-1)!.id)
  await vi.waitFor(() => expect(document.activeElement).toBe(change))
})
