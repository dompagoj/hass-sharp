import { afterEach, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route } from '@solidjs/router'
import { VisualAutomationView } from '../src/views/VisualAutomationView'
let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); document.body.innerHTML = '' })
it('protects a visual draft from navigation and allows explicit discard', async () => {
  const root = document.createElement('div'); document.body.append(root)
  dispose = render(() => <MemoryRouter root={props => <><A href="/elsewhere">Leave</A>{props.children}</>}>
    <Route path="/" component={VisualAutomationView} /><Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
  </MemoryRouter>, root)
  root.querySelector<HTMLAnchorElement>('a')!.click()
  await Promise.resolve()
  expect(root.querySelector('dialog[open]')?.textContent).toContain('Leave visual draft?')
  const stay = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Keep editing')!
  stay.click()
  expect(root.querySelector('[aria-label="Visual automation editor"]')).not.toBeNull()
  root.querySelector<HTMLAnchorElement>('a')!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Discard draft')!.click()
  await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
  expect(root.querySelector('[aria-label="Visual automation editor"]')).toBeNull()
})
