// Development-only fixture. No backend or Home Assistant services are called.
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import services from '../../../../tests/fixtures/visual-services.json'
import { connectData, fillGraphLayout, type Graph, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { visualFixture as makeVisualFixture } from './visual-fixture'
import { render } from 'solid-js/web'
import App from '../src/App'
import '../src/style.css'
import type { AutomationRun, HomeAssistant, RunTrace, RunTraceResponse, TraceEvent } from '../src/types'

const shadowFixture = new URLSearchParams(location.search).has('shadow')
// `?view=trace`: the run trace view with a sample run (`&live=1`: the same run arriving step by step; `&drop=1` with
// it: every third read of the live run fails, as when the connection drops).
const traceFixture = new URLSearchParams(location.search).get('view') === 'trace'
const liveTrace = new URLSearchParams(location.search).has('live')
const gridTrace = new URLSearchParams(location.search).has('grid')
const dropTrace = new URLSearchParams(location.search).has('drop')
let traceReads = 0
// Fixture controls are deliberately local: no HA websocket or device call is made.
const fixture = { historyError: false, dispatchError: false, result: 'Succeeded' as AutomationRun['status'] }
;(window as any).runFixture = fixture
// Run history v2 samples of the typed demo: the trigger's label, the trigger summary, and which runs have a trace.
const statuses: AutomationRun['status'][] = ['Failed', 'Skipped', 'Succeeded', 'Stopped', 'Cancelled', 'Succeeded', 'Interrupted']
const errors: Partial<Record<AutomationRun['status'], string>> = {
  Failed: 'Example error detail\nSecond line (fixture only).', Stopped: 'Stopped by a Stop step: nobody is home', Cancelled: 'Restarted by a newer trigger', Interrupted: 'Home Assistant restarted during the run',
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
// More than one page (20 runs), so the Runs tab's pagination can be checked.
const runs: AutomationRun[] = Array.from({ length: 23 }, (_, i) => visualRun(i))

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
// a connected and an unconnected input, value nodes (a Constant read by the service and a Compare read by a Log step),
// a Custom C# node (Edit code) and the rich library sample below them.
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
  for (const [kind, id] of [['constant', 'level'], ['compare', 'bright'], ['log', 'note']] as const) g = addNode(g, kind, id, catalog)
  g = updateNode(g, 'level', { label: 'Evening brightness', position: { x: 420, y: 400 }, config: { ...config(g, 'level'), type: 'number', value: lit('number', 80) } })
  g = updateNode(g, 'bright', { label: 'Bright enough?', position: { x: 1240, y: 240 }, config: { ...config(g, 'bright'), operator: 'gt', right: lit('number', 50) } })
  g = updateNode(g, 'note', { position: { x: 1640, y: 20 } })
  g = connectNodes(g, 'service-1', 'note', undefined, catalog)
  g = connectData(g, 'level', 'value', 'service-1', 'field:brightness_pct', catalog)
  g = connectData(g, 'level', 'value', 'bright', 'left', catalog)
  g = connectData(g, 'bright', 'value', 'note', 'value', catalog)
  // A Custom C# node from the template after the log; its code editor checks with the fixture (`!!` is an error).
  g = addNode(g, 'custom', 'greeting', catalog)
  g = updateNode(g, 'greeting', { position: { x: 2040, y: 20 } })
  g = connectNodes(g, 'note', 'greeting', undefined, catalog)
  g = connectData(g, 'trigger-1', 'entity_id', 'greeting', 'text', catalog)
  // A step after it: making the code a value node joins Log to it.
  g = addNode(g, 'log', 'after', catalog)
  g = updateNode(g, 'after', { label: 'Log done', position: { x: 2440, y: 20 } })
  g = connectNodes(g, 'greeting', 'after', undefined, catalog)
  const sample = fillGraphLayout(rich.cases[0].graph as Graph)
  return { ...g, nodes: [...g.nodes, ...sample.nodes.map((node, i) => ({ ...node, position: { x: 40 + i * 400, y: 820 } }))], edges: [...g.edges, ...sample.edges] }
}
if(typedFixture)visualBackend.controls.services = services as unknown as ServicesSection
if(typedFixture)visualBackend.documents.set('typed-demo',{version:1,id:'typed-demo',revision:1,graph:typedDemo(),source:'// Typed fixture generated on the server',sourceMap:[],automationId:'visual:typed-demo',currentRevisionActive:true,catalogGeneration:'saved-fixture'})
// A run of "Motion lights v2": a branch (only its true side ran), a loop over four lights where the second one failed
// and was recovered through its error output, a cut value (the weather's forecast), a hidden value (an API key) and
// a long message that was cut. Value nodes: a Constant worked out in every iteration, a chain (Entity field → Compare →
// Create object) worked out just before the notification reads it, and a Constant only the step that did not run reads. The automation has been saved again since, so the view says it used an earlier version.
const traceGraph = (): Graph => {
  const catalog = rich.catalog as unknown as VisualCatalogSnapshot, lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
  const config = (graph: Graph, id: string) => graph.nodes.find(n => n.id === id)!.config
  const place = (graph: Graph, id: string, x: number, y: number, patch: Partial<Graph['nodes'][number]> = {}) =>
    updateNode(graph, id, { ...patch, position: { x, y } })
  let g = createGraph('Motion lights v2')
  g = { ...g, edges: g.edges.filter(e => e.source !== 'condition-1') }
  g = place(g, 'trigger-1', 40, 120, { label: 'Motion in the hall', config: { type: 'entity', entity: 'binary_sensor.hall_0', manual: true, mode: 'Restart' } })
  g = place(g, 'condition-1', 420, 120, { label: 'Is it dark?', config: { ...config(g, 'condition-1'), state: 'on' } })
  g = place(g, 'service-1', 1960, 640, { label: 'Everything off', config: { ...config(g, 'service-1'), service: 'light.turn_off', target: lit('target', { entity_id: 'light.hall_1' }), fields: { transition: { kind: 'unset' } } } })
  for (const [kind, id] of [['foreach', 'lights'], ['service', 'turn-on'], ['log', 'note'], ['read_entity', 'weather'], ['object_create', 'report'], ['service', 'notify'],
    ['constant', 'level'], ['constant', 'fade'], ['entity_field', 'temp'], ['compare', 'warm']] as const) g = addNode(g, kind, id, catalog)
  g = place(g, 'lights', 800, 60, { label: 'Each hall light', config: { ...config(g, 'lights'), itemType: 'entity_id', items: lit('array', ['light.hall_1', 'light.hall_3', 'light.hall_5', 'light.hall_7']) } })
  g = place(g, 'turn-on', 1180, -60, { label: 'Turn on', config: { ...config(g, 'turn-on'), service: 'light.turn_on', fields: { brightness_pct: { kind: 'unset' } } } })
  g = place(g, 'level', 800, -160, { label: 'Brightness', config: { ...config(g, 'level'), type: 'number', value: lit('number', 80) } })
  g = place(g, 'fade', 1560, 940, { label: 'Fade time', config: { ...config(g, 'fade'), type: 'number', value: lit('number', 2) } })
  g = place(g, 'temp', 1180, 620, { label: 'Temperature', config: { ...config(g, 'temp'), field: 'attribute', attribute: 'temperature', type: 'number' } })
  g = place(g, 'warm', 1560, 660, { label: 'Above 5 °C?', config: { ...config(g, 'warm'), operator: 'gt', right: lit('number', 5) } })
  g = place(g, 'note', 1560, 120, { label: 'Note the failure', config: { ...config(g, 'note'), level: 'Warn', value: lit('string', 'A hall light did not respond') } })
  g = place(g, 'weather', 1180, 360, { label: 'Read the weather', config: { ...config(g, 'weather'), entity: lit('entity_id', 'weather.home') } })
  g = place(g, 'report', 1560, 360, { label: 'Build the report', config: { ...config(g, 'report'), fields: { lights: lit('number', 4), api_key: lit('string', 'not-shown'), warm: { kind: 'unset' } } } })
  g = place(g, 'notify', 1960, 300, { label: 'Tell my phone', config: { ...config(g, 'notify'), service: 'notify.notify', fields: { message: lit('string', 'Lights on. Weather: …'), data: { kind: 'unset' } } } })
  g = connectNodes(g, 'condition-1', 'lights', 'true', catalog)
  g = connectNodes(g, 'condition-1', 'service-1', 'false', catalog)
  g = connectNodes(g, 'lights', 'turn-on', 'body', catalog)
  g = connectNodes(g, 'turn-on', 'note', 'error', catalog)
  g = connectNodes(g, 'lights', 'weather', 'next', catalog)
  g = connectNodes(g, 'weather', 'notify', 'next', catalog)
  g = connectData(g, 'trigger-1', 'entity_id', 'condition-1', 'entity', catalog)
  g = connectData(g, 'lights', 'item', 'turn-on', 'target', catalog)
  g = connectData(g, 'report', 'value', 'notify', 'field:data', catalog)
  g = connectData(g, 'level', 'value', 'turn-on', 'field:brightness_pct', catalog)
  g = connectData(g, 'fade', 'value', 'service-1', 'field:transition', catalog)
  g = connectData(g, 'weather', 'value', 'temp', 'entity', catalog)
  g = connectData(g, 'temp', 'value', 'warm', 'left', catalog)
  g = connectData(g, 'warm', 'value', 'report', 'field:warm', catalog)
  // `&grid=1`: the same graph as one saved through the API without positions (the 300 × 180 px grid it then gets),
  // where taller cards would overlap the ones below them.
  return gridTrace ? fillGraphLayout({ ...g, nodes: g.nodes.map(({ position, ...node }) => node) }) : g
}
const traceCatalog = (): VisualCatalogSnapshot => ({ ...(rich.catalog as unknown as VisualCatalogSnapshot), services: services as unknown as ServicesSection })
const traceEvents = (): TraceEvent[] => {
  const lights = ['light.hall_1', 'light.hall_3', 'light.hall_5', 'light.hall_7']
  const state = (entity_id: string, value: string, attributes: Record<string, unknown>) => ({ entity_id, state: value, attributes, last_changed: '2026-11-12T18:00:00Z', last_updated: '2026-11-12T18:00:00Z' })
  const forecast = Array.from({ length: 50 }, (_, i) => ({ datetime: `2026-11-${String(13 + Math.floor(i / 24)).padStart(2, '0')}T${String(i % 24).padStart(2, '0')}:00:00Z`, temperature: 6 + (i % 7), condition: i % 5 ? 'cloudy' : 'rainy' }))
  const message = `Lights on in the hall. Weather: cloudy, 7 °C. ${'Forecast: rain later this evening, then clearing overnight. '.repeat(20)}`.slice(0, 1000)
  const events: Omit<TraceEvent, 'seq'>[] = [
    { node: 'trigger-1', frame: '', t: 0, ms: 2, status: 'ok', port: 'next', outputs: {
      entity_id: { type: 'entity_id:binary_sensor', v: 'binary_sensor.hall_0' },
      to_state: { type: 'global::HassSharp.EntityStateSnapshot', v: state('binary_sensor.hall_0', 'on', { device_class: 'motion', friendly_name: 'Hall motion' }) },
      from_state: { type: 'global::HassSharp.EntityStateSnapshot', v: state('binary_sensor.hall_0', 'off', { device_class: 'motion', friendly_name: 'Hall motion' }) },
    } },
    { node: 'condition-1', frame: '', t: 2.4, ms: 1.1, status: 'ok', port: 'true', inputs: { entity: { type: 'entity_id:binary_sensor', v: 'binary_sensor.hall_0', src: { node: 'trigger-1', port: 'entity_id' } } } },
    { node: 'lights', frame: '', t: 3.8, ms: 1712, status: 'ok', port: 'next', inputs: { items: { type: 'array', v: lights } } },
    ...lights.flatMap((light, i): Omit<TraceEvent, 'seq'>[] => {
      const frame = `lights#${i}:body`, t = 4 + i * 430
      const inputs = { target: { type: 'entity_id', v: light, src: { node: 'lights', port: 'item' } }, 'field:brightness_pct': { type: 'number', v: 80, src: { node: 'level', port: 'value' } } }
      const level: Omit<TraceEvent, 'seq'> = { node: 'level', frame, t, ms: 0.02, status: 'ok', inputs: { value: { type: 'number', v: 80 } }, outputs: { value: { type: 'number', v: 80 } } }
      if (i !== 1) return [level, { node: 'turn-on', frame, t, ms: 412 + i * 7, status: 'ok', port: 'next', inputs }]
      return [
        level,
        { node: 'turn-on', frame, t, ms: 403, status: 'error', port: 'error', inputs, error: { type: 'HomeAssistantException', message: 'light.hall_3 is unavailable: the Zigbee device did not answer within 10 seconds.' } },
        { node: 'note', frame, t: t + 404, ms: 0.6, status: 'ok', port: 'next', inputs: { value: { type: 'string', v: 'A hall light did not respond' } } },
      ]
    }),
    { node: 'weather', frame: '', t: 1716, ms: 3.2, status: 'ok', port: 'next', inputs: { entity: { type: 'entity_id', v: 'weather.home' } },
      outputs: { value: { type: 'global::HassSharp.EntityStateSnapshot', v: state('weather.home', 'cloudy', { temperature: 7, humidity: 88, friendly_name: 'Home', forecast }), trunc: true } } },
    { node: 'temp', frame: '', t: 1720, ms: 0.05, status: 'ok', inputs: { entity: { type: 'global::HassSharp.EntityStateSnapshot', v: state('weather.home', 'cloudy', { temperature: 7, friendly_name: 'Home' }), src: { node: 'weather', port: 'value' }, trunc: true } },
      outputs: { value: { type: 'number', v: 7 } } },
    { node: 'warm', frame: '', t: 1720.1, ms: 0.02, status: 'ok', inputs: { left: { type: 'number', v: 7, src: { node: 'temp', port: 'value' } }, right: { type: 'number', v: 5 } },
      outputs: { value: { type: 'boolean', v: true } } },
    { node: 'report', frame: '', t: 1720.2, ms: 0.4, status: 'ok', inputs: { 'field:lights': { type: 'number', v: 4 }, 'field:api_key': { type: 'string', v: '***', redacted: true }, 'field:warm': { type: 'boolean', v: true, src: { node: 'warm', port: 'value' } } },
      outputs: { value: { type: 'object', v: { lights: 4, api_key: '***', warm: true } } } },
    { node: 'notify', frame: '', t: 1721, ms: 288, status: 'ok', port: 'next', inputs: {
      'field:message': { type: 'string', v: message, trunc: true }, 'field:data': { type: 'object', v: { lights: 4, api_key: '***', warm: true }, src: { node: 'report', port: 'value' } },
    } },
  ]
  return events.map((event, i) => ({ ...event, seq: i + 1 }))
}
let tracePolls = 0
const traceResponse = (): RunTraceResponse => {
  const all = traceEvents()
  // Live: one more step every poll (every 2 s), the last one still running, until the run ends.
  const shown = liveTrace ? Math.min(all.length, 2 + tracePolls++) : all.length
  const live = shown < all.length
  const events = all.slice(0, shown).map((event, i, list) => (live && i === list.length - 1 ? { ...event, status: 'running' as const, ms: undefined, port: undefined, outputs: undefined } : event))
  const summary: RunTrace['summary'] = {}
  for (const event of events) {
    const row = (summary[event.node] ??= { count: 0, failed: 0, last: 'ok' })
    row.count++; row.last = event.status
  }
  const run: AutomationRun = {
    runId: 'fixture-trace-run', automationId: 'visual:trace-demo', revision: 'fixture-revision-2', action: 'Visual.TraceDemo::Trigger_trigger_1', actionLabel: 'Motion in the hall',
    triggerNodeId: 'trigger-1', visualId: 'trace-demo', visualRevision: 2, cause: 'Entity', trigger: { kind: 'entity', entityId: 'binary_sensor.hall_0', from: 'off', to: 'on' },
    plannedUtc: null, receivedUtc: '2026-11-12T18:00:00Z', startUtc: '2026-11-12T18:00:00.010Z', endUtc: live ? null : '2026-11-12T18:00:02.020Z', durationMs: live ? null : 2009,
    status: live ? 'Running' : 'Succeeded', error: null, hasTrace: true,
  }
  const trace: RunTrace = {
    format: 1, runId: run.runId, rootNodeId: 'trigger-1', startedUtc: '2026-11-12T18:00:00.010Z', complete: !live, truncated: false, dropped: 0,
    trigger: { platform: 'entity', entity_id: 'binary_sensor.hall_0', from_state: { state: 'off' }, to_state: { state: 'on', attributes: { device_class: 'motion' } } },
    events, summary,
  }
  return { version: 1, run, live, snapshot: { revision: 'fixture-revision-2', visualId: 'trace-demo', visualRevision: 2, graph: traceGraph(), catalog: traceCatalog() }, trace }
}
if (traceFixture) visualBackend.documents.set('trace-demo', { version: 1, id: 'trace-demo', revision: 3, graph: traceGraph(), source: '// Trace fixture', sourceMap: [], automationId: 'visual:trace-demo', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
;(window as any).visualFixture = visualBackend
const visualFixture = new URLSearchParams(location.search).get('view') === 'visual'
const libraryFixture = new URLSearchParams(location.search).get('view') === 'library'
// The automations list (no view, or `?view=list`): six saved automations: healthy with mixed runs and two triggers, not
// active with problems, never run, running now, a long name (wrapping on a small screen) and off. `&state=loading`
// keeps the list loading, `&state=empty` has no automations, `&state=error` fails the list (Try again still fails).
const listState = new URLSearchParams(location.search).get('state')
const listFixture = !traceFixture && !libraryFixture && !visualFixture
if (listFixture && listState !== 'empty') {
  const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()
  const recent = (statuses: AutomationRun['status'][], every = 47) =>
    statuses.map((status, i) => ({ runId: `fixture-home-${i}`, status, startUtc: ago((statuses.length - i) * every), durationMs: status === 'Running' ? null : 20 + i * 7 }))
  const sample = (id: string, name: string, options: { triggers?: Record<string, unknown>[]; extra?: number; active?: boolean; enabled?: boolean; savedMinutes?: number } = {}) => {
    let graph = updateNode(createGraph(name), 'trigger-1', { config: { type: 'entity', entity: 'binary_sensor.hall_0', manual: true, mode: 'Single' } })
    for (const [i, config] of (options.triggers ?? []).entries()) {
      graph = addNode(graph, 'trigger', `trigger-${i + 2}`)
      graph = updateNode(graph, `trigger-${i + 2}`, { config, position: { x: 60, y: 300 + i * 200 } })
    }
    for (let i = 0; i < (options.extra ?? 0); i++) {
      graph = addNode(graph, 'service', `step-${i}`)
      graph = updateNode(graph, `step-${i}`, { position: { x: 960 + i * 300, y: 100 + (i % 2) * 160 } })
      graph = connectNodes(graph, i ? `step-${i - 1}` : 'service-1', `step-${i}`)
    }
    visualBackend.documents.set(id, { version: 1, id, revision: 3, graph, source: '// List fixture', sourceMap: [], automationId: `visual:${id}`,
      currentRevisionActive: options.active ?? true, active: options.active ?? true, enabled: options.enabled, catalogGeneration: 'saved-fixture',
      activationError: options.active === false ? 'Compilation failed during activation' : null, savedUtc: ago(options.savedMinutes ?? 600) })
  }
  sample('evening-lights', 'Evening lights', { triggers: [{ type: 'daily', time: '19:30' }], extra: 2, savedMinutes: 30 })
  visualBackend.runs.set('evening-lights', { recentRuns: recent(['Succeeded', 'Succeeded', 'Failed', 'Succeeded', 'Skipped', 'Succeeded', 'Cancelled', 'Succeeded', 'Stopped', 'Succeeded']), runsToday: 6, failedLastDay: 1, lastRunUtc: ago(47) })
  sample('broken', 'Heating schedule', { triggers: [{ type: 'daily', time: '06:00' }, { type: 'entity', entity: 'binary_sensor.hall_2' }, { type: 'startup' }], active: false, savedMinutes: 5 })
  visualBackend.runs.set('broken', { recentRuns: recent(['Succeeded', 'Succeeded', 'Failed'], 600), runsToday: 0, lastRunUtc: ago(600), problemCount: 2 })
  sample('never', 'Garage door reminder', { triggers: [{ type: 'entity', entity: 'binary_sensor.hall_4' }], savedMinutes: 2000 })
  sample('running', 'Morning coffee', { extra: 4, savedMinutes: 900 })
  visualBackend.runs.set('running', { recentRuns: recent(['Succeeded', 'Succeeded', 'Running'], 1), runsToday: 3, lastRunUtc: ago(0.2) })
  sample('long-name', 'A very long automation name to check that wrapping works on a small phone screen without overflow')
  visualBackend.runs.set('long-name', { recentRuns: recent(['Succeeded'], 3000), runsToday: 0, lastRunUtc: ago(3000) })
  sample('off', 'Holiday lights', { triggers: [{ type: 'daily', time: '17:00' }], enabled: false, savedMinutes: 9000 })
  visualBackend.runs.set('off', { recentRuns: recent(['Succeeded', 'Succeeded'], 20000), runsToday: 0, lastRunUtc: ago(20000) })
}
if (listFixture && listState === 'error') visualBackend.controls.homeError = true
history.replaceState(null, '', traceFixture ? '/hass-sharp/visual/trace-demo/runs/fixture-trace-run' : libraryFixture ? '/hass-sharp/libraries' : visualFixture ? typedFixture?'/hass-sharp/visual/typed-demo':'/hass-sharp/visual/new?name=Motion%20lights' : '/hass-sharp/automations')
const hass = {
  user: { is_admin: true },
  connection:visualBackend.connection,
  async callWS(message: { type: string }) {
    if (message.type === 'hass_sharp/get_entities') return { entities: Array.from({length:80},(_,i)=>({entity_id: `${i%2?'light':'binary_sensor'}.hall_${i}`, name: `Hall ${i%2?'lamp':'motion'} ${i}`})) }
    if (message.type === 'get_services') return { light: { turn_on: {name:'Turn on',description:'Turn on a light'}, turn_off: {name:'Turn off',description:'Turn off a light'} }, climate: { set_temperature: {name:'Set temperature',description:'Change the target temperature'} } }
    if (listState === 'loading' && (message as any).request?.view === 'home') return new Promise(() => {})
    if (message.type === 'hass_sharp/visual'||message.type==='hass_sharp/node_library') return visualBackend.callWS(message)
    if (message.type === 'hass_sharp/run_trace') {
      if (dropTrace && ++traceReads % 3 === 0) throw { code: 3, message: 'Connection lost' }
      return JSON.parse(JSON.stringify(traceResponse()))
    }
    if (message.type === 'hass_sharp/run_history') {
      if (fixture.historyError) throw { message: 'Example history connection error' }
      const request = message as any
      const matching = runs.filter(run => run.automationId === request.automationId && (!request.status?.length || request.status.includes(run.status)))
      return { version: 2, offset: request.offset, limit: request.limit, runs: structuredClone(matching.slice(request.offset, request.offset + request.limit)) }
    }
    if (message.type === 'hass_sharp/run_now') {
      if (fixture.dispatchError) throw { message: 'Example manual dispatch rejection' }
      const request = message as any
      const run: AutomationRun = { ...visualRun(0), automationId: request.automationId, action: `${request.className}::${request.method}`, runId: `fixture-manual-${Date.now()}`,
        cause: 'Manual', trigger: { kind: 'manual' }, receivedUtc: new Date().toISOString(), startUtc: null, plannedUtc: null, endUtc: null, durationMs: null, status: 'Pending', error: null }
      runs.unshift(run)
      setTimeout(() => Object.assign(run, { status: fixture.result, startUtc: new Date().toISOString(), endUtc: new Date().toISOString(), durationMs: 1000 }), 1000)
      return { version: 1, runId: run.runId }
    }
    return []
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
