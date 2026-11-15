import { useEffect,useRef,useState } from "react";
import { Check, Copy, Download } from "lucide-react";
import QRCode from "react-qr-code";

type Props={uri:string|null;payeeName:string;upiId:string;amount:string;fileName:string};

/** Browser download and Android document-picker save for the same high-resolution UPI QR. */
export default function CustomerUpiQr({uri,payeeName,upiId,amount,fileName}:Props){
 const root=useRef<HTMLDivElement|null>(null);
 const [error,setError]=useState("");
 const [status,setStatus]=useState("");
 const [busy,setBusy]=useState(false);
 const nativeAppMode=typeof navigator!=="undefined"&&navigator.userAgent.includes("SmartPrintAndroid/1");

 useEffect(()=>{
  if(!nativeAppMode)return;
  const onNativeMessage=(event:MessageEvent)=>{
   const message=typeof event.data==="string"?event.data:"";
   if(message.startsWith("CUSTOMER_PAYMENT_QR_SAVED|")){setBusy(false);setError("");setStatus("Payment QR saved to your device.");}
   else if(message.startsWith("CUSTOMER_PAYMENT_QR_CANCELLED|")){setBusy(false);setStatus("QR save cancelled.");}
   else if(message.startsWith("CUSTOMER_PAYMENT_QR_FAILED|")){setBusy(false);setError(message.slice("CUSTOMER_PAYMENT_QR_FAILED|".length)||"The QR image could not be saved.");}
  };
  window.addEventListener("message",onNativeMessage);
  return ()=>window.removeEventListener("message",onNativeMessage);
 },[nativeAppMode]);

 async function copyText(value:string,label:string){
  if(!value)return;
  setError("");setStatus("");
  try{
   let copied=false;
   try{
    if(navigator.clipboard?.writeText){await navigator.clipboard.writeText(value);copied=true;}
   }catch{/* Fall back to the WebView-compatible copy path below. */}
   if(!copied){
    const input=document.createElement("textarea");
    input.value=value;input.setAttribute("readonly","");
    input.style.position="fixed";input.style.opacity="0";
    document.body.appendChild(input);input.select();
    copied=document.execCommand("copy");input.remove();
   }
   if(!copied)throw new Error("Clipboard access is unavailable.");
   setStatus(label+" copied.");
  }catch(e){setError(e instanceof Error?e.message:"Could not copy "+label.toLowerCase()+".");}
 }

 async function download(){
  const svg=root.current?.querySelector("svg");
  if(!uri||!svg||busy)return;
  setBusy(true);setError("");setStatus("");
  let svgUrl:string|null=null,pngUrl:string|null=null,waitingForNativeSave=false;
  try{
   const clone=svg.cloneNode(true) as SVGSVGElement;
   clone.setAttribute("xmlns","http://www.w3.org/2000/svg");
   clone.setAttribute("width","1024");clone.setAttribute("height","1024");
   svgUrl=URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(clone)],{type:"image/svg+xml;charset=utf-8"}));
   const image=new Image();
   await new Promise<void>((resolve,reject)=>{image.onload=()=>resolve();image.onerror=()=>reject(new Error("The QR image could not be prepared."));image.src=svgUrl!;});
   const canvas=document.createElement("canvas"),quiet=80,size=1024;
   canvas.width=size+quiet*2;canvas.height=size+quiet*2;
   const context=canvas.getContext("2d");
   if(!context)throw new Error("This browser cannot create the QR image.");
   context.fillStyle="#fff";context.fillRect(0,0,canvas.width,canvas.height);
   context.imageSmoothingEnabled=false;context.drawImage(image,quiet,quiet,size,size);
   const png=await new Promise<Blob>((resolve,reject)=>canvas.toBlob(blob=>blob?resolve(blob):reject(new Error("The QR image could not be saved.")),"image/png"));
   if(nativeAppMode){
    const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
    if(!bridge?.postMessage)throw new Error("Android QR saving is unavailable. Please try again.");
    const dataUrl=await new Promise<string>((resolve,reject)=>{
     const reader=new FileReader();
     reader.onload=()=>typeof reader.result==="string"?resolve(reader.result):reject(new Error("The QR image could not be prepared."));
     reader.onerror=()=>reject(new Error("The QR image could not be prepared."));
     reader.readAsDataURL(png);
    });
    bridge.postMessage("saveCustomerPaymentQr|"+fileName+"|"+dataUrl.slice(dataUrl.indexOf(",")+1));
    waitingForNativeSave=true;
    setStatus("Choose where to save the QR image.");
    return;
   }
   pngUrl=URL.createObjectURL(png);
   const anchor=document.createElement("a");anchor.href=pngUrl;anchor.download=fileName;anchor.rel="noopener";
   document.body.appendChild(anchor);anchor.click();anchor.remove();
   const downloadedUrl=pngUrl;window.setTimeout(()=>URL.revokeObjectURL(downloadedUrl),1500);pngUrl=null;
   setStatus("QR image download started.");
  }catch(e){setError(e instanceof Error?e.message:"The QR image could not be downloaded.");}
  finally{if(svgUrl)URL.revokeObjectURL(svgUrl);if(pngUrl)URL.revokeObjectURL(pngUrl);if(!waitingForNativeSave)setBusy(false);}
 }
 return <div className="customer-upi-details">
  <div className="customer-upi-qr" ref={root}>
   {uri?<QRCode value={uri} size={180} level="H" bgColor="#ffffff" fgColor="#111827"/>:
    <div className="customer-upi-qr-unavailable">The payment QR is unavailable. Ask the shop to verify its UPI ID and try again.</div>}
  </div>
  <strong>{payeeName}</strong><span>{upiId}</span><strong className="customer-upi-amount">{amount}</strong>
  {uri&&<div className="customer-upi-quick-actions">
   <button type="button" className="secondary-button" onClick={()=>void copyText(upiId,"UPI ID")}><Copy size={13}/>Copy UPI ID</button>
   <button type="button" className="secondary-button" onClick={()=>void copyText(amount,"Amount")}><Copy size={13}/>Copy amount</button>
  </div>}
  {uri&&<button type="button" className="secondary-button customer-upi-download" onClick={()=>void download()} disabled={busy}><Download size={15}/>{busy?"Preparing QR…":"Download QR image"}</button>}
  {status&&<small className="customer-upi-download-status" role="status"><Check size={13}/>{status}</small>}
  {error&&<small className="customer-upi-download-error" role="alert">{error}</small>}
  {uri&&<a href={uri} className="customer-flow-button customer-upi-open">Open UPI app</a>}
 </div>;
}
