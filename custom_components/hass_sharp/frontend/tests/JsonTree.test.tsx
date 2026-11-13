import { afterEach, expect, it } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { JsonTree } from '../src/components/JsonTree'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })
const mount = (value: unknown) => {
  const root = document.createElement('div'); document.body.append(root)
  dispose = render(() => <JsonTree value={value} />, root)
  return root
}
const open = (details: HTMLDetailsElement) => { details.open = true; details.dispatchEvent(new Event('toggle')) }

it('renders the top level at once and a nested level only when it is first opened', () => {
  const root = mount({ name: 'Hall', state: { attributes: { brightness: 200 } } })
  expect(root.textContent).toContain('Hall')
  const details = root.querySelector<HTMLDetailsElement>('details')!
  expect(details.querySelector('summary')!.textContent).toBe('state 1 field')
  // Closed: its members are not built yet.
  expect(details.querySelector('ul')).toBeNull()
  open(details)
  expect(details.querySelector('ul')).not.toBeNull()
  const inner = details.querySelector<HTMLDetailsElement>('details')!
  expect(inner.querySelector('ul')).toBeNull()
  open(inner)
  expect(inner.textContent).toContain('200')
  // Closing keeps what was built (and the state inside it).
  details.open = false; details.dispatchEvent(new Event('toggle'))
  expect(details.querySelector('ul')).not.toBeNull()
})

it('shows at most 100 members per level, then an "N more" row that shows the next ones', () => {
  const root = mount(Array.from({ length: 250 }, (_, i) => i))
  const items = () => root.querySelectorAll('.hs-json-tree > ul > li:not(.hs-json-more)')
  expect(items()).toHaveLength(100)
  const more = root.querySelector<HTMLButtonElement>('.hs-json-more button')!
  expect(more.textContent).toBe('150 more')
  more.click()
  expect(items()).toHaveLength(200)
  expect(root.querySelector('.hs-json-more button')!.textContent).toBe('50 more')
  const last = root.querySelector<HTMLButtonElement>('.hs-json-more button')!
  last.focus(); last.click()
  expect(items()).toHaveLength(250)
  expect(root.querySelector('.hs-json-more')).toBeNull()
  // The button went away with that press: focus is on the first member it showed, not on the page.
  expect(document.activeElement).toBe(items()[200])
})

it('keeps what was opened and shown when the value is replaced by one of the same shape', () => {
  const root = document.createElement('div'); document.body.append(root)
  const value = (n: number) => ({ room: 'Hall', state: { attributes: { brightness: n, list: Array.from({ length: 150 }, (_, i) => i + n) } } })
  const [current, setCurrent] = createSignal<unknown>(value(1))
  dispose = render(() => <JsonTree value={current()} />, root)
  const outer = root.querySelector<HTMLDetailsElement>('details')!
  open(outer)
  const inner = outer.querySelector<HTMLDetailsElement>('details')!
  open(inner)
  const list = inner.querySelector<HTMLDetailsElement>('details')!
  open(list)
  list.querySelector<HTMLButtonElement>('.hs-json-more button')!.click()
  setCurrent(value(2))
  // The same elements, still open, now with the new values.
  expect(root.querySelector('details')).toBe(outer)
  expect(outer.querySelector('details')).toBe(inner)
  expect(inner.open && list.open).toBe(true)
  expect(inner.textContent).toContain('brightness 2')
  expect(list.querySelectorAll(':scope > ul > li:not(.hs-json-more)')).toHaveLength(150)
})

it('lets a caller show a member in words in place of its JSON text', () => {
  const root = document.createElement('div'); document.body.append(root)
  dispose = render(() => <JsonTree value={{ api_key: '***', note: '***' }} leaf={(name, v) => (name === 'api_key' && v === '***' ? <em>Hidden</em> : undefined)} />, root)
  const leaves = [...root.querySelectorAll('.hs-json-leaf')].map(leaf => leaf.textContent)
  expect(leaves).toEqual(['api_key Hidden', 'note "***"'])
})

it('keeps names as they were recorded, including a repeated key\'s position suffix', () => {
  const root = mount({ '1': 'a', '1#1': 'b' })
  expect([...root.querySelectorAll('.hs-json-key')].map(key => key.textContent)).toEqual(['1', '1#1'])
})
