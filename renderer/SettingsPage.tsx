import React from "react";
import type {Library} from "./bridge";

export type GlobePreferences={
  quality:"auto"|"low"|"medium"|"high";
  cloudsOn:boolean;
  cloudMode:"forecast"|"satellite";
  cloudStrength:number;
  auroraOn:boolean;
};

export const defaultGlobePreferences:GlobePreferences={quality:"auto",cloudsOn:true,cloudMode:"forecast",cloudStrength:65,auroraOn:true};

export function readGlobePreferences(userId:string):GlobePreferences{
  try{
    const saved=JSON.parse(localStorage.getItem(`atlas-globe-${userId}`)||"{}");
    return {
      quality:["auto","low","medium","high"].includes(saved.quality)?saved.quality:"auto",
      cloudsOn:typeof saved.cloudsOn==="boolean"?saved.cloudsOn:true,
      cloudMode:saved.cloudMode==="satellite"?"satellite":"forecast",
      cloudStrength:Number.isFinite(saved.cloudStrength)?Math.max(0,Math.min(100,Math.round(saved.cloudStrength))):65,
      auroraOn:typeof saved.auroraOn==="boolean"?saved.auroraOn:true,
    };
  }catch{return {...defaultGlobePreferences};}
}

type Props={
  library:Library;
  busy:boolean;
  preferences:GlobePreferences;
  onPreferences:(next:GlobePreferences)=>void;
  onPerform:(method:string,label:string,data?:Record<string,unknown>)=>Promise<void>;
};

export function SettingsPage({library,busy,preferences,onPreferences,onPerform}:Props){
  const [simulator,setSimulator]=React.useState("MSFS 2024");
  const set=<K extends keyof GlobePreferences>(key:K,value:GlobePreferences[K])=>onPreferences({...preferences,[key]:value});
  const addFolder=(kind:string)=>void onPerform("roots.add","Choosing add-on folder…",{kind,simulator});
  return <section className="settings-page" aria-label="Settings">
    <div className="settings-inner">
      <div className="settings-heading"><span className="eyebrow">SCENERY ATLAS</span><h1>Settings</h1><p>Set up your simulator folders and choose how the globe looks. Display choices are saved for this local account.</p></div>
      <div className="settings-layout">
        <nav className="settings-index" aria-label="Settings sections">
          <a href="#settings-simulators">Simulator folders</a>
          <a href="#settings-clouds">Volumetric clouds</a>
          <a href="#settings-display">Globe display</a>
        </nav>
        <div className="settings-sections">
          <section className="settings-card" id="settings-simulators">
            <div className="settings-card-head"><div><span className="eyebrow">01 / LIBRARY</span><h2>Simulator folders</h2></div><span className="settings-count">{library.scan?.roots.length??0} found</span></div>
            <p>Select the actual Community or Custom Scenery folder, including folders on another drive. Add an external Addon Linker storage folder to show packages that are currently disabled. Scanning does not change your sim files.</p>
            <div className="settings-field"><strong>Simulator</strong><div className="choice-row" role="group" aria-label="Simulator">{["MSFS 2024","MSFS 2020","X-Plane 12","X-Plane 11"].map(name=><button key={name} className={simulator===name?"choice active":"choice"} aria-pressed={simulator===name} onClick={()=>setSimulator(name)}>{name}</button>)}</div></div>
            <div className="settings-actions"><button className="primary" disabled={busy} onClick={()=>addFolder(simulator.startsWith("MSFS")?"primary-community":"community")}>Select {simulator.startsWith("MSFS")?"Community":"Custom Scenery"} folder</button><button className="secondary" disabled={busy} onClick={()=>addFolder("library")}>Add external add-on storage</button></div>
            <div className="settings-roots"><h3>Detected folders</h3>{library.scan?.roots.length?library.scan.roots.map(root=><div className="root-card" key={root.simulator+root.path}><div><strong>{root.simulator} · {root.source}</strong><span className="path">{root.path}</span></div>{library.extraRoots.some(r=>r.path===root.path)?<button className="text-button" disabled={busy} onClick={()=>void onPerform("roots.remove","Removing folder from this profile…",{path:root.path,simulator:root.simulator})}>Remove</button>:<span className="badge">DETECTED</span>}</div>):<div className="empty">No simulator folders detected. Select a folder above to get started.</div>}{library.scan?.warnings.length?<details className="scan-warnings"><summary>Scan notes · {library.scan.warnings.length}</summary>{library.scan.warnings.map((warning,index)=><p key={index}>{warning}</p>)}</details>:null}</div>
          </section>
          <section className="settings-card" id="settings-clouds">
            <div className="settings-card-head"><div><span className="eyebrow">02 / ATMOSPHERE</span><h2>Volumetric clouds</h2></div><label className="switch-row"><input type="checkbox" checked={preferences.cloudsOn} onChange={event=>set("cloudsOn",event.target.checked)}/><span>Enabled</span></label></div>
            <p>Cloud position comes from the selected weather source. Strength changes only their appearance; it never creates cloud cover in clear areas.</p>
            <div className="settings-field"><strong>Cloud source</strong><div className="choice-row" role="group" aria-label="Cloud source"><button className={preferences.cloudMode==="forecast"?"choice active":"choice"} aria-pressed={preferences.cloudMode==="forecast"} onClick={()=>set("cloudMode","forecast")}>Global forecast <small>NOAA GFS · worldwide</small></button><button className={preferences.cloudMode==="satellite"?"choice active":"choice"} aria-pressed={preferences.cloudMode==="satellite"} onClick={()=>set("cloudMode","satellite")}>Observed satellite <small>GOES · available regions</small></button></div></div>
            <div className="settings-field"><div className="range-heading"><label htmlFor="cloud-strength">Cloud strength</label><output htmlFor="cloud-strength">{preferences.cloudStrength}%</output></div><input id="cloud-strength" type="range" min="0" max="100" step="1" value={preferences.cloudStrength} disabled={!preferences.cloudsOn} onChange={event=>set("cloudStrength",Number(event.target.value))}/><small>Visual opacity only. Zero hides clouds; higher values make detected clouds more visible.</small></div>
          </section>
          <section className="settings-card" id="settings-display">
            <div className="settings-card-head"><div><span className="eyebrow">03 / DISPLAY</span><h2>Globe display</h2></div></div>
            <div className="settings-field"><strong>Render quality</strong><div className="choice-row" role="group" aria-label="Render quality">{(["auto","low","medium","high"] as const).map(level=><button key={level} className={preferences.quality===level?"choice active":"choice"} aria-pressed={preferences.quality===level} onClick={()=>set("quality",level)}>{level==="auto"?"Auto · adaptive":level[0].toUpperCase()+level.slice(1)}</button>)}</div><small>Auto adapts to your hardware. Lower quality reduces cloud rendering work.</small></div>
            <div className="settings-field setting-toggle"><div><strong>NOAA aurora</strong><small>Show northern and southern lights from NOAA OVATION data when available.</small></div><label className="switch-row"><input type="checkbox" checked={preferences.auroraOn} onChange={event=>set("auroraOn",event.target.checked)}/><span>{preferences.auroraOn?"On":"Off"}</span></label></div>
          </section>
        </div>
      </div>
    </div>
  </section>;
}
