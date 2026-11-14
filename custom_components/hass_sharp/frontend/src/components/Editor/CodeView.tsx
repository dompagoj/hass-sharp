import { onCleanup, onMount } from 'solid-js'
import * as monaco from 'monaco-editor'

import { followTheme } from './monaco-setup'

/** Monaco's line height at the view's 13 px font, for sizing the view to its text. */
const LINE = 19

/**
 * C# shown read only with Monaco's colouring: text can be selected and copied, nothing can be typed, and no language
 * service is asked. Loaded only when a type's definition opens (with Monaco, a chunk of its own).
 */
export function CodeView(props: { text: string; label: string }) {
  let element!: HTMLDivElement
  const lines = props.text.split('\n').length
  onMount(() => {
    const model = monaco.editor.createModel(props.text, 'csharp')
    const editor = monaco.editor.create(element, {
      model,
      readOnly: true,
      domReadOnly: true,
      ariaLabel: props.label,
      theme: 'visual-assist',
      automaticLayout: true,
      fixedOverflowWidgets: true,
      fontSize: 13,
      lineHeight: LINE,
      lineNumbers: 'off',
      glyphMargin: false,
      folding: false,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      renderLineHighlight: 'none',
      overviewRulerLanes: 0,
      hideCursorInOverviewRuler: true,
      // A short definition leaves the wheel to the dialog.
      scrollbar: { alwaysConsumeMouseWheel: false },
      occurrencesHighlight: 'off',
      hover: { enabled: 'off' },
      contextmenu: true,
      // Tab leaves the view, as it leaves any other part of the dialog.
      tabFocusMode: true,
      padding: { top: 8, bottom: 8 },
    })
    followTheme(editor, element)
    onCleanup(() => { editor.dispose(); model.dispose() })
  })
  // The text, its padding and room for the horizontal scroll bar a long line brings, so no vertical one is needed.
  return <div class="hs-monaco hs-code-view" ref={element} style={{ height: `min(60dvh, ${lines * LINE + 16 + 14}px)` }} />
}
