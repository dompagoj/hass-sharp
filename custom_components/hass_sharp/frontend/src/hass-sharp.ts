import { LitElement, html, nothing } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import { keyed } from 'lit/directives/keyed.js'
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
  declare public hass: HomeAssistant

  private _solidDispose?: () => void
  private _stylesheetReady = false
  private _stylesheetFailed = false
  private _stylesheetAttempt = 0
  // Home Assistant sets a new `hass` on every update: the app reads the latest through `liveHass`.
  private _hass = createSignal<HomeAssistant>()

  protected override createRenderRoot(): HTMLElement | DocumentFragment {
    return this
  }

  override connectedCallback() {
    super.connectedCallback()
    // HA detaches hidden panels after five minutes, then reuses the same element on return.
    // firstUpdated only runs once, so every connection needs a chance to mount again.
    // Chrome may refetch the reinserted link: an earlier load event no longer proves CSS is ready.
    this._stylesheetReady = !!this.querySelector<HTMLLinkElement>('#cssLink')?.sheet
    this.requestUpdate()
  }

  protected override updated(changed: Map<PropertyKey, unknown>) {
    if (changed.has('hass') && this.hass) this._hass[1](() => this.hass)
    this._mount()
  }

  private _mount() {
    if (!this.isConnected || !this._stylesheetReady || !this.hass || this._solidDispose) return
    const container = this.querySelector('#app')!
    injectGlobalStyle()
    this._hass[1](() => this.hass)
    const current = this._hass[0] as () => HomeAssistant
    this._solidDispose = render(() => App({ hass: liveHass(current) }), container)
  }

  override disconnectedCallback() {
    super.disconnectedCallback()
    this._solidDispose?.()
    this._solidDispose = undefined
    this._stylesheetReady = false
  }

  private _stylesheetLoaded() {
    this._stylesheetReady = true
    this._stylesheetFailed = false
    this.requestUpdate()
  }

  private _stylesheetError() {
    this._stylesheetReady = false
    this._stylesheetFailed = true
    this.requestUpdate()
  }

  private _retryStylesheet() {
    this._stylesheetFailed = false
    this._stylesheetAttempt++
    this.requestUpdate()
  }

  override render() {
    return html`
      ${keyed(this._stylesheetAttempt, html`<link id="cssLink" rel="stylesheet" href=${stylesheetUrl}
        @load=${this._stylesheetLoaded} @error=${this._stylesheetError} />`)}
      ${!this._stylesheetReady ? this._stylesheetFailed
        ? html`<p role="alert">Could not load HassSharp styles. <button @click=${this._retryStylesheet}>Try again</button></p>`
        : html`<p role="status">Loading HassSharp…</p>` : nothing}
      <div class="h-full" id="app"></div>
    `
  }
}
