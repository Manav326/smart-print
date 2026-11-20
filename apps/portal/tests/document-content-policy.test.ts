import test from "node:test";
import assert from "node:assert/strict";
import {detectSupportedDocumentContent,identifySupportedDocument} from "../src/document-content-policy.ts";
test("detects PDF anywhere in first 1024 bytes",()=>{const b=new Uint8Array(1028);b.set(new TextEncoder().encode("%PDF-"),1023);assert.deepEqual(detectSupportedDocumentContent(b),{mediaType:"application/pdf",extension:".pdf"});});
test("rejects PDF header after first 1024 bytes",()=>{const b=new Uint8Array(1030);b.set(new TextEncoder().encode("%PDF-"),1024);assert.equal(detectSupportedDocumentContent(b),null);});
test("detects JPEG and PNG signatures",()=>{assert.deepEqual(detectSupportedDocumentContent(new Uint8Array([255,216,255,224])),{mediaType:"image/jpeg",extension:".jpg"});assert.deepEqual(detectSupportedDocumentContent(new Uint8Array([137,80,78,71,13,10,26,10])),{mediaType:"image/png",extension:".png"});});
test("rejects unsupported or empty content regardless of name or declared MIME",()=>{assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("not a PDF")),null);assert.equal(detectSupportedDocumentContent(new Uint8Array()),null);assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("GIF89a")),null);assert.equal(detectSupportedDocumentContent(new TextEncoder().encode("RIFF0000WEBP")),null);});


function createCompletePdfFixture(pageCount=2) {
 const objects=[];
 objects[1]="<< /Type /Catalog /Pages 2 0 R >>";
 const pageRefs=[];
 for(let index=0;index<pageCount;index++){
  const pageId=3+index*2,contentId=pageId+1;
  pageRefs.push(pageId+" 0 R");
  objects[pageId]="<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents "+contentId+" 0 R >>";
  const stream="q 0.96 g 0 0 595 842 re f Q\n";
  objects[contentId]="<< /Length "+Buffer.byteLength(stream,"ascii")+" >>\nstream\n"+stream+"endstream";
 }
 objects[2]="<< /Type /Pages /Count "+pageCount+" /Kids ["+pageRefs.join(" ")+"] >>";
 const infoId=3+pageCount*2;
 objects[infoId]="<< /Producer (SmartPrint test) /Title (Extensionless PDF) >>";
 let pdf="%PDF-1.4\n% Complete extensionless PDF selection fixture\n";
 const offsets=new Array(infoId+1).fill(0);
 for(let id=1;id<=infoId;id++){
  offsets[id]=Buffer.byteLength(pdf,"ascii");
  pdf+=id+" 0 obj\n"+objects[id]+"\nendobj\n";
 }
 const xrefOffset=Buffer.byteLength(pdf,"ascii");
 pdf+="xref\n0 "+(infoId+1)+"\n0000000000 65535 f \n";
 for(let id=1;id<=infoId;id++)pdf+=String(offsets[id]).padStart(10,"0")+" 00000 n \n";
 pdf+="trailer\n<< /Size "+(infoId+1)+" /Root 1 0 R /Info "+infoId+" 0 R >>\nstartxref\n"+xrefOffset+"\n%%EOF\n";
 return new TextEncoder().encode(pdf);
}
test("identifies a complete PDF from content when its filename has no extension",async()=>{
 const bytes=createCompletePdfFixture();
 const file=new File([bytes],"college-document-multipage",{type:"application/octet-stream"});
 assert.deepEqual(await identifySupportedDocument(file),{mediaType:"application/pdf",extension:".pdf"});
});
test("identifies a complete PDF from content when its filename has a wrong image extension",async()=>{
 const bytes=createCompletePdfFixture(1);
 const file=new File([bytes],"renamed-document.jpg",{type:"image/jpeg"});
 assert.deepEqual(await identifySupportedDocument(file),{mediaType:"application/pdf",extension:".pdf"});
});
