import { readFileSync } from 'node:fs'
import { expect, it } from 'vitest'

// Happy DOM has no layout: these check the rules the full-screen layout keys on (360 px included).
const shell = readFileSync('src/styles/editor-shell.css', 'utf8')
const flow = readFileSync('src/styles/flow.css', 'utf8')
const phone = (css: string) => [...css.matchAll(/@media \(max-width: 699px\) \{([\s\S]*?)\n\}/g)].map(m => m[1]).join('\n')

it('gives the floating "Add step" button way to the canvas toolbar\'s Add a step in full screen on a phone', () => {
  expect(phone(shell)).toMatch(/\.hs-visual--fullscreen \.hs-visual-workspace > \.hs-lib \{ display: none; \}/)
  expect(shell).not.toMatch(/\.hs-visual--fullscreen \.hs-visual-workspace > \.hs-lib \{ top: 64px; \}/)
})

it('keeps canvas messages below the compact full-screen bar, and below the "Add step" button on a phone', () => {
  // The compact bar is raised over the canvas (z-index 12), so a message at the canvas top would sit under it.
  expect(shell).toMatch(/\.hs-visual--fullscreen \.hs-topbar \{[^}]*z-index: 12; top: 12px;/)
  expect(flow).toMatch(/\.hs-visual--fullscreen \.hs-flow \.hs-connection-prompt, \.hs-app \.hs-visual--fullscreen \.hs-flow \.hs-canvas-notice \{ top: 72px; \}/)
  expect(flow).toMatch(/\.hs-visual--fullscreen \.hs-flow \.hs-connection-prompt ~ \.hs-canvas-notice \{ top: 112px; \}/)
  expect(phone(flow)).toMatch(/\.hs-connection-prompt, \.hs-app \.hs-canvas-notice \{ top: 60px; \}/)
  expect(phone(flow)).toMatch(/\.hs-connection-prompt ~ \.hs-canvas-notice \{ top: 100px; \}/)
})
