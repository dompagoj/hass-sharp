import { LitElement, html } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import App from './App'
import { liveHass } from './context'
import type { HomeAssistant } from './types'
import { injectGlobalStyle, panelStylesheetUrl } from './panel-stylesheet'

import './style.css'

const stylesheetUrl = panelStylesheetUrl(import.meta.url)

@customElement('ha-panel-hass-sharp-view')
export class HassSharpView extends LitElement {
  @property({ attribute: false })
  public hass!: HomeAssistant

  private _solidDispose?: () => void
  // Home Assistant sets a new `hass` on every update: the app reads the latest through `liveHass`.
  private _hass = createSignal<HomeAssistant>()

  protected override createRenderRoot(): HTMLElement | DocumentFragment {
    return this
  }

  protected override updated(changed: Map<PropertyKey, unknown>) {
    if (changed.has('hass') && this.hass) this._hass[1](() => this.hass)
  }

  protected override firstUpdated() {
    // Stop HA global shortcuts at the panel boundary
    const container = this.renderRoot?.querySelector('#app')

    if (container) {
      // container.addEventListener('keydown', e => e.stopPropagation())
      injectGlobalStyle()
    }

    if (container) {
      this._hass[1](() => this.hass)
      const current = this._hass[0] as () => HomeAssistant
      this._solidDispose = render(() => App({ hass: liveHass(current) }), container)
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
      <link id="cssLink" rel="stylesheet" href=${stylesheetUrl} />

      <div class="h-full" id="app"></div>
    `
  }
}
