import * as THREE from "three";
import {geodeticToWorld} from "@/lib/radar-globe-geodesy";
import type {Scenery} from "./bridge";
let rows:Scenery[]=[];let revision=0;
export function setSceneryMarkers(items:Scenery[]){rows=items.filter(i=>i.latitude!==null&&i.longitude!==null);revision++;}
export const sceneryColour=(item:Scenery)=>item.activation==="disabled"&&item.installed?"#a8a0bd":item.activation==="active"?"#59dbb1":item.installed?"#69b8ed":item.purchased?"#edba74":"#b9bdf5";
export class SceneryOverlay{
  private shown=-1;private pointerStart:[number,number]=[0,0];private positions=new Float32Array();private visibleRows:Scenery[]=[];
  private geometry=new THREE.BufferGeometry();private eye=new THREE.Vector3();private point=new THREE.Vector3();
  private material=new THREE.ShaderMaterial({transparent:true,depthTest:false,depthWrite:false,
    uniforms:{uEye:{value:new THREE.Vector3()},uSize:{value:14}},
    vertexShader:`attribute vec3 color;varying vec3 vColor;varying float vVisible;uniform vec3 uEye;uniform float uSize;
      void main(){vColor=color;vVisible=step(-10.,dot(position,uEye-position));gl_Position=projectionMatrix*modelViewMatrix*vec4(position,1.);gl_PointSize=uSize*vVisible;}`,
    fragmentShader:`varying vec3 vColor;varying float vVisible;void main(){float d=length(gl_PointCoord-vec2(.5));if(d>.48||vVisible<.5)discard;
      float alpha=1.-smoothstep(.38,.48,d);vec3 c=mix(vColor*.28,vColor,1.-smoothstep(.27,.34,d));gl_FragColor=vec4(c,alpha);}`});
  private points=new THREE.Points(this.geometry,this.material);
  constructor(private earth:THREE.Group,private camera:THREE.PerspectiveCamera,private host:HTMLElement){
    this.points.layers.set(6);this.points.frustumCulled=false;this.earth.add(this.points);
    this.host.addEventListener("pointerdown",this.down);this.host.addEventListener("pointerup",this.up);
  }
  private down=(e:PointerEvent)=>{this.pointerStart=[e.clientX,e.clientY];};
  private up=(e:PointerEvent)=>{
    if(e.button!==0||Math.hypot(e.clientX-this.pointerStart[0],e.clientY-this.pointerStart[1])>5)return;
    const rect=this.host.getBoundingClientRect();let best=12**2;let chosen:Scenery|undefined;
    for(let i=0;i<this.visibleRows.length;i++){
      this.point.fromArray(this.positions,i*3);if(this.point.dot(this.eye.clone().sub(this.point))<0)continue;
      this.point.applyMatrix4(this.earth.matrixWorld).project(this.camera);if(this.point.z < -1 || this.point.z > 1)continue;
      const dx=(this.point.x+1)*rect.width/2-(e.clientX-rect.left),dy=(1-this.point.y)*rect.height/2-(e.clientY-rect.top),distance=dx*dx+dy*dy;
      if(distance<best){best=distance;chosen=this.visibleRows[i];}
    }
    if(chosen)this.host.dispatchEvent(new CustomEvent("atlas-scenery-select",{detail:chosen.id,bubbles:true}));
  };
  update(){
    if(this.shown!==revision){
      this.visibleRows=rows;this.positions=new Float32Array(rows.length*3);const colours=new Float32Array(rows.length*3),colour=new THREE.Color();
      rows.forEach((row,i)=>{this.positions.set(geodeticToWorld(row.latitude!,row.longitude!,150),i*3);colour.set(sceneryColour(row)).toArray(colours,i*3);});
      this.geometry.dispose();this.geometry=new THREE.BufferGeometry();this.geometry.setAttribute("position",new THREE.BufferAttribute(this.positions,3));
      this.geometry.setAttribute("color",new THREE.BufferAttribute(colours,3));this.points.geometry=this.geometry;
      this.shown=revision;
    }
    this.earth.worldToLocal(this.camera.getWorldPosition(this.eye));this.material.uniforms.uEye.value.copy(this.eye);
    this.material.uniforms.uSize.value=14*Math.min(2,window.devicePixelRatio||1);
  }
  dispose(){this.host.removeEventListener("pointerdown",this.down);this.host.removeEventListener("pointerup",this.up);this.points.removeFromParent();this.geometry.dispose();this.material.dispose();}
}
