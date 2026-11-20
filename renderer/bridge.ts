export type Airport = {icao:string;name:string;latitude:number;longitude:number;country:string};
export type Scenery = { id:string;title:string;developer:string;version:string;icao:string|null;latitude:number|null;longitude:number|null;simulator:string;packageName:string|null;localPath:string|null;installed:boolean;activation:"active"|"enabled"|"disabled"|"unknown";evidence:string;storeUrl:string|null;provider:string|null;purchased:boolean;available:boolean;price:string|null;compatibleSimulators:string[]|null;requiredLibraries:{name:string;downloadUrl:string;packageName:string|null}[]|null;kind:string };
export type UpdateStatus = {installedVersion:string;latestVersion:string|null;available:boolean;installerPath:string|null;message:string;folder:string|null;source:"github"|"local"};
export type Root = {path:string;simulator:string;mounted:boolean;source:string};
export type Library = {items:Scenery[];scan:{roots:Root[];warnings:string[];scannedAt:string;packagesExamined:number}|null;extraRoots:Root[]};
export type User = {id:string;username:string;displayName:string};
declare global { interface Window {chrome?:{webview?:{postMessage:(message:unknown)=>void;addEventListener:(name:string,handler:(event:MessageEvent)=>void)=>void}}} }
let sequence=0;
const requests=new Map<string,{resolve:(value:any)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>();
window.chrome?.webview?.addEventListener("message",event=>{
  const result=event.data, pending=requests.get(result?.id);if(!pending)return;
  clearTimeout(pending.timer);requests.delete(result.id);
  if(result.ok)pending.resolve(result.result);else pending.reject(new Error(result.error||"Action failed."));
});
export function call<T>(method:string,data:Record<string,unknown>={}):Promise<T>{
  if(!window.chrome?.webview)return Promise.reject(new Error("Open this screen in the Scenery Atlas desktop application."));
  const id=String(++sequence);
  return new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{requests.delete(id);reject(new Error("The app is still busy. Try again shortly."));},method==="updates.install"?600_000:180_000);
    requests.set(id,{resolve,reject,timer});window.chrome!.webview!.postMessage({id,method,data});
  });
}
