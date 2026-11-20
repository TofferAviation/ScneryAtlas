import assert from "node:assert/strict";
import {gzipSync} from "node:zlib";
import {unpackCloudData} from "../vendor/website/src/lib/radar-cloud-transport.ts";

const volume=Buffer.alloc(4*2*4,0);
const input={
  source:"NOAA GFS",encoding:"rgba8-gzip-base64",width:4,height:2,
  layers:[{id:"low",volume:gzipSync(volume).toString("base64")}],
};
const unpacked=await unpackCloudData(input);
assert.equal(unpacked.encoding,"rgba8-base64");
assert.equal(unpacked.layers[0].volume,volume.toString("base64"));
assert.equal(Object.hasOwn(unpacked,"satellite"),false,"Forecast transport must not add a satellite field");
console.log("PASS forecast cloud transport retains the strict forecast schema");
