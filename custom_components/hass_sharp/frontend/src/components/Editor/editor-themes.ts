import type { editor } from 'monaco-editor'

/** One Dark / One Light palettes, using the token names emitted by Monaco's C# grammar. */
export const darkEditorTheme: editor.IStandaloneThemeData = {
  base: 'vs-dark',
  inherit: true,
  colors: {
    'editor.background': '#282c34',
    'editor.foreground': '#abb2bf',
    'editor.lineHighlightBackground': '#2c313c',
    'editor.selectionBackground': '#3e4451',
    'editor.inactiveSelectionBackground': '#3e445180',
    'editorCursor.foreground': '#528bff',
    'editorLineNumber.foreground': '#636d83',
    'editorLineNumber.activeForeground': '#abb2bf',
    'editorIndentGuide.background1': '#3b4048',
    'editorIndentGuide.activeBackground1': '#636d83',
    'editorWhitespace.foreground': '#4b5263',
  },
  rules: [
    { token: 'identifier', foreground: 'ABB2BF' },
    { token: 'keyword', foreground: 'C678DD' },
    { token: 'function', foreground: '61AFEF' },
    { token: 'type.identifier', foreground: 'E5C07B' },
    { token: 'namespace', foreground: '56B6C2' },
    { token: 'number', foreground: 'D19A66' },
    { token: 'string', foreground: '98C379' },
    { token: 'string.escape', foreground: '56B6C2' },
    { token: 'comment', foreground: '7F848E', fontStyle: 'italic' },
    { token: 'delimiter', foreground: 'ABB2BF' },
    { token: 'invalid', foreground: 'E06C75' },
  ],
}

export const lightEditorTheme: editor.IStandaloneThemeData = {
  base: 'vs',
  inherit: true,
  colors: {
    'editor.background': '#fafafa',
    'editor.foreground': '#383a42',
    'editor.lineHighlightBackground': '#f0f0f0',
    'editor.selectionBackground': '#d7d7e3',
    'editor.inactiveSelectionBackground': '#e5e5e6',
    'editorCursor.foreground': '#526fff',
    'editorLineNumber.foreground': '#9d9d9f',
    'editorLineNumber.activeForeground': '#383a42',
    'editorIndentGuide.background1': '#e5e5e6',
    'editorIndentGuide.activeBackground1': '#b5b5b7',
    'editorWhitespace.foreground': '#d0d0d2',
  },
  rules: [
    { token: 'identifier', foreground: '383A42' },
    { token: 'keyword', foreground: 'A626A4' },
    { token: 'function', foreground: '4078F2' },
    { token: 'type.identifier', foreground: '986801' },
    { token: 'namespace', foreground: '0184BC' },
    { token: 'number', foreground: '986801' },
    { token: 'string', foreground: '50A14F' },
    { token: 'string.escape', foreground: '0184BC' },
    { token: 'comment', foreground: '717277', fontStyle: 'italic' },
    { token: 'delimiter', foreground: '383A42' },
    { token: 'invalid', foreground: 'E45649' },
  ],
}
