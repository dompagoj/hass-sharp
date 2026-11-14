export function panelStylesheetUrl(moduleUrl: string): string {
  // HA caches static assets for 31 days. Reuse the module's version query so
  // an integration upgrade also invalidates the browser's cached stylesheet.
  return `/hass-sharp-static/hass-sharp.css${new URL(moduleUrl).search}`
}

/**
 * Styles for what lives outside the panel's own stylesheet: the codicon font, and the screen reader
 * region Monaco appends to document.body (unstyled, it sits below the page and makes all of it scroll).
 */
export function injectGlobalStyle(doc: Document = document) {
  if (doc.head.querySelector('#hass-sharp-global')) return
  const style = doc.createElement('style')
  style.id = 'hass-sharp-global'
  style.textContent = `
    @font-face {
      font-family: 'codicon';
      src: url('/hass-sharp-static/codicon.ttf') format('truetype');
    }
    /* Visually hidden, still read: a long announcement must not run back into view from left: -999em. */
    .monaco-aria-container { position: absolute; left: -999em; top: 0; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  `
  doc.head.appendChild(style)
}
