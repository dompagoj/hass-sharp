import { afterEach, expect, it } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { NodeConfig } from '../src/visual/NodeConfig'
import { NodeChooser } from '../src/visual/NodeChooser'
import { TriggerConfig } from '../src/visual/TriggerConfig'
import { addNode, createGraph, palette, categories } from '../src/visual/graph'
import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
function host() { const host = document.createElement('div'); document.body.append(host); return host }
function change(root: Element, label: string, value: string) {
 const field = root.querySelector<HTMLInputElement>(`[aria-label="${label}"]`)!
 expect(field,label).not.toBeNull(); field.value=value; field.dispatchEvent(new Event(field.tagName === 'SELECT' ? 'change' : 'input',{bubbles:true}))
}
it.each(['default', 'selected'])('clears a %s service target explicitly and restores picker focus', async selection => {
 const root=host(); const graph=addNode(createGraph('Service'),'service','service'); const [node,setNode]=createSignal(graph.nodes.at(-1)!)
 client=new QueryClient()
 dispose=render(()=><QueryClientProvider client={client}><HassCtx.Provider value={{callWS:async()=>({entities:[{entity_id:'light.kitchen',friendly_name:'Kitchen'}]})} as any}><NodeConfig node={node()} nodes={graph.nodes} onChange={config=>setNode({...node(),config})}/></HassCtx.Provider></QueryClientProvider>,root)
 const picker=root.querySelector<HTMLButtonElement>('[aria-label="Entity ID"]')!
 picker.focus(); picker.click(); await Promise.resolve()
 if (selection==='selected') {
  // Manual entry also covers a saved target absent from the current catalog.
  change(root,'Search entities','light.kitchen')
  Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(b=>b.textContent==='Use ID: light.kitchen')!.click()
  expect(node().config.entity).toBe('light.kitchen'); picker.click(); await Promise.resolve()
 }
 expect(node().config.entity).toBe(selection==='default'?'light.hall':'light.kitchen')
 const clear=Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(b=>b.textContent==='No target')
 expect(clear,'optional service target has an explicit clear action').toBeDefined()
 clear!.click(); await Promise.resolve()
 expect(node().config.entity).toBe('')
 expect(picker.getAttribute('aria-expanded')).toBe('false')
 expect(document.activeElement).toBe(picker)
 picker.click(); await Promise.resolve()
 change(root,'Search entities','light.other')
 Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find(b=>b.textContent==='Cancel')!.click()
 expect(node().config.entity).toBe('')
 expect(document.activeElement).toBe(picker)
})
it.each(['trigger','condition','read_entity','wait_until'] as const)('keeps the %s entity picker required', kind => {
 const root=host(); const graph=addNode(createGraph('Required'),kind,'required'); const node=graph.nodes.at(-1)!
 client=new QueryClient()
 dispose=render(()=><QueryClientProvider client={client}><HassCtx.Provider value={{callWS:async()=>({entities:[]})} as any}>{kind==='trigger'?<TriggerConfig node={node} version={3} onChange={()=>{}}/>:<NodeConfig node={node} nodes={graph.nodes} onChange={()=>{}}/>}</HassCtx.Provider></QueryClientProvider>,root)
 root.querySelector<HTMLButtonElement>('[aria-label="Entity ID"]')!.click()
 expect(root.textContent).not.toContain('No target')
 change(root,'Search entities','')
 expect(root.textContent).not.toContain('Use ID:')
})
it('categorizes every node and filters chooser by category and search', () => {
 const root=host(); let chosen=''
 dispose=render(()=><NodeChooser x={0} y={0} allowed={()=>true} onClose={()=>{}} onChoose={kind=>chosen=kind}/>,root)
 expect(new Set(palette.map(item=>item.category))).toEqual(new Set(categories))
 change(root,'Step category','Logic')
 expect(root.querySelectorAll('[role="option"]')).toHaveLength(6)
 change(root,'Search steps','exactly')
 expect(root.querySelectorAll('[role="option"]')).toHaveLength(1)
 root.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}))
 expect(chosen).toBe('xor')
 change(root,'Step category','Flow')
 expect(root.textContent).toContain('No available steps')
})
it('filters boolean references and stores typed literal edits without mutating producer nodes', () => {
 const root=host(); let graph=createGraph('Values');graph=addNode(addNode(addNode(graph,'set_value','text'),'and','boolean'),'if','if')
 const [node,setNode]=createSignal(graph.nodes.at(-1)!)
 dispose=render(()=><NodeConfig node={node()} nodes={graph.nodes} onChange={config=>setNode({...node(),config})}/>,root)
 change(root,'Condition source','variable')
 const refs=root.querySelector<HTMLSelectElement>('[aria-label="Condition reference"]')!
 expect([...refs.options].map(o=>o.value)).toEqual(['','and_result'])
 expect(node().config.value).toEqual({kind:'variable',name:'and_result'})
 change(root,'Condition source','literal');change(root,'Condition literal','false')
 expect(node().config.value).toEqual({kind:'literal',type:'boolean',value:false})
 expect(graph.nodes.find(n=>n.id==='boolean')!.config.left).toEqual({kind:'literal',type:'boolean',value:true})
})
it('offers current entity attributes while retaining an unavailable saved attribute and explicit type', () => {
 const root=host();const graph=addNode(createGraph('Read'),'read_entity','read')
 const [node,setNode]=createSignal({...graph.nodes.at(-1)!,config:{...graph.nodes.at(-1)!.config,entity:'sensor.temperature',field:'attribute',attribute:'saved_key'}})
 client=new QueryClient()
 dispose=render(()=><QueryClientProvider client={client}><HassCtx.Provider value={{states:{'sensor.temperature':{attributes:{unit_of_measurement:'°C',temperature:21}}},callWS:async()=>({entities:[]})} as any}><NodeConfig node={node()} nodes={graph.nodes} onChange={config=>setNode({...node(),config} as any)}/></HassCtx.Provider></QueryClientProvider>,root)
 expect([...root.querySelectorAll('datalist option')].map(o=>o.getAttribute('value'))).toEqual(['temperature','unit_of_measurement'])
 expect(root.querySelector<HTMLInputElement>('[aria-label="Attribute"]')!.value).toBe('saved_key')
 change(root,'Attribute','temperature');change(root,'Result type','string')
 expect(node().config).toMatchObject({attribute:'temperature',type:'string'})
})
it('preserves intermediate negative/decimal numeric input and makes cleared input invalid', () => {
 const root=host();const graph=addNode(createGraph('Numbers'),'compare','compare');const [node,setNode]=createSignal(graph.nodes.at(-1)!)
 dispose=render(()=><NodeConfig node={node()} nodes={graph.nodes} onChange={config=>setNode({...node(),config})}/>,root)
 const field=()=>root.querySelector<HTMLInputElement>('[aria-label="Left operand literal"]')!
 for (const value of ['-','-2','-2.','-2.5']) { change(root,'Left operand literal',value);expect(field().value).toBe(value) }
 expect(node().config.left).toEqual({kind:'literal',type:'number',value:-2.5})
 change(root,'Left operand literal','')
 expect(node().config.left).toEqual({kind:'literal',type:'number',value:''})
 expect(field().getAttribute('aria-invalid')).toBe('true')
 expect(root.textContent).toContain('Enter a finite number')
})
