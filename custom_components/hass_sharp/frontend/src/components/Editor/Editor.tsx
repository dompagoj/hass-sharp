import { createEffect, Show } from 'solid-js'
import { A } from '@solidjs/router'
import * as monaco from 'monaco-editor'
import { initVimMode, type VimAdapterInstance } from 'monaco-vim'

import visualAssistTheme from '../../visual-assist.json'

import 'monaco-editor/min/vs/editor/editor.main.css'
import { useHass } from '../../context'
import { useLocalStorage } from '../../hooks'
import { useMutation } from '@tanstack/solid-query'
import { useEditorActions } from './editor-actions'

monaco.languages.register({ id: 'csharp', extensions: ['cs'] })
// @ts-ignore
monaco.editor.defineTheme('visual-assist', visualAssistTheme)

interface Props {
  fileName: string
  onSave: (model: monaco.editor.ITextModel) => Promise<any>
  initial?: string
  onBackRef?: string
}

export const Editor = (props: Props) => {
  const hass = useHass()
  let editorRef: HTMLDivElement = undefined!
  let statusBarRef: HTMLDivElement = undefined!
  let editor: monaco.editor.IStandaloneCodeEditor

  const [editorSettings, setEditorSettings] = useLocalStorage<{ vimEnabled: boolean; fontSize?: number }>(
    'editor-settings',
    { vimEnabled: false, fontSize: 16 },
  )

  const editorSettingsFont = () => editorSettings().fontSize ?? 16
  let monacoVimMode: VimAdapterInstance | undefined = undefined

  const editorActions = useEditorActions(hass)

  createEffect(() => {
    if (!editorRef) return

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
      fontSize: editorSettingsFont(),
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

    return editorActions.register(editor)
  })

  createEffect(() => {
    if (editorRef && editorSettings().vimEnabled) {
      monacoVimMode = initVimMode(editor, statusBarRef)
    } else monacoVimMode?.dispose()
  })

  const onSaveWrapper = () => {
    const model = editor.getModel()
    const markers = monaco.editor.getModelMarkers({ owner: 'csharp' })

    if (markers.length) return Promise.resolve()

    return props.onSave(model!)
  }

  const saveMut = useMutation(() => ({
    mutationFn: onSaveWrapper,
  }))

  return (
    <>
      <ha-card class="flex p-2 mb-2 grow items-center gap-2 rounded-none! border-none!">
        <div class="flex grow items-center gap-2">
          <Show when={!!props.onBackRef}>
            <A href={props.onBackRef!}>
              <ha-icon-button-arrow-prev class="text-sm" />
            </A>
          </Show>
          <div class="border-l-[0.5px] border-solid h-10 flex items-center border-gray-500 pl-2 pr-4">
            <span class="font-bold text-xl">{props.fileName}</span>
          </div>
          <ha-button
            loading={saveMut.isPending}
            raised
            onClick={saveMut.mutateAsync}
            disabled={editorActions.saveDisabled()}
          >
            <ha-icon icon="mdi:content-save-outline" slot="start"></ha-icon>
            Save
          </ha-button>
        </div>
        <div class="flex items-center gap-2" style="--ha-textfield-input-width: 25px;">
          <ha-icon icon="mdi:format-size" />
          <span class="whitespace-nowrap">Font size</span>
          <ha-combo-box
            value={editorSettingsFont().toString()}
            items={[8, 10, 14, 16, 18, 20, 24, 28]}
            item-value-path=""
            hide-clear-icon
            item-label-path=""
            item-id-path=""
            hideClearIcon={true}
            on:value-changed={e => {
              const fontSize = parseInt(e.detail.value)
              setEditorSettings(prev => {
                prev.fontSize = fontSize
                return prev
              })
              editor.updateOptions({
                fontSize,
              })
            }}
          />
          <div class="border-l-[0.5px] border-solid h-10 flex items-center border-gray-500 pl-4 ml-2">
            <span class="font-bold">VIM</span>
          </div>
          <ha-switch
            checked={editorSettings().vimEnabled}
            onChange={() => {
              const vimEnabled = !editorSettings().vimEnabled
              setEditorSettings(prev => {
                return {
                  fontSize: prev.fontSize,
                  vimEnabled,
                }
              })
            }}
          />
        </div>
      </ha-card>
      <div ref={statusBarRef} />
      <div class="h-full" ref={editorRef} id="editor" />
    </>
  )
}
