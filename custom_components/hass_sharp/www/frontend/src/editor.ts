import { LitElement, html } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import * as monaco from 'monaco-editor'
import type { CompletionItem, HomeAssistant } from './types'
import editorCss from 'monaco-editor/min/vs/editor/editor.main.css?inline'
import type { MessageBase } from 'home-assistant-js-websocket'

monaco.languages.register({ id: 'csharp', extensions: ['cs'] })

function tagsToKind(tags: string[]) {
  const first = tags[0]

  if (first === 'Method') return monaco.languages.CompletionItemKind.Method
  if (first === 'Class') return monaco.languages.CompletionItemKind.Class
  if (first === 'Property') return monaco.languages.CompletionItemKind.Property
  if (first === 'Enum') return monaco.languages.CompletionItemKind.Enum
  if (first === 'Delegate') return monaco.languages.CompletionItemKind.Function
  if (first === 'Keyword') return monaco.languages.CompletionItemKind.Keyword
  if (first === 'Structure') return monaco.languages.CompletionItemKind.Struct
  if (first === 'ExtensionMethod') return monaco.languages.CompletionItemKind.Function

  console.log('Unknown tag: ', first)
  return monaco.languages.CompletionItemKind.Snippet
}

@customElement('hass-sharp-editor')
export class HassSharpEditor extends LitElement {
  private editor: monaco.editor.IStandaloneCodeEditor = null!

  @property({ attribute: false })
  public hass!: HomeAssistant

  override createRenderRoot() {
    return this
  }

  protected override firstUpdated() {
    const container = this.querySelector('#editor') as HTMLDivElement
    if (!container) {
      console.error('Editor container not found')
      return
    }

    // Register completion provider
    monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: async (model, position) => {
        const source = model.getValue()
        const offset = model.getOffsetAt(position)

        try {
          const message: MessageBase = {
            type: 'hass_sharp/get_completions',
            source: source,
            position: offset,
          }

          const completions = await this.hass.callWS<CompletionItem[]>(message)

          return {
            suggestions: completions.map(item => ({
              label: item.displayText,
              kind: tagsToKind(item.tags),
              insertText: item.displayText,
              range: {
                startLineNumber: position.lineNumber,
                endLineNumber: position.lineNumber,
                startColumn: position.column,
                endColumn: position.column,
              },
            })),
          }
        } catch (e) {
          console.error('Failed to get completions', e)
          return { suggestions: [] }
        }
      },
    })

    try {
      this.editor = monaco.editor.create(container, {
        value: `using HassSharp;

public class MyAutomation : Automation
{
    public void ExampleAutomation()
    {
        
    }
}`,
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
