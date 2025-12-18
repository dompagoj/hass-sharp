import { LitElement, css, html } from 'lit'
import { customElement, property, query } from 'lit/decorators.js'
import './editor'
import type { HomeAssistant } from './types'

@customElement('ha-panel-hass-sharp-view')
export class HassSharpView extends LitElement {
  @property({ attribute: false })
  public hass!: HomeAssistant

  static get styles() {
    return css`
      ha-panel-hass-sharp-view {
        display: block;
        height: calc(100vh);
        overflow: hidden;
      }
    `
  }

  render() {
    return html`
      <style>
        ha-panel-hass-sharp-view {
          display: block;
          height: 100vh;
          overflow: hidden;
        }
      </style>
      <hass-sharp-editor></hass-sharp-editor>
    `
  }
}
