import { createEffect, createSignal, createUniqueId, onMount, onCleanup, Show } from 'solid-js'
import { A } from '@solidjs/router'
import * as monaco from 'monaco-editor'
import { initVimMode, type VimAdapterInstance } from 'monaco-vim'

import visualAssistTheme from '../../visual-assist.json'

import { useHass } from '../../context'
import { useLocalStorage } from '../../hooks'
import { useMutation } from '@tanstack/solid-query'
import { useEditorActions } from './editor-actions'
import { Button, Icon, Status } from '../Controls'
import { Tabs } from '../Tabs'

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
  const tabId = createUniqueId()
  const [tab, setTab] = createSignal('source')
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

  onMount(() => {
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
      fontSize: 16,
      padding: {
        top: 25,
      },
      hover: {
        enabled: 'on',
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

    // Follow HA's inherited theme, including changes while the panel stays mounted.
    const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
    const updateTheme = () => {
      const channels = getComputedStyle(editorRef).backgroundColor.match(/[\d.]+/g)?.map(Number)
      const dark = channels && channels.length >= 3 && channels[3] !== 0
        ? channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722 < 128
        : darkQuery.matches
      editor.updateOptions({ theme: dark ? 'visual-assist' : 'vs' })
    }
    updateTheme()
    const themeObserver = new MutationObserver(updateTheme)
    let ancestor: Node | null = editorRef.parentElement
    while (ancestor) {
      if (ancestor instanceof Element) themeObserver.observe(ancestor, { attributes: true, attributeFilter: ['class', 'style'] })
      ancestor = ancestor.parentNode ?? (ancestor instanceof ShadowRoot ? ancestor.host : null)
    }
    darkQuery.addEventListener('change', updateTheme)
    onCleanup(() => { themeObserver.disconnect(); darkQuery.removeEventListener('change', updateTheme) })

    const model = editor.getModel()
    editorActions.register(editor, model => props.onSave(model), () => saveMut.mutateAsync())
    createEffect(() => { editor.updateOptions({ fontSize: editorSettingsFont() }) })
    createEffect(() => {
      if (editorSettings().vimEnabled) {
        monacoVimMode = initVimMode(editor, statusBarRef)
        onCleanup(() => { monacoVimMode?.dispose(); monacoVimMode = undefined })
      }
    })
    onCleanup(() => {
      editorActions.dispose()
      editor.dispose()
      model?.dispose()
    })
  })

  const saveMut = useMutation(() => ({ mutationFn: () => editorActions.save() }))

  return (
    <section class="hs-editor" aria-label={`Edit ${props.fileName}`}>
      <header class="hs-editor-toolbar">
        <div class="hs-editor-heading">
          <Show when={props.onBackRef}><A class="hs-button hs-button--ghost hs-icon-button" href={props.onBackRef!} aria-label="Back to automations"><Icon name="arrow" /></A></Show>
          <div class="hs-editor-title"><p class="hs-eyebrow">C# AUTOMATION</p><h1>{props.fileName}</h1></div>
        </div>
        <Button data-save variant="primary" busy={saveMut.isPending} onClick={() => { void saveMut.mutateAsync().catch(() => {}) }} disabled={editorActions.saveDisabled()}>
          <Icon name="save" />{saveMut.isPending ? 'Saving…' : 'Save'}
        </Button>
      </header>
      <div class="hs-editor-subbar">
        <Tabs id={tabId} label="Automation editor" value={tab()} onChange={setTab} items={[{ id: 'source', label: 'Source' }, { id: 'settings', label: 'Editor settings' }]} />
        <span class="hs-save-hint">Ctrl / ⌘ S to save</span>
      </div>
      <Show when={saveMut.isError}><div class="hs-notice hs-notice--danger" role="alert">Save failed: {String(saveMut.error)}</div></Show>
      <div class="hs-editor-feedback" role="status" aria-live="polite">
        <Show when={saveMut.isPending}>Formatting, checking and saving…</Show>
      </div>
      <section class="hs-editor-source" role="tabpanel" id={`${tabId}-source-panel`} aria-labelledby={`${tabId}-source`} hidden={tab() !== 'source'}>
        <div class="hs-monaco" ref={editorRef} id="editor" />
        <footer class="hs-editor-status"><Status>C#</Status><span>Roslyn language services</span><div ref={statusBarRef} /></footer>
      </section>
      <section class="hs-editor-settings" role="tabpanel" id={`${tabId}-settings-panel`} aria-labelledby={`${tabId}-settings`} hidden={tab() !== 'settings'}>
        <h2>Make yourself comfortable</h2><p class="hs-muted">Editor preferences are saved in this browser.</p>
        <div class="hs-setting-row"><label for={`${tabId}-font`}>Font size<span class="hs-muted">Change the size of your code.</span></label>
          <select id={`${tabId}-font`} class="hs-input" value={editorSettingsFont()} onChange={event => {
            const fontSize = Number(event.currentTarget.value)
            setEditorSettings(prev => ({ ...prev, fontSize }))
          }}><ForFontSizes /></select>
        </div>
        <div class="hs-setting-row"><label for={`${tabId}-vim`}>Vim mode<span class="hs-muted">Use Vim keyboard bindings in the editor.</span></label>
          <input id={`${tabId}-vim`} type="checkbox" role="switch" class="hs-switch" checked={editorSettings().vimEnabled} onChange={event => {
            const vimEnabled = event.currentTarget.checked
            setEditorSettings(prev => ({ ...prev, vimEnabled }))
          }} />
        </div>
        <p class="hs-muted hs-keyboard-tip">Use Ctrl+M (Control+Shift+M on Mac) to toggle whether Tab moves focus out of the code editor.</p>
      </section>
    </section>
  )
}

const ForFontSizes = () => <>{[8, 10, 12, 14, 16, 18, 20, 24, 28].map(size => <option value={size}>{size} px</option>)}</>
