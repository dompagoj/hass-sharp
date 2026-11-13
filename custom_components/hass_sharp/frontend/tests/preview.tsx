// Development-only fixture. No backend or Home Assistant services are called.
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import services from '../../../../tests/fixtures/visual-services.json'
import { connectData, fillGraphLayout, type Graph, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { FlowAdapter, type FlowTrace } from '../src/visual/FlowAdapter'
import { visualFixture as makeVisualFixture } from './visual-fixture'
import { render } from 'solid-js/web'
import App from '../src/App'
import '../src/style.css'
import type { AutomationRun, HomeAssistant, UserScriptDTO } from '../src/types'

const diagnosticsFixture = new URLSearchParams(location.search).has('diagnostics')
const shadowFixture = new URLSearchParams(location.search).has('shadow')
// `?view=trace`: the canvas read-only with a sample run trace (the run view itself is a later task).
const traceFixture = new URLSearchParams(location.search).get('view') === 'trace'
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
// Run history v2 samples: the action's plain label, the trigger summary, and which runs have a trace.
const statuses: AutomationRun['status'][] = ['Failed', 'Skipped', 'Succeeded', 'Stopped', 'Cancelled', 'Succeeded', 'Interrupted']
const errors: Partial<Record<AutomationRun['status'], string>> = {
  Failed: 'Example error detail\nSecond line (fixture only).', Stopped: 'Stopped by a Stop step: nobody is home', Cancelled: 'Replaced by a newer run (mode Restart)', Interrupted: 'Home Assistant restarted during the run',
}
const scriptRun = (i: number): AutomationRun => {
  const status = i < statuses.length ? statuses[i] : 'Succeeded', kind = i % 3 === 0 ? 'schedule' : i % 3 === 1 ? 'entity' : 'manual'
  return {
    runId: `fixture-run-${i}`, automationId: 'fixture-bathroom', revision: 'fixture-revision', action: 'Home.BathroomHeating::Execute', actionLabel: 'BathroomHeating · Execute',
    triggerNodeId: null, visualId: null, visualRevision: null,
    cause: kind === 'schedule' ? 'Schedule' : kind === 'entity' ? 'Entity' : 'Manual',
    trigger: kind === 'schedule' ? { kind, scheduleId: 'morning', planned: '2026-11-12T00:59:00Z' } : kind === 'entity' ? { kind, entityId: 'sensor.bathroom_temperature', from: '20.5', to: '19.0' } : { kind },
    plannedUtc: kind === 'schedule' ? '2026-11-12T00:59:00Z' : null, receivedUtc: '2026-11-12T01:00:00Z',
    startUtc: '2026-11-12T01:00:00Z', endUtc: '2026-11-12T01:00:01Z', durationMs: 1000,
    status, error: errors[status] ?? null, hasTrace: false,
  }
}
const visualRun = (i: number): AutomationRun => {
  const status = (['Succeeded', 'Failed', 'Stopped', 'Succeeded', 'Cancelled'] as const)[i % 5], manual = i % 4 === 2
  return {
    runId: `fixture-visual-run-${i}`, automationId: 'visual:typed-demo', revision: 'fixture-revision', action: 'Visual.TypedDemo::Trigger_trigger_1', actionLabel: 'Motion in the hall',
    triggerNodeId: 'trigger-1', visualId: 'typed-demo', visualRevision: 1,
    cause: manual ? 'Manual' : 'Entity', trigger: manual ? { kind: 'manual' } : { kind: 'entity', entityId: 'binary_sensor.hall_0', from: 'off', to: 'on' },
    plannedUtc: null, receivedUtc: new Date(Date.parse('2026-11-12T18:00:00Z') - i * 3_600_000).toISOString(),
    startUtc: '2026-11-12T18:00:00Z', endUtc: '2026-11-12T18:00:02Z', durationMs: 2000,
    status, error: errors[status] ?? null, hasTrace: true,
  }
}
const runs: AutomationRun[] = [...Array.from({ length: 23 }, (_, i) => scriptRun(i)), ...Array.from({ length: 8 }, (_, i) => visualRun(i))]

;(window as any).setPreviewTheme = (theme: 'light' | 'dark') => {
  const vars = theme === 'light'
    ? ['#f5f6fa', '#ffffff', '#202535', '#626c7c', '#dce0e9', '#6355cf', '#ffffff', '#bb283f']
    : ['#131720', '#1d2330', '#edf0f8', '#aab3c5', '#343d4e', '#aa9fff', '#17122c', '#ff8394']
  ;['primary-background-color', 'card-background-color', 'primary-text-color', 'secondary-text-color', 'divider-color', 'primary-color', 'text-primary-color', 'error-color'].forEach((name, index) => document.documentElement.style.setProperty(`--${name}`, vars[index]))
  document.documentElement.style.colorScheme = theme
}
;(window as any).setPreviewTheme('light')
const visualBackend = makeVisualFixture({catalog:rich.catalog as unknown as VisualCatalogSnapshot,libraries:[{id:rich.sourceId,name:'Typed sample.cs',revision:rich.revision,source:rich.source}, {id:'helper',name:'Helper.cs',revision:1,source:'public static class Factory { public static Reading Make(string? label, int count = 1) => new(label ?? ""); public static Reading Read(EntityStateSnapshot entity) => Samples.Describe(entity); }'}]})
const typedFixture=new URLSearchParams(location.search).has('typed')
// Node cards: a state trigger, light.turn_on with a target and two fields, a condition with both branches, a delay,
// a connected and an unconnected input, and the rich library sample below them.
const typedDemo = (): Graph => {
  const catalog = rich.catalog as unknown as VisualCatalogSnapshot, lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
  const config = (graph: Graph, id: string) => graph.nodes.find(n => n.id === id)!.config
  let g = createGraph('Motion lights')
  g = updateNode(g, 'trigger-1', { label: 'Motion in the hall', config: { type: 'entity', entity: 'binary_sensor.hall_0', manual: true, mode: 'Single' }, position: { x: 40, y: 80 } })
  g = updateNode(g, 'condition-1', { position: { x: 420, y: 80 } })
  g = updateNode(g, 'service-1', { position: { x: 840, y: 20 }, config: { ...config(g, 'service-1'), service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall_1' }), fields: { brightness_pct: lit('number', 80), transition: lit('number', 2) } } })
  g = addNode(g, 'delay', 'delay-1', catalog)
  g = updateNode(g, 'delay-1', { position: { x: 840, y: 480 }, config: { ...config(g, 'delay-1'), seconds: lit('number', 300) } })
  g = addNode(g, 'service', 'service-2', catalog)
  g = updateNode(g, 'service-2', { label: 'Lights off', position: { x: 1240, y: 480 }, config: { ...config(g, 'service-2'), service: 'light.turn_off', target: lit('target', { entity_id: 'light.hall_1' }), fields: { transition: { kind: 'unset' } } } })
  g = connectNodes(connectNodes(g, 'condition-1', 'delay-1', 'false', catalog), 'delay-1', 'service-2', undefined, catalog)
  g = connectData(g, 'trigger-1', 'entity_id', 'condition-1', 'entity', catalog)
  const sample = fillGraphLayout(rich.cases[0].graph as Graph)
  return { ...g, nodes: [...g.nodes, ...sample.nodes.map((node, i) => ({ ...node, position: { x: 40 + i * 400, y: 820 } }))], edges: [...g.edges, ...sample.edges] }
}
if(typedFixture)visualBackend.controls.services = services as unknown as ServicesSection
if(typedFixture)visualBackend.documents.set('typed-demo',{version:1,id:'typed-demo',revision:1,graph:typedDemo(),source:'// Typed fixture generated on the server',sourceMap:[],automationId:'visual:typed-demo',currentRevisionActive:true,catalogGeneration:'saved-fixture'})
;(window as any).visualFixture = visualBackend
const visualFixture = new URLSearchParams(location.search).get('view') === 'visual'
const libraryFixture = new URLSearchParams(location.search).get('view') === 'library'
history.replaceState(null, '', libraryFixture ? '/hass-sharp/libraries' : diagnosticsFixture ? '/hass-sharp/automations/BathroomHeating.cs' : visualFixture ? typedFixture?'/hass-sharp/visual/typed-demo':'/hass-sharp/visual/new?name=Motion%20lights' : '/hass-sharp/automations')
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
    if (message.type === 'hass_sharp/get_entities') return { entities: Array.from({length:80},(_,i)=>({entity_id: `${i%2?'light':'binary_sensor'}.hall_${i}`, name: `Hall ${i%2?'lamp':'motion'} ${i}`})) }
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
      const matching = runs.filter(run => run.automationId === request.automationId && (!request.status?.length || request.status.includes(run.status)))
      return { version: 2, offset: request.offset, limit: request.limit, runs: structuredClone(matching.slice(request.offset, request.offset + request.limit)) }
    }
    if (message.type === 'hass_sharp/run_now') {
      if (fixture.dispatchError) throw { message: 'Example manual dispatch rejection' }
      const request = message as any
      const visual = String(request.automationId).startsWith('visual:')
      const run: AutomationRun = { ...(visual ? visualRun(0) : scriptRun(2)), automationId: request.automationId, action: `${request.className}::${request.method}`, runId: `fixture-manual-${Date.now()}`,
        cause: 'Manual', trigger: { kind: 'manual' }, receivedUtc: new Date().toISOString(), startUtc: null, plannedUtc: null, endUtc: null, durationMs: null, status: 'Pending', error: null }
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
const sampleTrace: FlowTrace = {
  nodes: { 'trigger-1': { status: 'ok', runs: 1, ms: 3 }, 'condition-1': { status: 'ok', runs: 1, ms: 1 }, 'delay-1': { status: 'running', runs: 1 }, t: { status: 'ok', runs: 1 }, r: { status: 'ok', runs: 1, ms: 12 }, describe: { status: 'error', runs: 1, ms: 40 }, consume: { status: 'failed', runs: 2, ms: 1250 } },
  edges: new Set(['trigger-1->condition-1', 'condition-1:false->delay-1', 'flow0', 'flow1', 'flow2', 'wire3', 'wire4']),
  focusNodeId: 'consume',
  value: (node, port, side) => node === 'delay-1' && port === 'seconds' ? { type: 'number', v: 300 } : node === 'condition-1' && port === 'entity' ? { type: 'entity_id', v: 'binary_sensor.hall_0' } : node === 'r' && side === 'out' ? { type: 'object', v: { state: '21.5', attributes: { unit: 'C' } }, trunc: true } : undefined,
}
render(() => traceFixture ? <div class="hs-app" style={{ height: '100%' }}><FlowAdapter graph={typedDemo()} catalog={rich.catalog as unknown as VisualCatalogSnapshot} readOnly trace={sampleTrace} onSelect={() => {}} /></div> : <App hass={hass} />, previewRoot)
// Browser smoke checks can inspect the real Monaco model without replacing it.
import * as monaco from 'monaco-editor'
;(window as any).previewCode = () => monaco.editor.getModels().map(model => model.getValue())

;(window as any).previewSaved = () => source
