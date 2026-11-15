import { beforeAll, expect, it, vi } from 'vitest'

// Use Monaco's real tokenizer; only its worker and unused Vim adapter are outside this check.
vi.mock('monaco-editor', () => import('monaco-editor/editor/editor.api'))
vi.mock('../src/components/Editor/monaco-worker', () => ({}))
vi.mock('monaco-vim', () => ({ initVimMode: vi.fn() }))

let monaco: typeof import('monaco-editor')
beforeAll(async () => {
  window.matchMedia = vi.fn().mockReturnValue({ matches: false, addEventListener() {}, removeEventListener() {} })
  monaco = await import('monaco-editor')
  monaco.languages.register({ id: 'csharp' })
  const { language } = await import('monaco-editor/languages/definitions/csharp/csharp')
  monaco.languages.setMonarchTokensProvider('csharp', language)
  await import('../src/components/Editor/monaco-setup')
})

function tokenAt(source: string, text: string) {
  const offset = source.indexOf(text), prefix = source.slice(0, offset).split('\n')
  return monaco.editor.tokenize(source, 'csharp')[prefix.length - 1]
    .findLast(token => token.offset <= prefix.at(-1)!.length)?.type
}

it('distinguishes C# method declarations and qualified calls from types and variables', () => {
  expect(tokenAt('public static bool Check(NodeContext context)', 'Check')).toBe('function.cs')
  expect(tokenAt('var local = TimeZoneInfo.ConvertTime(now, zone);', 'ConvertTime')).toBe('function.cs')
  expect(tokenAt('var result = values.Select<string>(read);', 'Select')).toBe('function.cs')
  expect(tokenAt('var result = Read<Dictionary<string, List<int>>>();', 'Read')).toBe('function.cs')
  expect(tokenAt('var result = source?.Read();', 'Read')).toBe('function.cs')
  expect(tokenAt('var now = DateTimeOffset.UtcNow;', 'DateTimeOffset')).toBe('type.identifier.cs')
  expect(tokenAt('var now = DateTimeOffset.UtcNow;', 'now')).toBe('identifier.cs')
})

it('preserves keyword, string, comment, and interpolation highlighting around method syntax', () => {
  expect(tokenAt('if (ready) return true;', 'if')).toBe('keyword.if.cs')
  expect(tokenAt('using (Sync.EnterScope())', 'using')).toBe('keyword.using.cs')
  expect(tokenAt('var text = "Check()";', 'Check')).toBe('string.cs')
  expect(tokenAt('// Check() stays a comment', 'Check')).toBe('comment.cs')
  expect(tokenAt('var text = $"Result: {Read()}";', 'Read')).toBe('function.cs')
  expect(tokenAt('/* Check()\nRead() */', 'Check')).toBe('comment.cs')
  expect(tokenAt('/* Check()\nRead() */', 'Read')).toBe('comment.cs')
})

it.each(['hass-sharp-dark', 'hass-sharp-light'])('renders distinct syntax colors in %s', async theme => {
  monaco.editor.setTheme(theme)
  const container = document.createElement('div')
  container.innerHTML = await monaco.editor.colorize('bool Check(NodeContext context) => "ready";', 'csharp', {})
  const names = ['bool', 'Check', 'NodeContext', 'context', '"ready"']
  const classes = names.map(name => [...container.querySelectorAll('span[class]')].find(span => span.textContent?.includes(name))?.className)
  expect(classes.every(Boolean), container.innerHTML).toBe(true)
  expect(new Set(classes).size).toBe(names.length)
})
