import { afterEach, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route } from '@solidjs/router'
import { MainLayout } from '../src/layout/MainLayout'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); document.body.innerHTML = '' })
it('moves skip-link focus to content when embedded inside Home Assistant shadow DOM', () => {
  const host = document.createElement('div'); document.body.append(host)
  const root = host.attachShadow({ mode: 'open' })
  dispose = render(() => <MemoryRouter root={MainLayout}><Route path="/" component={() => <p>Content</p>} /></MemoryRouter>, root)
  const link = root.querySelector<HTMLAnchorElement>('.hs-skip-link')!
  link.focus(); link.click()
  expect(root.activeElement).toBe(root.querySelector('main'))
})
