import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { editors, resetHost } from './fake-monaco'
import { visualFixture } from './visual-fixture'
import { HassCtx } from '../src/context'
import { CustomCodeEditor } from '../src/visual/CustomCodeEditor'
import { NodeLibraryView } from '../src/App'
import { addNode, createGraph } from '../src/visual/graph'
import type { VisualCatalogSnapshot } from '../src/visual/contract'

// A chunk that cannot be fetched (offline, or a tab left open across an upgrade) rejects its dynamic import (vitest
// rewords the reason, so the test checks only that a reason is given).
const offline = vi.hoisted(() => ({ editor: false, library: false }))
vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
vi.mock('../src/components/Editor/CustomCode', async importOriginal => {
  if (offline.editor) throw new TypeError('Failed to fetch dynamically imported module: CustomCode.js')
  return importOriginal()
})
vi.mock('../src/views/NodeLibraryView', () => {
  if (offline.library) throw new TypeError('Failed to fetch dynamically imported module: NodeLibraryView.js')
  return { NodeLibraryView: () => <p class="libraries">Node libraries</p> }
})

const catalog = rich.catalog as unknown as VisualCatalogSnapshot
let dispose: (() => void) | undefined
beforeEach(() => resetHost())
afterEach(async () => {
  dispose?.(); dispose = undefined
  vi.restoreAllMocks(); vi.resetModules()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})
const root = () => { const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el); return el }
const button = (scope: ParentNode, label: string) => [...scope.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label)

it('says when the code editor could not load, claims no check, and loads it on Try again', async () => {
  offline.editor = true
  const fixture = visualFixture({ catalog })
  const node = addNode(createGraph('Code'), 'custom', 'code', catalog).nodes.find(n => n.id === 'code')!
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={fixture as any}>
    <CustomCodeEditor node={node} catalog={catalog} onClose={() => {}} />
  </HassCtx.Provider></QueryClientProvider>, root())
  const dialog = () => document.querySelector<HTMLDialogElement>('dialog.hs-code-dialog')!
  await vi.waitFor(() => expect(dialog().querySelector('[role="alert"]')?.textContent).toMatch(/^Could not load the code editor: .+Try again$/))
  expect(dialog().querySelector('.hs-code-state')!.textContent).toBe('Not checked')
  expect(dialog().textContent).not.toContain('Loading the code editor')
  expect(button(dialog(), 'Apply')!.disabled).toBe(true)
  expect(editors).toHaveLength(0)
  offline.editor = false
  button(dialog().querySelector('[role="alert"]')!, 'Try again')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  await vi.waitFor(() => expect(dialog().querySelector('.hs-code-state')!.textContent).toBe('No problems'))
  expect(dialog().querySelector('[role="alert"]')).toBeNull()
})

it('says when the node libraries page could not load, and loads it on Try again', async () => {
  offline.library = true
  const host = root()
  dispose = render(() => <NodeLibraryView />, host)
  await vi.waitFor(() => expect(host.querySelector('[role="alert"]')?.textContent).toMatch(/^Could not load the node libraries: .+Try again$/))
  offline.library = false
  button(host, 'Try again')!.click()
  await vi.waitFor(() => expect(host.querySelector('.libraries')?.textContent).toBe('Node libraries'))
  expect(host.querySelector('[role="alert"]')).toBeNull()
})
