import * as monaco from 'monaco-editor'
import type { CompletionItem, HomeAssistant } from '../../types'
import type { MessageBase } from 'home-assistant-js-websocket'
import { createSignal, onCleanup } from 'solid-js'

function itemToLabel(item: CompletionItem) {
  if (!item.displayTextPrefix && !item.displayTextSuffix) return item.displayText

  const res = `${item.displayTextPrefix}${item.displayText}${item.displayTextSuffix}`

  return res
}

const tagToKindMap: { [key: string]: monaco.languages.CompletionItemKind } = {
  Method: monaco.languages.CompletionItemKind.Method,
  Class: monaco.languages.CompletionItemKind.Class,
  Property: monaco.languages.CompletionItemKind.Property,
  Enum: monaco.languages.CompletionItemKind.Enum,
  Delegate: monaco.languages.CompletionItemKind.Function,
  Keyword: monaco.languages.CompletionItemKind.Keyword,
  Structure: monaco.languages.CompletionItemKind.Struct,
  ExtensionMethod: monaco.languages.CompletionItemKind.Function,
  Local: monaco.languages.CompletionItemKind.Variable,
  Interface: monaco.languages.CompletionItemKind.Interface,
  Snippet: monaco.languages.CompletionItemKind.Snippet,
  TypeParameter: monaco.languages.CompletionItemKind.TypeParameter,
  Namespace: monaco.languages.CompletionItemKind.Snippet,
  Field: monaco.languages.CompletionItemKind.Field,
  Parameter: monaco.languages.CompletionItemKind.Variable,
}

function tagsToKind(tags: string[]) {
  const first = tags[0]
  const found = tagToKindMap[first]

  if (found) return found

  console.warn('Unknown tag: ', first)
  return monaco.languages.CompletionItemKind.Snippet
}

export const useEditorActions = (hass: HomeAssistant) => {
  let completionTimeout: number

  let diposer: () => void

  const [saveDisabled, setSaveDisabled] = createSignal(false)

  const onScriptSave = async (e: KeyboardEvent, editor: monaco.editor.IStandaloneCodeEditor) => {
    e.preventDefault()
    const model = editor.getModel()
    if (!model) return
    try {
      setSaveDisabled(true)

      const message: MessageBase = {
        type: 'hass_sharp/format_source',
        source: model.getValue(),
      }
      const formatted = await hass.callWS<string>(message)

      const currPos = editor.getPosition()
      let textToRight = ''
      if (currPos) {
        const lineContent = model.getLineContent(currPos.lineNumber)
        textToRight = lineContent
          .substring(currPos.column - 1)
          .trimStart()
          .substring(0, 10)
      }

      editor.executeEdits('format', [
        {
          range: model.getFullModelRange(),
          text: formatted,
        },
      ])

      if (currPos && textToRight) {
        const newLineContent = model.getLineContent(currPos.lineNumber)
        const newColumn = newLineContent.indexOf(textToRight)
        if (newColumn !== -1) {
          editor.setPosition({ lineNumber: currPos.lineNumber, column: newColumn + 1 })
        } else {
          const firstCol = model.getLineFirstNonWhitespaceColumn(currPos.lineNumber)
          editor.setPosition({ lineNumber: currPos.lineNumber, column: firstCol || 1 })
        }
      }
      editor.focus()
    } finally {
      setSaveDisabled(false)
    }
  }

  const register = (editor: monaco.editor.IStandaloneCodeEditor) => {
    const disposeCompletionProvider = monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: (model, position) => {
        return new Promise(resolve => {
          clearTimeout(completionTimeout)
          completionTimeout = setTimeout(async () => {
            const source = model.getValue()
            const offset = model.getOffsetAt(position)

            const word = model.getWordUntilPosition(position)
            const range = {
              startLineNumber: position.lineNumber,
              endLineNumber: position.lineNumber,
              startColumn: word.startColumn,
              endColumn: word.endColumn,
            }

            try {
              const message: MessageBase = {
                type: 'hass_sharp/get_completions',
                source: source,
                position: offset,
              }

              const completions = await hass.callWS<CompletionItem[]>(message)

              resolve({
                suggestions: completions.map(item => {
                  const label = itemToLabel(item)
                  return {
                    label,
                    kind: tagsToKind(item.tags),
                    insertText: label,
                    range,
                  }
                }),
              })
            } catch (e) {
              console.error('Failed to get completions', e)
              resolve({ suggestions: [] })
            }
          }, 150)
        })
      },
    })

    const keydownCallback = async (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 's') onScriptSave(e, editor)

      e.stopPropagation()
    }

    document.addEventListener('keydown', keydownCallback)

    const validate = async () => {
      const model = editor.getModel()
      if (!model) return

      try {
        const diagnostics = await hass.callWS<any[]>({
          type: 'hass_sharp/get_diagnostics',
          source: model.getValue(),
        })

        if (diagnostics.length) console.error(diagnostics)

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

        setSaveDisabled(!!markers.length)
      } catch (e) {
        console.error('Failed to get diagnostics', e)
        setSaveDisabled(false)
      }
    }

    let timeoutId: number
    editor.onDidChangeModelContent(() => {
      setSaveDisabled(true)
      clearTimeout(timeoutId)
      timeoutId = setTimeout(validate, 500)
    })

    const disposeHoverProvider = monaco.languages.registerHoverProvider('csharp', {
      provideHover: async (model, position) => {
        const source = model.getValue()
        const offset = model.getOffsetAt(position)

        try {
          const hoverText = await hass.callWS<string | null>({
            type: 'hass_sharp/get_hover',
            source: source,
            position: offset,
          })

          if (!hoverText) return null

          return {
            contents: [{ value: hoverText }],
          }
        } catch (e) {
          console.error('Failed to get hover', e)
          return null
        }
      },
    })

    // Initial validation
    validate()

    diposer?.()
    diposer = () => {
      disposeHoverProvider.dispose()
      disposeCompletionProvider.dispose()
      document.removeEventListener('keydown', keydownCallback)
    }
  }

  onCleanup(() => {
    diposer?.()
  })

  return {
    register,
    saveDisabled,
  }
}
