import { afterEach, expect, it } from 'vitest'
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
  expect(details.querySelector('summary')!.textContent).toBe('state 1 key')
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
  root.querySelector<HTMLButtonElement>('.hs-json-more button')!.click()
  expect(items()).toHaveLength(250)
  expect(root.querySelector('.hs-json-more')).toBeNull()
})

it('keeps names as they were recorded, including a repeated key\'s position suffix', () => {
  const root = mount({ '1': 'a', '1#1': 'b' })
  expect([...root.querySelectorAll('.hs-json-key')].map(key => key.textContent)).toEqual(['1', '1#1'])
})
