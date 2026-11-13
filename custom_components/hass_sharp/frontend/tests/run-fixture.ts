// Run history v2 records (contract C9) for run list tests. Values are samples only.
import type { AutomationRun, RunTrigger } from '../src/types'

export const triggers = {
  haState: { kind: 'ha', platform: 'state', id: '0', description: 'state of climate.outside_bathroom_vtrv', entityId: 'climate.outside_bathroom_vtrv', from: 'off', to: 'heat' },
  haEvent: { kind: 'ha', platform: 'event', id: '0', description: 'event \'doorbell_pressed\'', eventType: 'doorbell_pressed' },
  haTime: { kind: 'ha', platform: 'time', id: '0', description: 'time' },
  entity: { kind: 'entity', entityId: 'binary_sensor.hall_motion', from: 'off', to: 'on' },
  schedule: { kind: 'schedule', scheduleId: 'daily-0', planned: '2026-11-12T06:30:00Z' },
  manual: { kind: 'manual' },
  startup: { kind: 'startup' },
} satisfies Record<string, RunTrigger>

export const run = (overrides: Partial<AutomationRun> = {}): AutomationRun => ({
  runId: 'run-1', automationId: 'automation-1', revision: 'revision-1', action: 'VisualAutomation::Run_7431',
  actionLabel: 'VTRV stays in heat mode for 30 minutes', triggerNodeId: 't1', visualId: 'heating', visualRevision: 7,
  cause: 'HomeAssistantTrigger', trigger: triggers.haState, plannedUtc: null, receivedUtc: '2026-11-12T01:00:00Z',
  startUtc: '2026-11-12T01:00:01Z', endUtc: '2026-11-12T01:00:02Z', durationMs: 1000,
  status: 'Succeeded', error: null, hasTrace: true, ...overrides,
})

/** A row as the v1 backend still returns it (before Task 10): no label, trigger summary, or trace flag. */
export const v1Run = (overrides: Record<string, unknown> = {}) => ({
  runId: 'run-v1', automationId: 'automation-1', revision: 'revision-1', action: 'Home.Lights::Execute',
  cause: 'Manual', details: '{"platform":"state","entity_id":"light.hall"}', plannedUtc: null, receivedUtc: '2026-11-12T01:00:00Z',
  startUtc: '2026-11-12T01:00:01Z', endUtc: '2026-11-12T01:00:02Z', durationMs: 1000,
  status: 'Succeeded', error: null, ...overrides,
})

export const page = (runs: unknown[], offset = 0) => ({ version: 2, offset, limit: 20, runs })
