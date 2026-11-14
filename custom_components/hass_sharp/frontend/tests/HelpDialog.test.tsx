import { afterEach, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import { HelpDialog } from '../src/components/HelpDialog'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); document.body.innerHTML = '' })

it('explains steps, value nodes and Custom C# as they are, and names no removed part', async () => {
  const root = document.createElement('div'); document.body.append(root)
  dispose = render(() => <HelpDialog open onClose={() => {}} />, root)
  await Promise.resolve()
  const text = document.body.textContent ?? ''
  expect(text).toContain('Value nodes')
  expect(text).toContain('worked out each time a step that uses it runs')
  expect(text).toContain('from a step that ran before it on its path, or from a value node')
  expect(text).toContain('[Node]')
  expect(text).toContain('NodeContext')
  for (const removed of ['Set value', 'Usings', 'usings', 'Helper members', 'helper members', 'Expose to C#', 'variable', 'Variable', 'Advanced section'])
    expect(text, removed).not.toContain(removed)
})
