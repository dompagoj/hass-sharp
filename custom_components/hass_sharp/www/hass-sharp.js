import { LitElement, html, css } from 'https://unpkg.com/lit-element@2.0.1/lit-element.js?module'

class HassSharpView extends LitElement {
  // Home Assistant sets this property automatically
  static get properties() {
    return {
      hass: { type: Object },
      narrow: { type: Boolean },
      route: { type: Object },
      panel: { type: Object },
      config: { type: Object },
    }
  }

  render() {
    console.log(this.config)
    return html` <div>Well hello there123123 ${this.hass.states['input_number.test'].state}</div> `
  }
}

// THIS NAME MUST MATCH 'component_name' FROM PYTHON
customElements.define('ha-panel-hass-sharp-view', HassSharpView)
