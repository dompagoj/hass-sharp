import { createEffect, onCleanup, type Accessor } from 'solid-js'
import * as monaco from 'monaco-editor'
import { initVimMode, type VimAdapterInstance } from 'monaco-vim'

import './monaco-worker'

import visualAssistTheme from '../../visual-assist.json'
import { useLocalStorage } from '../../hooks'

// @ts-ignore
monaco.editor.defineTheme('visual-assist', visualAssistTheme)

/** What every C# editor of the panel shares: the node library editor and a Custom C# node's code editor. */
export const codeEditorOptions: monaco.editor.IStandaloneEditorConstructionOptions = {
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
}

export interface EditorSettings { vimEnabled: boolean; fontSize?: number }
/** Editor preferences, saved in this browser and shared by every C# editor. */
export const useEditorSettings = () => useLocalStorage<EditorSettings>('editor-settings', { vimEnabled: false, fontSize: 16 })
export const editorFontSize = (settings: EditorSettings) => settings.fontSize ?? 16

/** Follows HA's inherited theme, including changes while the panel stays mounted. Call inside the owner of `editor`. */
export function followTheme(editor: monaco.editor.IStandaloneCodeEditor, element: HTMLElement) {
  const darkQuery = window.matchMedia('(prefers-color-scheme: dark)')
  const updateTheme = () => {
    const channels = getComputedStyle(element).backgroundColor.match(/[\d.]+/g)?.map(Number)
    const dark = channels && channels.length >= 3 && channels[3] !== 0
      ? channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722 < 128
      : darkQuery.matches
    editor.updateOptions({ theme: dark ? 'visual-assist' : 'vs' })
  }
  updateTheme()
  const themeObserver = new MutationObserver(updateTheme)
  let ancestor: Node | null = element.parentElement
  while (ancestor) {
    if (ancestor instanceof Element) themeObserver.observe(ancestor, { attributes: true, attributeFilter: ['class', 'style'] })
    ancestor = ancestor.parentNode ?? (ancestor instanceof ShadowRoot ? ancestor.host : null)
  }
  darkQuery.addEventListener('change', updateTheme)
  onCleanup(() => { themeObserver.disconnect(); darkQuery.removeEventListener('change', updateTheme) })
}

/** Applies the font size and Vim mode as they change; Vim's status line goes into `statusBar`. */
export function applyEditorSettings(editor: monaco.editor.IStandaloneCodeEditor, settings: Accessor<EditorSettings>, statusBar: () => HTMLElement) {
  let vim: VimAdapterInstance | undefined
  createEffect(() => { editor.updateOptions({ fontSize: editorFontSize(settings()) }) })
  createEffect(() => {
    if (settings().vimEnabled) {
      vim = initVimMode(editor, statusBar())
      onCleanup(() => { vim?.dispose(); vim = undefined })
    }
  })
}
