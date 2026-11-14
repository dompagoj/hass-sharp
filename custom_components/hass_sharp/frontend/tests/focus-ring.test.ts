import { readFileSync } from 'node:fs'
import { expect, it } from 'vitest'

const sheets = ['base', 'controls', 'widgets', 'flow', 'inspector', 'editor-shell'].map(name => ({ name, css: readFileSync(`src/styles/${name}.css`, 'utf8') }))
/** Every rule as [selector, body], comments removed. */
const rules = (css: string) => [...css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(m => [m[1].trim(), m[2]] as const)

it('never rings a row, a pin cell or a card around a focused control inside it: :focus-within draws no outline or shadow', () => {
  for (const { name, css } of sheets)
    for (const [selector, body] of rules(css))
      if (selector.includes(':focus-within')) expect(body, `${name}.css: ${selector}`).not.toMatch(/outline\s*:\s*(?!none)|box-shadow\s*:/)
})

it('rings an input row only when the row itself is focused, and a control on a card with the standard 2px ring', () => {
  const inspector = rules(sheets.find(s => s.name === 'inspector')!.css)
  const row = inspector.find(([selector]) => selector === '.hs-app [data-input-id]:focus')
  expect(row?.[1]).toMatch(/outline: 2px solid var\(--hs-focus\)/)
  const flow = rules(sheets.find(s => s.name === 'flow')!.css)
  const control = flow.find(([selector]) => selector === '.hs-app .hs-flow .hs-card :is(input, select, textarea, button):focus-visible')
  expect(control?.[1]).toMatch(/outline: 2px solid var\(--hs-focus\); outline-offset: 2px/)
})
