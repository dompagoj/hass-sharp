// Development-only fixture. No backend or Home Assistant services are called.
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { fillGraphLayout, type GraphV4, type VisualCatalogSnapshot } from '../src/visual/graphV4'
import { visualFixture as makeVisualFixture } from './visual-fixture'
import { render } from 'solid-js/web'
import App from '../src/App'
import '../src/style.css'
import type { AutomationRun, HomeAssistant, UserScriptDTO } from '../src/types'

const diagnosticsFixture = new URLSearchParams(location.search).has('diagnostics')
const shadowFixture = new URLSearchParams(location.search).has('shadow')
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
if (diagnosticsFixture) source = 'public class Broken { void Run() { MissingName(); } }'
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
const visualBackend = makeVisualFixture({catalog:rich.catalog as unknown as VisualCatalogSnapshot,libraries:[{id:rich.sourceId,name:'Typed sample',revision:rich.revision,source:rich.source}]})
const typedFixture=new URLSearchParams(location.search).has('typed')
if(typedFixture)visualBackend.documents.set('typed-demo',{version:1,id:'typed-demo',revision:1,graph:fillGraphLayout(rich.cases[0].graph as GraphV4),source:'// Typed fixture generated on the server',sourceMap:[],automationId:'visual:typed-demo',currentRevisionActive:true,catalogGeneration:'saved-fixture'})
;(window as any).visualFixture = visualBackend
const visualFixture = new URLSearchParams(location.search).get('view') === 'visual'
history.replaceState(null, '', diagnosticsFixture ? '/hass-sharp/automations/BathroomHeating.cs' : visualFixture ? typedFixture?'/hass-sharp/visual/typed-demo':'/hass-sharp/visual/new?name=Motion%20lights' : '/hass-sharp/automations')
const hass = {
  user: { is_admin: true },
  connection:visualBackend.connection,
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
    if (message.type === 'hass_sharp/get_entities') return { entities: Array.from({length:80},(_,i)=>({entity_id: `${i%2?'light':'binary_sensor'}.hall_${i}`, friendly_name: `Hall ${i%2?'lamp':'motion'} ${i}`})) }
    if (message.type === 'get_services') return { light: { turn_on: {name:'Turn on',description:'Turn on a light'}, turn_off: {name:'Turn off',description:'Turn off a light'} }, climate: { set_temperature: {name:'Set temperature',description:'Change the target temperature'} } }
    if (message.type === 'hass_sharp/visual'||message.type==='hass_sharp/node_library') return visualBackend.callWS(message)
    if (diagnosticsFixture && message.type.endsWith('get_diagnostics')) {
      const start = message.source.indexOf('MissingName')
      if (start < 0) return []
      const before = message.source.slice(0, start).split('\n')
      return [{ startLine: before.length, startColumn: before.at(-1)!.length + 1, endLine: before.length, endColumn: before.at(-1)!.length + 12, message: "The name 'MissingName' does not exist in the current context", severity: 3 }]
    }
    if (message.type.endsWith('format_source')) return message.source
    if (message.type === 'hass_sharp/run_history') {
      if (fixture.historyError) throw { message: 'Example history connection error' }
      const request = message as any
      return { version: 1, offset: request.offset, limit: request.limit, runs: structuredClone(runs.filter(run => run.automationId === request.automationId).slice(request.offset, request.offset + request.limit)) }
    }
    if (message.type === 'hass_sharp/run_now') {
      if (fixture.dispatchError) throw { message: 'Example manual dispatch rejection' }
      const request = message as any
      const run: AutomationRun = { ...runs[0], automationId: request.automationId, action: `${request.className}::${request.method}`, runId: `fixture-manual-${Date.now()}`, cause: 'Manual', details: null,
        receivedUtc: new Date().toISOString(), startUtc: null, plannedUtc: null, endUtc: null, durationMs: null, status: 'Pending', error: null }
      runs.unshift(run)
      setTimeout(() => Object.assign(run, { status: fixture.result, startUtc: new Date().toISOString(), endUtc: new Date().toISOString(), durationMs: 1000 }), 1000)
      return { version: 1, runId: run.runId }
    }
    return message.type.endsWith('get_hover') ? null : []
  },
} as unknown as HomeAssistant
let previewRoot: HTMLElement | ShadowRoot = document.getElementById('preview')!
if (shadowFixture) {
  for (let level = 0; level < 2; level++) {
    const host = document.createElement('div')
    host.style.cssText = 'display:block;height:100%'
    previewRoot.append(host)
    previewRoot = host.attachShadow({ mode: 'open' })
  }
  // Mirror the built CSS into HA-like nested shadow roots. Vite's global dev style
  // injection cannot cross this boundary; keep the preview's real app styling.
  for (const style of document.querySelectorAll('style, link[rel="stylesheet"]')) previewRoot.append(style.cloneNode(true))
}
render(() => <App hass={hass} />, previewRoot)
// Browser smoke checks can inspect the real Monaco model without replacing it.
import * as monaco from 'monaco-editor'
;(window as any).previewCode = () => monaco.editor.getModels().map(model => model.getValue())

;(window as any).previewSaved = () => source
