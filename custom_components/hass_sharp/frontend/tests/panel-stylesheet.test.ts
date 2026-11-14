import { expect, it } from 'vitest'
import { panelStylesheetUrl } from '../src/panel-stylesheet'

it('requests fresh CSS when Home Assistant changes the panel module version', () => {
  const oldUrl = panelStylesheetUrl('http://homeassistant.local:8123/hass-sharp-static/hass-sharp.js?v=1794489080000000000')
  const newUrl = panelStylesheetUrl('http://homeassistant.local:8123/hass-sharp-static/hass-sharp.js?v=1794490000000000000')

  expect(oldUrl).toBe('/hass-sharp-static/hass-sharp.css?v=1794489080000000000')
  expect(newUrl).toBe('/hass-sharp-static/hass-sharp.css?v=1794490000000000000')
  expect(newUrl).not.toBe(oldUrl)
})

it('supports an unversioned module without appending an invalid query', () => {
  expect(panelStylesheetUrl('https://ha.example/hass-sharp-static/hass-sharp.js'))
    .toBe('/hass-sharp-static/hass-sharp.css')
})

it('keeps the screen reader region Monaco adds to the page body off screen, where the panel stylesheet cannot reach it', async () => {
  // Monaco appends its aria container to document.body, outside the panel; unstyled, it made the
  // whole Home Assistant page scroll once a code editor had been opened.
  const { injectGlobalStyle } = await import('../src/panel-stylesheet')
  injectGlobalStyle(document); injectGlobalStyle(document)
  const styles = document.head.querySelectorAll('#hass-sharp-global')
  expect(styles).toHaveLength(1)
  expect(styles[0].textContent).toMatch(/\.monaco-aria-container\s*\{\s*position: absolute; left: -999em; top: 0; width: 1px; height: 1px; overflow: hidden;/)
  expect(styles[0].textContent).toContain("font-family: 'codicon'")
})
