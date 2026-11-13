import { createSignal, Show } from 'solid-js'
import { Button } from './Controls'
import { Dialog } from './Dialog'

/**
 * Help for the C# or the visual editor. Without `open` it brings its own "Help" button; with `open` the page opens
 * it (the visual editor does, from its "⋯" menu).
 */
export function HelpDialog(props: { mode: 'visual' | 'code'; open?: boolean; onClose?: () => void }){
 const [own, setOwn] = createSignal(false)
 const open = () => props.open ?? own()
 const close = () => { setOwn(false); props.onClose?.() }
 return <><Show when={props.open === undefined}><Button aria-label={props.mode==='visual'?'Visual automation help':'C# automation help'} onClick={() => setOwn(true)}>Help</Button></Show>
 <Dialog open={open()} title={props.mode==='visual'?'Visual automation help':'C# automation help'} width="large" onClose={close}>
 <Show when={props.mode==='visual'} fallback={<><h3>Configure typed triggers</h3><p>Configure declares actions and their triggers. Use generated Entities references for entity subscriptions; entity display names are not C# identifiers.</p><pre><code>{`public override AutomationConfiguration Configure() => new(
    new AutomationAction(nameof(Execute), AutomationMode.Single,
        Entities: new IEntityReference[] { Entities.BinarySensor.HallMotion },
        Manual: true));

public Task Execute() {
    Logger.Info("Motion changed");
    return Task.CompletedTask;
}`}</code></pre><p>Single skips overlapping invocations. Use the editor completion list to discover entity and service helpers. Save compiles and activates the automation; Run now dispatches an enabled manual action.</p></>}>
 <h3>Build and connect steps</h3>
 <p>An automation starts with a trigger and runs its steps from left to right. Add a step from the step library on the left: click it to put it in the middle of the view, or drag it onto the canvas. On the canvas, double-click or right-click empty space, press A, or use Add step to search for a step.</p>
 <p>Connect steps on the canvas only. Drag from a step's output to another step's input; drop on empty space to add a step that is connected right away. From the keyboard, press Enter on a pin, then on the pin to connect it to. To remove a connection, select it and press Delete.</p>
 <p>Conditions continue on If true or If false. On error is followed when a step fails. Triggers can share the steps after them.</p>
 <h3>Edit a step</h3>
 <p>Select a step to edit it in the panel on the right. Simple values can also be changed on the step itself; both show the same value. A value that comes from another step shows where it comes from. On a phone, selecting a step shows its name at the bottom: tap it to edit the step.</p>
 <p>Search for an entity or a service by its name or its id. Optional fields are added with "+ Add field" and removed with their remove button.</p>
 <h3>Values and logic</h3>
 <p>Read entity gives a state or an attribute. Set value stores a value or converts it. And, Or, Exclusive or, Not and Compare give Yes/No values, and If continues on one of them. A step can only use a value from a step that ran before it. A missing or unsuitable value follows On error.</p>
 <h3>Repeat and wait</h3>
 <p>Repeat runs its Body up to 1,000 times, then continues with Next. Stop ends the whole run. Wait until checks an entity until it matches or the time limit passes. Waits and repeats stop when the run is cancelled.</p>
 <h3>Problems, code and runs</h3>
 <p>The bar at the bottom opens Problems, Generated C# and Runs. Select a problem to go to the step and the field it is about. Run now starts the saved automation; Runs lists what happened, and View trace shows the path a run took and its values.</p>
 <h3>Keyboard</h3>
 <p>Ctrl+Z undoes and Ctrl+Y (or Ctrl+Shift+Z) redoes. On the canvas, arrow keys move the selected steps, Ctrl+C, Ctrl+V and Ctrl+D copy, paste and duplicate, and Delete removes. F makes the canvas full screen; Escape or "Exit full screen" leaves it.</p>
 <h3>Custom C#</h3>
 <p>A Custom C# step runs your own code. Its Advanced section also has the C# shared by this automation: usings, for example <code>using System.Linq;</code>, and helper members, for example <code>private bool IsEven(int value) =&gt; value % 2 == 0;</code>. Fields keep their value between runs. Await other work and pass CancellationToken to delays and I/O.</p>
 <p>Saving activates the automation; a trigger set to run when Home Assistant starts may run right away.</p>
 </Show>
 </Dialog></>
}
