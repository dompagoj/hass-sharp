export function panelStylesheetUrl(moduleUrl: string): string {
  // HA caches static assets for 31 days. Reuse the module's version query so
  // an integration upgrade also invalidates the browser's cached stylesheet.
  return `/hass-sharp-static/hass-sharp.css${new URL(moduleUrl).search}`
}
