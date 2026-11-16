import { afterEach, expect, it, vi } from 'vitest'
import { createSignal, Show } from 'solid-js'
import { render } from 'solid-js/web'
import { BottomDrawer } from '../src/visual/BottomDrawer'
import { viewportWidth } from './media-fixture'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; vi.unstubAllGlobals(); document.body.innerHTML = '' })

function mount(width = 699, variant?: 'compact' | 'expanded') {
  const resize = viewportWidth(width)
  const host = document.body.appendChild(document.createElement('div'))
  const root = host.attachShadow({ mode: 'open' })
  const [open, setOpen] = createSignal(true)
  const [tab, setTab] = createSignal('first')
  dispose = render(() => <>
    <button id="before">Before drawer</button>
    <BottomDrawer variant={variant} open={open()} onOpenChange={setOpen} tab={tab()} onTabChange={setTab} tabs={[
      { id: 'first', label: 'First', content: () => <Show when={tab() === 'first'}><input aria-label="Drawer field" /></Show> },
      { id: 'second', label: 'Second', content: () => <button>Panel action</button> },
    ]} />
    <button id="after">After drawer</button>
  </>, root)
  const drawer = root.querySelector<HTMLElement>('.hs-drawer')!
  const tabs = [...drawer.querySelectorAll<HTMLButtonElement>('[role="tab"]')]
  return { root, drawer, tabs, resize, open }
}

it.each(['Tab', 'Shift+Tab'])('exposes focus after ordinary %s traversal out of the mobile drawer', direction => {
  const { root, drawer, tabs, open } = mount()
  const origin = direction === 'Tab' ? drawer.querySelector<HTMLButtonElement>('.hs-drawer__toggle')! : tabs[0]
  const destination = root.querySelector<HTMLButtonElement>(direction === 'Tab' ? '#after' : '#before')!
  origin.focus()
  const event = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: direction === 'Shift+Tab', bubbles: true, cancelable: true })
  origin.dispatchEvent(event)
  expect(event.defaultPrevented).toBe(false)
  // happy-dom does not implement the browser's default Tab focus movement.
  destination.focus()
  expect(root.activeElement).toBe(destination)
  expect(open()).toBe(false)
})

it('keeps the mobile drawer open for internal focus, browser blur and tab changes that remove the focused field', () => {
  const { root, drawer, tabs, open } = mount()
  tabs[0].focus()
  const field = drawer.querySelector<HTMLInputElement>('input')!
  field.focus()
  expect(open()).toBe(true)
  field.blur()
  expect(open()).toBe(true)
  field.focus()
  tabs[1].click()
  expect(field.isConnected).toBe(false)
  expect(tabs[1].getAttribute('aria-selected')).toBe('true')
  expect(open()).toBe(true)
  tabs[1].focus()
  drawer.querySelector<HTMLButtonElement>('.hs-drawer__panel:not([hidden]) button')!.focus()
  expect(open()).toBe(true)
  tabs[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true, cancelable: true }))
  expect(root.activeElement).toBe(tabs[0])
  expect(tabs[0].getAttribute('aria-selected')).toBe('true')
  expect(open()).toBe(true)
})

it('follows a resize across the mobile boundary before deciding whether outside focus closes the drawer', () => {
  const { root, tabs, resize, open } = mount(700)
  tabs[0].focus()
  root.querySelector<HTMLButtonElement>('#before')!.focus()
  expect(open()).toBe(true)
  resize(699)
  tabs[0].focus()
  root.querySelector<HTMLButtonElement>('#before')!.focus()
  expect(open()).toBe(false)
})

it.each(['#before', '#after'])('keeps the compact mobile drawer open when focus moves outside to %s', destination => {
  const { root, drawer, open } = mount(699, 'compact')
  drawer.querySelector<HTMLInputElement>('input')!.focus()
  const target = root.querySelector<HTMLButtonElement>(destination)!
  target.focus()
  expect(root.activeElement).toBe(target)
  expect(open()).toBe(true)
})
