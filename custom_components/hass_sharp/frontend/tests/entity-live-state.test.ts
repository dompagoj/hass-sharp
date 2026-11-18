import { afterEach, expect, it, vi } from 'vitest'
import { createRoot, createSignal } from 'solid-js'
import { liveState } from '../src/entities/liveState'

// The entities page's live states: one `subscribe_entities` subscription for the ids it is given, fed by Home
// Assistant's compressed events (`a` adds, `c` changes, `r` removes).
type Subscription = { message: any; push(event: unknown): void; unsubscribe: ReturnType<typeof vi.fn> }
/** A connection whose `subscribeMessage` records each subscription; `fail` rejects the next one. */
function connection() {
  const subscriptions: Subscription[] = []
  const control = { fail: false, wait: undefined as Promise<void> | undefined }
  const subscribeMessage = vi.fn(async (callback: (event: unknown) => void, message: unknown) => {
    if (control.fail) throw { code: 'unknown_command', message: 'Unknown command.' }
    if (control.wait) await control.wait
    const subscription: Subscription = { message, push: callback, unsubscribe: vi.fn(async () => {}) }
    subscriptions.push(subscription)
    return subscription.unsubscribe
  })
  return { hass: { connection: { subscribeMessage } } as any, subscriptions, subscribeMessage, control }
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined })

it('subscribes once to the ids, applies added, changed and removed entities, and unsubscribes on cleanup', async () => {
  const { hass, subscriptions, subscribeMessage } = connection()
  const [ids] = createSignal(['switch.plug_relay', 'binary_sensor.motion_any', 'switch.plug_relay'])
  const states = createRoot(done => { dispose = done; return liveState(hass, ids) })
  await settle()
  expect(subscribeMessage).toHaveBeenCalledTimes(1)
  // Sorted, each id once.
  expect(subscriptions[0].message).toEqual({ type: 'subscribe_entities', entity_ids: ['binary_sensor.motion_any', 'switch.plug_relay'] })
  expect(states()).toEqual({})
  subscriptions[0].push({ a: { 'switch.plug_relay': { s: 'on', a: { friendly_name: 'Relay' }, c: 'x', lc: 1 }, 'binary_sensor.motion_any': { s: 'unavailable', a: {} } } })
  expect(states()).toEqual({ 'switch.plug_relay': { state: 'on', available: true }, 'binary_sensor.motion_any': { state: 'unavailable', available: false } })
  // A change carries `s` only when the state changed; an attribute-only change keeps it.
  subscriptions[0].push({ c: { 'binary_sensor.motion_any': { '+': { s: 'off', lc: 2 } }, 'switch.plug_relay': { '+': { a: { icon: 'mdi:power' } }, '-': { a: ['friendly_name'] } } } })
  expect(states()).toEqual({ 'switch.plug_relay': { state: 'on', available: true }, 'binary_sensor.motion_any': { state: 'off', available: true } })
  subscriptions[0].push({ c: { 'switch.plug_relay': { '+': { s: 'unavailable' } } } })
  expect(states()['switch.plug_relay']).toEqual({ state: 'unavailable', available: false })
  subscriptions[0].push({ r: ['switch.plug_relay'] })
  expect(states()).toEqual({ 'binary_sensor.motion_any': { state: 'off', available: true } })
  dispose!(); dispose = undefined
  expect(subscriptions[0].unsubscribe).toHaveBeenCalledTimes(1)
  // Events after cleanup change nothing.
  subscriptions[0].push({ a: { 'switch.plug_relay': { s: 'on' } } })
  expect(states()['switch.plug_relay']).toBeUndefined()
})

it('keeps none of a camera\'s attributes: not its access token, nor its picture\'s address', async () => {
  const { hass, subscriptions } = connection()
  const [ids] = createSignal(['camera.front'])
  const states = createRoot(done => { dispose = done; return liveState(hass, ids) })
  await settle()
  const token = 'f3c1d9-access-token'
  subscriptions[0].push({ a: { 'camera.front': { s: 'idle', a: { access_token: token, entity_picture: `/api/camera_proxy/camera.front?token=${token}`, friendly_name: 'Front', supported_features: 0 } } } })
  expect(states()).toEqual({ 'camera.front': { state: 'idle', available: true } })
  subscriptions[0].push({ c: { 'camera.front': { '+': { a: { access_token: `${token}-2`, entity_picture: `/api/camera_proxy/camera.front?token=${token}-2` } } } } })
  expect(states()).toEqual({ 'camera.front': { state: 'idle', available: true } })
  expect(JSON.stringify(states())).not.toContain(token)
})

it('resubscribes only when the set of ids changes, not for the same ids in another order or array', async () => {
  const { hass, subscriptions, subscribeMessage } = connection()
  const [ids, setIds] = createSignal(['light.a', 'light.b'])
  const states = createRoot(done => { dispose = done; return liveState(hass, ids) })
  await settle()
  subscriptions[0].push({ a: { 'light.a': { s: 'on' }, 'light.b': { s: 'off' } } })
  setIds(['light.b', 'light.a'])
  setIds(['light.a', 'light.b', 'light.a'])
  await settle()
  expect(subscribeMessage).toHaveBeenCalledTimes(1)
  expect(subscriptions[0].unsubscribe).not.toHaveBeenCalled()
  // Another set: the old subscription closes and a new one opens for the new ids; the states of ids no longer asked
  // for go, those still asked for stay until the new subscription answers.
  setIds(['light.a', 'light.c'])
  await settle()
  expect(subscriptions[0].unsubscribe).toHaveBeenCalledTimes(1)
  expect(subscribeMessage).toHaveBeenCalledTimes(2)
  expect(subscriptions[1].message).toEqual({ type: 'subscribe_entities', entity_ids: ['light.a', 'light.c'] })
  expect(states()).toEqual({ 'light.a': { state: 'on', available: true } })
  // The old subscription's late events are ignored.
  subscriptions[0].push({ a: { 'light.b': { s: 'on' } } })
  expect(states()['light.b']).toBeUndefined()
  subscriptions[1].push({ a: { 'light.a': { s: 'off' }, 'light.c': { s: 'unknown' } } })
  expect(states()).toEqual({ 'light.a': { state: 'off', available: true }, 'light.c': { state: 'unknown', available: true } })
  // No ids: no subscription at all, and no states.
  setIds([])
  await settle()
  expect(subscriptions[1].unsubscribe).toHaveBeenCalledTimes(1)
  expect(subscribeMessage).toHaveBeenCalledTimes(2)
  expect(states()).toEqual({})
})

it('subscribes to nothing without ids, and survives a refused subscription or one that answers after cleanup', async () => {
  const empty = connection()
  createRoot(done => { dispose = done; return liveState(empty.hass, () => []) })
  await settle()
  expect(empty.subscribeMessage).not.toHaveBeenCalled()
  dispose!(); dispose = undefined

  const refused = connection()
  refused.control.fail = true
  const states = createRoot(done => { dispose = done; return liveState(refused.hass, () => ['light.a']) })
  await settle()
  expect(refused.subscribeMessage).toHaveBeenCalledTimes(1)
  expect(states()).toEqual({})
  dispose!(); dispose = undefined

  // Left before Home Assistant confirmed the subscription: it is closed as soon as it is confirmed.
  const slow = connection()
  let confirm!: () => void
  slow.control.wait = new Promise<void>(go => (confirm = go))
  createRoot(done => { dispose = done; return liveState(slow.hass, () => ['light.a']) })
  await settle()
  dispose!(); dispose = undefined
  confirm()
  await settle()
  expect(slow.subscriptions[0].unsubscribe).toHaveBeenCalledTimes(1)

  // No connection (a test double without one): no states, no error.
  const none = createRoot(done => { dispose = done; return liveState({} as any, () => ['light.a']) })
  await settle()
  expect(none()).toEqual({})
})

it('does not subscribe at all when it is cleaned up before the subscription is sent', async () => {
  const { hass, subscribeMessage } = connection()
  // Its effect has run (the subscription is on its way), but the call has not been made yet.
  const done = createRoot(dispose => { liveState(hass, () => ['light.a']); return dispose })
  done()
  await settle()
  expect(subscribeMessage).not.toHaveBeenCalled()
})
