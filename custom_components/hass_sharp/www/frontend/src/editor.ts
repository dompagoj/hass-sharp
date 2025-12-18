import { LitElement, html, unsafeCSS } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import * as monaco from 'monaco-editor'
import type { HomeAssistant } from './types'
import editorCss from 'monaco-editor/min/vs/editor/editor.main.css?inline'

monaco.languages.register({ id: 'csharp', extensions: ['cs'] })

const tm = monaco.editor.createModel(`class Test {}`, 'typescript', monaco.Uri.parse('file:///main.ts'))

@customElement('hass-sharp-editor')
export class HassSharpView extends LitElement {
  private editor: monaco.editor.IStandaloneCodeEditor = null!

  @property({ attribute: false })
  public hass!: HomeAssistant

  override createRenderRoot() {
    return this
  }

  static get styles() {
    return unsafeCSS(editorCss)
  }

  protected override firstUpdated() {
    const container = this.querySelector('#editor') as HTMLDivElement
    if (!container) {
      console.error('Editor container not found')
      return
    }
    try {
      this.editor = monaco.editor.create(container, {
        model: tm,
        language: 'csharp',
        theme: 'vs-dark',
        automaticLayout: true,
      })
    } catch (e) {
      console.error('Failed to create Monaco', e)
    }
  }

  render() {
    return html`
      <style>
        #editor {
          flex-grow: 1;
          width: 100%;
          display: block;
          position: relative;
          overflow: hidden;
        }
        ha-card {
          height: 100%;
          display: flex;
          flex-direction: column;
          margin: 0 !important;
          border-radius: 0;
        }

        ${editorCss}
      </style>
      <ha-card>
        <div id="editor" @keydown=${(e: KeyboardEvent) => e.stopPropagation()}></div>
      </ha-card>
    `
  }
}
