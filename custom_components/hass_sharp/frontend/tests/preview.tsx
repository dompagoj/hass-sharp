// Development-only fixture. No backend or Home Assistant services are called.
import { render } from 'solid-js/web'
import App from '../src/App'
import '../src/style.css'
import type { AutomationRun, HomeAssistant, UserScriptDTO } from '../src/types'

const scripts: UserScriptDTO[] = [
  { fileName: 'BathroomHeating.cs', automationId: 'fixture-bathroom', revision: 'fixture-revision', classes: [{ name: 'Home.BathroomHeating', methods: ['Execute'] }] },
  { fileName: 'EveningLights.cs', classes: [{ name: 'Home.EveningLights', methods: ['Configure', 'ExecuteAsync'] }] },
  { fileName: 'A very long automation file name to check wrapping on a small screen.cs', classes: [] },
]
let source = `public class BathroomHeating : Automation
{
    public override AutomationConfiguration Configure() => new(
        new AutomationAction(nameof(Execute), AutomationMode.Single, Manual: true));

    public Task Execute()
    {
        Logger.Info("Manual automation started");
        return Task.CompletedTask;
    }
}`
// Fixture controls are deliberately local: no HA websocket or device call is made.
const fixture = { historyError: false, dispatchError: false, result: 'Succeeded' as AutomationRun['status'] }
;(window as any).runFixture = fixture
const runs: AutomationRun[] = Array.from({ length: 23 }, (_, i) => ({
  runId: `fixture-run-${i}`, automationId: 'fixture-bathroom', revision: 'fixture-revision', action: 'Home.BathroomHeating::Execute',
  cause: i % 3 === 0 ? 'Schedule' : i % 3 === 1 ? 'Entity' : 'Manual', details: i % 3 === 1 ? 'sensor.bathroom_temperature' : null,
  plannedUtc: i % 3 === 0 ? '2026-11-12T00:59:00Z' : null, receivedUtc: '2026-11-12T01:00:00Z',
  startUtc: '2026-11-12T01:00:00Z', endUtc: '2026-11-12T01:00:01Z', durationMs: 1000,
  status: i === 0 ? 'Failed' : i === 1 ? 'Skipped' : 'Succeeded', error: i === 0 ? 'Example error detail\nSecond line (fixture only).' : null,
}))

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
  user: { is_admin: true },
  async callApi(method: string, path: string, parameters?: { name?: string; source?: string }) {
    const file = decodeURIComponent(path.split('/')[2] ?? '')
    if (method === 'GET') return file ? { fileName: file, source } : [...scripts]
    if (method === 'POST' && parameters?.source !== undefined) { source = parameters.source; return {} }
    if (method === 'DELETE') { scripts.splice(scripts.findIndex(script => script.fileName === file), 1); return {} }
    const fileName = `${parameters?.name?.replace(/\.cs$/, '')}.cs`
    if (scripts.some(script => script.fileName === fileName)) throw { body: { error: 'That name is already in use.' } }
    if (method === 'PUT') scripts.find(script => script.fileName === file)!.fileName = fileName
    else scripts.push({ fileName, automationId: `fixture-${fileName}`, revision: 'fixture-revision', classes: [] })
    return { fileName }
  },
  async callWS(message: { type: string; source: string }) {
    if (message.type.endsWith('format_source')) return message.source
    if (message.type === 'hass_sharp/run_history') {
      if (fixture.historyError) throw { message: 'Example history connection error' }
      const request = message as any
      return { version: 1, offset: request.offset, limit: request.limit, runs: structuredClone(runs.filter(run => run.automationId === request.automationId).slice(request.offset, request.offset + request.limit)) }
    }
    if (message.type === 'hass_sharp/run_now') {
      if (fixture.dispatchError) throw { message: 'Example manual dispatch rejection' }
      const run: AutomationRun = { ...runs[0], runId: `fixture-manual-${Date.now()}`, cause: 'Manual', details: null,
        receivedUtc: new Date().toISOString(), startUtc: null, plannedUtc: null, endUtc: null, durationMs: null, status: 'Pending', error: null }
      runs.unshift(run)
      setTimeout(() => Object.assign(run, { status: fixture.result, startUtc: new Date().toISOString(), endUtc: new Date().toISOString(), durationMs: 1000 }), 1000)
      return { version: 1, runId: run.runId }
    }
    return message.type.endsWith('get_hover') ? null : []
  },
} as unknown as HomeAssistant
render(() => <App hass={hass} />, document.getElementById('preview')!)
// Browser smoke checks can inspect the real Monaco model without replacing it.
import * as monaco from 'monaco-editor'
;(window as any).previewCode = () => monaco.editor.getModels().map(model => model.getValue())

;(window as any).previewSaved = () => source
