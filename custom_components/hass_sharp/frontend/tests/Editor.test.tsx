import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createSignal } from 'solid-js'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { editors, resetHost, completions, hovers } from './fake-monaco'
import { HassCtx } from '../src/context'
import { Editor } from '../src/components/Editor/Editor'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
const vim = vi.hoisted(() => ({ init: vi.fn(), dispose: vi.fn() }))
vi.mock('monaco-vim', () => ({ initVimMode: vim.init }))
let dispose: () => void
beforeEach(() => { resetHost(); vi.useFakeTimers(); localStorage.clear(); vim.init.mockReset(); vim.dispose.mockReset(); vim.init.mockReturnValue({ dispose: vim.dispose }) })
afterEach(() => { dispose?.(); document.body.innerHTML = ''; vi.useRealTimers() })
it('keeps the editor model through font/Vim/prop updates and disposes all owned resources', async () => {
  const div = document.createElement('div'); document.body.append(div)
  const [initial, setInitial] = createSignal('original')
  const saved: string[] = []
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS: async (m: any) => m.type.endsWith('format_source') ? 'formatted' : [] } as any}>
    <Editor fileName="test.cs" initial={initial()} onSave={async model => { saved.push(model.getValue()) }} />
  </HassCtx.Provider></QueryClientProvider>, div)
  await Promise.resolve()
  const editor = editors[0]
  editor.change('unsaved changes')
  div.querySelector('ha-combo-box')!.dispatchEvent(new CustomEvent('value-changed', { detail: { value: '24' } }))
  div.querySelector('ha-switch')!.dispatchEvent(new Event('change'))
  setInitial('new prop value')
  expect(editors).toHaveLength(1)
  expect(editor.model.getValue()).toBe('unsaved changes')
  expect(editor.options.fontSize).toBe(24)
  expect(vim.init).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(600)
  div.querySelector('ha-button')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.advanceTimersByTimeAsync(0)
  expect(saved).toEqual(['formatted'])
  dispose()
  expect(editor.disposed).toBe(true)
  expect(editor.model.disposed).toBe(true)
  expect(vim.dispose).toHaveBeenCalledTimes(1)
  expect(completions).toHaveLength(0); expect(hovers).toHaveLength(0)
})
