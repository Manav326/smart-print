export type SupportedDocumentType={mediaType:"application/pdf"|"image/jpeg"|"image/png";extension:".pdf"|".jpg"|".png"};
const PDF=[0x25,0x50,0x44,0x46,0x2d],JPEG=[0xff,0xd8,0xff],PNG=[0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a];
const CACHE=new WeakMap<object,Promise<SupportedDocumentType|null>>();
function starts(bytes:Uint8Array,sig:number[]){return bytes.length>=sig.length&&sig.every((x,i)=>bytes[i]===x);}
export function detectSupportedDocumentContent(header:Uint8Array):SupportedDocumentType|null {
 if(starts(header,PNG))return {mediaType:"image/png",extension:".png"};
 if(starts(header,JPEG))return {mediaType:"image/jpeg",extension:".jpg"};
 const last=Math.min(1023,header.length-PDF.length);
 for(let i=0;i<=last;i++)if(PDF.every((x,j)=>header[i+j]===x))return {mediaType:"application/pdf",extension:".pdf"};
 return null;
}
/** Reads a bounded signature only; filename and browser MIME labels are ignored. */
export function identifySupportedDocument(file:Pick<File,"slice">):Promise<SupportedDocumentType|null>{
 const key=file as object,cached=CACHE.get(key);if(cached)return cached;
 const pending=(async()=>{try{return detectSupportedDocumentContent(new Uint8Array(await file.slice(0,1028).arrayBuffer()));}catch{return null;}})();
 CACHE.set(key,pending);return pending;
}
