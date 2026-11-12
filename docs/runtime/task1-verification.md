# Task 1 verification

Executed 2026-11-12 with .NET SDK 11 preview targeting net10.0.

- New API harness initially failed compilation because Configure, AutomationConfiguration and InvocationCause did not exist.
- Python unload regression failed: unsubscribe dictionary retained sensor.test. It passes after clearing bookkeeping before calling unsubscribers.
- Queue regression failed: 100 entity triggers executed out of arrival order. It passes after replacing semaphore scheduling with a registration-order task chain.
- Instance-isolation regression failed: second instance sharing the same MethodInfo timed out waiting for startup. It passes with instance-and-method policy keys.
- Motion-off regression failed: no turn_off dispatch. It passes when the off-trigger owns its cancellable delay.

Final runtime executable passes seven scenario groups: four modes, 100-trigger FIFO,
instance isolation/unload cancellation, and motion-off. Mode scenarios cover startup
without blocking load, entity during startup, manual cause, shortened cancellable
delay and refresh, duplicate declarations, stale callbacks, unload and resubscribe.

Python unittest: one pass. Release publish and example DLL build pass.
No live HA/Python.NET event-loop or device verification was performed. Service tests
verify dispatch only. Cancellation remains cooperative. Public Run now bridge and
persistent attempt/outcome history belong to Task 3. Historical README examples
are marked obsolete; the new declarative guide is the current Task 1 API example.
