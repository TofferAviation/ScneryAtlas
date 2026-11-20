import {build} from "esbuild";
import {readFile,mkdir,cp,writeFile,access} from "node:fs/promises";
import path from "node:path";
import {fileURLToPath} from "node:url";
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),"..");
const appVersion=JSON.parse(await readFile(path.join(root,"package.json"),"utf8")).version;
if(!(await readFile(path.join(root,"app/SceneryAtlas.App.csproj"),"utf8")).includes(`<Version>${appVersion}</Version>`))throw Error("package.json and .NET app version differ.");
const website=path.resolve(root,"../website-build-check");
const vendor=path.join(root,"vendor/website");
const exists=async p=>{try{await access(p);return true;}catch{return false;}};
const source=await exists(path.join(vendor,"src/components/radar-globe/scene.ts"))?vendor:website;
const output=path.join(root,"app/Web");await mkdir(output,{recursive:true});
function replaceOnce(text,from,to){if(!text.includes(from))throw Error(`Globe integration hook changed: ${from}`);return text.replace(from,()=>to);}
const result=await build({entryPoints:[path.join(root,"renderer/main.tsx")],bundle:true,format:"esm",target:"chrome120",minify:true,
  outfile:path.join(output,"app.js"),metafile:true,jsx:"automatic",external:["textures/*"],define:{"process.env.NODE_ENV":'"production"',"__ATLAS_VERSION__":JSON.stringify(appVersion)},
  alias:{"@":path.join(source,"src"),"three/addons":path.join(root,"node_modules/three/examples/jsm"),three:path.join(root,"node_modules/three"),react:path.join(root,"node_modules/react"),"react-dom":path.join(root,"node_modules/react-dom")},
  plugins:[{name:"standalone-scenery-overlay",setup(api){api.onLoad({filter:/[\\/]radar-globe[\\/]scene\.ts$/},async(args)=>{
    let contents=await readFile(args.path,"utf8");
    contents=`import {SceneryOverlay} from ${JSON.stringify(path.join(root,"renderer/scenery-overlay.ts"))};\n`+contents;
    contents=replaceOnce(contents,'const camera = new THREE.PerspectiveCamera(testView?.fov ?? 35, 1, 0.5, 120000);','const camera = new THREE.PerspectiveCamera(testView?.fov ?? 35, 1, 0.5, 120000);\nconst atlasOverlay = new SceneryOverlay(earth,camera,host);');
    contents=contents.replaceAll('earth.updateMatrixWorld(true);','earth.updateMatrixWorld(true);\natlasOverlay.update();');
    contents=replaceOnce(contents,'canvas.addEventListener("globe-reset-view", resetView);',`canvas.addEventListener("globe-reset-view", resetView);
      const atlasFocus=(event)=>{const point=event.detail;if(!Number.isFinite(point.latitude)||!Number.isFinite(point.longitude))return;latitude=THREE.MathUtils.clamp(point.latitude,-89.9,89.9);longitude=wrapLongitude(point.longitude);targetDistance=WGS84_A_KM+450;pitch=0;groundRequested=false;groundPitchTarget=null;orbit();};
      canvas.addEventListener("globe-scenery-focus",atlasFocus);`);
    contents=replaceOnce(contents,'canvas.removeEventListener("globe-reset-view", resetView);','canvas.removeEventListener("globe-reset-view", resetView);\ncanvas.removeEventListener("globe-scenery-focus",atlasFocus);atlasOverlay.dispose();');
    return {contents,loader:"ts",resolveDir:path.dirname(args.path)};
  });}}]
});
// Snapshot the actually bundled browser modules so future builds do not need the BA website checkout.
if(source===website){
  for(const input of Object.keys(result.metafile.inputs)){
    const absolute=path.resolve(root,input);const rel=path.relative(website,absolute);
    if(rel.startsWith("src"+path.sep)&&!rel.startsWith("..")){const destination=path.join(vendor,rel);await mkdir(path.dirname(destination),{recursive:true});await cp(absolute,destination);}
  }
  await mkdir(path.join(vendor,"public"),{recursive:true});await cp(path.join(website,"public/textures"),path.join(vendor,"public/textures"),{recursive:true});
}
await cp(path.join(source===website?website:vendor,"public/textures"),path.join(output,"textures"),{recursive:true});
await cp(path.join(root,"renderer/index.html"),path.join(output,"index.html"));
const csv=await exists(path.join(root,"data/airports-source.csv"))?await readFile(path.join(root,"data/airports-source.csv"),"utf8"):null;
function parseCSV(text){const rows=[];let row=[],field="",quoted=false;for(let i=0;i<text.length;i++){const c=text[i];if(c==='"'){if(quoted&&text[i+1]==='"'){field+='"';i++;}else quoted=!quoted;}else if(c===','&&!quoted){row.push(field);field="";}else if(c==='\n'&&!quoted){row.push(field.replace(/\r$/,""));rows.push(row);row=[];field="";}else field+=c;}if(field||row.length){row.push(field);rows.push(row);}return rows;}
const [columns,...rows]=csv?parseCSV(csv):[[]],index=name=>columns.indexOf(name);const airports=new Map();
if(!csv){for(const airport of JSON.parse(await readFile(path.join(root,"data/airports.json"),"utf8")))airports.set(airport.icao,airport);}
for(const row of rows){const icao=row[index("icao_code")];if(!/^[A-Z][A-Z0-9]{3}$/.test(icao)||row[index("type")]==="closed")continue;
  const [latitude,longitude]=(row[index("coordinates")]||"").split(/,\s*/).map(Number);
  if(!Number.isFinite(latitude)||!Number.isFinite(longitude)||Math.abs(latitude)>90||Math.abs(longitude)>180)continue;
  if(!airports.has(icao))airports.set(icao,{icao,name:row[index("name")],latitude,longitude,country:row[index("iso_country")]});
}
await mkdir(path.join(root,"data"),{recursive:true});
await writeFile(path.join(root,"data/airports.json"),JSON.stringify([...airports.values()]));
await cp(path.join(root,"data/airports.json"),path.join(output,"airports.json"));
await writeFile(path.join(root,"data/globe-source.json"),JSON.stringify({websiteCommit:"0be31f4325eff92a0c5ae0e9d77cc2981bf9380d",snapshotDate:"2026-11-20",modules:Object.keys(result.metafile.inputs).length},null,2));
console.log(`Standalone renderer built; ${airports.size} ICAO airports. Browser globe bundled locally; no BA website account or server required.`);
