import { expect } from 'vitest'

/** A drag event with `data` (happy-dom exposes DragEvent as Event and omits DataTransfer and coordinates). */
export function dragEvent(type: string, data: DataTransfer, clientX = 0, clientY = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY })
  Object.defineProperty(event, 'dataTransfer', { value: data })
  return event
}

/** The step library's item for the step named `label`. */
export function libraryItem(root: Element, label: string): HTMLButtonElement {
  const item = [...root.querySelectorAll<HTMLButtonElement>('.hs-lib__item')].find(item => item.querySelector('strong')?.textContent === label)
  expect(item, label).toBeDefined()
  return item!
}

/** Drags the library's `label` step onto the canvas at `at` (a client point), the way a person adds a step. */
export function dropStep(root: Element, label: string, at: { x: number; y: number } = { x: 400, y: 300 }): void {
  const data = new DataTransfer()
  libraryItem(root, label).dispatchEvent(dragEvent('dragstart', data))
  const canvas = root.querySelector('.hs-flow')!
  canvas.dispatchEvent(dragEvent('dragover', data, at.x, at.y))
  canvas.dispatchEvent(dragEvent('drop', data, at.x, at.y))
}
