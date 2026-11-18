import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { MainLayout } from '../src/layout/MainLayout'
import { AppRoutes } from '../src/App'
import { HassCtx } from '../src/context'

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

const navLinks = (root: ParentNode) => [...root.querySelectorAll<HTMLAnchorElement>('nav[aria-label="HassSharp"] a')]
const activeLinks = (root: ParentNode) => navLinks(root).filter(link => link.classList.contains('hs-nav-link--active')).map(link => link.textContent)

it('links Entities between Automations and Node library, active on the entities page and everything under it', async () => {
  const root = document.createElement('div'); document.body.append(root)
  const history = createMemoryHistory(); history.set({ value: '/entities' })
  dispose = render(() => <MemoryRouter history={history} root={MainLayout}>
    <Route path="/automations" component={() => <p>Automations</p>} />
    <Route path="/entities" component={() => <p>Entities</p>} />
    <Route path="/entities/:id" component={() => <p>One</p>} />
    <Route path="/libraries" component={() => <p>Libraries</p>} />
  </MemoryRouter>, root)
  expect(navLinks(root).map(link => [link.textContent, link.getAttribute('href')])).toEqual([
    ['Automations', '/automations'], ['Entities', '/entities'], ['Node library', '/libraries'],
  ])
  expect(activeLinks(root)).toEqual(['Entities'])
  for (const path of ['/entities/new?logic=graph', '/entities/plug', '/entities/plug/runs/r1']) {
    history.set({ value: path })
    await vi.waitFor(() => expect(activeLinks(root)).toEqual(['Entities']))
  }
  history.set({ value: '/automations' })
  await vi.waitFor(() => expect(activeLinks(root)).toEqual(['Automations']))
})

it('sends /devices to the entities page, and keeps the entity definition routes', async () => {
  const root = document.createElement('div'); document.body.append(root)
  const hass = {
    user: { is_admin: true },
    callWS: async (message: any) => (message.type === 'hass_sharp/entities'
      ? { version: 1, ok: true, result: message.request.operation === 'types' ? { types: [], generation: 'g' } : [] }
      : { entities: [] }),
  }
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory(); history.set({ value: '/devices' })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter history={history} root={MainLayout}><AppRoutes /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  await vi.waitFor(() => expect(history.get()).toBe('/entities'))
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Entities'))
  expect(activeLinks(root)).toEqual(['Entities'])
  // The list page has not replaced the editor's routes.
  for (const path of ['/entities/new?logic=class&name=Camera', '/entities/plug']) {
    history.set({ value: path })
    await vi.waitFor(() => expect(root.querySelector('#entities-title')).toBeNull())
    expect(root.querySelector('main')!.children.length, path).toBeGreaterThan(0)
  }
  client.clear()
})
