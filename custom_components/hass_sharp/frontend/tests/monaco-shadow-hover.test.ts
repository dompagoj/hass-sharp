import { readFileSync } from 'node:fs'
import { expect, it, vi } from 'vitest'

// Exercise the shipped listener body, not a second implementation of its containment check.
const mouseHandler = readFileSync('node_modules/monaco-editor/esm/vs/editor/browser/controller/mouseHandler.js', 'utf8')
const body = mouseHandler.match(/this\._mouseLeaveMonitor = addDisposableListener\([^\n]+, 'mousemove', \(e\) => \{([\s\S]+?)\n                \}\);/)?.[1]
if (!body) throw new Error('Monaco mouseleave listener changed; review the pinned shadow DOM patch')

it('keeps hover alive through retargeted mousemoves in nested open shadow roots and leaves for outside moves', () => {
  const outer = document.createElement('div'); document.body.append(outer)
  const outerRoot = outer.attachShadow({ mode: 'open' })
  const inner = document.createElement('div'); outerRoot.append(inner)
  const innerRoot = inner.attachShadow({ mode: 'open' })
  const editor = document.createElement('div'); innerRoot.append(editor)
  const text = document.createElement('span'); editor.append(text)
  const leave = vi.fn()
  const listener = new Function('EditorMouseEvent', `return function(e) {${body}}`)(class { constructor(public event: Event) {} })
    .bind({ viewHelper: { viewDomNode: editor }, _onMouseLeave: leave })
  let documentMoves = 0
  const onMove = (event: Event) => {
    documentMoves++
    // Happy DOM exposes the original target; browsers retarget it to the outer shadow host.
    // Keep the actual dispatched composed path, supplying browser document target semantics.
    const browserEvent = { target: event.composedPath().includes(editor) ? outer : event.target,
      composedPath: () => event.composedPath() }
    listener(browserEvent)
  }
  document.addEventListener('mousemove', onMove)
  try {
    text.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, composed: true }))
    expect(documentMoves).toBe(1)
    expect(leave).not.toHaveBeenCalled()
    document.body.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, composed: true }))
    expect(documentMoves).toBe(2)
    expect(leave).toHaveBeenCalledTimes(1)
  } finally { document.removeEventListener('mousemove', onMove); outer.remove() }
})
