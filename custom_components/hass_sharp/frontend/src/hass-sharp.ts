import { LitElement, html } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import { render } from 'solid-js/web'
import App from './App'
import type { HomeAssistant } from './types'

import './style.css'

// Function to "leak" the font to the global scope
const injectGlobalFont = () => {
  if (document.head.querySelector('#monaco-fonts')) return
  const style = document.createElement('style')
  style.id = 'monaco-fonts'
  // Use the name defined in your bundled CSS
  style.innerHTML = `
    @font-face {
      font-family: 'codicon';
      src: url('/hass-sharp-static/codicon.ttf') format('truetype');
    }
  `
  document.head.appendChild(style)
}

@customElement('ha-panel-hass-sharp-view')
export class HassSharpView extends LitElement {
  @property({ attribute: false })
  public hass!: HomeAssistant

  private _solidDispose?: () => void

  protected override createRenderRoot(): HTMLElement | DocumentFragment {
    return this
  }

  protected override firstUpdated() {
    // Stop HA global shortcuts at the panel boundary
    const container = this.renderRoot?.querySelector('#app')

    if (container) {
      container.addEventListener('keydown', e => e.stopPropagation())
      injectGlobalFont()
    }

    if (container) {
      this._solidDispose = render(() => App({ hass: this.hass }), container)
    }
  }

  disconnectedCallback() {
    super.disconnectedCallback()
    if (this._solidDispose) {
      this._solidDispose()
    }
  }

  render() {
    return html`
      <link rel="stylesheet" href="/hass-sharp-static/hass-sharp.css" />

      <div class="h-full" id="app"></div>
    `
  }
}
