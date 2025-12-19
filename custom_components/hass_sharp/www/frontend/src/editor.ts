import { LitElement, html } from 'lit'
import { customElement, property } from 'lit/decorators.js'
import * as monaco from 'monaco-editor'
import type { CompletionItem, HomeAssistant } from './types'
import editorCss from 'monaco-editor/min/vs/editor/editor.main.css?inline'
import type { MessageBase } from 'home-assistant-js-websocket'
import visualAssistTheme from './visual-assist.json' with { type: 'json' }

monaco.languages.register({ id: 'csharp', extensions: ['cs'] })
// @ts-ignore
monaco.editor.defineTheme('visual-assist', visualAssistTheme)

function itemToLabel(item: CompletionItem) {
  if (!item.displayTextPrefix && !item.displayTextSuffix) return item.displayText

  const res = `${item.displayTextPrefix}${item.displayText}${item.displayTextSuffix}`

  return res
}

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

    // Inject Monaco CSS globally to ensure overflow widgets (hovers) are styled
    const styleId = 'monaco-global-styles'
    if (!document.getElementById(styleId)) {
      const style = document.createElement('style')
      style.id = styleId
      style.textContent = editorCss
      document.head.appendChild(style)
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
            suggestions: completions.map(item => {
              const label = itemToLabel(item)
              return {
                label,
                kind: tagsToKind(item.tags),
                insertText: label,
                range: {
                  startLineNumber: position.lineNumber,
                  endLineNumber: position.lineNumber,
                  startColumn: position.column,
                  endColumn: position.column,
                },
              }
            }),
          }
        } catch (e) {
          console.error('Failed to get completions', e)
          return { suggestions: [] }
        }
      },
    })

    try {
      this.editor = monaco.editor.create(container, {
        value: `public class MyAutomation : Automation
{
    public void ExampleAutomation()
    {
        
    }
}`,
        language: 'csharp',
        theme: 'visual-assist',
        automaticLayout: true,
        // Set fixedOverflowWidgets to false to keep it inside the component's DOM
        fixedOverflowWidgets: false,
        readOnly: false,
        domReadOnly: false,
        renderLineHighlight: 'all',
        quickSuggestions: true,
        suggest: {
          insertMode: 'replace',
        },
        glyphMargin: true, // Enable glyph margin to see if that helps with hit-testing
      })

      // Validation logic
      const validate = async () => {
        const model = this.editor.getModel()
        if (!model) return

        try {
          const diagnostics = await this.hass.callWS<any[]>({
            type: 'hass_sharp/get_diagnostics',
            source: model.getValue(),
          })

          const markers = diagnostics.map(d => {
            return {
              startLineNumber: d.startLine,
              startColumn: d.startColumn,
              endLineNumber: d.endLine,
              endColumn: d.endColumn,
              message: d.message,
              origin: 'Compiler',
              severity:
                d.severity === 3
                  ? monaco.MarkerSeverity.Error
                  : d.severity === 2
                  ? monaco.MarkerSeverity.Warning
                  : monaco.MarkerSeverity.Info,
            } as monaco.editor.IMarkerData
          })

          monaco.editor.setModelMarkers(model, 'csharp', markers)
        } catch (e) {
          console.error('Failed to get diagnostics', e)
        }
      }

      // Validate on change with debounce
      let timeoutId: number
      this.editor.onDidChangeModelContent(() => {
        clearTimeout(timeoutId)
        timeoutId = setTimeout(validate, 500)
      })

      // Initial validation
      validate()
    } catch (e) {
      console.error('Failed to create Monaco', e)
    }
  }

  render() {
    return html`
      <style>
        :host {
          display: block;
          height: 100%;
          overflow: visible !important;
        }
        #editor {
          flex-grow: 1;
          width: 100%;
          display: block;
          position: relative;
          overflow: visible !important;
        }
        ha-card {
          height: 100%;
          display: flex;
          flex-direction: column;
          margin: 0 !important;
          border-radius: 0;
          overflow: visible !important;
        }

        ${editorCss}
      </style>
      <ha-card>
        <div id="editor" @keydown=${(e: KeyboardEvent) => e.stopPropagation()}></div>
      </ha-card>
    `
  }
}
