import { afterEach, expect, it } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { Dialog } from '../src/components/Dialog'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); document.body.innerHTML = '' })
function setup(preventClose = false) {
  const root = document.createElement('div'); document.body.append(root)
  const [open, setOpen] = createSignal(false)
  dispose = render(() => <>
    <button onClick={() => setOpen(true)}>Open</button>
    <Dialog open={open()} title="Rename automation" preventClose={preventClose} onClose={() => setOpen(false)}
      footer={<button>Save</button>}>
      <label>Name<input autofocus /></label>
    </Dialog>
  </>, root)
  const trigger = root.querySelector('button')!
  trigger.focus(); trigger.click()
  return { root, trigger, open, setOpen }
}
const key = (element: Element, value: string, shiftKey = false) => element.dispatchEvent(new KeyboardEvent('keydown', { key: value, shiftKey, bubbles: true, cancelable: true }))

it('names a modal, focuses its field, traps both Tab directions, and returns focus after Escape', async () => {
  const { root, trigger, open } = setup()
  await Promise.resolve()
  const dialog = root.querySelector('[role="dialog"]')!
  expect(dialog).not.toBeNull()
  expect(dialog.getAttribute('aria-modal')).toBe('true')
  expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)?.textContent).toBe('Rename automation')
  const input = dialog.querySelector('input')!
  expect(document.activeElement).toBe(input)
  const buttons = dialog.querySelectorAll('button')
  buttons[buttons.length - 1].focus(); key(buttons[buttons.length - 1], 'Tab')
  expect(document.activeElement).toBe(buttons[0])
  key(buttons[0], 'Tab', true)
  expect(document.activeElement).toBe(buttons[buttons.length - 1])
  key(input, 'Escape')
  await Promise.resolve()
  expect(open()).toBe(false)
  expect(document.activeElement).toBe(trigger)
})
it('keeps a busy modal open on Escape, close button and backdrop', async () => {
  const { root, open, setOpen, trigger } = setup(true)
  await Promise.resolve()
  const dialog = root.querySelector('[role="dialog"]')!
  expect(dialog).not.toBeNull()
  key(dialog, 'Escape')
  ;(dialog.querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click()
  ;(root.querySelector('.hs-dialog-backdrop') as HTMLElement).click()
  expect(open()).toBe(true)
  setOpen(false)
  await Promise.resolve()
  expect(document.activeElement).toBe(trigger)
})

it('stops modal keystrokes before native Home Assistant ancestor shortcut listeners', async () => {
  const { root } = setup()
  await Promise.resolve()
  const shortcuts: string[] = []
  root.addEventListener('keydown', event => shortcuts.push(event.key))
  key(root.querySelector('input')!, 'e')
  expect(shortcuts).toEqual([])
})
