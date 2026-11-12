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
