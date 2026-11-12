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
  const settingsTab = div.querySelector<HTMLButtonElement>('[role="tab"][id$="settings"]')!
  expect(settingsTab).not.toBeNull()
  settingsTab.click()
  const font = div.querySelector('select')!
  font.value = '24'; font.dispatchEvent(new Event('change', { bubbles: true }))
  div.querySelector<HTMLInputElement>('[role="switch"]')!.click()
  setInitial('new prop value')
  expect(editors).toHaveLength(1)
  expect(editor.model.getValue()).toBe('unsaved changes')
  expect(editor.options.fontSize).toBe(24)
  expect(vim.init).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(600)
  div.querySelector<HTMLButtonElement>('[data-save]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.advanceTimersByTimeAsync(0)
  expect(saved).toEqual(['formatted'])
  dispose()
  expect(editor.disposed).toBe(true)
  expect(editor.model.disposed).toBe(true)
  expect(vim.dispose).toHaveBeenCalledTimes(1)
  expect(completions).toHaveLength(0); expect(hovers).toHaveLength(0)
})

it('moves between editor tabs with arrow keys without recreating the model', async () => {
  const div = document.createElement('div'); document.body.append(div)
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS: async () => [] } as any}>
    <Editor fileName="test.cs" initial="source" onSave={async () => {}} />
  </HassCtx.Provider></QueryClientProvider>, div)
  const tabs = div.querySelectorAll<HTMLButtonElement>('[role="tab"]')
  expect(tabs).toHaveLength(2)
  tabs[0].focus()
  tabs[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }))
  expect(document.activeElement).toBe(tabs[1])
  expect(tabs[1].getAttribute('aria-selected')).toBe('true')
  expect(tabs[0].tabIndex).toBe(-1)
  tabs[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }))
  expect(document.activeElement).toBe(tabs[0])
  expect(editors).toHaveLength(1)
})

it('keeps every editor keyboard event away from Home Assistant without blocking typing or Monaco shortcuts', () => {
  const div = document.createElement('div'); document.body.append(div)
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS: async () => [] } as any}>
    <Editor fileName="keys.cs" initial="source" onSave={async () => {}} />
  </HassCtx.Provider></QueryClientProvider>, div)
  // Monaco owns descendants of its host, including text input and suggestion/find widgets.
  const input = document.createElement('textarea'); div.querySelector('.hs-monaco')!.append(input)
  const homeAssistant = vi.fn()
  const local = vi.fn()
  for (const type of ['keydown', 'keypress', 'keyup']) {
    document.addEventListener(type, homeAssistant)
    input.addEventListener(type, local)
  }
  try {
    for (const type of ['keydown', 'keypress', 'keyup']) {
      for (const key of ['c', 'e', 'd', 'Escape', 'Enter', 'ArrowDown', 's']) {
        const event = new KeyboardEvent(type, { key, ctrlKey: key === 's', bubbles: true, composed: true, cancelable: true })
        input.dispatchEvent(event)
        expect(event.defaultPrevented).toBe(false)
      }
    }
    expect(local).toHaveBeenCalledTimes(21)
    expect(homeAssistant).not.toHaveBeenCalled()
    // HA shortcuts still work elsewhere in the panel.
    div.dispatchEvent(new KeyboardEvent('keydown', { key: 'c', bubbles: true }))
    expect(homeAssistant).toHaveBeenCalledTimes(1)
  } finally {
    for (const type of ['keydown', 'keypress', 'keyup']) document.removeEventListener(type, homeAssistant)
  }
})

it('opens the C# automation guide without losing the current draft', () => {
  const div = document.createElement('div'); document.body.append(div)
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS: async () => [] } as any}>
    <Editor fileName="help.cs" initial="unsaved source" onSave={async () => {}} />
  </HassCtx.Provider></QueryClientProvider>, div)
  const help = div.querySelector<HTMLAnchorElement>('a[aria-label="C# automation help"]')
  expect(help).not.toBeNull()
  expect(help!.href).toContain('/docs/writing-csharp-automations.md')
  expect(help!.target).toBe('_blank')
  expect(editors[0].model.getValue()).toBe('unsaved source')
})
