import { createEffect } from 'solid-js'
import * as monaco from 'monaco-editor'
import type { MessageBase } from 'home-assistant-js-websocket'
import { initVimMode, type VimAdapterInstance } from 'monaco-vim'

import type { CompletionItem } from '../types'
import visualAssistTheme from '../visual-assist.json'

import 'monaco-editor/min/vs/editor/editor.main.css'
import { useHass } from '../context'
import { useLocalStorage } from '../hooks'

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
  if (first === 'Local') return monaco.languages.CompletionItemKind.Variable
  if (first === 'Interface') return monaco.languages.CompletionItemKind.Interface
  if (first === 'Snippet') return monaco.languages.CompletionItemKind.Snippet
  if (first === 'TypeParameter') return monaco.languages.CompletionItemKind.TypeParameter
  if (first === 'TypeParameter') return monaco.languages.CompletionItemKind.TypeParameter
  if (first === 'Namespace') return monaco.languages.CompletionItemKind.Module
  if (first === 'Field') return monaco.languages.CompletionItemKind.Field
  if (first === 'Parameter') return monaco.languages.CompletionItemKind.Variable

  console.warn('Unknown tag: ', first)
  return monaco.languages.CompletionItemKind.Snippet
}

export const Editor = (props: { initial?: string }) => {
  const hass = useHass()
  let editorRef: HTMLDivElement = undefined!
  let statusBarRef: HTMLDivElement = undefined!
  let editor: monaco.editor.IStandaloneCodeEditor

  const [vimEnabled, setVimEnabled] = useLocalStorage('vim-enabled', false)

  let monacoVimMode: VimAdapterInstance | undefined = undefined

  createEffect(() => {
    if (!editorRef) return

    let completionTimeout: number
    monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: async (model, position) => {
        return new Promise(resolve => {
          clearTimeout(completionTimeout)
          completionTimeout = setTimeout(async () => {
            const source = model.getValue()
            const offset = model.getOffsetAt(position)

            // Calculate word range for replacement
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
          }, 150) // 150ms debounce
        })
      },
    })

    editor = monaco.editor.create(editorRef, {
      value:
        props.initial ??
        `public class MyAutomation : Automation
{
    public void ExampleAutomation()
    {

    }
}`,
      language: 'csharp',
      inlayHints: {
        enabled: 'on',
      },
      theme: 'visual-assist',
      renderLineHighlight: 'all',
      fixedOverflowWidgets: true,
      automaticLayout: true,
      domReadOnly: true,
      smoothScrolling: true,
      cursorSmoothCaretAnimation: 'on',
      padding: {
        top: 25,
      },
      hover: {
        enabled: true,
        delay: 300,
        sticky: true,
        above: true,
      },
      suggest: {
        insertMode: 'replace',
        snippetsPreventQuickSuggestions: false,
        showWords: false,
        showMethods: true,
        showFunctions: true,
        showIcons: true,
        showConstructors: true,
        showFields: true,
        showVariables: true,
        showClasses: true,
        showInterfaces: true,
        showModules: true,
        showProperties: true,
        showEvents: true,
        showOperators: true,
        showUnits: true,
        showValues: true,
        showConstants: true,
        showEnums: true,
        showEnumMembers: true,
        showKeywords: true,
        showFolders: true,
        showColors: true,
        showFiles: true,
        showReferences: true,
        showSnippets: false,
        showTypeParameters: true,
        showIssues: true,
        showUsers: true,
      },
    })

    const keydownCallback = (e: KeyboardEvent) => {
      console.log(e)
      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault()
        console.log('SAVE')
      }

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
      } catch (e) {
        console.error('Failed to get diagnostics', e)
      }
    }

    let timeoutId: number
    editor.onDidChangeModelContent(() => {
      clearTimeout(timeoutId)
      timeoutId = setTimeout(validate, 500)
    })

    // Initial validation
    validate()
    monaco.languages.registerHoverProvider('csharp', {
      provideHover: async (model, position) => {
        console.log('Hover provider uuu')
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

    return () => {
      document.removeEventListener('keydown', keydownCallback)
    }
  })

  createEffect(() => {
    if (!editorRef || !editor) return

    if (vimEnabled()) monacoVimMode = initVimMode(editor, statusBarRef)
    else monacoVimMode?.dispose()
  })

  return (
    <>
      <ha-card class="flex justify-end p-4 my-4 items-center gap-2">
        <span>VIM</span>
        <ha-switch checked={vimEnabled()} onChange={() => setVimEnabled(!vimEnabled())}></ha-switch>
      </ha-card>
      <div ref={statusBarRef} />
      <div class="h-full bg-red-500" ref={editorRef} id="editor" />
    </>
  )
}
