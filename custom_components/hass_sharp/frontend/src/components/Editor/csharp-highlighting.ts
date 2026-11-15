import type { languages } from 'monaco-editor'
import { language } from 'monaco-editor/languages/definitions/csharp/csharp'

// Monaco's C# grammar labels every name as an identifier. Add lexical hints for
// methods and PascalCase names (types/properties), keeping its string/comment states.
// These are syntax hints, not Roslyn semantic classifications.
const nameRules: languages.IMonarchLanguageRule[] = [
  [/@?[a-zA-Z_]\w*(?=\s*(?:<[\w\s.,?<>\[\]]+>\s*)?\()/, {
    cases: { '@keywords': { token: 'keyword.$0' }, '@default': 'function' },
  }],
  [/@?[A-Z]\w*/, 'type.identifier'],
]

export const csharpLanguage: languages.IMonarchLanguage = {
  ...language,
  tokenizer: {
    ...language.tokenizer,
    root: [...nameRules, ...language.tokenizer.root],
    qualified: [...nameRules, ...language.tokenizer.qualified],
  },
}
