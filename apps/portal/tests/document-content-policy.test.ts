import test from "node:test";
import assert from "node:assert/strict";
import {detectSupportedDocumentContent} from "../src/document-content-policy.ts";
test("detects PDF anywhere in first 1024 bytes",()=>{const b=new Uint8Array(1028);b.set(new TextEncoder().encode("%PDF-1.7"),1023);assert.deepEqual(detectSupportedDocumentContent(b),{mediaType:"application/pdf",extension:".pdf"});});
test("rejects PDF header after first 1024 bytes",()=>{const b=new Uint8Array(1030);b.set(new TextEncoder().encode("%PDF-1.7"),1024);assert.equal(detectSupportedDocumentContent(b),null);});
test("detects JPEG and PNG signatures",()=>{assert.deepEqual(detectSupportedDocumentContent(new Uint8Array([255,216,255,224])),{mediaType:"image/jpeg",extension:".jpg"});assert.deepEqual(detectSupportedDocumentContent(new Uint8Array([137,80,78,71,13,10,26,10])),{mediaType:"image/png",extension:".png"});});
test("rejects unsupported or empty content regardless of name or declared MIME",()=>{assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("not a PDF")),null);assert.equal(detectSupportedDocumentContent(new Uint8Array()),null);assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("GIF89a")),null);assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("RIFF0000WEBP")),null);});
