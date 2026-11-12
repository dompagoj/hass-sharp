import { createSignal, Show } from 'solid-js'
import { Button } from './Controls'
import { Dialog } from './Dialog'
export function HelpDialog(props: { mode: 'visual' | 'code' }){
 const [open, setOpen] = createSignal(false)
 return <><Button aria-label={props.mode==='visual'?'Visual automation help':'C# automation help'} onClick={() => setOpen(true)}>Help</Button>
 <Dialog open={open()} title={props.mode==='visual'?'Visual automation help':'C# automation help'} width="large" onClose={() => setOpen(false)}>
 <Show when={props.mode==='visual'} fallback={<><h3>Configure typed triggers</h3><p>Configure declares actions and their triggers. Use generated Entities references for entity subscriptions; entity display names are not C# identifiers.</p><pre><code>{`public override AutomationConfiguration Configure() => new(
    new AutomationAction(nameof(Execute), AutomationMode.Single,
        Entities: new IEntityReference[] { Entities.BinarySensor.HallMotion },
        Manual: true));

public Task Execute() {
    Logger.Info("Motion changed");
    return Task.CompletedTask;
}`}</code></pre><p>Single skips overlapping invocations. Use the editor completion list to discover entity and service helpers. Save compiles and activates the automation; Run now dispatches an enabled manual action.</p></>}>
 <h3>Build and connect steps</h3><p>Add a trigger, then actions or conditions. Drag connections or use Output and Connect to in Step details. Conditions have If true and If false paths. On error handles a failed step.</p><p>Select Entity ID or Service to search the live Home Assistant catalog. Search by friendly name or domain, then choose a result. Cancel preserves your selection.</p><p>Triggers can share downstream steps. Choose Hass, Logic, Flow, Data or Code in the palette and search chooser. Repeat runs a bounded Body before Next; body paths must stay separate. Parallel work requires custom C#. Saving activates the flow and startup triggers may run immediately.</p>
 <h3>Typed values and logic</h3><p>Read entity saves a state or attribute as a boolean, number or string. Set value assigns or explicitly converts a value. And, Or, Xor, Not and Compare produce named boolean values; If branches on one. Select Literal or Value reference for inputs. Producers must execute before their values are read. Missing or incompatible inputs follow On error. Values are separate for each invocation.</p>
 <h3>Bounded flow</h3><p>Repeat runs 0–1,000 iterations. Leave a body output unconnected to finish that iteration; use Next on Repeat for continuation. Up to 8 nested repeats and 100,000 executed steps are allowed. Stop ends the entire invocation. Wait until polls fresh entity state or attributes with a mandatory timeout; it cannot wait on a frozen local value. Wait, Repeat and Wait until respect cancellation.</p>
 </Show>
 <h3>Advanced C# and helpers</h3><p>Imports add namespaces, for example <code>using System.Linq;</code>. Members hold reusable methods and fields inside the generated class. A helper can be <code>private bool IsEven(int value) =&gt; value % 2 == 0;</code>. Fields persist across invocations; avoid unsafe shared state.</p><p>Await child work and pass CancellationToken to delays and I/O. Keep generated class and action signatures intact. Select a diagnostic to locate its source; visual imports and members expand automatically.</p>
 </Dialog></>
}
