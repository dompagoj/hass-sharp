// Development-only fixture. No backend or Home Assistant services are called.
import { render } from 'solid-js/web'
import App from '../src/App'
import '../src/style.css'
import type { HomeAssistant, UserScriptDTO } from '../src/types'

const scripts: UserScriptDTO[] = [
  { fileName: 'BathroomHeating.cs', classes: [{ name: 'Home.BathroomHeating', methods: ['Configure', 'ExecuteAsync'] }] },
  { fileName: 'EveningLights.cs', classes: [{ name: 'Home.EveningLights', methods: ['Configure', 'ExecuteAsync'] }] },
  { fileName: 'A very long automation file name to check wrapping on a small screen.cs', classes: [] },
]
let source = `public class BathroomHeating : ManagedAutomation\n{\n    // Your automation starts here.\n    public override void Configure()\n    {\n        OnStartup();\n    }\n\n    public override async Task ExecuteAsync(AutomationContext context)\n    {\n        await Task.Delay(1000, context.CancellationToken);\n    }\n}`
;(window as any).setPreviewTheme = (theme: 'light' | 'dark') => {
  const vars = theme === 'light'
    ? ['#f5f6fa', '#ffffff', '#202535', '#626c7c', '#dce0e9', '#6355cf', '#ffffff', '#bb283f']
    : ['#131720', '#1d2330', '#edf0f8', '#aab3c5', '#343d4e', '#aa9fff', '#17122c', '#ff8394']
  ;['primary-background-color', 'card-background-color', 'primary-text-color', 'secondary-text-color', 'divider-color', 'primary-color', 'text-primary-color', 'error-color'].forEach((name, index) => document.documentElement.style.setProperty(`--${name}`, vars[index]))
  document.documentElement.style.colorScheme = theme
}
;(window as any).setPreviewTheme('light')
const visualFixture = new URLSearchParams(location.search).get('view') === 'visual'
history.replaceState(null, '', visualFixture ? '/hass-sharp/visual/new?name=Motion%20lights' : '/hass-sharp/automations')
const hass = {
  async callApi(method: string, path: string, parameters?: { name?: string; source?: string }) {
    const file = decodeURIComponent(path.split('/')[2] ?? '')
    if (method === 'GET') return file ? { fileName: file, source } : [...scripts]
    if (method === 'POST' && parameters?.source !== undefined) { source = parameters.source; return {} }
    if (method === 'DELETE') { scripts.splice(scripts.findIndex(script => script.fileName === file), 1); return {} }
    const fileName = `${parameters?.name?.replace(/\.cs$/, '')}.cs`
    if (scripts.some(script => script.fileName === fileName)) throw { body: { error: 'That name is already in use.' } }
    if (method === 'PUT') scripts.find(script => script.fileName === file)!.fileName = fileName
    else scripts.push({ fileName, classes: [] })
    return { fileName }
  },
  async callWS(message: { type: string; source: string }) {
    if (message.type.endsWith('format_source')) return message.source
    return message.type.endsWith('get_hover') ? null : []
  },
} as unknown as HomeAssistant
render(() => <App hass={hass} />, document.getElementById('preview')!)
// Browser smoke checks can inspect the real Monaco model without replacing it.
import * as monaco from 'monaco-editor'
;(window as any).previewCode = () => monaco.editor.getModels().map(model => model.getValue())

;(window as any).previewSaved = () => source
