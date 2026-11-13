import { UPLOAD_LIMITS } from "./upload-limits.generated";
import { Fragment, useCallback, useEffect, useId, useRef, useState, type ChangeEvent, type FormEvent, type ReactNode, type CSSProperties } from "react";
import {
  Activity, ArrowRight, Check, Clock3, Copy, CreditCard, FileText, FileUp, FolderOpen, LayoutDashboard,
  ChevronDown, LifeBuoy, LogOut, Menu, Monitor, Plus, Printer, QrCode, Search, Settings, Wifi, Image as ImageIcon, UserRound,
  ShieldCheck, Users, Wallet, X, PanelLeftClose, PanelLeftOpen, RotateCcw, RefreshCw, Zap, Info, Lock
} from "lucide-react";
import QRCode from "react-qr-code";
import PhotoStudio, { type PreparedPhotoSheet, type PhotoStudioEditorSettings } from "./PhotoStudio";
import CustomerUpiQr from "./CustomerUpiQr";
import ShopSubscriptionCheckout from "./ShopSubscriptionCheckout";
import { buildCustomerUpiIntent } from "./upi-payment-intent";
import { shouldBlockBatchCheckoutForExpiredQuote, shouldBlockQuoteRefreshForStatus } from "./batch-checkout-state";
import { isCustomerBatchFinished } from "./customer-session-state";
import { canFallbackToCloudFromLocalFailure, resolveCustomerUploadMode } from "./local-upload-routing";
import AuthScreen from "./AuthScreen";
import PublicLandingPage from "./PublicLandingPage";
import QrStationPrintDocument from "./QrStationPrintDocument";
import { getPublicDownloadPath, usePublicRelease } from "./release-metadata";
import PlatformAdminConsole from "./PlatformAdminConsole";
import PlatformAdminAuthScreen from "./PlatformAdminAuthScreen";
import { resolvePortalRoute, shopPathForSection, shopSectionFromPath } from "./portal-routing";
import {
  ApiError, LocalUploadOutcomeUnknownError, LocalUploadPreflightError, isPlatformAdminSession, acceptCustomerQuote, applyShopPrintBatchReview, approveShopPrintJob, assignShopPrintJobPrinter, calculateCustomerQuote, getShopCustomerHistory, getShopCustomers, getShopReportSummary,
  confirmShopPrintOutput, dispatchShopPrintJob,
  createAgentEnrollmentCode, createCustomerSession, createPrinter, createStation,
  createShopPhotoJob, downloadShopDocument, replaceShopPhotoDocument, replaceShopPhotoDocumentLocally, getCustomerJobStatus, getShopAgentDevices, getShopJobEvents, getShopPrinters, getShopPrintJobsPage, getShopPrintBatchJobs, getShopPayments, loadShopData,
  logout, readStoredTokens, rejectShopPrintJob, rejectShopPayment, revokeAgentDevice,
  reportCustomerBatchPayment, reportCustomerUpiPayment, renewCustomerPrintSession, getNativeCustomerPrintSession, saveNativeCustomerPrintSession, saveNativeCustomerShopProfile, clearNativeCustomerPrintSession, resumeCustomerSession, rotateStationCode, startCustomerBatchPayment, startCustomerPayment, storeTokens,
  updatePrinterActive, updateShopPaymentSettings, updateShopPricing, updateShopPrintJobSettings,
  updateShopOwnerAccount, updateShopProfile, updateStationEnabled, uploadCustomerDocument, uploadCustomerDocumentLocally,
  verifyShopPayment, retryShopPdfExport, recordShopPrintRefund, previewShopPrintRefund, type CustomerBatchPayment, type CustomerPayment, type CustomerPhotoPricing, type ShopPayment, type ShopPaymentSettings, type ShopRefund,
  type AgentDevice, type AuthTokens, type CustomerJobStatus, type PrinterDevice, type PrintQuoteResult, type ShopJobEvent,
  type QrStation, type ShopCustomer, type ShopCustomerHistory, type ShopCustomerPage, type ShopPricingProfile, type ShopPrintJob, type ShopProfile, type ShopReportSummary, type ShopSubscriptionSummary,
  type UploadedPrintJob, type CustomerPrintRequestSettings
} from "./api";
import {
  enrollLocalAgent, getLocalAgentStatus, getLocalNetworkSnapshot, selectLocalNetwork, startLocalPhoneTest,
  isDesktopApp, setDesktopContentProtection, type DesktopAgentStatus, type DesktopLocalNetworkSnapshot, type DesktopLocalPhoneTest
} from "./desktop-bridge";
import { verifiedPdfExportPath } from "./print-output";
import { applyThemePreference, readDarkModePreference, readSidebarCollapsedPreference, saveDarkModePreference, saveSidebarCollapsedPreference } from "./ui-preferences";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import OrderJourneyPage from "./OrderJourneyPage";
import { IST_TIME_ZONE, formatIstDate, formatIstDateTime, formatIstTime, istDateKey } from "./date-time";

type Section = "Order Journey" | "Overview" | "Print Queue" | "Printers" | "QR Stations" | "Print Agent" | "Pricing" | "Subscription" | "Payments" | "Customers" | "Reports" | "Settings";
type Modal = "printer" | "station" | null;

const sections: { label: Section; icon: typeof LayoutDashboard }[] = [
  { label: "Order Journey", icon: Activity },
  { label: "Overview", icon: LayoutDashboard },
  { label: "Print Queue", icon: FileText },
  { label: "Printers", icon: Printer },
  { label: "QR Stations", icon: QrCode },
  { label: "Print Agent", icon: Monitor },
  { label: "Pricing", icon: Wallet },
  { label: "Subscription", icon: ShieldCheck },
  { label: "Payments", icon: CreditCard },
  { label: "Customers", icon: Users },
  { label: "Reports", icon: Activity },
  { label: "Settings", icon: Settings }
];

const sectionGroups: { label: string; items: Section[] }[] = [
  { label: "DAILY WORK", items: ["Order Journey", "Overview", "Print Queue", "Customers"] },
  { label: "DEVICES & CONNECTIVITY", items: ["Printers", "QR Stations", "Print Agent"] },
  { label: "BUSINESS", items: ["Pricing", "Payments"] },
  { label: "INSIGHTS", items: ["Reports"] }
];

type PrivatePhotoStudioBridge = {
  postMessage: (value: string) => void;
  addEventListener?: (type: "message", listener: (event: MessageEvent) => void) => void;
  removeEventListener?: (type: "message", listener: (event: MessageEvent) => void) => void;
  onmessage?: ((event: MessageEvent) => void) | null;
};

type PrivatePhotoStudioDocument = {
  id: string;
  title: string;
  sizeCode: string;
  photoCount: number;
  photosPerPage: number;
  pageCount: number;
  resolutionDpi: number;
  sizeBytes: number;
  thumbnailBase64?: string;
};

function bytesToBase64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  const chunkSize = 0x8000;
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(bytes.length, offset + chunkSize)));
  }
  return btoa(binary);
}

function base64ToBytes(value:string):Uint8Array {
  const binary=atob(value);
  const bytes=new Uint8Array(binary.length);
  for(let index=0;index<binary.length;index++)bytes[index]=binary.charCodeAt(index);
  return bytes;
}

function PrivatePhotoStudioEntry({dark}:{dark:boolean}) {
  const [source,setSource]=useState<File|null>(null);
  const [saving,setSaving]=useState(false);
  const [status,setStatus]=useState("");
  const [error,setError]=useState("");
  const [inputKey,setInputKey]=useState(0);
  const [documents,setDocuments]=useState<PrivatePhotoStudioDocument[]>([]);
  const [libraryLoading,setLibraryLoading]=useState(true);
  const [editingDocumentId,setEditingDocumentId]=useState<string|null>(null);
  const [initialSettings,setInitialSettings]=useState<Partial<PhotoStudioEditorSettings>|undefined>(undefined);
  const [preview,setPreview]=useState<{id:string;title:string;pageIndex:number;pageCount:number;image:string|null}|null>(null);
  const [renameTarget,setRenameTarget]=useState<PrivatePhotoStudioDocument|null>(null);
  const [renameValue,setRenameValue]=useState("");
  const [deleteTarget,setDeleteTarget]=useState<PrivatePhotoStudioDocument|null>(null);
  const [menuDocumentId,setMenuDocumentId]=useState<string|null>(null);
  const nativeBridge=()=> (window as Window & {SmartPrintNative?:PrivatePhotoStudioBridge}).SmartPrintNative;

  function refreshLibrary(){
    setLibraryLoading(true);
    const bridge=nativeBridge();
    if(!bridge){setLibraryLoading(false);setError("Open Photo Studio from the SmartPrint Android app to use its private document library.");return;}
    bridge.postMessage("listPhotoStudioDocuments");
  }

  useEffect(()=>{
    const bridge=nativeBridge();
    if(!bridge){setLibraryLoading(false);return;}
    const onMessage=(event:MessageEvent)=>{
      const message=String(event.data??"");
      if(message.startsWith("PHOTO_LIBRARY|")){
        try{
          const parsed=JSON.parse(message.slice("PHOTO_LIBRARY|".length)) as PrivatePhotoStudioDocument[];
          setDocuments(Array.isArray(parsed)?parsed:[]);
          setLibraryLoading(false);
          setError("");
        }catch{
          setLibraryLoading(false);
          setError("The saved Photo Studio library could not be read.");
        }
      }else if(message.startsWith("PHOTO_LIBRARY_FAILED|")){
        setLibraryLoading(false);
        setError(message.slice("PHOTO_LIBRARY_FAILED|".length)||"The saved Photo Studio library could not be read.");
      }else if(message.startsWith("PHOTO_SAVED|")||message.startsWith("PHOTO_UPDATED|")){
        setSaving(false);
        setSource(null);
        setEditingDocumentId(null);
        setInitialSettings(undefined);
        setInputKey(value=>value+1);
        setStatus(message.startsWith("PHOTO_UPDATED|")
          ? "Your photo sheet was updated privately. Its saved print details are retained."
          : "Saved privately on this device. Connect to a shop whenever you are ready to print.");
        setError("");
        refreshLibrary();
      }else if(message.startsWith("PHOTO_SAVE_FAILED|")||message.startsWith("PHOTO_UPDATE_FAILED|")){
        setSaving(false);
        setError(message.slice(message.indexOf("|")+1)||"The private photo sheet could not be saved.");
      }else if(message.startsWith("PHOTO_PREVIEW_PAGE|")){
        const parts=message.split("|",4);
        if(parts.length===4){
          setPreview(current=>current&&current.id===parts[1]?{...current,pageIndex:Number(parts[2])||0,image:parts[3]}:current);
        }
      }else if(message.startsWith("PHOTO_PREVIEW_FAILED|")){
        setError(message.slice("PHOTO_PREVIEW_FAILED|".length)||"The private photo preview could not be opened.");
        closePreview();
      }else if(message.startsWith("PHOTO_SOURCE|")){
        try{
          const payload=JSON.parse(message.slice("PHOTO_SOURCE|".length)) as {
            metadata: Record<string,unknown>;
            sourceBase64: string;
          };
          const metadata=payload.metadata;
          const mime=typeof metadata.sourceMime==="string"?metadata.sourceMime:"image/jpeg";
          const name=typeof metadata.sourceName==="string"?metadata.sourceName:"photo.jpg";
          const bytes=base64ToBytes(payload.sourceBase64);
          setSource(new File([bytes],name,{type:mime,lastModified:Date.now()}));
          const saved=metadata.editorSettings;
          setInitialSettings(saved&&typeof saved==="object"?saved as Partial<PhotoStudioEditorSettings>:undefined);
          setEditingDocumentId(String(metadata.id??""));
          setStatus("");
          setError("");
          setMenuDocumentId(null);
        }catch{
          setError("The original photo could not be reopened for editing.");
        }
      }else if(message.startsWith("PHOTO_SOURCE_FAILED|")){
        setError(message.slice("PHOTO_SOURCE_FAILED|".length)||"The original photo could not be reopened.");
      }else if(message.startsWith("PHOTO_RENAMED|")){
        setRenameTarget(null);
        setRenameValue("");
        setMenuDocumentId(null);
        setStatus("Document name updated.");
        refreshLibrary();
      }else if(message.startsWith("PHOTO_RENAME_FAILED|")){
        setError(message.slice("PHOTO_RENAME_FAILED|".length)||"The document could not be renamed.");
      }else if(message.startsWith("PHOTO_DELETED|")){
        setMenuDocumentId(null);
        setPreview(current=>current?.id===message.slice("PHOTO_DELETED|".length)?null:current);
        setStatus("Private photo document deleted from this device.");
        refreshLibrary();
      }else if(message.startsWith("PHOTO_DELETE_FAILED|")){
        setError(message.slice("PHOTO_DELETE_FAILED|".length)||"The private photo document could not be deleted.");
      }
    };
    if(bridge.addEventListener){
      bridge.addEventListener("message",onMessage);
    }else{
      const previous=bridge.onmessage;
      bridge.onmessage=event=>{previous?.(event);onMessage(event);};
    }
    bridge.postMessage("listPhotoStudioDocuments");
    return ()=>{
      bridge.removeEventListener?.("message",onMessage);
      if(bridge.onmessage===onMessage)bridge.onmessage=null;
      bridge.postMessage("setSensitivePreview|on");
      bridge.postMessage("setPhotoStudioOpen|off");
    };
  },[]);

  function chooseSource(event:ChangeEvent<HTMLInputElement>){
    const selected=event.currentTarget.files?.[0]??null;
    if(!selected)return;
    const supported=selected.type.toLowerCase().startsWith("image/")||/\.(jpe?g|png)$/i.test(selected.name);
    if(!supported){setError("Choose one JPG or PNG photo.");event.currentTarget.value="";return;}
    if(selected.size<=0||selected.size>UPLOAD_LIMITS.maxFileBytes){setError(`Choose a photo up to ${UPLOAD_LIMITS.maxFileBytes / (1024 * 1024)} MB.`);event.currentTarget.value="";return;}
    setSource(selected);setEditingDocumentId(null);setInitialSettings(undefined);setStatus("");setError("");
  }

  async function savePreparedSheets(sheets:PreparedPhotoSheet[]){
    const sheet=sheets[0];
    if(!source||!sheet||saving)return;
    const bridge=nativeBridge();
    if(!bridge){setError("Open Photo Studio from the SmartPrint Android app to save a private sheet.");return;}
    if(source.size<=0||source.size>UPLOAD_LIMITS.maxFileBytes||sheet.file.size<=0||sheet.file.size>UPLOAD_LIMITS.maxFileBytes){
      setError(`Each photo file must be within ${UPLOAD_LIMITS.maxFileBytes / (1024 * 1024)} MB. Choose a smaller original or reduce the photo count.`);
      return;
    }
    setSaving(true);setError("");setStatus("Saving securely on this device…");
    try{
      const [sourceBuffer,pdfBuffer]=await Promise.all([source.arrayBuffer(),sheet.file.arrayBuffer()]);
      const payload={
        title:source.name.replace(/\.[^.]+$/,"").slice(0,80)||"Photo sheet",
        sourceName:source.name.slice(0,180),
        sourceMime:source.type||"image/jpeg",
        sourceBase64:bytesToBase64(sourceBuffer),
        pdfBase64:bytesToBase64(pdfBuffer),
        sizeCode:sheet.sizeCode,
        photoCount:sheet.photoCount,
        photosPerPage:sheet.photosPerPage,
        pageCount:Math.ceil(sheet.photoCount/sheet.photosPerPage),
        resolutionDpi:sheet.resolutionDpi??300,
        captionHeightMm:sheet.captionHeightMm,
        editorSettings:sheet.editorSettings??null
      };
      const encoded=JSON.stringify(payload);
      bridge.postMessage(editingDocumentId
        ? "updatePhotoStudioDocument|"+editingDocumentId+"|"+encoded
        : "savePhotoStudioDocument|"+encoded);
    }catch(cause){
      setSaving(false);
      setError(cause instanceof Error?cause.message:"The private photo sheet could not be transferred to this device.");
    }
  }

  function openPreview(document:PrivatePhotoStudioDocument,pageIndex=0){
    const bridge=nativeBridge();
    if(!bridge){setError("Open Photo Studio from the SmartPrint Android app to preview saved sheets.");return;}
    setMenuDocumentId(null);
    setPreview({id:document.id,title:document.title,pageIndex,pageCount:Math.max(1,document.pageCount||1),image:null});
    bridge.postMessage("setPhotoStudioOpen|on");
    bridge.postMessage("setSensitivePreview|off");
    bridge.postMessage("getPhotoStudioPreviewPage|"+document.id+"|"+pageIndex);
  }

  function closePreview(){
    nativeBridge()?.postMessage("setSensitivePreview|on");
    nativeBridge()?.postMessage("setPhotoStudioOpen|off");
    setPreview(null);
  }

  function changePreviewPage(next:number){
    if(!preview)return;
    const pageIndex=Math.min(preview.pageCount-1,Math.max(0,next));
    setPreview({...preview,pageIndex,image:null});
    nativeBridge()?.postMessage("getPhotoStudioPreviewPage|"+preview.id+"|"+pageIndex);
  }

  function beginEdit(document:PrivatePhotoStudioDocument){
    setMenuDocumentId(null);
    nativeBridge()?.postMessage("getPhotoStudioSource|"+document.id);
  }

  function beginRename(document:PrivatePhotoStudioDocument){
    setMenuDocumentId(null);
    setRenameTarget(document);
    setRenameValue(document.title);
  }

  function submitRename(event:FormEvent<HTMLFormElement>){
    event.preventDefault();
    if(!renameTarget)return;
    const title=renameValue.trim().slice(0,80);
    if(!title){setError("Enter a document name.");return;}
    nativeBridge()?.postMessage("renamePhotoStudioDocument|"+renameTarget.id+"|"+title);
  }

  function deleteDocument(document:PrivatePhotoStudioDocument){
    setMenuDocumentId(null);
    setDeleteTarget(document);
  }

  function confirmDeleteDocument(){
    if(!deleteTarget)return;
    nativeBridge()?.postMessage("deletePhotoStudioDocument|"+deleteTarget.id);
    setDeleteTarget(null);
  }

  if(source){
    return <>
      <PhotoStudio
        source={source}
        mode="private-library"
        dark={dark}
        photoPrices={{}}
        initialPhotoCount={initialSettings?.copiesInput?Number(initialSettings.copiesInput):undefined}
        initialSettings={initialSettings}
        onCancel={()=>{setSource(null);setEditingDocumentId(null);setInitialSettings(undefined);setError("");}}
        onApply={savePreparedSheets}
      />
      {saving&&<div role="status" style={{position:"fixed",inset:0,zIndex:10000,display:"grid",placeItems:"center",background:"rgba(8,16,30,.72)",color:"#fff",fontWeight:700}}>Saving privately…</div>}
      {error&&<div role="alert" style={{position:"fixed",left:16,right:16,bottom:16,zIndex:10001,padding:14,borderRadius:10,background:"#fff0f0",color:"#9a2626",fontSize:13}}>{error}</div>}
    </>;
  }

  return <main className={"private-photo-studio-entry"+(dark?" dark":"")} style={{minHeight:"100dvh",padding:"24px 18px 36px",background:"var(--surface,#f6f8fc)",color:"var(--text,#172033)"}}>
    <section style={{width:"min(100%,760px)",margin:"0 auto"}}>
      <header style={{display:"flex",alignItems:"center",justifyContent:"space-between",gap:12,marginBottom:18}}>
        <div>
          <div style={{fontSize:10,fontWeight:800,letterSpacing:".14em",color:"var(--accent,#5366c9)"}}>SMARTPRINT BY QUANTAEDGE</div>
          <h1 style={{fontSize:26,lineHeight:1.2,margin:"7px 0 4px"}}>Photo Studio</h1>
          <p style={{fontSize:13,lineHeight:1.5,margin:0,color:"var(--muted,#64748b)"}}>Your private photo sheets, saved on this device.</p>
        </div>
        <button type="button" onClick={refreshLibrary} disabled={libraryLoading} style={{border:"1px solid var(--border,#dbe2ec)",background:"var(--card,#fff)",color:"inherit",borderRadius:10,padding:"9px 12px",fontWeight:700}}>Refresh</button>
      </header>

      <section style={{padding:17,border:"1px solid var(--border,#dbe2ec)",borderRadius:16,background:"var(--card,#fff)",marginBottom:18,boxShadow:"0 8px 24px rgba(20,38,66,.05)"}}>
        <div style={{fontSize:16,fontWeight:750}}>Prepare one photo</div>
        <p style={{fontSize:12,lineHeight:1.5,color:"var(--muted,#64748b)",margin:"5px 0 12px"}}>Choose a single photo to crop, enhance and arrange. Your original and finished sheet remain encrypted on this device.</p>
        <label htmlFor="private-photo-source" style={{display:"inline-flex",alignItems:"center",gap:8,padding:"11px 15px",borderRadius:10,background:"var(--accent,#4f5fc4)",color:"#fff",fontSize:13,fontWeight:750,cursor:"pointer"}}>
          Choose one photo
          <input key={inputKey} id="private-photo-source" type="file" accept="image/jpeg,image/png,.jpg,.jpeg,.png" onChange={chooseSource} style={{position:"absolute",width:1,height:1,opacity:0,pointerEvents:"none"}}/>
        </label>
      </section>

      <section>
        <div style={{display:"flex",alignItems:"baseline",justifyContent:"space-between",gap:10,marginBottom:10}}>
          <h2 style={{fontSize:18,margin:0}}>Saved photo documents</h2>
          <span style={{fontSize:11,color:"var(--muted,#64748b)"}}>{documents.length} saved</span>
        </div>
        {libraryLoading&&<p role="status" style={{fontSize:13,color:"var(--muted,#64748b)"}}>Loading your private documents…</p>}
        {!libraryLoading&&documents.length===0&&<div style={{padding:22,border:"1px dashed var(--border,#c8d1df)",borderRadius:14,textAlign:"center",background:"var(--card,#fff)"}}>
          <strong style={{fontSize:14}}>No saved photo sheets yet</strong>
          <p style={{fontSize:12,color:"var(--muted,#64748b)",margin:"6px 0 0"}}>Your prepared documents will appear here. You can print them later through a connected shop.</p>
        </div>}
        <div style={{display:"grid",gridTemplateColumns:"repeat(auto-fill,minmax(min(100%,310px),1fr))",gap:12}}>
          {documents.map(document=><article key={document.id} style={{display:"flex",gap:12,alignItems:"stretch",minWidth:0,padding:12,border:"1px solid var(--border,#dbe2ec)",borderRadius:14,background:"var(--card,#fff)",boxShadow:"0 4px 14px rgba(20,38,66,.04)"}}>
            <div style={{width:88,flex:"0 0 88px",alignSelf:"flex-start",position:"relative",overflow:"hidden",borderRadius:8,border:"1px solid var(--border,#dbe2ec)",background:"#fff",aspectRatio:"210/297"}}>
              {document.thumbnailBase64
                ? <img src={"data:image/jpeg;base64,"+document.thumbnailBase64} alt={"Preview of "+document.title} style={{width:"100%",height:"100%",objectFit:"contain",display:"block"}}/>
                : <div style={{display:"grid",placeItems:"center",height:"100%",fontSize:10,color:"#64748b"}}>A4 PHOTO</div>}
              <span style={{position:"absolute",left:3,right:3,bottom:4,fontSize:6,fontWeight:800,textAlign:"center",color:"#46536e",background:"rgba(255,255,255,.74)",padding:2}}>SmartPrint by QuantaEdge</span>
            </div>
            <div style={{minWidth:0,flex:1,display:"flex",flexDirection:"column",gap:5}}>
              <strong style={{fontSize:14,lineHeight:1.35,overflowWrap:"anywhere"}}>{document.title||"Photo sheet"}</strong>
              <span style={{fontSize:11,color:"var(--muted,#64748b)"}}>{document.sizeCode} · {document.photoCount} photos · {document.photosPerPage}/A4 sheet</span>
              <span style={{fontSize:11,color:"var(--muted,#64748b)"}}>{document.pageCount} A4 {document.pageCount===1?"page":"pages"} · {document.resolutionDpi||300} DPI</span>
              <div style={{display:"flex",alignItems:"center",gap:8,marginTop:"auto",flexWrap:"wrap"}}>
                <button type="button" onClick={()=>openPreview(document)} style={{padding:"7px 12px",borderRadius:8,border:0,background:"var(--accent,#4f5fc4)",color:"#fff",fontSize:12,fontWeight:750}}>View</button>
                <button type="button" aria-label={"More actions for "+document.title} aria-expanded={menuDocumentId===document.id} onClick={()=>setMenuDocumentId(value=>value===document.id?null:document.id)} style={{width:34,height:32,borderRadius:8,border:"1px solid var(--border,#dbe2ec)",background:"transparent",color:"inherit",fontSize:20,lineHeight:1}}>⋮</button>
              </div>
              {menuDocumentId===document.id&&<div style={{display:"flex",gap:6,flexWrap:"wrap"}}>
                <button type="button" onClick={()=>beginEdit(document)} style={actionStyle}>Edit</button>
                <button type="button" onClick={()=>beginRename(document)} style={actionStyle}>Rename</button>
                <button type="button" onClick={()=>deleteDocument(document)} style={{...actionStyle,color:"#b42318"}}>Delete</button>
              </div>}
            </div>
          </article>)}
        </div>
      </section>

      <p style={{fontSize:11,lineHeight:1.5,color:"var(--muted,#64748b)",marginTop:18}}>To print a saved sheet, connect to a shop and choose it from the Photo Studio private documents section in the shop's file picker. The preview carries SmartPrint security and QuantaEdge branding; the actual print sheet is clean.</p>
      {status&&<p role="status" style={{fontSize:12,color:"var(--accent,#5366c9)",marginTop:12}}>{status}</p>}
      {error&&<p role="alert" style={{fontSize:12,color:"#bd3434",marginTop:12}}>{error}</p>}
    </section>

    {renameTarget&&<div role="presentation" onClick={event=>{if(event.target===event.currentTarget)setRenameTarget(null);}} style={{position:"fixed",inset:0,zIndex:1000,display:"grid",placeItems:"center",padding:18,background:"rgba(10,18,32,.52)"}}>
      <form onSubmit={submitRename} role="dialog" aria-modal="true" aria-labelledby="photo-rename-title" style={{width:"min(100%,380px)",padding:20,borderRadius:14,background:"var(--card,#fff)",color:"var(--text,#172033)",boxShadow:"0 20px 54px rgba(0,0,0,.22)"}}>
        <h2 id="photo-rename-title" style={{fontSize:17,margin:"0 0 12px"}}>Rename photo document</h2>
        <input autoFocus maxLength={80} value={renameValue} onChange={event=>setRenameValue(event.target.value)} aria-label="Document name" style={{width:"100%",boxSizing:"border-box",padding:11,borderRadius:8,border:"1px solid var(--border,#c8d1df)",background:"var(--surface,#fff)",color:"inherit"}}/>
        <div style={{display:"flex",justifyContent:"flex-end",gap:8,marginTop:14}}>
          <button type="button" onClick={()=>setRenameTarget(null)} style={actionStyle}>Cancel</button>
          <button type="submit" style={{...actionStyle,background:"var(--accent,#4f5fc4)",color:"#fff",borderColor:"transparent"}}>Save name</button>
        </div>
      </form>
    </div>}

    {deleteTarget&&<div role="presentation" onClick={event=>{if(event.target===event.currentTarget)setDeleteTarget(null);}} style={{position:"fixed",inset:0,zIndex:1050,display:"grid",placeItems:"center",padding:18,background:"rgba(10,18,32,.56)"}}>
      <section role="dialog" aria-modal="true" aria-labelledby="photo-delete-title" style={{width:"min(100%,380px)",padding:20,borderRadius:14,background:"var(--card,#fff)",color:"var(--text,#172033)",boxShadow:"0 20px 54px rgba(0,0,0,.22)"}}>
        <h2 id="photo-delete-title" style={{fontSize:17,margin:"0 0 10px"}}>Delete private photo document?</h2>
        <p style={{fontSize:13,lineHeight:1.55,color:"var(--muted,#64748b)",margin:"0 0 16px"}}>Delete “{deleteTarget.title}” and its saved original photo from this device? This cannot be undone.</p>
        <div style={{display:"flex",justifyContent:"flex-end",gap:8}}>
          <button type="button" onClick={()=>setDeleteTarget(null)} style={actionStyle}>Keep document</button>
          <button type="button" onClick={confirmDeleteDocument} style={{...actionStyle,background:"#b42318",color:"#fff",borderColor:"transparent"}}>Delete</button>
        </div>
      </section>
    </div>}

    {preview&&<div role="presentation" onClick={event=>{if(event.target===event.currentTarget)closePreview();}} style={{position:"fixed",inset:0,zIndex:1100,display:"grid",gridTemplateRows:"auto minmax(0,1fr) auto",padding:14,background:"rgba(4,10,20,.94)",color:"#fff"}}>
      <header style={{display:"flex",alignItems:"center",justifyContent:"space-between",gap:10,padding:"4px 2px 12px"}}>
        <div style={{minWidth:0}}>
          <strong style={{fontSize:14,display:"block",overflowWrap:"anywhere"}}>{preview.title}</strong>
          <span style={{fontSize:11,opacity:.78}}>Private preview · Page {preview.pageIndex+1} of {preview.pageCount}</span>
        </div>
        <button type="button" onClick={closePreview} style={{padding:"8px 12px",border:"1px solid #ffffff55",borderRadius:8,background:"transparent",color:"#fff"}}>Close</button>
      </header>
      <div style={{minHeight:0,display:"grid",placeItems:"center",overflow:"auto"}}>
        {preview.image
          ? <div style={{position:"relative",width:"min(100%,620px)",maxHeight:"100%",aspectRatio:"210/297",background:"#fff",boxShadow:"0 12px 36px rgba(0,0,0,.4)"}}>
              <img src={"data:image/jpeg;base64,"+preview.image} alt={"A4 photo-sheet preview, page "+(preview.pageIndex+1)} style={{display:"block",width:"100%",height:"100%",objectFit:"contain"}}/>
              <div aria-hidden="true" style={{position:"absolute",inset:0,pointerEvents:"none",display:"grid",placeItems:"center",backgroundImage:"repeating-linear-gradient(135deg,transparent 0 66px,rgba(35,48,74,.035) 66px 68px)"}}>
                <span style={{position:"absolute",left:"7%",right:"7%",top:"42%",transform:"rotate(-28deg)",fontSize:"clamp(10px,2vw,16px)",lineHeight:2.2,fontWeight:900,letterSpacing:".18em",textAlign:"center",color:"rgba(38,52,78,.15)"}}>SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW</span>
                <strong style={{position:"absolute",left:"8%",right:"8%",bottom:"7%",padding:"8px 10px",border:"1px solid rgba(44,59,84,.22)",borderRadius:5,background:"rgba(255,255,255,.55)",color:"rgba(28,43,69,.72)",fontSize:"clamp(10px,1.8vw,15px)",letterSpacing:".12em",textAlign:"center"}}>SmartPrint by QuantaEdge</strong>
              </div>
            </div>
          : <div role="status" style={{fontSize:13,opacity:.8}}>Preparing secure preview…</div>}
      </div>
      <footer style={{display:"flex",alignItems:"center",justifyContent:"center",gap:16,paddingTop:12}}>
        <button type="button" disabled={preview.pageIndex===0} onClick={()=>changePreviewPage(preview.pageIndex-1)} style={{...actionStyle,background:"#ffffff12",color:"#fff",borderColor:"#ffffff55"}}>Previous</button>
        <span style={{fontSize:12}}>Page {preview.pageIndex+1} / {preview.pageCount}</span>
        <button type="button" disabled={preview.pageIndex>=preview.pageCount-1} onClick={()=>changePreviewPage(preview.pageIndex+1)} style={{...actionStyle,background:"#ffffff12",color:"#fff",borderColor:"#ffffff55"}}>Next</button>
      </footer>
    </div>}
  </main>;
}

const actionStyle:CSSProperties={padding:"7px 10px",borderRadius:8,border:"1px solid var(--border,#dbe2ec)",background:"transparent",color:"inherit",fontSize:11,fontWeight:700,cursor:"pointer"};

export default function App() {
  const route = resolvePortalRoute(window.location.pathname);
  const [dark, setDark] = useState(readDarkModePreference);
  const toggleTheme = () => setDark(value => !value);
  useEffect(() => {
    saveDarkModePreference(dark);
    applyThemePreference(dark);
  }, [dark]);
  useEffect(() => {
    const syncNativeTheme = (event: Event) => {
      const selected = (event as CustomEvent<string>).detail;
      if (selected === "light" || selected === "dark") setDark(selected === "dark");
    };
    window.addEventListener("smartprint-theme-change", syncNativeTheme);
    return () => window.removeEventListener("smartprint-theme-change", syncNativeTheme);
  }, []);
  const [tokens, setTokens] = useState<AuthTokens | null>(() =>
    route.kind === "admin" ? readStoredTokens("admin")
      : route.kind === "shop" ? readStoredTokens("shop")
      : null
  );

  if (route.kind === "photo-studio") {
    return <PrivatePhotoStudioEntry dark={dark} />;
  }

  if (route.kind === "station") {
    return <CustomerEntry publicCode={route.publicCode} dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "public") {
    return <PublicLandingPage dark={dark} onToggleTheme={toggleTheme} />;
  }

  if (route.kind === "admin") {
    const onAdminSignOut = async () => {
      await storeTokens(null, "admin");
      setTokens(null);
    };
    if (!tokens) return <PlatformAdminAuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
    if (!isPlatformAdminSession(tokens)) {
      return <ScopeAccessDenied scope="platform administration" onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
    }
    return <PlatformAdminConsole tokens={tokens} onSignOut={onAdminSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }

  const onShopSignOut = async () => {
    await storeTokens(null, "shop");
    setTokens(null);
  };
  if (!tokens) return <AuthScreen onAuthenticated={setTokens} dark={dark} onToggleTheme={toggleTheme}/>;
  if (isPlatformAdminSession(tokens)) {
    return <ScopeAccessDenied scope="the shop workspace" onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
  }
  return <ShopConsole tokens={tokens} onSignOut={onShopSignOut} dark={dark} onToggleTheme={toggleTheme}/>;
}

function ScopeAccessDenied({ scope, onSignOut, dark, onToggleTheme }: { scope: string; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const [busy, setBusy] = useState(false);
  async function signOut() {
    setBusy(true);
    try { await onSignOut(); } finally { setBusy(false); }
  }
  return (
    <main className="auth-screen">
      <header className="auth-topbar">
        <BrandLockup className="auth-brand" />
        <div className="auth-top-actions"><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/><a className="auth-home-link" href="/">Back to website</a></div>
      </header>
      <section className="auth-card" style={{ maxWidth: 520, margin: "12vh auto" }}>
        <div className="auth-card-heading"><div className="auth-card-icon"><ShieldCheck size={20}/></div><div><span>APPLICATION SCOPE MISMATCH</span><h2>Access not available</h2><p>This signed-in account is not authorized for {scope}.</p></div></div>
        <button className="auth-submit" onClick={() => void signOut()} disabled={busy}>{busy ? <span className="button-spinner"/> : "Sign out and use another account"} <ArrowRight size={16}/></button>
      </section>
    </main>
  );
}

function ShopConsole({ tokens, onSignOut, dark, onToggleTheme }: { tokens: AuthTokens; onSignOut: () => Promise<void>; dark: boolean; onToggleTheme: () => void }) {
  const [active, setActive] = useState<Section>(() => shopSectionFromPath(window.location.pathname));

  useEffect(() => {
    const syncSectionFromPath = () => setActive(shopSectionFromPath(window.location.pathname));
    window.addEventListener("popstate", syncSectionFromPath);
    return () => window.removeEventListener("popstate", syncSectionFromPath);
  }, []);

  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(readSidebarCollapsedPreference);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const [ownerAccountOpen, setOwnerAccountOpen] = useState(false);
  const accountMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!accountMenuOpen) return;
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!accountMenuRef.current?.contains(event.target as Node)) setAccountMenuOpen(false);
    };
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") setAccountMenuOpen(false);
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [accountMenuOpen]);
  const reloadInFlight = useRef(false);
  const desktopAgentSyncTimer = useRef<number | null>(null);
  const desktopAgentSyncInFlight = useRef(false);
  const desktopAgentSyncAttempts = useRef(0);
  const desktopConnectInFlight = useRef(false);
  const locallyAuthorizedAgentId = useRef<string | null>(null);
  const [localAgentStatus, setLocalAgentStatus] = useState<DesktopAgentStatus | null>(null);
  const [localAgentConflict, setLocalAgentConflict] = useState(false);
  const [localAgentRevoked, setLocalAgentRevoked] = useState(false);
  const [localAgentNeedsRepair, setLocalAgentNeedsRepair] = useState(false);
  const [localAgentEnrollmentIssue, setLocalAgentEnrollmentIssue] = useState<{ code: string | null; message: string } | null>(null);
  const localAgentEnrollmentIssueRef = useRef<{ code: string | null; message: string } | null>(null);
  const [localAgentOwnershipChecking, setLocalAgentOwnershipChecking] = useState(false);
  const [cloudStatus, setCloudStatus] = useState<"checking" | "connected" | "unavailable">("checking");
  const [profile, setProfile] = useState<ShopProfile | null>(null);
  const [printers, setPrinters] = useState<PrinterDevice[]>([]);
  const [stations, setStations] = useState<QrStation[]>([]);
  const [pricing, setPricing] = useState<ShopPricingProfile | null>(null);
  const [printJobs, setPrintJobs] = useState<ShopPrintJob[]>([]);
  const [journeyJobs, setJourneyJobs] = useState<ShopPrintJob[]>([]);
  const [journeyPayments, setJourneyPayments] = useState<ShopPayment[]>([]);
  const [journeyRefreshing, setJourneyRefreshing] = useState(false);
  const journeyRefreshInFlight = useRef(false);
  const [hasMoreQueueJobs, setHasMoreQueueJobs] = useState(false);
  const [loadingOlderQueueJobs, setLoadingOlderQueueJobs] = useState(false);
  const loadingOlderQueueJobsRef = useRef(false);
  const [queueApiOffset, setQueueApiOffset] = useState(0);
  const [queueSearch, setQueueSearch] = useState("");
  const queueSearchGeneration = useRef(0);
  const queuePageGeneration = useRef(0);
  const queueFirstPageIds = useRef<Set<string>>(new Set());
  const [agentDevices, setAgentDevices] = useState<AgentDevice[]>([]);
  const agentDevicesRef = useRef<AgentDevice[]>([]);
  agentDevicesRef.current = agentDevices;
  const [paymentSettings, setPaymentSettings] = useState<ShopPaymentSettings | null>(null);
  const [payments, setPayments] = useState<ShopPayment[]>([]);
  const [refunds, setRefunds] = useState<ShopRefund[]>([]);
  const [subscription, setSubscription] = useState<ShopSubscriptionSummary | null>(null);
  const [apiConnectionState, setApiConnectionState] = useState<"checking" | "connected" | "degraded" | "offline">("checking");
  const [enrollmentCode, setEnrollmentCode] = useState<{ code: string; expiresUtc: string } | null>(null);
  const [agentBusy, setAgentBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [modal, setModal] = useState<Modal>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState("");
  const [preview, setPreview] = useState<{ name: string; url: string; blob: Blob; mediaType: string; job: ShopPrintJob; readOnly: boolean; isSavedOutput?: boolean } | null>(null);
  const [focusShopPhotoEdit,setFocusShopPhotoEdit]=useState(false);
  const [shopPhotoStudio,setShopPhotoStudio]=useState<{
    source:File;
    job:ShopPrintJob;
    returnPreview:{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean};
  }|null>(null);
  const [shopPhotoOrderOpen,setShopPhotoOrderOpen]=useState(false);
  const [shopPhotoOrderDraft,setShopPhotoOrderDraft]=useState<ShopPhotoOrderDraft|null>(null);
  const [shopPhotoOrderReceipt,setShopPhotoOrderReceipt]=useState<Awaited<ReturnType<typeof createShopPhotoJob>>|null>(null);
  const [refundJob, setRefundJob] = useState<ShopPrintJob | null>(null);
  const [batchReviewJobs, setBatchReviewJobs] = useState<ShopPrintJob[] | null>(null);
  const [batchReviewDecisions, setBatchReviewDecisions] = useState<Record<string, boolean>>({});
  const [batchReviewRetry, setBatchReviewRetry] = useState(false);
  const [batchReviewRetryIds, setBatchReviewRetryIds] = useState<string[]>([]);
  const [batchReviewLoadingId, setBatchReviewLoadingId] = useState<string | null>(null);
  type ActionDialogState = { title: string; message: string; inputLabel?: string; confirmLabel: string; resolve: (value: string | null) => void };
  const [actionDialog, setActionDialog] = useState<ActionDialogState | null>(null);
  const [actionDialogValue, setActionDialogValue] = useState("");
  const [busyJobId, setBusyJobId] = useState<string | null>(null);
  const [busyResourceId, setBusyResourceId] = useState<string | null>(null);
  const [eventHistory, setEventHistory] = useState<{ job: ShopPrintJob; events: ShopJobEvent[] } | null>(null);

  async function reload() {
    if (reloadInFlight.current) return;
    queueSearchGeneration.current++;
    queuePageGeneration.current++;
    reloadInFlight.current = true;
    setApiConnectionState("checking");
    setLoading(true);
    setError("");
    try {
      const [snapshot, unfilteredJourneyJobs] = await Promise.all([
        loadShopData(tokens.accessToken, queueSearch),
        queueSearch.trim()
          ? getShopPrintJobsPage(tokens.accessToken, 0, 100)
          : Promise.resolve(null)
      ]);
      setProfile(snapshot.profile);
      setPrinters(snapshot.printers);
      setStations(snapshot.stations);
      setPricing(snapshot.pricing);
      setPrintJobs(snapshot.printJobs);
      setJourneyJobs(unfilteredJourneyJobs ?? snapshot.printJobs);
      setJourneyPayments(snapshot.payments);
      queueFirstPageIds.current = new Set(snapshot.printJobs.map(job => job.id));
      setHasMoreQueueJobs(snapshot.printJobs.length === 100);
      setQueueApiOffset(snapshot.printJobs.length);
      agentDevicesRef.current = snapshot.agentDevices;
      setAgentDevices(snapshot.agentDevices);
      setPaymentSettings(snapshot.paymentSettings);
      setPayments(snapshot.payments);
      setRefunds(snapshot.refunds);
      setSubscription(snapshot.subscription);
      setApiConnectionState("connected");
      setCloudStatus("connected");
      if (isDesktopApp()) {
        // The Windows service may still be starting after installation or reboot.
        // Keep discovery off the initial shop-data load and retry quietly.
        scheduleDesktopAgentSync(0);
      }
    } catch (exception) {
      setCloudStatus("unavailable");
      if (exception instanceof ApiError && exception.status === 401) {
        storeTokens(null);
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : "Could not clear the secure desktop session."); }
      } else {
        handleShopApiError(exception, "Could not load your shop.");
      }
    } finally {
      setLoading(false);
      reloadInFlight.current = false;
    }
  }

  function handleShopApiError(exception: unknown, fallback: string) {
    if (exception instanceof ApiError && exception.status === 401) {
      storeTokens(null);
      onSignOut();
      return;
    }
    const status = exception instanceof ApiError ? exception.status : 0;
    setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
    setError(exception instanceof Error ? exception.message : fallback);
  }

  async function refreshPrintQueue() {
    const generation = queuePageGeneration.current;
    try {
      const [latest, latestPrinters, latestAgents] = await Promise.all([
        getShopPrintJobsPage(tokens.accessToken, 0, 100, queueSearch),
        getShopPrinters(tokens.accessToken),
        getShopAgentDevices(tokens.accessToken)
      ]);
      if (generation !== queuePageGeneration.current) return;
      setApiConnectionState("connected");
      setPrinters(latestPrinters);
      setAgentDevices(latestAgents);
      const latestIds = new Set(latest.map(job => job.id));
      const firstPageMembershipChanged = latest.length !== queueFirstPageIds.current.size ||
        latest.some(job => !queueFirstPageIds.current.has(job.id));
      if (firstPageMembershipChanged) {
        // Any insert/removal shifts every offset page. Reset pagination to the
        // current first page so no older records are skipped or duplicated.
        queuePageGeneration.current++;
        queueFirstPageIds.current = latestIds;
        setPrintJobs(latest);
        setQueueApiOffset(latest.length);
        setHasMoreQueueJobs(latest.length === 100);
        return;
      }
      setPrintJobs(current => {
        const byId = new Map(current.map(job => [job.id, job]));
        for (const job of latest) byId.set(job.id, job);
        return Array.from(byId.values()).sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setHasMoreQueueJobs(latest.length === 100 || printJobs.length > latest.length);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not refresh the print queue.");
    }
  }

  async function loadOlderQueueJobs() {
    if (loadingOlderQueueJobsRef.current || !hasMoreQueueJobs) return;
    loadingOlderQueueJobsRef.current = true;
    const generation = queuePageGeneration.current;
    const offset = queueApiOffset;
    setLoadingOlderQueueJobs(true);
    try {
      const older = await getShopPrintJobsPage(tokens.accessToken, offset, 100, queueSearch);
      if (generation !== queuePageGeneration.current) return;
      setPrintJobs(current => {
        const knownIds = new Set(current.map(job => job.id));
        const merged = [...current, ...older.filter(job => !knownIds.has(job.id))];
        return merged.sort((left, right) =>
          Date.parse(right.createdUtc) - Date.parse(left.createdUtc) || right.id.localeCompare(left.id)
        );
      });
      setQueueApiOffset(offset => offset + older.length);
      setHasMoreQueueJobs(older.length === 100);
    } catch (exception) {
      if (generation !== queuePageGeneration.current) return;
      handleShopApiError(exception, "Could not load older print jobs.");
    } finally {
      loadingOlderQueueJobsRef.current = false;
      setLoadingOlderQueueJobs(false);
    }
  }

  async function searchPrintQueue(value: string) {
    const normalized = value.trim().slice(0, 120);
    const generation = ++queueSearchGeneration.current;
    queuePageGeneration.current++;
    setQueueSearch(normalized);
    try {
      const matches = await getShopPrintJobsPage(tokens.accessToken, 0, 100, normalized);
      if (generation !== queueSearchGeneration.current) return;
      queueFirstPageIds.current = new Set(matches.map(job => job.id));
      setPrintJobs(matches);
      setQueueApiOffset(matches.length);
      setHasMoreQueueJobs(matches.length === 100);
    } catch (exception) {
      if (generation !== queueSearchGeneration.current) return;
      handleShopApiError(exception, "Could not search the print queue.");
    }
  }

  async function refreshOrderJourney() {
    if (journeyRefreshInFlight.current) return;
    journeyRefreshInFlight.current = true;
    setJourneyRefreshing(true);
    try {
      const [latestJobs, latestPayments] = await Promise.all([
        getShopPrintJobsPage(tokens.accessToken, 0, 100),
        getShopPayments(tokens.accessToken)
      ]);
      setJourneyJobs(latestJobs);
      setJourneyPayments(latestPayments);
      setApiConnectionState("connected");
    } catch (exception) {
      handleShopApiError(exception, "Could not refresh the order journey.");
    } finally {
      journeyRefreshInFlight.current = false;
      setJourneyRefreshing(false);
    }
  }

  function requestActionDialog(options: { title: string; message: string; inputLabel?: string; confirmLabel?: string }): Promise<string | null> {
    return new Promise(resolve => {
      setActionDialogValue("");
      setActionDialog({ ...options, confirmLabel: options.confirmLabel || "Confirm", resolve });
    });
  }

  async function confirmInApp(message: string, title = "Please confirm", confirmLabel = "Confirm"): Promise<boolean> {
    return (await requestActionDialog({ title, message, confirmLabel })) !== null;
  }

  async function promptInApp(message: string, title = "Enter details", inputLabel = "Reason"): Promise<string | null> {
    return requestActionDialog({ title, message, inputLabel, confirmLabel: "Continue" });
  }

  function closeActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(null);
  }

  function submitActionDialog() {
    const current = actionDialog;
    setActionDialog(null);
    current?.resolve(actionDialogValue);
  }

  function toggleSidebarCollapsed() {
    setSidebarCollapsed(current => {
      const next = !current;
      saveSidebarCollapsedPreference(next);
      return next;
    });
  }

  useEffect(() => {
    void reload();
    // A token change is the only trigger; mutations call reload explicitly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tokens.accessToken]);

  useEffect(() => () => {
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
      desktopAgentSyncTimer.current = null;
    }
  }, []);

  useEffect(() => {
    if (active === "Print Queue") return;
    let mounted = true;
    let inFlight = false;
    const refreshDevices = async () => {
      if (inFlight) return;
      inFlight = true;
      try {
        const [latestPrinters, latestAgents] = await Promise.all([
          getShopPrinters(tokens.accessToken),
          getShopAgentDevices(tokens.accessToken)
        ]);
        if (mounted) {
          setPrinters(latestPrinters);
          setAgentDevices(latestAgents);
          setApiConnectionState("connected");
        }
      } catch (exception) {
        if (mounted) {
          if (exception instanceof ApiError && exception.status === 401) {
            storeTokens(null);
            onSignOut();
          } else {
            const status = exception instanceof ApiError ? exception.status : 0;
            setApiConnectionState(status === 0 ? "offline" : status >= 500 ? "degraded" : "connected");
          }
        }
      } finally {
        inFlight = false;
      }
    };
    void refreshDevices();
    const timer = window.setInterval(() => void refreshDevices(), 15_000);
    return () => {
      mounted = false;
      window.clearInterval(timer);
    };
  }, [active, tokens.accessToken]);

  useEffect(() => {
    if (active !== "Order Journey") return;
    void refreshOrderJourney();
    const timer = window.setInterval(() => void refreshOrderJourney(), 15_000);
    return () => window.clearInterval(timer);
    // The refresh callback is scoped to the active shop session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, tokens.accessToken]);

  const activeAgentCount = agentDevices.filter(agent => !agent.revokedUtc).length;
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const onlineAgentCount = onlineAgentIds.size;
  const usablePrinterCount = printers.filter(printer => isPrinterAvailable(printer, onlineAgentIds)).length;
  const offlinePrinterCount = printers.filter(printer =>
    printer.isActive && !isPrinterAvailable(printer, onlineAgentIds)
  ).length;
  const today = new Intl.DateTimeFormat("en-IN", { timeZone: IST_TIME_ZONE, weekday: "long", day: "numeric", month: "long" }).format(new Date());

  async function signOut() {
    try { await logout(tokens.accessToken); } catch { /* Local sign-out still clears the session. */ }
    try { await onSignOut(); }
    catch (exception) { setError(exception instanceof Error ? exception.message : "Could not clear the secure desktop session."); }
  }

  async function submitPrinter(values: {
    displayName: string; manufacturer: string; model: string; connectionType: string;
    connectionAddress?: string; queueName?: string;
  }) {
    setBusy(true);
    try {
      await createPrinter(tokens.accessToken, values);
      setModal(null);
      setToast("Printer added to your shop.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not add printer.");
    } finally {
      setBusy(false);
    }
  }

  async function submitStation(values: { displayName: string; printerDeviceId?: string }) {
    setBusy(true);
    try {
      const created = await createStation(tokens.accessToken, values);
      setStations(current => [...current, created]);
      setModal(null);
      setToast("QR station created. Save or print its QR code now.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not create QR station.");
    } finally {
      setBusy(false);
    }
  }

  async function togglePrinter(printer: PrinterDevice) {
    if (busyResourceId) return;
    if (printer.isActive && !(await confirmInApp(
      `Disable ${printer.displayName}? New jobs can no longer be assigned to it. A Windows job already accepted by the spooler is not cancelled; an active job may prevent disabling until it is resolved.`,
      "Disable this printer?", "Disable printer"
    ))) return;
    setBusyResourceId(printer.id);
    setError("");
    try {
      await updatePrinterActive(tokens.accessToken, printer.id, !printer.isActive);
      setToast(printer.isActive ? "Printer disabled. Existing Windows output is not cancelled." : "Printer enabled for new assignments.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update printer.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function toggleStation(station: QrStation) {
    if (busyResourceId) return;
    if (station.isEnabled && !(await confirmInApp(
      `Disable the QR station “${station.displayName}”? Customers will not be able to start new sessions from this QR until you enable it again. Previously started customer sessions are not recalled.`,
      "Disable this QR station?", "Disable station"
    ))) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const updated = await updateStationEnabled(tokens.accessToken, station.id, !station.isEnabled);
      setStations(current => current.map(item => item.id === station.id ? { ...item, isEnabled: updated.isEnabled } : item));
      setToast(updated.isEnabled ? "QR station enabled." : "QR station disabled. Existing customer sessions are unchanged.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update QR station.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function createFreshStationCode(station: QrStation) {
    if (busyResourceId) return;
    const confirmed = await confirmInApp("Generating a replacement QR will invalidate every previously printed sign for this station. Continue only when you intend to replace them.", "Replace this station's QR?", "Replace QR");
    if (!confirmed) return;
    setBusyResourceId(station.id);
    setError("");
    try {
      const result = await rotateStationCode(tokens.accessToken, station.id);
      setStations(current => current.map(item => item.id === station.id
        ? { ...item, publicUrl: result.publicUrl, publicCodeHint: result.publicCodeHint }
        : item));
      setToast("New QR code generated. The previous code is now invalid.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate a QR code.");
    } finally {
      setBusyResourceId(null);
    }
  }

  async function generateAgentCode() {
    setAgentBusy(true);
    setError("");
    setEnrollmentCode(null);
    try {
      const code = await createAgentEnrollmentCode(tokens.accessToken);
      setEnrollmentCode(code);
      setToast("One-time pairing code generated. It expires in 10 minutes.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not generate an agent pairing code.");
    } finally {
      setAgentBusy(false);
    }
  }

  async function connectLocalComputer(
    silent = false,
    knownAgents: AgentDevice[] = agentDevicesRef.current,
    allowPreviousShopReconnection = false
  ) {
    if (desktopConnectInFlight.current) return;
    desktopConnectInFlight.current = true;
    if (!silent) {
      setAgentBusy(true);
      setError("");
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
    }
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      setLocalAgentRevoked(status.status === "REVOKED");
      setLocalAgentNeedsRepair(status.status === "IDENTITY_UNAVAILABLE");

      if (!status.serviceReachable) {
        if (!silent) setError("The SmartPrint background service is not responding. Restart Windows or repair SmartPrint, then retry.");
        return;
      }

      if (status.paired) {
        const currentShopDevice = knownAgents.find(agent =>
          !agent.revokedUtc && agent.id === status.agentDeviceId
        );
        const revokedCurrentShopDevice = knownAgents.find(agent =>
          agent.revokedUtc && agent.id === status.agentDeviceId
        );
        if (!currentShopDevice) {
          if (revokedCurrentShopDevice) {
            // This shop revoked this exact device. Only an explicit reconnect
            // action may create a fresh authorization; background sync never does.
            setLocalAgentRevoked(true);
            if (silent) return;
          } else {
            setLocalAgentConflict(true);
            if (!allowPreviousShopReconnection) {
              if (!silent) setError("This computer is still paired with another shop. Its owner must revoke it before you can connect it here.");
              return;
            }

            const confirmed = await confirmInApp(
              "Continue only after the previous shop owner has revoked this computer from that shop's Print Agent page. SmartPrint will verify the revocation with the server before changing this computer's connection.",
              "Reconnect this computer?",
              "Check and reconnect"
            );
            if (!confirmed) return;
          }
        } else {
          setLocalAgentRevoked(false);
          setToast("This computer is already connected to your shop.");
          return;
        }
      }

      const authorization = await createAgentEnrollmentCode(tokens.accessToken);
      const enrolled = await enrollLocalAgent(authorization.code);
      if (!enrolled.success) {
        const message = enrolled.message || "This computer could not be connected to the shop.";
        if (enrolled.code) throw new ApiError(message, 409, enrolled.code);
        throw new Error(message);
      }
      locallyAuthorizedAgentId.current = enrolled.agentDeviceId;
      let postEnrollmentStatus = status;
      try {
        // Read back the local listener state after enrollment. Failure here must
        // not undo a successful cloud pairing or interrupt the owner's workflow.
        postEnrollmentStatus = await getLocalAgentStatus();
      } catch {
        // Keep the last known listener status; the background health loop retries.
      }
      setLocalAgentStatus({
        serviceReachable: true,
        localApiReachable: postEnrollmentStatus.localApiReachable,
        apiBaseUrl: postEnrollmentStatus.apiBaseUrl,
        paired: true,
        agentDeviceId: enrolled.agentDeviceId,
        status: "CONNECTED"
      });
      setLocalAgentConflict(false);
      setLocalAgentRevoked(false);
      setLocalAgentNeedsRepair(false);
      localAgentEnrollmentIssueRef.current = null;
      setLocalAgentEnrollmentIssue(null);
      setLocalAgentOwnershipChecking(false);

      // Enrollment creates the AgentDevice in the cloud immediately. Refresh its
      // shop-scoped record so the UI recognizes this computer as connected on the
      // first run instead of waiting for the next manual reload.
      try {
        const refreshedSnapshot = await loadShopData(tokens.accessToken);
        agentDevicesRef.current = refreshedSnapshot.agentDevices;
        setAgentDevices(refreshedSnapshot.agentDevices);
        setPrinters(refreshedSnapshot.printers);
      } catch {
        // Keep the successful local enrollment state; the next normal refresh
        // will synchronize the cloud inventory without enrolling a second time.
      }

      setToast("This computer is connected. SmartPrint is finding its printers.");
      if (!silent) await reload();
    } catch (exception) {
      if (exception instanceof ApiError && exception.status === 401) {
        try { await onSignOut(); }
        catch (signOutError) { setError(signOutError instanceof Error ? signOutError.message : "Could not clear the secure desktop session."); }
      } else if (exception instanceof ApiError && exception.status === 409) {
        const issue = { code: exception.code, message: exception.message };
        localAgentEnrollmentIssueRef.current = issue;
        setLocalAgentEnrollmentIssue(issue);
        setLocalAgentConflict(exception.code === "agent_already_enrolled");
        setLocalAgentRevoked(false);
        setLocalAgentNeedsRepair(false);
        setLocalAgentOwnershipChecking(false);
        if (!silent) setError(exception.message);
      } else if (!silent) {
        const message = exception instanceof Error ? exception.message : "Could not connect this computer to the shop.";
        setError(message);
      }
    } finally {
      desktopConnectInFlight.current = false;
      if (!silent) setAgentBusy(false);
    }
  }

  function nextDesktopAgentRetryDelay(): number {
    const exponent = Math.min(desktopAgentSyncAttempts.current, 4);
    desktopAgentSyncAttempts.current += 1;
    return Math.min(2_000 * (2 ** exponent), 30_000);
  }

  function scheduleDesktopAgentSync(delayMs: number): void {
    if (!isDesktopApp()) return;
    if (desktopAgentSyncTimer.current !== null) {
      window.clearTimeout(desktopAgentSyncTimer.current);
    }
    desktopAgentSyncTimer.current = window.setTimeout(() => {
      desktopAgentSyncTimer.current = null;
      void synchronizeDesktopAgent();
    }, delayMs);
  }

  async function synchronizeDesktopAgent(): Promise<void> {
    if (!isDesktopApp() || desktopAgentSyncInFlight.current) return;
    desktopAgentSyncInFlight.current = true;
    try {
      const status = await getLocalAgentStatus();
      setLocalAgentStatus(status);

      if (!status.serviceReachable) {
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
        return;
      }

      // A server-revoked identity is intentionally not auto-enrolled again.
      // The shop owner can reconnect it with one explicit click, without typing a code.
      if (status.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (status.status === "IDENTITY_UNAVAILABLE") {
        // Don't create endless one-time enrollment codes while the old cloud
        // device may still be active. Let the owner review/revoke it first.
        setLocalAgentNeedsRepair(true);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (localAgentEnrollmentIssueRef.current) {
        // The API has already explained why enrollment cannot proceed. Do not
        // keep creating short-lived one-time authorizations in the background.
        setLocalAgentEnrollmentIssue(localAgentEnrollmentIssueRef.current);
        setLocalAgentConflict(localAgentEnrollmentIssueRef.current.code === "agent_already_enrolled");
        setLocalAgentOwnershipChecking(false);
        return;
      }

      setLocalAgentNeedsRepair(false);
      if (status.paired && status.agentDeviceId) {
        let deviceRecord = agentDevicesRef.current.find(agent =>
          agent.id === status.agentDeviceId
        );

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord) {
          // Verify cloud ownership before declaring another-shop pairing.
          setLocalAgentOwnershipChecking(true);
          try {
            const refreshedSnapshot = await loadShopData(tokens.accessToken);
            agentDevicesRef.current = refreshedSnapshot.agentDevices;
            setAgentDevices(refreshedSnapshot.agentDevices);
            setPrinters(refreshedSnapshot.printers);
            deviceRecord = refreshedSnapshot.agentDevices.find(agent =>
              agent.id === status.agentDeviceId
            );
          } catch {
            scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
            return;
          }
        }

        if (deviceRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }

        if (!deviceRecord && locallyAuthorizedAgentId.current === status.agentDeviceId) {
          // Enrollment succeeded but its shop record has not appeared yet.
          // Keep checking; do not mislabel it as another shop or re-enroll it.
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }

        const belongsToCurrentShop = Boolean(deviceRecord && !deviceRecord.revokedUtc);
        setLocalAgentOwnershipChecking(false);
        setLocalAgentRevoked(false);
        setLocalAgentConflict(!belongsToCurrentShop);
        if (belongsToCurrentShop) {
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
        } else {
          // Never move a computer across shops without the previous owner revoking it.
          scheduleDesktopAgentSync(60_000);
        }
        return;
      }

      setLocalAgentRevoked(false);
      setLocalAgentConflict(false);
      setLocalAgentOwnershipChecking(false);
      await connectLocalComputer(true, agentDevicesRef.current);
      const verifiedStatus = await getLocalAgentStatus();
      setLocalAgentStatus(verifiedStatus);

      if (verifiedStatus.status === "REVOKED") {
        setLocalAgentRevoked(true);
        setLocalAgentNeedsRepair(false);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
        scheduleDesktopAgentSync(60_000);
        return;
      }

      if (verifiedStatus.serviceReachable && verifiedStatus.paired && verifiedStatus.agentDeviceId) {
        const verifiedRecord = agentDevicesRef.current.find(agent =>
          agent.id === verifiedStatus.agentDeviceId
        );
        if (verifiedRecord?.revokedUtc) {
          setLocalAgentRevoked(true);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(false);
          scheduleDesktopAgentSync(60_000);
          return;
        }
        if (verifiedRecord && !verifiedRecord.revokedUtc) {
          setLocalAgentRevoked(false);
          setLocalAgentOwnershipChecking(false);
          setLocalAgentConflict(false);
          desktopAgentSyncAttempts.current = 0;
          scheduleDesktopAgentSync(15_000);
          return;
        }
        if (locallyAuthorizedAgentId.current === verifiedStatus.agentDeviceId) {
          setLocalAgentRevoked(false);
          setLocalAgentConflict(false);
          setLocalAgentOwnershipChecking(true);
          scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
          return;
        }
        setLocalAgentRevoked(false);
        setLocalAgentConflict(true);
        setLocalAgentOwnershipChecking(false);
      }

      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } catch {
      // Startup, network and temporary pipe failures recover without owner action.
      scheduleDesktopAgentSync(nextDesktopAgentRetryDelay());
    } finally {
      desktopAgentSyncInFlight.current = false;
    }
  }

  async function revokeAgent(agent: AgentDevice) {
    if (!(await confirmInApp(`Revoke the Print Agent on ${agent.deviceName}? It will no longer be able to report printers or connect to this shop.`, "Revoke Print Agent", "Revoke agent"))) return;
    setAgentBusy(true);
    setError("");
    try {
      await revokeAgentDevice(tokens.accessToken, agent.id);
      if (localAgentStatus?.agentDeviceId === agent.id) {
        setLocalAgentRevoked(true);
        setLocalAgentConflict(false);
        setLocalAgentOwnershipChecking(false);
      }
      setToast("Agent access revoked.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not revoke this agent.");
    } finally {
      setAgentBusy(false);
    }
  }



  async function assignPrinter(job: ShopPrintJob, printerDeviceId: string) {
    if (!printerDeviceId || printerDeviceId === job.printerDeviceId) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const assignment=await assignShopPrintJobPrinter(tokens.accessToken, job.id, printerDeviceId);
      setToast(`${assignment.printerName} assigned to ${assignment.assignedJobCount} file(s) in this batch. The paired computer can now receive the documents.`);
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not assign this printer.");
    } finally {
      setBusyJobId(null);
    }
  }


  async function updatePrintJobSettings(job:ShopPrintJob,settings:CustomerPrintRequestSettings) {
    setBusyJobId(job.id);
    setError("");
    try {
      const revised=await updateShopPrintJobSettings(tokens.accessToken,job.id,settings);
      setToast(`Revised quote ${formatMoney(revised.quotedAmountMinor,revised.currency)} sent to the customer for approval. Printing remains paused until approval and release.`);
      await reload();
    } catch(exception) {
      setError(exception instanceof Error?exception.message:"Could not revise this print request.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function dispatchPrintJob(job: ShopPrintJob) {
    const pdfExport = job.outputMode === "PDF_EXPORT";
    const ok = await confirmInApp(pdfExport
      ? `Save a PDF for ${job.jobNumber} in the shop's QRtoPrint Exports folder? No Windows printer dialog will be opened.`
      : `Start printing ${job.jobNumber} on ${job.printerName || "the assigned printer"}? The document will be submitted once to Windows. Spooler acceptance is not proof of paper output.`, pdfExport ? "Save PDF export?" : "Start printing?", pdfExport ? "Save PDF" : "Start printing");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast(pdfExport
        ? "PDF export authorised. The Windows Agent will create the PDF in the shop's export folder."
        : "Print authorised. The Windows agent will submit this job once.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not start printing.");
    } finally { setBusyJobId(null); }
  }

  async function retryPdfExport(job: ShopPrintJob) {
    const ok = await confirmInApp(
      `Confirm that no PDF file was saved for ${job.jobNumber}? The Agent will create the PDF in the shop's QRtoPrint Exports folder. Do not continue if a file from this job already exists.`,
      "Retry PDF export?", "Retry export"
    );
    if (!ok) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await retryShopPdfExport(tokens.accessToken, job.id);
      await dispatchShopPrintJob(tokens.accessToken, job.id);
      setToast("PDF export retry authorised. The Windows Agent will save the PDF locally.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not retry this PDF export.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function confirmPrintedOutput(job: ShopPrintJob) {
    const ok = await confirmInApp(`Have you physically checked all requested pages for ${job.jobNumber}? Only confirm after checking the actual paper output.`, "Confirm paper output", "Confirm output");
    if (!ok) return;
    setBusyJobId(job.id); setError("");
    try {
      await confirmShopPrintOutput(tokens.accessToken, job.id);
      setToast("Physical output confirmed by the shop operator.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not confirm physical output.");
    } finally { setBusyJobId(null); }
  }

  async function savePaymentSettings(values: {
    upiEnabled: boolean; upiId: string | null; payeeName: string | null; cashEnabled: boolean; instantPaymentEnabled: boolean;
  }) {
    setBusy(true);
    setError("");
    try {
      const updated = await updateShopPaymentSettings(tokens.accessToken, values);
      setPaymentSettings(updated);
      setToast("Shop payment settings saved. Customer print revenue continues to go directly to your shop.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save shop payment settings.");
    } finally {
      setBusy(false);
    }
  }

  async function verifyPayment(payment: ShopPayment) {
    const isBatch = Boolean(payment.batchPaymentId);
    const confirmation = payment.method === "UPI"
      ? isBatch
        ? `Have you independently confirmed the full batch UPI amount in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
        : `Have you independently confirmed this UPI transaction in your shop's bank/UPI app? Reference: ${payment.paymentReference || "not supplied"}`
      : isBatch
        ? "Have you physically received the full cash amount for this batch?"
        : "Have you physically received the cash for this print job?";
    if (!(await confirmInApp(confirmation, "Verify payment received", isBatch ? "Verify batch payment" : "Verify payment"))) return;

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await verifyShopPayment(tokens.accessToken, payment.id);
      setToast(isBatch
        ? "Batch payment verified. Every accepted file is now authorised; assign and dispatch each file through the print queue."
        : "Payment verified. The job is paid/authorised; printing still requires printer assignment and an explicit dispatch.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not verify this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectPayment(payment: ShopPayment) {
    const cancellationWarning = payment.method === "UPI" &&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED"].includes(payment.status)
      ? "Before rejecting this UPI attempt, check your bank/UPI ledger against the full amount and the customer's reference. A submitted or missing UTR does not prove that money was or was not received. If the credit is present, verify this attempt instead of rejecting it.\n\n"
      : "";
    const reason = await promptInApp(
      cancellationWarning + (payment.batchPaymentId
        ? "Explain why this full batch payment is being rejected."
        : "Explain why this payment report is being rejected."),
      payment.batchPaymentId ? "Reject batch payment" : "Reject payment report",
      "Rejection reason"
    );
    if (reason === null) return;
    if (reason.trim().length < 3) {
      setError("A rejection reason of at least 3 characters is required.");
      return;
    }

    setBusyJobId(payment.printJobId);
    setError("");
    try {
      await rejectShopPayment(tokens.accessToken, payment.id, reason.trim());
      setToast(payment.batchPaymentId
        ? "Batch payment rejected. The customer can retry checkout for the accepted files."
        : "Payment report rejected. The customer can choose a new payment attempt.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this payment.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function saveProfile(values: { displayName: string; logoUrl: string | null; accentColor: string }) {
    setBusy(true);
    try {
      await updateShopProfile(tokens.accessToken, values);
      setToast("Shop identity updated.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not update shop identity.");
    } finally {
      setBusy(false);
    }
  }

  async function saveOwnerAccount(values: { ownerName: string; email: string; currentPassword?: string }) {
    setBusy(true);
    try {
      const updated = await updateShopOwnerAccount(tokens.accessToken, values);
      setProfile(current => current ? {
        ...current,
        ownerName: updated.ownerName,
        ownerEmail: updated.ownerEmail
      } : current);
      setOwnerAccountOpen(false);
      setToast(updated.emailChanged
        ? "Personal details saved. Your new email is now your sign-in address."
        : "Personal details updated.");
    } finally {
      setBusy(false);
    }
  }

  async function savePricing(values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
  }) {
    setBusy(true);
    try {
      const updated = await updateShopPricing(tokens.accessToken, values);
      setPricing(updated);
      setToast("Print rate card saved. New quotes will use this pricing version.");
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not save print prices.");
    } finally {
      setBusy(false);
    }
  }

  async function previewJob(
    job: ShopPrintJob,
    readOnly = false,
    variant: "original" | "output" = "original"
  ) {
    setBusyJobId(job.id);
    setError("");
    try {
      const blob = await downloadShopDocument(tokens.accessToken, job.id, variant);
      const isSavedOutput = variant === "output";
      const supportedTypes = new Set(["application/pdf", "image/jpeg", "image/png"]);
      const storedType=(job.document.mediaType||"").split(";")[0].trim().toLowerCase();
      const responseType = (blob.type || "").split(";")[0].trim().toLowerCase();
      const extension = job.document.originalFileName.toLowerCase().split(".").pop();
      const extensionType = extension === "pdf" ? "application/pdf" : extension === "jpg" || extension === "jpeg" ? "image/jpeg" : extension === "png" ? "image/png" : "";
      // Trust the persisted MIME type, then response headers, then the validated file extension.
      const mediaType = isSavedOutput ? "application/pdf" : supportedTypes.has(storedType) ? storedType : supportedTypes.has(responseType) ? responseType : extensionType;
      const previewBlob = mediaType && blob.type !== mediaType ? new Blob([blob], { type: mediaType }) : blob;
      const url = URL.createObjectURL(previewBlob);
      setPreview({
        name: isSavedOutput ? job.outputFileName || `${job.jobNumber}_SmartPrint.pdf` : job.document.originalFileName,
        url,
        blob: previewBlob,
        mediaType,
        job,
        readOnly: readOnly || isSavedOutput,
        isSavedOutput
      });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not open the document.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyShopCreatedPhotoStudio(sheets:PreparedPhotoSheet[]){
    const draft=shopPhotoOrderDraft;
    if(!draft||sheets.length!==1)throw new Error("Prepare exactly one multi-page A4 photo sheet before creating this order.");
    const sheet=sheets[0];
    setBusyJobId("shop-photo-create");
    setError("");
    try{
      const result=await createShopPhotoJob(tokens.accessToken,sheet.file,{
        photoSizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage,
        recipientType:draft.recipientType,customerUserId:draft.customerUserId,
        recipientName:draft.recipientName,recipientPhone:draft.recipientPhone,
        paymentMethod:draft.paymentMethod,idempotencyKey:draft.idempotencyKey
      });
      setShopPhotoOrderDraft(null);
      setShopPhotoOrderReceipt(result);
      const amount=formatMoney(result.job.quotedAmountMinor,result.job.currency);
      const paymentNote=result.payment.method==="UPI"
        ? `UPI payment is pending. Collect at ${result.payment.upiIdSnapshot||"the shop's configured UPI ID"} and verify actual credit in your UPI/bank app before authorising print.`
        : "Cash payment is pending. Mark received only after cash is physically collected.";
      setToast(`Photo order ${result.job.jobNumber} created for ${result.job.recipientName} · ${amount}. ${paymentNote}`);
      await reload();
    }catch(exception){
      throw exception instanceof Error?exception:new Error("Could not create the shop photo order. Your original photo is unchanged; retry safely.");
    }finally{setBusyJobId(null);}
  }

  async function openShopPhotoStudioForJob(job:ShopPrintJob){
    setBusyJobId(job.id);setError("");
    try{
      const blob=await downloadShopDocument(tokens.accessToken,job.id,"original");
      const stored=(job.document.mediaType||"").split(";")[0].trim().toLowerCase();
      const response=(blob.type||"").split(";")[0].trim().toLowerCase();
      const extension=job.document.originalFileName.toLowerCase().split(".").pop();
      const mediaType=stored==="image/jpeg"||stored==="image/png"?stored:response==="image/jpeg"||response==="image/png"?response:extension==="png"?"image/png":extension==="jpg"||extension==="jpeg"?"image/jpeg":"";
      if(!mediaType)throw new Error("Photo Studio can open JPG and PNG originals. Preview this file first to confirm its format.");
      const sourceBlob=blob.type===mediaType?blob:new Blob([blob],{type:mediaType});
      const source=new File([sourceBlob],job.document.originalFileName,{type:mediaType,lastModified:Date.now()});
      const returnPreview={name:job.document.originalFileName,blob:sourceBlob,mediaType,job,readOnly:false};
      setShopPhotoStudio({source,job,returnPreview});
    }catch(exception){setError(exception instanceof Error?exception.message:"Could not open Photo Studio for this file.");}
    finally{setBusyJobId(null);}
  }

  function openShopPhotoStudio(){
    if(!preview||preview.readOnly||preview.mediaType!=="image/jpeg"&&preview.mediaType!=="image/png")return;
    const source=new File([preview.blob],preview.name,{type:preview.mediaType,lastModified:Date.now()});
    const returnPreview={name:preview.name,blob:preview.blob,mediaType:preview.mediaType,job:preview.job,readOnly:preview.readOnly};
    URL.revokeObjectURL(preview.url);
    setPreview(null);
    setFocusShopPhotoEdit(false);
    setShopPhotoStudio({source,job:preview.job,returnPreview});
  }

  function cancelShopPhotoStudio(){
    if(!shopPhotoStudio){setShopPhotoStudio(null);return;}
    const restored=shopPhotoStudio.returnPreview;
    setPreview({...restored,url:URL.createObjectURL(restored.blob)});
    setFocusShopPhotoEdit(true);
    setShopPhotoStudio(null);
  }

  async function applyShopPhotoStudio(sheets:PreparedPhotoSheet[]){
    const editor=shopPhotoStudio;
    if(!editor||sheets.length!==1)throw new Error("Prepare one multi-page A4 photo PDF before saving this job.");
    const sheet=sheets[0];
    setBusyJobId(editor.job.id);
    try{
      if(editor.job.storageMode==="LOCAL_AGENT") {
        await replaceShopPhotoDocumentLocally(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      } else {
        await replaceShopPhotoDocument(tokens.accessToken,editor.job.id,sheet.file,sheet.sizeCode,sheet.photoCount,sheet.photosPerPage);
      }
    }catch(exception){
      setError(exception instanceof Error?exception.message:"Could not save the enhanced photo sheet.");
      throw exception;
    }

    setShopPhotoStudio(null);
    setFocusShopPhotoEdit(false);
    setToast("The enhanced multi-page A4 photo PDF is saved to the same print job and its photo price has been recalculated.");
    try{
      await reload();
      const blob=await downloadShopDocument(tokens.accessToken,editor.job.id,"original");
      const previewBlob=blob.type==="application/pdf"?blob:new Blob([blob],{type:"application/pdf"});
      const savedJob:ShopPrintJob={...editor.job,printKind:"PHOTO",photoSizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage,pageCount:Math.max(1,Math.ceil(sheet.photoCount/sheet.photosPerPage)),document:{...editor.job.document,originalFileName:sheet.file.name,mediaType:"application/pdf",sizeBytes:sheet.file.size}};
      setPreview({name:sheet.file.name,url:URL.createObjectURL(previewBlob),blob:previewBlob,mediaType:"application/pdf",job:savedJob,readOnly:true});
    }catch{
      setError("The A4 photo PDF was saved to the existing print job, but the queue could not refresh its preview. Refresh the queue before approving or printing.");
    }finally{
      setBusyJobId(null);
    }
  }

  async function viewJobHistory(job: ShopPrintJob) {
    setBusyJobId(job.id);
    setError("");
    try {
      const events = await getShopJobEvents(tokens.accessToken, job.id);
      setEventHistory({ job, events });
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load job history.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function approveJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Approve ${job.jobNumber} for payment? It will not be sent to a printer yet.`, "Accept this file", "Accept file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await approveShopPrintJob(tokens.accessToken, job.id);
      setToast(result.nextStep || "Shop approval saved. This job is awaiting verified payment.");
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not approve this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function rejectJob(job: ShopPrintJob) {
    if (!(await confirmInApp(`Reject ${job.jobNumber}? The customer will see that the shop declined the request.`, "Reject this file?", "Reject file"))) return;
    setBusyJobId(job.id);
    setError("");
    try {
      await rejectShopPrintJob(tokens.accessToken, job.id);
      setToast("The print request was rejected.");
      await reload();
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not reject this job.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function applyBatchReview(batchJobs: ShopPrintJob[], acceptedJobIds: string[], pdfBrandingEnabled: boolean) {
    const batchId=batchJobs.find(job=>job.batchId)?.batchId;
    if(!batchId||batchJobs.length===0)return;
    const accepted=new Set(acceptedJobIds);
    const acceptCount=batchJobs.filter(job=>accepted.has(job.id)).length;
    const rejectCount=batchJobs.length-acceptCount;
    if (!(await confirmInApp(
      `Accept ${acceptCount} file(s) and reject ${rejectCount} file(s)? Previously rejected files you tick will be restored only if no payment has started. Expired quotes must be recalculated before checkout. PDF footer branding will be ${pdfBrandingEnabled ? "enabled on safe blank margins" : "off"} for this batch. Original uploads and normal paper prints remain unchanged. The decisions are applied together.`,
      "Confirm batch review",
      "Apply review"
    ))) return;

    setBusyJobId("batch-review");
    setError("");
    try {
      const result=await applyShopPrintBatchReview(
        tokens.accessToken,
        batchId,
        batchJobs.map(job=>job.id),
        acceptedJobIds,
        pdfBrandingEnabled
      );
      await reload();
      setBatchReviewJobs(null);
      setBatchReviewRetry(false);
      setBatchReviewRetryIds(current=>current.filter(id=>!batchJobs.some(job=>job.id===id)));
      setBatchReviewDecisions(current=>{
        const next={...current};
        for(const job of batchJobs)delete next[job.id];
        return next;
      });
      setToast(result.quoteRefreshRequiredCount>0
        ?`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. ${result.quoteRefreshRequiredCount} accepted file(s) need a fresh quote before checkout.`
        :`Batch review saved: ${acceptCount} accepted and ${rejectCount} rejected. All accepted files remain part of one checkout.`);
    } catch(exception) {
      await reload().catch(()=>undefined);
      setError(exception instanceof Error?exception.message:"Could not apply this batch review. The decisions were not changed; refresh and retry the same review.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function recordFileRefund(job: ShopPrintJob, values: { refundMethod: "UPI" | "CASH"; refundReference: string | null; reason: string; rejectedPageNumbers: number[]; idempotencyKey: string }) {
    setBusyJobId(job.id);
    setError("");
    try {
      const result = await recordShopPrintRefund(tokens.accessToken, job.id, {
        ...values, confirmedSent: true
      });
      setToast(`Refund of ${formatMoney(result.amountMinor, result.currency)} recorded for ${job.document.originalFileName}. The return was logged under the signed-in shopkeeper.`);
      setRefundJob(null);
      if (preview?.job.id === job.id) {
        URL.revokeObjectURL(preview.url);
        setPreview(null);
      }
      await reload();
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not record this refund.");
    } finally {
      setBusyJobId(null);
    }
  }

  async function openBatchReview(batchId: string) {
    setBatchReviewLoadingId(batchId);
    setError("");
    try {
      // Always load the complete batch from the tenant-scoped API. The queue
      // may currently be filtered or paginated, so its visible rows are not
      // a safe source for per-file acceptance/rejection decisions.
      const allBatchJobs = await getShopPrintBatchJobs(tokens.accessToken, batchId);
      const reviewableStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
      const waiting = allBatchJobs.filter(job => reviewableStatuses.has(job.status));
      if (waiting.length === 0) {
        setBatchReviewJobs(null);
        setToast("This batch has no files eligible for pre-checkout review.");
        return;
      }
      setBatchReviewDecisions(current => {
        const next = { ...current };
        for (const job of waiting) {
          if (next[job.id] === undefined) next[job.id] = job.status !== "REJECTED";
        }
        return next;
      });
      setBatchReviewRetry(waiting.some(job => batchReviewRetryIds.includes(job.id)));
      setBatchReviewJobs(waiting);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Could not load every file in this batch. No review decisions were changed.");
    } finally {
      setBatchReviewLoadingId(current => current === batchId ? null : current);
    }
  }

  const navAction = (section: Section) => {
    const nextPath = shopPathForSection(section);
    if (window.location.pathname !== nextPath) {
      window.history.pushState({}, "", nextPath);
    }
    setActive(section);
    setSidebarOpen(false);
    setAccountMenuOpen(false);
    setError("");
  };

  const activePageTitle = active === "Settings" ? "Shop profile" : active;

  if (loading && !profile) {
    return <div className={dark ? "app-shell dark boot-screen" : "app-shell light boot-screen"}><div className="boot-card"><BrandLockup className="boot-brand-lockup" /><div className="boot-title">Preparing your workspace</div><div className="boot-subtitle">Connecting securely to your shop…</div><div className="button-spinner boot-spinner"/></div></div>;
  }

  return (
    <div className={`${dark ? "app-shell dark" : "app-shell light"}${sidebarCollapsed ? " sidebar-collapsed" : ""}`}>
      <aside className={sidebarOpen ? "sidebar sidebar-open" : "sidebar"}>
        <BrandLockup>
          <button className="icon-button sidebar-close" aria-label="Close navigation" onClick={() => setSidebarOpen(false)}><X size={18}/></button>
        </BrandLockup>
        <div className="workspace-switcher">
          <div className="shop-avatar">{(profile?.displayName || "S").slice(0,1).toUpperCase()}</div>
          <div className="workspace-copy"><strong>{profile?.displayName || "Your shop"}</strong><span>{profile?.businessName || "Shop workspace"}</span></div>
          <span className="tenant-status-dot" title="Tenant workspace active"/>
        </div>
        <nav className="primary-nav" aria-label="Shop navigation">
          {sectionGroups.map(({ label: groupLabel, items }) => (
            <div className="nav-group" key={groupLabel}>
              <div className="nav-label">{groupLabel}</div>
              <div className="nav-group-items">
                {items.map(label => {
                  const section = sections.find(item => item.label === label);
                  if (!section) return null;
                  const Icon = section.icon;
                  return (
                    <button key={label} title={label} aria-label={label}
                      className={active === label ? "nav-item active" : "nav-item"}
                      onClick={() => navAction(label)}>
                      <Icon size={18} strokeWidth={1.8}/><span>{label}</span>
                    </button>
                  );
                })}
              </div>
            </div>
          ))}
        </nav>
      </aside>
      {sidebarOpen && <button className="mobile-scrim" aria-label="Close navigation overlay" onClick={() => setSidebarOpen(false)}/>}

      <main className="main-panel">
        <header className="topbar">
          <div className="topbar-left">
            <button className="icon-button sidebar-toggle" aria-label={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} title={sidebarCollapsed ? "Expand navigation" : "Collapse navigation"} onClick={toggleSidebarCollapsed}>{sidebarCollapsed ? <PanelLeftOpen size={18}/> : <PanelLeftClose size={18}/>}</button>
            <button className="icon-button mobile-menu" aria-label="Open navigation" onClick={() => setSidebarOpen(true)}><Menu size={20}/></button>
            <div className="breadcrumb"><span>Shop workspace</span><span className="crumb-slash">/</span><strong>{activePageTitle}</strong></div>
          </div>
          <div className="topbar-actions">
            <div className="connection-pill" role="status" aria-live="polite" title={cloudStatus === "connected" ? "The latest shop data refresh succeeded." : cloudStatus === "checking" ? "Checking your shop connection." : "The latest shop data refresh could not reach the cloud service."}>
              <span className="live-dot" style={{ opacity: cloudStatus === "unavailable" ? 0.45 : 1 }}/>
              <span>{cloudStatus === "connected" ? "Cloud connected" : cloudStatus === "checking" ? "Checking cloud" : "Cloud unavailable"}</span>
            </div>
            <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="theme-toggle-compact"/>
            <div className="service-status-row" aria-label="Live shop system status">
              <span className={"service-status "+apiConnectionState} title="API connection"><i/>API {apiConnectionState === "connected" ? "Connected" : apiConnectionState === "degraded" ? "Degraded" : apiConnectionState === "offline" ? "Offline" : "Checking"}</span>
              <span className={"service-status "+(onlineAgentCount > 0 ? "connected" : activeAgentCount > 0 ? "offline" : "checking")} title="Windows Agent heartbeat"><i/>Agent {onlineAgentCount > 0 ? onlineAgentCount+" online" : activeAgentCount > 0 ? "Offline" : "Not paired"}</span>
              <span className={"service-status "+(usablePrinterCount > 0 ? "connected" : offlinePrinterCount > 0 ? "offline" : "checking")} title="Active printer availability"><i/>Printers {usablePrinterCount > 0 ? usablePrinterCount+" available" : offlinePrinterCount > 0 ? offlinePrinterCount+" unavailable" : "Unavailable"}</span>
            </div>
            <div className="topbar-divider"/>
            <span className="topbar-product-tag"><span className="mode-indicator"/> {isDesktopApp() ? "Desktop workspace" : "Shop console"}</span>
            <div className="account-menu" ref={accountMenuRef}>
              <button type="button" className="account-menu-trigger"
                aria-label={`Account options for ${profile?.ownerName || "shop owner"}`}
                aria-expanded={accountMenuOpen} aria-controls="shop-account-menu"
                title="Account and shop settings"
                onClick={() => setAccountMenuOpen(value => !value)}>
                <span className="account-avatar">{initials(profile?.ownerName || "Shop owner")}</span>
                <span className="account-menu-trigger-copy">
                  <strong>{profile?.ownerName || "Shop owner"}</strong>
                  <small>Account</small>
                </span>
                <ChevronDown size={14} aria-hidden="true"/>
              </button>
              {accountMenuOpen && (
                <div className="account-menu-popover" id="shop-account-menu" aria-label="Account options">
                  <div className="account-menu-heading">
                    <span className="account-avatar">{initials(profile?.ownerName || "Shop owner")}</span>
                    <div>
                      <strong>{profile?.ownerName || "Shop owner"}</strong>
                      <span>{profile?.businessName || "Your shop"}</span>
                    </div>
                  </div>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setOwnerAccountOpen(true); }}>
                    <UserRound size={16}/>Personal details
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Settings")}>
                    <Settings size={16}/>Shop profile &amp; branding
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => navAction("Subscription")}>
                    <ShieldCheck size={16}/>Trial &amp; subscription
                  </button>
                  <button type="button" className="account-menu-item" onClick={() => { setAccountMenuOpen(false); setHelpOpen(true); }}>
                    <LifeBuoy size={16}/>Help &amp; support
                  </button>
                  <div className="account-menu-divider"/>
                  <button type="button" className="account-menu-item account-menu-signout" onClick={() => { setAccountMenuOpen(false); void signOut(); }}>
                    <LogOut size={16}/>Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>

        <div className="content-wrap">
          <div className="page-heading">
            <div><div className="eyebrow"><span className="eyebrow-line"/>SHOP WORKSPACE</div><h1>{active === "Overview" ? `Welcome, ${(profile?.ownerName || "there").split(" ")[0]}` : activePageTitle}</h1><p>{active === "Overview" ? "Your shop setup and operations, all in one place." : pageDescription(active)}</p></div>
            <div className="heading-actions"><span className="date-chip"><Clock3 size={15}/>{today}</span>{active === "Printers" && <button className="primary-button" onClick={() => setModal("printer")}><Plus size={16}/> Add printer</button>}{active === "QR Stations" && <button className="primary-button" onClick={() => setModal("station")}><Plus size={16}/> Create QR station</button>}</div>
          </div>

          {error && <div className="inline-error" role="alert"><span>{error}</span><button onClick={() => setError("")} aria-label="Dismiss error"><X size={15}/></button></div>}
          {toast && <div className="inline-success" role="status"><Check size={15}/><span>{toast}</span><button onClick={() => setToast("")} aria-label="Dismiss message"><X size={15}/></button></div>}

          {active === "Order Journey" && <OrderJourneyPage
            jobs={journeyJobs}
            payments={journeyPayments}
            printers={printers}
            agents={agentDevices}
            refreshing={journeyRefreshing}
            onRefresh={() => void refreshOrderJourney()}
            onNavigate={navAction}
            onReviewBatch={openBatchReview}
            onVerifyPayment={verifyPayment}
            onPreview={job=>{navAction("Print Queue");void previewJob(job,true);}}
            onApprove={approveJob}
            onReject={rejectJob}
            onRefund={job=>setRefundJob(job)}
            onAssignPrinter={assignPrinter}
            onDispatch={dispatchPrintJob}
            onConfirmOutput={confirmPrintedOutput}
            onRetryPdfExport={retryPdfExport}
            onHistory={viewJobHistory}
            onOpenPhotoStudio={job=>void openShopPhotoStudioForJob(job)}
          />}
          {active === "Overview" && <Overview profile={profile} printers={printers} stations={stations} printJobs={printJobs} pricing={pricing} agentDevices={agentDevices} paymentSettings={paymentSettings} onNavigate={navAction}/>}
          {active === "Printers" && <PrintersPage printers={printers} busyResourceId={busyResourceId} onAdd={() => setModal("printer")} onToggle={togglePrinter}/>}
          {active === "QR Stations" && <StationsPage stations={stations} profile={profile} busyResourceId={busyResourceId} onAdd={() => setModal("station")} onToggle={toggleStation} onGenerate={createFreshStationCode}/>}
          {active === "Print Agent" && <PrintAgentPage agents={agentDevices} enrollmentCode={enrollmentCode} busy={agentBusy} onGenerate={generateAgentCode} onRevoke={revokeAgent} desktop={isDesktopApp()} localAgentStatus={localAgentStatus} localAgentConflict={localAgentConflict} localAgentRevoked={localAgentRevoked} localAgentNeedsRepair={localAgentNeedsRepair} localAgentEnrollmentIssue={localAgentEnrollmentIssue} localAgentOwnershipChecking={localAgentOwnershipChecking} onConnect={()=>void connectLocalComputer()} onReconnectAfterRevocation={()=>void connectLocalComputer(false,agentDevices,true)}/>}
          {active === "Pricing" && pricing && <PricingPage pricing={pricing} onSave={savePricing} busy={busy}/>}
          {active === "Subscription" && subscription && <SubscriptionPage summary={subscription} token={tokens.accessToken} onUpdated={setSubscription}/>}
          {active === "Payments" && paymentSettings && <PaymentsPage settings={paymentSettings} payments={payments} refunds={refunds} onSave={savePaymentSettings} onVerify={verifyPayment} onReject={rejectPayment} busy={busy} busyJobId={busyJobId}/>}
          {active === "Settings" && profile && <SettingsPage profile={profile} onSave={saveProfile} busy={busy}/>}
          {active === "Print Queue" && <PrintQueuePage jobs={printJobs} printers={printers} agentDevices={agentDevices} searchQuery={queueSearch} loadingBatchId={batchReviewLoadingId} onCreatePhotoOrder={()=>setShopPhotoOrderOpen(true)} onSearch={searchPrintQueue} onPreview={previewJob} onPreviewOutput={job=>void previewJob(job,true,"output")} onApprove={approveJob} onReject={rejectJob} onRefund={job=>setRefundJob(job)} onReviewBatch={openBatchReview} onHistory={viewJobHistory} onAssignPrinter={assignPrinter} onUpdateSettings={updatePrintJobSettings} onDispatch={dispatchPrintJob} onConfirmOutput={confirmPrintedOutput} onRetryPdfExport={retryPdfExport} onRefresh={refreshPrintQueue} onLoadOlder={loadOlderQueueJobs} hasMoreJobs={hasMoreQueueJobs} loadingOlderJobs={loadingOlderQueueJobs} busyJobId={busyJobId}/>} 
          {active === "Customers" && <CustomersPage token={tokens.accessToken}/>}
          {active === "Reports" && <ReportsPage token={tokens.accessToken}/>} 

          <footer className="app-footer"><span>© 2026 SmartPrint by QuantaEdge</span><span><span className="footer-status"/> API session authenticated</span><span>Made for print shops, with care.</span></footer>
        </div>
      </main>

      {ownerAccountOpen && profile && <ShopOwnerAccountModal profile={profile} busy={busy} onClose={() => setOwnerAccountOpen(false)} onSave={saveOwnerAccount}/>}
      {helpOpen && <HelpSupportModal onClose={() => setHelpOpen(false)} onNavigate={section => { setHelpOpen(false); navAction(section); }}/>}
      {modal === "printer" && <PrinterModal busy={busy} onClose={() => setModal(null)} onSubmit={submitPrinter}/>}
      {modal === "station" && <StationModal busy={busy} printers={printers.filter(printer => printer.isActive)} onClose={() => setModal(null)} onSubmit={submitStation}/>}
            {batchReviewJobs && <BatchReviewModal jobs={batchReviewJobs} busy={busyJobId==="batch-review"} decisions={batchReviewDecisions} retrying={batchReviewRetry} onDecision={(jobId,accepted)=>setBatchReviewDecisions(current=>({...current,[jobId]:accepted}))} onClose={()=>setBatchReviewJobs(null)} onPreview={job=>void previewJob(job,true)} onApply={(acceptedIds,pdfBrandingEnabled)=>void applyBatchReview(batchReviewJobs,acceptedIds,pdfBrandingEnabled)}/>} 
      {preview && <DocumentPreviewModal name={preview.name} url={preview.url} mediaType={preview.mediaType} job={preview.job} readOnly={preview.readOnly} isSavedOutput={preview.isSavedOutput} focusEditOnMount={focusShopPhotoEdit} onEditPhoto={preview.mediaType.startsWith("image/")&&(preview.job.storageMode==="CLOUD"||preview.job.storageMode==="LOCAL_AGENT")&&preview.job.status==="AWAITING_SHOP_APPROVAL"&&[pricing?.photo35x45Minor,pricing?.photo25x35Minor,pricing?.photo2x2Minor,pricing?.photo4x6Minor].some(rate=>(rate??0)>0)?openShopPhotoStudio:undefined} onApprove={()=>void approveJob(preview.job)} onReject={()=>void rejectJob(preview.job)} onRefund={()=>setRefundJob(preview.job)} onClose={() => { URL.revokeObjectURL(preview.url); setPreview(null); setFocusShopPhotoEdit(false); }}/>} 
      {shopPhotoStudio && <PhotoStudio source={shopPhotoStudio.source} mode="shop" dark={dark} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onCancel={cancelShopPhotoStudio} onApply={applyShopPhotoStudio}/>} 
      {shopPhotoOrderReceipt&&<ShopPhotoOrderReceipt receipt={shopPhotoOrderReceipt} onClose={()=>setShopPhotoOrderReceipt(null)} onOpenPayments={()=>{setShopPhotoOrderReceipt(null);navAction("Payments");}}/>}
      {shopPhotoOrderOpen&&<ShopPhotoOrderModal token={tokens.accessToken} paymentSettings={paymentSettings} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onClose={()=>setShopPhotoOrderOpen(false)} onContinue={draft=>{setShopPhotoOrderOpen(false);setShopPhotoOrderDraft(draft);}}/>}
      {shopPhotoOrderDraft&&<PhotoStudio source={shopPhotoOrderDraft.source} mode="shop-create" dark={dark} photoPrices={{"35x45":pricing?.photo35x45Minor??0,"25x35":pricing?.photo25x35Minor??0,"2x2":pricing?.photo2x2Minor??0,"4x6":pricing?.photo4x6Minor??0}} onCancel={()=>setShopPhotoOrderDraft(null)} onApply={applyShopCreatedPhotoStudio}/>} 
      {eventHistory && <JobHistoryModal job={eventHistory.job} events={eventHistory.events} onClose={() => setEventHistory(null)}/>}
      {refundJob && <RefundModal job={refundJob} token={tokens.accessToken} busy={busyJobId===refundJob.id} onClose={()=>setRefundJob(null)} onSubmit={values=>void recordFileRefund(refundJob,values)}/>} 
      {actionDialog && <Modal title={actionDialog.title} subtitle="This action will be recorded against your shop account." onClose={closeActionDialog}>
        <div className="in-app-dialog-body"><p>{actionDialog.message}</p>{actionDialog.inputLabel&&<label className="dialog-input-label">{actionDialog.inputLabel}<textarea autoFocus rows={3} maxLength={300} value={actionDialogValue} onChange={event=>setActionDialogValue(event.target.value)} placeholder="Enter at least 3 characters"/></label>}</div>
        <div className="modal-actions"><button className="secondary-button" onClick={closeActionDialog}>Cancel</button><button className="primary-button" disabled={!!actionDialog.inputLabel&&actionDialogValue.trim().length<3} onClick={submitActionDialog}>{actionDialog.confirmLabel}</button></div>
      </Modal>}
    </div>
  );
}

type ShopPhotoOrderDraft = {
  source:File; recipientType:"EXISTING_CUSTOMER"|"WALK_IN"; customerUserId?:string;
  recipientName?:string; recipientPhone?:string; paymentMethod:"UPI"|"CASH"; idempotencyKey:string;
};

function ShopPhotoOrderReceipt({receipt,onClose,onOpenPayments}:{
  receipt:Awaited<ReturnType<typeof createShopPhotoJob>>;
  onClose:()=>void;
  onOpenPayments:()=>void;
}) {
  const {job,payment}=receipt;
  const amount=formatMoney(payment.amountMinor,payment.currency);
  const upiIntent=payment.method==="UPI"&&payment.status!=="VERIFIED"
    ? buildCustomerUpiIntent({
        upiId:payment.upiIdSnapshot,
        payeeName:payment.payeeNameSnapshot,
        amountMinor:payment.amountMinor,
        currency:payment.currency,
        transactionReference:job.jobNumber,
        description:`SmartPrint photo order ${job.jobNumber}`
      })
    : null;
  return <Modal title="Photo order created" subtitle={`Job ${job.jobNumber} · ${job.recipientName}`} onClose={onClose}>
    <div className="photo-order-receipt">
      <div className="photo-order-receipt-summary">
        <span>Server-calculated amount</span><strong>{amount}</strong>
        <small>{job.photoCount} photos · {job.photoSizeCode} · {job.pageCount} A4 page(s)</small>
      </div>
      {payment.status==="VERIFIED"
        ? <div className="file-review-note"><Check size={16}/><span>Payment is already verified. Continue from the Print Queue to assign a printer and explicitly dispatch the job.</span></div>
        : payment.method==="UPI"
          ? <div className="photo-order-upi">
              {upiIntent?<QRCode value={upiIntent} size={184} level="M"/>:<p className="form-error">A UPI QR could not be prepared from the saved shop payment details. Open Payments and check the configured UPI ID.</p>}
              <strong>{payment.payeeNameSnapshot||"Shop payment"}</strong>
              <span>{payment.upiIdSnapshot||"UPI ID not available"}</span>
              <small>Payment pending · Ask the customer to scan and pay the exact amount. Verify the actual credit in your UPI/bank app; this QR does not confirm payment.</small>
            </div>
          : <div className="file-review-note"><Wallet size={16}/><span>Cash payment pending. Collect {amount} at the counter and mark it received only after the cash is physically in the shop.</span></div>}
      <div className="form-hint">The job is already in your Print Queue. It cannot be dispatched until payment has been manually verified. Printing must then be started explicitly.</div>
      <div className="modal-actions">
        <button type="button" className="secondary-button" onClick={onClose}>Done</button>
        <button type="button" className="primary-button" onClick={onOpenPayments}>Open Payments <ArrowRight size={15}/></button>
      </div>
    </div>
  </Modal>;
}

function ShopPhotoOrderModal({token,paymentSettings,photoPrices,onClose,onContinue}:{
  token:string; paymentSettings:ShopPaymentSettings|null; photoPrices:Record<string,number>;
  onClose:()=>void; onContinue:(draft:ShopPhotoOrderDraft)=>void;
}) {
  const [file,setFile]=useState<File|null>(null);
  const [recipientType,setRecipientType]=useState<"EXISTING_CUSTOMER"|"WALK_IN">("EXISTING_CUSTOMER");
  const [customers,setCustomers]=useState<ShopCustomer[]>([]);
  const [customerSearch,setCustomerSearch]=useState("");
  const [selectedCustomerId,setSelectedCustomerId]=useState("");
  const [loadingCustomers,setLoadingCustomers]=useState(false);
  const [customerError,setCustomerError]=useState("");
  const [recipientName,setRecipientName]=useState("");
  const [recipientPhone,setRecipientPhone]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentSettings?.cashEnabled?"CASH":"UPI");
  const [error,setError]=useState("");

  async function searchCustomers(search=customerSearch) {
    setLoadingCustomers(true);setCustomerError("");
    try {
      const page=await getShopCustomers(token,{search,page:1,pageSize:20});
      setCustomers(page.items);
      if(selectedCustomerId&&!page.items.some(item=>item.id===selectedCustomerId))setSelectedCustomerId("");
    } catch(exception) {
      setCustomerError(exception instanceof Error?exception.message:"Could not load this shop's customers.");
    } finally {setLoadingCustomers(false);}
  }
  useEffect(()=>{void searchCustomers("");},[token]);

  const enabledPhotoSizes=Object.values(photoPrices).some(rate=>rate>0);
  const hasPaymentMethod=!!(paymentSettings?.upiEnabled||paymentSettings?.cashEnabled);
  const selectedPaymentEnabled=paymentMethod==="UPI"?!!paymentSettings?.upiEnabled:!!paymentSettings?.cashEnabled;
  const selectedCustomer=customers.find(item=>item.id===selectedCustomerId);
  const canContinue=!!file&&enabledPhotoSizes&&selectedPaymentEnabled&&
    (recipientType==="EXISTING_CUSTOMER"?!!selectedCustomer:recipientName.trim().length>0);

  function selectFile(event:ChangeEvent<HTMLInputElement>) {
    const candidate=event.currentTarget.files?.[0]??null;setError("");
    if(!candidate){setFile(null);return;}
    const extension=candidate.name.toLowerCase().split(".").pop();
    const inferredType=candidate.type==="image/png"||candidate.type==="image/jpeg"
      ?candidate.type
      :extension==="png"?"image/png":extension==="jpg"||extension==="jpeg"?"image/jpeg":"";
    if(!["image/jpeg","image/png"].includes(inferredType)){
      setFile(null);setError("Choose a JPG, JPEG or PNG image. Other formats are not supported by Photo Studio.");event.currentTarget.value="";return;
    }
    if(candidate.size<=0||candidate.size>UPLOAD_LIMITS.maxFileBytes){
      setFile(null);setError(`The original photo must be between 1 byte and ${UPLOAD_LIMITS.maxFileBytes / (1024 * 1024)} MB.`);event.currentTarget.value="";return;
    }
    setFile(candidate.type===inferredType?candidate:new File([candidate],candidate.name,{type:inferredType,lastModified:candidate.lastModified}));
  }

  function submit(event:FormEvent<HTMLFormElement>) {
    event.preventDefault();setError("");
    if(!file){setError("Choose a photo before opening Photo Studio.");return;}
    if(!enabledPhotoSizes){setError("Configure at least one photo size and per-photo rate in Pricing first.");return;}
    if(!selectedPaymentEnabled){setError("Choose a payment method currently enabled by this shop.");return;}
    if(recipientType==="EXISTING_CUSTOMER"&&!selectedCustomer){setError("Choose a customer already known to this shop.");return;}
    if(recipientType==="WALK_IN"&&!recipientName.trim()){setError("Enter the walk-in customer's name.");return;}
    onContinue({
      source:file,recipientType,
      customerUserId:recipientType==="EXISTING_CUSTOMER"?selectedCustomer!.id:undefined,
      recipientName:recipientType==="WALK_IN"?recipientName.trim():undefined,
      recipientPhone:recipientType==="WALK_IN"?recipientPhone.trim()||undefined:undefined,
      paymentMethod,idempotencyKey:crypto.randomUUID()
    });
  }

  return <Modal title="New photo order" subtitle="Upload a shop-owned original, prepare its A4 photo sheets and create one shop-scoped print job." onClose={onClose}>
    <form className="modal-form" onSubmit={submit}>
      <label>Original photo<input aria-label="Choose original photo" type="file" accept=".jpg,.jpeg,.png,image/jpeg,image/png" onChange={selectFile}/></label>
      {file&&<div className="file-review-note"><ImageIcon size={16}/><span>{file.name} · {(file.size/1024/1024).toFixed(2)} MB. The original is not modified.</span></div>}
      <label>Order for<select value={recipientType} onChange={event=>setRecipientType(event.currentTarget.value as "EXISTING_CUSTOMER"|"WALK_IN")}><option value="EXISTING_CUSTOMER">Existing customer of this shop</option><option value="WALK_IN">Walk-in customer</option></select></label>
      {recipientType==="EXISTING_CUSTOMER"&&<>
        <label>Find a customer<div className="queue-search-form"><input aria-label="Search shop customers" value={customerSearch} onChange={event=>setCustomerSearch(event.currentTarget.value)} onKeyDown={event=>{if(event.key==="Enter"){event.preventDefault();void searchCustomers();}}} placeholder="Name or email"/><button type="button" className="secondary-button" onClick={()=>void searchCustomers()} disabled={loadingCustomers}>{loadingCustomers?"Searching…":"Search"}</button></div></label>
        <label>Select customer<select aria-label="Select shop customer" value={selectedCustomerId} onChange={event=>setSelectedCustomerId(event.currentTarget.value)}><option value="">Choose a customer</option>{customers.map(customer=><option key={customer.id} value={customer.id}>{customer.displayName} · {customer.maskedEmail}</option>)}</select></label>
        {customerError&&<p className="form-error" role="alert">{customerError}</p>}
        {!loadingCustomers&&!customerError&&customers.length===0&&<p className="form-hint">No customers found for this shop. Choose Walk-in customer when appropriate.</p>}
        {selectedCustomer&&<p className="form-hint">This order will be linked to {selectedCustomer.displayName}'s account and shop history. No customer login or QR session will be created.</p>}
      </>}
      {recipientType==="WALK_IN"&&<>
        <label>Walk-in customer name<input value={recipientName} maxLength={160} onChange={event=>setRecipientName(event.currentTarget.value)} placeholder="Customer name" required/></label>
        <label>Contact number (optional)<input type="tel" maxLength={40} value={recipientPhone} onChange={event=>setRecipientPhone(event.currentTarget.value)} placeholder="Phone number"/></label>
        <p className="form-hint">A walk-in is saved only as a recipient on this shop's job. No global customer account is created.</p>
      </>}
      <label>Payment method<select aria-label="Photo order payment method" value={paymentMethod} onChange={event=>setPaymentMethod(event.currentTarget.value as "UPI"|"CASH")}>{paymentSettings?.upiEnabled&&<option value="UPI">UPI · verify receipt manually</option>}{paymentSettings?.cashEnabled&&<option value="CASH">Cash at counter · mark received manually</option>}</select></label>
      <p className="form-hint">Payment will be recorded as pending. This action does not verify payment or start printing. The shop must verify the actual money received and explicitly dispatch the job.</p>
      {!enabledPhotoSizes&&<p className="form-error" role="alert">No photo sizes are priced. Configure Photo Printing under Pricing before creating an order.</p>}
      {!hasPaymentMethod&&<p className="form-error" role="alert">No payment method is enabled. Configure UPI or cash under Payment Settings.</p>}
      {error&&<p className="form-error" role="alert">{error}</p>}
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button type="submit" className="primary-button" disabled={!canContinue}>Open Photo Studio <ArrowRight size={15}/></button></div>
    </form>
  </Modal>;
}

function CustomersPage({ token }: { token: string }) {
  const [searchDraft, setSearchDraft] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [refreshKey, setRefreshKey] = useState(0);
  const [result, setResult] = useState<ShopCustomerPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [selectedCustomer, setSelectedCustomer] = useState<ShopCustomer | null>(null);
  const [history, setHistory] = useState<ShopCustomerHistory | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState("");
  const historyRequest = useRef(0);
  const pageSize = 20;

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    void getShopCustomers(token, { search, page, pageSize })
      .then(value => { if (current) setResult(value); })
      .catch(exception => {
        if (current) setError(exception instanceof Error ? exception.message : "Could not load shop customers.");
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, search, page, pageSize, refreshKey]);

  async function openHistory(customer: ShopCustomer) {
    const request = ++historyRequest.current;
    setSelectedCustomer(customer);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(true);
    try {
      const value = await getShopCustomerHistory(token, customer.id);
      if (request === historyRequest.current) setHistory(value);
    } catch (exception) {
      if (request === historyRequest.current) {
        setHistoryError(exception instanceof Error ? exception.message : "Could not load this customer's print history.");
      }
    } finally {
      if (request === historyRequest.current) setHistoryLoading(false);
    }
  }

  function closeHistory() {
    historyRequest.current++;
    setSelectedCustomer(null);
    setHistory(null);
    setHistoryError("");
    setHistoryLoading(false);
  }

  const totalPages = Math.max(1, Math.ceil((result?.totalCount ?? 0) / pageSize));
  const firstRow = result?.totalCount ? (page - 1) * pageSize + 1 : 0;
  const lastRow = Math.min(page * pageSize, result?.totalCount ?? 0);

  return (
    <section className="customers-workspace">
      <div className="customers-intro">
        <div>
          <span className="reports-eyebrow">SHOP RELATIONSHIPS</span>
          <h2>Customer directory</h2>
          <p>Signed-in SmartPrint customers who have connected to this shop. Guest sessions stay anonymous.</p>
        </div>
        <div className="customers-total">
          <strong>{result?.totalCount ?? "—"}</strong>
          <span>linked customers</span>
        </div>
      </div>

      <form className="customers-toolbar" onSubmit={event => {
        event.preventDefault();
        setPage(1);
        setSearch(searchDraft.trim());
      }}>
        <label className="search-field customers-search">
          <Search size={14}/>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)}
            placeholder="Search customer name or email" aria-label="Search customer name or email"/>
        </label>
        <button className="primary-button" type="submit"><Search size={14}/> Search</button>
        <button className="secondary-button" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}>
          <Activity size={14}/> Refresh
        </button>
      </form>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !result ? <div className="panel reports-loading"><span className="button-spinner"/> Loading customers…</div> : null}
      {!loading && !error && result?.items.length === 0 && (
        <div className="panel customers-empty">
          <span className="customers-empty-icon"><Users size={24}/></span>
          <h3>{search ? "No matching customers" : "No signed-in customers yet"}</h3>
          <p>{search
            ? "Try another name or email search."
            : "When a customer signs in before scanning this shop's QR code, their shop-specific activity will appear here. Guest jobs are intentionally not identified or linked."}</p>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="panel customers-table-panel">
          <div className="panel-heading customers-panel-heading">
            <div><h2>Customers connected to this shop</h2><p>Only activity created through this shop's QR sessions is included.</p></div>
            <span className="neutral-chip">{result.totalCount} total</span>
          </div>
          <div className="table-scroll">
            <table className="customers-table">
              <thead><tr><th>CUSTOMER</th><th>PRINT JOBS</th><th>COMPLETED</th><th>LAST QR SESSION</th><th>HISTORY</th></tr></thead>
              <tbody>
                {result.items.map(customer => (
                  <tr key={customer.id}>
                    <td><div className="customer-cell"><strong>{customer.displayName || "SmartPrint customer"}</strong><span>{customer.maskedEmail}</span></div></td>
                    <td><strong className="customers-number">{customer.printJobCount}</strong><small className="customers-subvalue">{customer.sessionCount} QR sessions</small></td>
                    <td><strong className="customers-number">{customer.completedJobCount}</strong><small className="customers-subvalue">output confirmed</small></td>
                    <td><span className="customers-date">{formatIstDateTime(customer.lastSeenUtc)}</span></td>
                    <td><button type="button" className="customers-history-button" onClick={() => void openHistory(customer)}>View history <ArrowRight size={13}/></button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="table-footer customers-pagination">
            <span>Showing <strong>{firstRow}–{lastRow}</strong> of <strong>{result.totalCount}</strong></span>
            <div><button type="button" disabled={page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>← Previous</button>
              <span>{page} / {totalPages}</span>
              <button type="button" disabled={page >= totalPages || loading} onClick={() => setPage(value => Math.min(totalPages, value + 1))}>Next →</button></div>
          </div>
        </div>
      )}

      {selectedCustomer && (
        <Modal title={selectedCustomer.displayName || "SmartPrint customer"}
          subtitle={selectedCustomer.maskedEmail + " · Only this shop's print history"}
          onClose={closeHistory}>
          <div className="customer-history-modal">
            {historyLoading && <div className="reports-loading"><span className="button-spinner"/> Loading print history…</div>}
            {historyError && <div className="inline-error" role="alert">{historyError}</div>}
            {history && (
              <>
                <div className="customer-history-summary"><strong>{history.totalJobs}</strong><span>print jobs at this shop</span></div>
                {history.jobs.length === 0
                  ? <div className="empty-search">No print jobs are available for this customer yet.</div>
                  : <div className="customer-history-list">
                    {history.jobs.map(job => {
                      const netPaid = job.payment
                        ? Math.max(0, job.payment.amountMinor - job.payment.refundedAmountMinor)
                        : 0;
                      const tone = job.status === "COMPLETED" ? "green"
                        : job.status.includes("REJECT") || job.status === "FAILED" ? "red"
                        : job.status.includes("AWAITING") ? "amber" : "blue";
                      return (
                        <article className="customer-history-row" key={job.id}>
                          <div className="customer-history-heading">
                            <div><strong>{job.document.originalFileName}</strong><PrintKindTag printKind={job.printKind}/><span>{job.jobNumber} · {formatIstDateTime(job.createdUtc)}</span></div>
                            <Status label={prettyStatus(job.status)} tone={tone}/>
                          </div>
                          <div className="customer-history-facts">
                            <span>{job.printKind==="PHOTO"?`Photo sheet · ${job.photoCount??0} × ${job.photoSizeCode??"photo"} · ${job.photosPerPage??"layout not recorded"} per A4 page · ${job.pageCount} A4 pages`:`${job.pageCount} pages · ${job.copies} copies`}</span>
                            <strong>{formatMoney(job.quotedAmountMinor, job.currency)}</strong>
                          </div>
                          {job.payment && <div className="customer-history-payment">
                            {job.payment.status.replaceAll("_", " ")} · {job.payment.method} · Net recorded: {formatMoney(netPaid, job.payment.currency)}
                            {job.payment.refundedAmountMinor > 0 ? " · Refund recorded" : ""}
                          </div>}
                          <div className="customer-history-output">{job.outputType === "PDF_EXPORT"
                            ? (job.outputCompletedUtc ? "PDF export created and verified" : "PDF export not yet completed")
                            : (job.outputConfirmedUtc ? "Shop confirmed physical output" : "Physical output not yet confirmed")}</div>
                        </article>
                      );
                    })}
                  </div>}
              </>
            )}
          </div>
        </Modal>
      )}
    </section>
  );
}

function ReportsPage({ token }: { token: string }) {
  const [days, setDays] = useState<7 | 30 | 90>(30);
  const [summary, setSummary] = useState<ShopReportSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let current = true;
    setLoading(true);
    setError("");
    setSummary(null);
    void getShopReportSummary(token, days)
      .then(result => { if (current) setSummary(result); })
      .catch(exception => {
        if (current) {
          setError(exception instanceof Error ? exception.message : "Could not load shop reports.");
        }
      })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [token, days, refreshKey]);

  const statusMaximum = Math.max(1, ...(summary?.jobsByStatus.map(item => item.count) ?? [0]));

  return (
    <section className="reports-workspace">
      <div className="reports-toolbar">
        <div><span className="reports-eyebrow">SHOP PERFORMANCE</span><h2>Operations report</h2><p>Live figures from this shop\u2019s recorded print jobs and verified collections.</p></div>
        <div className="reports-controls">
          <label className="reports-period">Period
            <select aria-label="Reporting period" value={days} onChange={event => setDays(Number(event.target.value) as 7 | 30 | 90)}>
              <option value={7}>Last 7 days</option><option value={30}>Last 30 days</option><option value={90}>Last 90 days</option>
            </select>
          </label>
          <button className="secondary-button reports-refresh" type="button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}><Activity size={14}/> Refresh</button>
        </div>
      </div>
      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !summary ? <div className="panel reports-loading"><span className="button-spinner"/> Loading shop activity…</div> : null}
      {!loading && !summary && !error ? <div className="panel reports-loading">No report data is available for this period yet.</div> : null}
      {summary && <>
        <div className="stat-grid reports-stat-grid">
          <Metric icon={<FileText size={18}/>} label="Print jobs" value={summary.totalJobs.toLocaleString("en-IN")} note={`Created in the last ${summary.periodDays} days`} accent="blue"/>
          <Metric icon={<Check size={18}/>} label="Output confirmed" value={summary.completedJobs.toLocaleString("en-IN")} note="Physically confirmed by shop operators" accent="mint"/>
          <Metric icon={<Printer size={18}/>} label="Pages requested" value={summary.pagesRequested.toLocaleString("en-IN")} note="Page count × requested copies" accent="violet"/>
          <Metric icon={<Wallet size={18}/>} label="Payments to review" value={summary.pendingPaymentCount.toLocaleString("en-IN")} note="Open UPI/cash attempts across all dates" accent="amber"/>
        </div>
        <div className="reports-grid">
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Job status breakdown</h2><p>Current status of jobs created during this period</p></div><span className="neutral-chip">{summary.totalJobs} jobs</span></div>
            {summary.jobsByStatus.length === 0 ? <div className="reports-empty">No print jobs were created in this period.</div> :
              <div className="report-status-list">{summary.jobsByStatus.map(item => <div className="report-status-row" key={item.status}>
                <div className="report-status-label"><span>{prettyStatus(item.status)}</span><strong>{item.count.toLocaleString("en-IN")}</strong></div>
                <div className="report-status-track"><span style={{ width: `${Math.max(2, item.count / statusMaximum * 100)}%` }}/></div>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Verified collections</h2><p>Shop-confirmed payments, grouped separately by currency</p></div><span className="neutral-chip">Verified only</span></div>
            {summary.verifiedCollectionsByMethod.length === 0 ? <div className="reports-empty">No payments have been verified in this period. Unverified UPI references are not included.</div> :
              <div className="report-collection-list">{summary.verifiedCollectionsByMethod.map(item => <div className="report-collection-row" key={`${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI" : "Cash at counter"}</strong><small>{item.paymentCount} verified {item.paymentCount === 1 ? "payment" : "payments"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
          <section className="panel reports-panel">
            <div className="panel-heading"><div><h2>Refunds recorded</h2><p>Refunds the shop confirmed sending during this period</p></div><span className="neutral-chip">Recorded as sent</span></div>
            {summary.refundsRecordedByMethod.length === 0 ? <div className="reports-empty">No refunds were recorded as sent during this period.</div> :
              <div className="report-collection-list">{summary.refundsRecordedByMethod.map(item => <div className="report-collection-row" key={`refund-${item.currency}-${item.method}`}>
                <div><strong>{item.method === "UPI" ? "UPI refund" : "Cash refund"}</strong><small>{item.refundCount} recorded {item.refundCount === 1 ? "refund" : "refunds"}</small></div>
                <strong className="report-collection-amount">{formatMoney(item.amountMinor, item.currency)}</strong>
              </div>)}</div>}
          </section>
        </div>
        {summary.outputUnconfirmedJobs > 0 && <div className="report-caution"><ShieldCheck size={16}/><span><strong>{summary.outputUnconfirmedJobs} output-unconfirmed {summary.outputUnconfirmedJobs === 1 ? "job" : "jobs"}.</strong> Check the actual pages before any manual retry. These jobs are not counted as completed.</span></div>}
        <div className="reports-footnote">Report window: {formatIstDateTime(summary.fromUtc)} – {formatIstDateTime(summary.toUtc)}. Job counts use creation time, gross collections use payment verification time, and refunds use the date the shop recorded sending them. Pages requested are not a claim of pages physically printed. A UPI reference is excluded until the shop verifies the receipt.</div>
      </>}
    </section>
  );
}

function Overview({ profile, printers, stations, printJobs, pricing, agentDevices, paymentSettings, onNavigate }: {
  profile: ShopProfile | null; printers: PrinterDevice[]; stations: QrStation[];
  printJobs: ShopPrintJob[]; pricing: ShopPricingProfile | null;
  agentDevices: AgentDevice[]; paymentSettings: ShopPaymentSettings | null;
  onNavigate: (section: Section) => void;
}) {
  const activePrinters = printers.filter(printer => printer.isActive);
  const todayKey = istDateKey(new Date());
  const jobsToday = printJobs.filter(job => istDateKey(job.createdUtc) === todayKey);
  const quotesAwaiting = printJobs.filter(job => job.status === "AWAITING_SHOP_APPROVAL");
  const onlineAgentIds = new Set(agentDevices
    .filter(agent => !agent.revokedUtc && agent.status === "Online" &&
      !!agent.lastSeenUtc && Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000)
    .map(agent => agent.id));
  const hasOnlineAgent = onlineAgentIds.size > 0;
  const hasReachablePrinter = printers.some(printer => isPrinterAvailable(printer, onlineAgentIds));
  const hasPaymentMethod = paymentSettings?.cashEnabled === true ||
    (paymentSettings?.upiEnabled === true &&
      !!paymentSettings.upiId?.trim() && !!paymentSettings.payeeName?.trim());
  const setupChecks: { complete: boolean; title: string; detail: string; target: Section }[] = [
    { complete: !!profile?.displayName, title: "Create your shop identity", detail: "Your shop name and customer-facing brand.", target: "Settings" },
    { complete: activePrinters.length > 0, title: "Register your printers", detail: "Add and enable the printers available at your counter.", target: "Printers" },
    { complete: hasOnlineAgent, title: "Connect the shop computer", detail: "The Windows Agent must be paired and recently online.", target: "Print Agent" },
    { complete: hasReachablePrinter, title: "Verify a live printer", detail: "At least one enabled output must be visible to the connected computer.", target: "Printers" },
    { complete: stations.some(station => station.isEnabled), title: "Enable a QR station", detail: "At least one customer-facing QR station must be enabled.", target: "QR Stations" },
    { complete: !!pricing?.isConfigured, title: "Set your print prices", detail: "Configure mono, colour and duplex rates.", target: "Pricing" },
    { complete: hasPaymentMethod, title: "Enable a payment method", detail: "Configure cash at counter or a complete UPI payee profile.", target: "Payments" }
  ];
  return (
    <>
      <div className="stat-grid">
        <Metric icon={<Printer size={18}/>} label="Registered printers" value={String(printers.length)} note={`${activePrinters.length} enabled for use`} accent="blue"/>
        <Metric icon={<QrCode size={18}/>} label="QR stations" value={String(stations.length)} note={`${stations.filter(station=>station.isEnabled).length} currently enabled`} accent="violet"/>
        <Metric icon={<FileText size={18}/>} label="Print jobs today" value={String(jobsToday.length)} note="Real customer uploads" accent="amber"/>
        <Metric icon={<Wallet size={18}/>} label="Quotes awaiting review" value={String(quotesAwaiting.length)} note="Quotes are not collected payments" accent="mint"/>
      </div>
      <div className="dashboard-grid">
        <section className="panel activity-empty-panel">
          <div className="panel-heading"><div><h2>Customer activity</h2><p>Recent uploads and quote requests from your QR stations</p></div><span className="neutral-chip">{printJobs.length} recent jobs</span></div>
          {printJobs.length===0 ? <div className="activity-empty-content"><div className="activity-empty-art"><div className="activity-art-sheet"><FileText size={25}/><i/><i/><i/></div><div className="activity-art-printer"><Printer size={27}/></div><span className="activity-art-spark">✦</span></div><h3>Ready when your first customer is</h3><p>Set your prices and share a QR station. Customer uploads will appear here as soon as they arrive.</p><button className="text-action" onClick={() => onNavigate(printers.length ? "QR Stations" : "Printers")}>{printers.length ? "Open QR stations" : "Add your first printer"} <span>→</span></button></div> :
            <div className="activity-job-list">
              {printJobs.slice(0,4).map(job=><div className="activity-job-row" key={job.id}><div className="activity-job-icon"><FileText size={16}/></div><div className="activity-job-copy"><strong>{job.jobNumber}</strong><span>{job.document.originalFileName} · {job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.photosPerPage??"layout not recorded"} per A4 page · ${job.pageCount} A4 pages` : `${job.pageCount} pages`}</span></div><div className="activity-job-status"><Status label={prettyStatus(job.status)} tone={job.status==="AWAITING_CUSTOMER_APPROVAL"||job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/><small>{formatIstTime(job.createdUtc)}</small></div></div>)}
              <button className="text-action" onClick={()=>onNavigate("Print Queue")}>Open print queue <span>→</span></button>
            </div>}
        </section>
        <section className="panel printer-panel">
          <div className="panel-heading"><div><h2>Printer setup</h2><p>Registered shop devices</p></div><button className="text-action" onClick={() => onNavigate("Printers")}>Manage <span>→</span></button></div>
          <div className="printer-summary"><div className="printer-summary-icon"><Printer size={19}/></div><div><strong>{printers.length} registered</strong><span>Live hardware status awaits the local agent</span></div><div className="health-score neutral-score">—</div></div>
          <div className="device-list">{printers.slice(0,4).map(printer=><Device key={printer.id} name={printer.displayName} detail={`${printer.manufacturer} · ${printer.connectionType}`} state={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/>)}{printers.length===0 && <div className="mini-empty">No printers registered yet.</div>}</div>
          <button className="text-action full-action" onClick={() => onNavigate("Printers")}>View all printers <span>→</span></button>
        </section>
      </div>
      <section className="panel setup-panel">
        <div className="panel-heading"><div><h2>Shop setup checklist</h2><p>Complete these steps before accepting customer print jobs.</p></div><span className="setup-count">{setupChecks.filter(check => check.complete).length} / {setupChecks.length} complete</span></div>
        <div className="setup-checks">
          {setupChecks.map(check => <SetupCheck key={check.title} complete={check.complete} title={check.title} detail={check.detail} onClick={() => onNavigate(check.target)}/>)}
        </div>
      </section>
    </>
  );
}

function PrintersPage({ printers, busyResourceId, onAdd, onToggle }: { printers: PrinterDevice[]; busyResourceId:string|null; onAdd:()=>void; onToggle:(printer:PrinterDevice)=>void }) {
  return (
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Your printers</h2><p>Each Windows queue stays separate. SmartPrint will never merge printers automatically, even when two queues share a port.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Add printer</button></div>
      {printers.length===0 ? <EmptyModule icon={<Printer size={24}/>} title="No printers registered yet" text="Add your first printer. It will appear here as awaiting local-agent discovery until the Windows Print Agent reports its capabilities and live status." actionLabel="Add your first printer" onAction={onAdd}/> :
      <div className="printer-grid">{printers.map(printer=>{
        const possibleDuplicates=printer.outputMode!=="PDF_EXPORT"&&printer.agentDeviceId&&printer.connectionAddress
          ?printers.filter(other=>other.id!==printer.id&&other.outputMode!=="PDF_EXPORT"&&other.agentDeviceId===printer.agentDeviceId&&
            !!other.connectionAddress&&other.connectionAddress.trim().toLowerCase()===printer.connectionAddress!.trim().toLowerCase())
          :[];
        return <article className="printer-card" key={printer.id}>
          <div className="printer-card-top"><div className="printer-card-icon"><Printer size={20}/></div><Status label={printer.status} tone={printer.status.toLowerCase()==="ready"?"green":"slate"}/></div>
          <h3>{printer.outputMode==="PDF_EXPORT"?"PDF export · "+printer.displayName:printer.displayName}</h3>
          <p>{printer.outputMode==="PDF_EXPORT"?"Direct local PDF generation · no Windows Save As dialog":printer.manufacturer+" · "+printer.model}</p>
          <div className="printer-meta">
            <span>{printer.outputMode==="PDF_EXPORT"?"PDF export destination":printer.connectionType}{printer.isDefault?(printer.outputMode==="PDF_EXPORT"?" · Default output destination":" · Default Windows queue"):""}</span>
            <span>{printer.queueName || printer.connectionAddress || "Connection not verified"}</span>
          </div>
          {possibleDuplicates.length>0&&<div className="printer-duplicate-warning" role="note">
            <strong>Possible duplicate printer</strong>
            <span>{possibleDuplicates.map(item=>item.displayName).join(", ")} uses the same Windows port. SmartPrint keeps both queues separate; confirm whether they are alternate queues for one physical printer.</span>
          </div>}
          <div className="printer-card-footer"><span className={printer.isActive?"enabled-label":"disabled-label"}>{printer.isActive?"Enabled":"Disabled"}</span><button className="subtle-button" disabled={!!busyResourceId} onClick={()=>onToggle(printer)}>{busyResourceId===printer.id?<span className="button-spinner"/>:printer.isActive?"Disable":"Enable"}</button></div>
        </article>;
      })}</div>}
    </section>
  );
}

function StationsPage({ stations, profile, busyResourceId, onAdd, onToggle, onGenerate }: {
  stations:QrStation[]; profile:ShopProfile|null; busyResourceId:string|null; onAdd:()=>void; onToggle:(station:QrStation)=>void; onGenerate:(station:QrStation)=>void;
}) {
  const [printingStationId,setPrintingStationId]=useState<string|null>(null);
  useEffect(()=>{
    if(!printingStationId)return;
    let cancelled=false;
    const onAfterPrint=()=>setPrintingStationId(null);
    const printSelectedStation=async()=>{
      const images=Array.from(document.querySelectorAll<HTMLImageElement>(".qr-print-document img"));
      await Promise.all(images.map(async image=>{
        try {
          if(!image.complete) await image.decode();
        } catch {
          // Shop name and the text wordmark remain available if a brand image fails.
        }
      }));
      await document.fonts.ready.catch(()=>undefined);
      await new Promise<void>(resolve=>{
        window.requestAnimationFrame(()=>window.requestAnimationFrame(()=>resolve()));
      });
      if(!cancelled)window.print();
    };
    window.addEventListener("afterprint",onAfterPrint);
    void printSelectedStation();
    return ()=>{
      cancelled=true;
      window.removeEventListener("afterprint",onAfterPrint);
    };
  },[printingStationId]);

  return (
    <>
    <section className="panel collection-panel">
      <div className="collection-header"><div><h2>Customer QR stations</h2><p>Each station has a permanent public link. Every scan creates a separate short-lived customer session.</p></div><button className="primary-button" onClick={onAdd}><Plus size={16}/> Create station</button></div>
      {stations.length===0 ? <EmptyModule icon={<QrCode size={24}/>} title="Your first QR station starts here" text="Create a station for a counter or printer. The QR code identifies the station; every scan gets a separate expiring customer session." actionLabel="Create QR station" onAction={onAdd}/> :
      <div className="station-grid">{stations.map(station=><article id={`qr-station-${station.id}`} className="station-card" key={station.id}><div className="station-card-head"><div className="station-icon"><QrCode size={18}/></div><div className="station-head-copy"><strong>{station.displayName}</strong><span>{station.isEnabled?"Accepting new sessions":"Disabled"}</span></div><Status label={station.isEnabled?"Enabled":"Disabled"} tone={station.isEnabled?"green":"slate"}/></div>
        <div className="station-qr-area">{station.publicUrl ? <div className="qr-paper">
          <div className="qr-shop-brand">{profile?.logoUrl?<img src={profile.logoUrl} alt={profile.displayName+" logo"}/>:<img src="/smartprint-mark.svg" alt="Smart Print"/>}<div><strong>{profile?.displayName||station.displayName}</strong><small>DOCUMENT PRINTING STATION</small></div></div>
          <div className="qr-code-frame"><QRCode value={station.publicUrl} size={172} level="H" fgColor="#101c35" bgColor="#ffffff"/></div>
          <span className="qr-scan-label">SCAN TO PRINT</span>
          <span className="qr-scan-hint">Scan to upload documents and send them to this shop</span>
          <div className="qr-shop-station">{station.displayName}</div>
          <div className="qr-powered"><small>POWERED BY</small><div className="qr-powered-lockup" aria-label="QuantaEdge, Smarter Decisions. Greater Growth."><img className="qr-quantaedge-mark" src="/branding/quantaedge-mark.png" alt="" onError={event=>{event.currentTarget.hidden=true;const fallback=event.currentTarget.nextElementSibling;if(fallback instanceof HTMLElement)fallback.hidden=false;}}/><span className="qr-quantaedge-mark-fallback" hidden aria-hidden="true">Q</span><div className="qr-powered-wordmark"><strong>Quanta<span>Edge</span></strong><small>Smarter Decisions. Greater Growth.</small></div></div></div>
        </div> : <div className="qr-missing"><QrCode size={34}/><strong>QR code not stored</strong><span>This older station needs a one-time QR replacement. Its existing printed code cannot be recovered from the old hash.</span><button className="secondary-button" disabled={!!busyResourceId} onClick={()=>onGenerate(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:"Reissue permanent QR"}</button></div>}</div>
        <div className="station-foot"><span>Code ending ···{station.publicCodeHint}</span><button className="subtle-button" disabled={!!busyResourceId} onClick={()=>onToggle(station)}>{busyResourceId===station.id?<span className="button-spinner"/>:station.isEnabled?"Disable station":"Enable station"}</button></div>
        {station.publicUrl && <div className="station-actions"><button className="secondary-button" onClick={()=>void copyText(station.publicUrl!)}> <Copy size={14}/> Copy link</button><button className="secondary-button" onClick={()=>setPrintingStationId(station.id)}>Print / download QR poster</button></div>}
      </article>)}</div>}
    </section>
    <QrStationPrintDocument station={stations.find(station => station.id === printingStationId) ?? null} profile={profile}/>
    </>
  );
}



function PrintAgentPage({agents,enrollmentCode,busy,onGenerate,onRevoke,desktop,localAgentStatus,localAgentConflict,localAgentRevoked,localAgentNeedsRepair,localAgentEnrollmentIssue,localAgentOwnershipChecking,onConnect,onReconnectAfterRevocation}:{
  agents:AgentDevice[];
  enrollmentCode:{code:string;expiresUtc:string}|null;
  busy:boolean;
  onGenerate:()=>void;
  onRevoke:(agent:AgentDevice)=>void;
  desktop:boolean;
  localAgentStatus:DesktopAgentStatus|null;
  localAgentConflict:boolean;
  localAgentRevoked:boolean;
  localAgentNeedsRepair:boolean;
  localAgentEnrollmentIssue:{code:string|null;message:string}|null;
  localAgentOwnershipChecking:boolean;
  onConnect:()=>void;
  onReconnectAfterRevocation:()=>void;
}) {
  const { release, state: releaseState } = usePublicRelease();
  const windowsInstallerUrl = release?.windowsAvailable ? getPublicDownloadPath("windows") : null;
  const connected = Boolean(localAgentStatus?.paired && agents.some(agent =>
    !agent.revokedUtc && agent.id === localAgentStatus.agentDeviceId
  ));
  return <div className="agent-page">
    <section className="panel agent-pairing-panel">
      {desktop ? <div className="desktop-agent-connect">
        <div className="agent-intro">
          <div className="agent-hero-icon"><Monitor size={22}/></div>
          <div><div className="eyebrow"><span className="eyebrow-line"/>THIS COMPUTER</div><h2>Your print computer</h2><p>SmartPrint connects this computer to your shop automatically. The background print service stays active even when you close this window.</p></div>
        </div>
        <div className="desktop-agent-status">
          <span className={connected ? "desktop-agent-indicator connected" : localAgentConflict ? "desktop-agent-indicator conflict" : "desktop-agent-indicator"} />
          <div><strong>{connected ? "Connected to this shop" : localAgentRevoked ? "Disconnected by the shop owner" : localAgentNeedsRepair ? "Connection needs repair" : localAgentEnrollmentIssue ? (localAgentConflict ? "Existing shop pairing needs revocation" : "Connection needs attention") : localAgentConflict ? "Connected to another shop" : localAgentOwnershipChecking ? "Checking shop registration" : localAgentStatus?.serviceReachable ? "Ready to connect" : "Print service not responding"}</strong>
            <small>{connected ? (localAgentStatus?.localApiReachable === false ? "Connected to your shop. Cloud printing remains available; local-network uploads are reconnecting automatically." : "Printer discovery and job hand-off are managed by the SmartPrint service.") : localAgentRevoked ? "This computer was revoked and will not reconnect automatically. Select Reconnect this computer when your shop is ready; no pairing code is needed." : localAgentNeedsRepair ? "Windows could not read this computer's saved pairing. Check the paired-computer list below; revoke its old entry if it is still active, then select Repair connection. No code entry is needed." : localAgentEnrollmentIssue ? localAgentEnrollmentIssue.message : localAgentConflict ? "This computer is still connected to another shop. Ask its previous owner to disconnect it before connecting it here." : localAgentOwnershipChecking ? "SmartPrint is verifying this computer's saved registration with your shop. It will not transfer or re-enroll it while ownership is uncertain." : localAgentStatus?.serviceReachable ? "Your owner session will authorize a one-time connection. No pairing code is shown." : "SmartPrint is starting or reconnecting its background service. It will retry automatically."}</small>
          </div>
        </div>
        <div className="desktop-agent-actions">
          {localAgentConflict ? (
            <button className="primary-button" disabled={busy} onClick={onReconnectAfterRevocation}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> I’ve had the previous shop revoke this computer</>}
            </button>
          ) : localAgentOwnershipChecking ? (
            <span role="status" aria-live="polite">Checking shop registration…</span>
          ) : localAgentRevoked ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Reconnect this computer</>}
            </button>
          ) : localAgentNeedsRepair ? (
            <button className="primary-button" disabled={busy} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><RotateCcw size={15}/> Repair connection</>}
            </button>
          ) : (
            <button className="primary-button" disabled={busy || connected || Boolean(localAgentStatus?.paired)} onClick={onConnect}>
              {busy ? <span className="button-spinner"/> : <><Monitor size={15}/>{localAgentStatus?.serviceReachable ? "Connect this computer" : "Retry connection"}</>}
            </button>
          )}
          <span>This computer will handle print orders for your shop while connected.</span>
        </div>
      </div> : <>
      <div className="agent-intro">
        <div className="agent-hero-icon"><Monitor size={22}/></div>
        <div><div className="eyebrow"><span className="eyebrow-line"/>LOCAL PRINT CONNECTIVITY</div><h2>Connect your shop computer</h2><p>Pair the Windows computer connected to your printers. SmartPrint will keep it ready to receive customer print orders.</p></div>
      </div>
      <div className="agent-steps">
        <div><span>01</span><strong>Generate a pairing code</strong><small>One-time code, valid for 10 minutes.</small></div>
        <div><span>02</span><strong>Run the guided Windows installer</strong><small>Enter the code in the setup wizard. No PowerShell commands are required.</small></div>
        <div><span>03</span><strong>Confirm the connection</strong><small>Return here and wait for the computer heartbeat and printer discovery.</small></div>
      </div>
      {enrollmentCode&&<div className="agent-code-panel">
        <div><span>ONE-TIME PAIRING CODE</span><strong>{enrollmentCode.code}</strong><small>Expires {formatIstTime(enrollmentCode.expiresUtc)}</small></div>
        <button className="secondary-button" onClick={()=>void copyText(enrollmentCode.code)}><Copy size={14}/> Copy code</button>
      </div>}
      <div className="agent-command-panel">
        <div><span>GUIDED WINDOWS INSTALLER</span><strong>Download and run SmartPrint Windows Setup on the shop computer.</strong><small>Run setup as administrator, enter the one-time pairing code in its wizard, and let it install and start the background service.</small></div>
        {windowsInstallerUrl
          ? <a className="secondary-button" href={windowsInstallerUrl}><Monitor size={14}/> Download Windows installer</a>
          : <span className="subtle-button" aria-disabled="true">{releaseState === "loading" ? "Checking installer availability…" : releaseState === "error" ? "Installer availability temporarily unavailable" : "Windows installer not published"}</span>}
      </div>
      <div className="agent-security-note"><ShieldCheck size={16}/><span>Your setup code works once and expires after 10 minutes. You can disconnect a computer from this page if it is replaced or lost.</span></div>
      <div className="agent-generate-row"><span>Pairing codes are single use. Generating a new one does not revoke already paired computers.</span><button className="primary-button" disabled={busy} onClick={onGenerate}>{busy?<span className="button-spinner"/>:<><Plus size={15}/> Generate pairing code</>}</button></div>
      </>}
    </section>
    {desktop && <ShopConnectionPanel connected={connected} localApiReady={localAgentStatus?.localApiReachable===true}/>}
    {!desktop && <section className="panel local-network-hint"><div className="local-network-mark"><Wifi size={20}/></div><div><h2>Shop connection</h2><p>Open SmartPrint on the Windows computer connected to your printers to choose and test the shop Wi-Fi or hotspot.</p></div></section>}
    <section className="panel agent-device-panel">
      <div className="collection-header"><div><h2>Paired Windows computers</h2><p>Agent connection history and the number of discovered printer queues.</p></div><span className="setup-count">{agents.filter(agent=>!agent.revokedUtc).length} paired</span></div>
      {agents.length===0?<div className="agent-empty"><Monitor size={25}/><strong>No Windows computer paired yet</strong><span>Generate a pairing code above and run the agent on the computer connected to your printers.</span></div>:
        <div className="agent-device-list">{agents.map(agent=><article className="agent-device-card" key={agent.id}>
          <div className="agent-device-icon"><Monitor size={19}/></div>
          <div className="agent-device-copy"><strong>{agent.deviceName}</strong><span>{agent.printerCount} printer {agent.printerCount===1?"queue":"queues"} discovered · Paired {formatIstDate(agent.createdUtc)}</span><small>{agent.lastSeenUtc?("Last contact "+formatIstDateTime(agent.lastSeenUtc)):"No heartbeat received yet"}</small>{agent.localEndpoints?.length?<small>Shop connection found · run the phone check on the shop computer</small>:<small>Connect this computer to the shop Wi-Fi or hotspot to enable phone uploads</small>}</div>
          <Status label={agent.revokedUtc?"Revoked":agent.status} tone={agent.revokedUtc?"red":agent.status==="Online"?"green":"slate"}/>
          {!agent.revokedUtc&&<button className="subtle-button reject-action" disabled={busy} onClick={()=>onRevoke(agent)}>Revoke</button>}
        </article>)}</div>}
    </section>
  </div>;
}

function ShopConnectionPanel({connected,localApiReady}:{connected:boolean;localApiReady:boolean}) {
  const [snapshot,setSnapshot]=useState<DesktopLocalNetworkSnapshot|null>(null);
  const [agentStatus,setAgentStatus]=useState<DesktopAgentStatus|null>(null);
  const [loading,setLoading]=useState(true);
  const [saving,setSaving]=useState(false);
  const [error,setError]=useState("");
  const [test,setTest]=useState<DesktopLocalPhoneTest|null>(null);
  const [verified,setVerified]=useState(false);
  const [expired,setExpired]=useState(false);

  async function refresh() {
    try {
      const [next,status]=await Promise.all([getLocalNetworkSnapshot(),getLocalAgentStatus()]);
      setSnapshot(next);
      setAgentStatus(status);
      setVerified(Boolean(next.phoneVerifiedUtc));
      setError("");
      return next;
    } catch(exception) {
      setError(exception instanceof Error?exception.message:"Could not check the shop connection.");
      return null;
    } finally { setLoading(false); }
  }

  useEffect(()=>{void refresh();},[]);

  useEffect(()=>{
    if(!connected||!localApiReady) {
      setTest(null);
      setVerified(false);
      setExpired(false);
      return;
    }
    void refresh();
  },[connected,localApiReady]);

  useEffect(()=>{
    if(!test) {
      const timer=window.setInterval(()=>void refresh(),30000);
      return ()=>window.clearInterval(timer);
    }
    let active=true;
    let polling=false;
    const started=Date.parse(test.startedUtc);
    const poll=async()=>{
      if(polling||!active)return;
      if(Date.now()>=Date.parse(test.expiresUtc)){
        setExpired(true);
        setTest(null);
        return;
      }
      polling=true;
      try {
        const next=await getLocalNetworkSnapshot();
        if(!active)return;
        setSnapshot(next);
        if(next.phoneVerifiedUtc&&Date.parse(next.phoneVerifiedUtc)>=started){
          setVerified(true);setTest(null);
        }
      }catch{
        // The QR remains visible while the Agent briefly restarts or reconnects.
        // Expiry is checked before the pipe call, so a stopped service cannot
        // leave a disabled test button on screen indefinitely.
      }finally{polling=false;}
    };
    const timer=window.setInterval(()=>void poll(),2000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[test?.startedUtc]);

  async function changeNetwork(interfaceId:string) {
    if(!interfaceId)return;
    setSaving(true);setError("");setTest(null);setVerified(false);setExpired(false);
    try {setSnapshot(await selectLocalNetwork(interfaceId));}
    catch(exception){setError(exception instanceof Error?exception.message:"Could not select this connection.");}
    finally{setSaving(false);}
  }

  async function beginPhoneTest() {
    setSaving(true);setError("");setExpired(false);setVerified(false);
    try {setTest(await startLocalPhoneTest());}
    catch(exception){setError(exception instanceof Error?exception.message:"Could not start the phone check.");}
    finally{setSaving(false);}
  }

  const selected=snapshot?.networks.find(network=>network.id===snapshot.selectedNetworkId)??null;
  const hasSelectedNetwork=Boolean(snapshot?.selectedNetworkId && selected);
  const phoneTestPassed=verified&&localApiReady;
  const readiness=agentStatus?.readiness??null;
  const pipelineReady=readiness?.localUploadReady===true;
  const statusTone=phoneTestPassed&&pipelineReady?"green":hasSelectedNetwork||snapshot?.networks.length?"amber":"slate";
  const statusLabel=loading?"Checking…":error&&!snapshot?"Connection unavailable":connected&&!localApiReady?"Print service starting":localApiReady&&!readiness?"Agent update needed":localApiReady&&!pipelineReady?"Print pipeline recovering":phoneTestPassed?"Phone test passed":hasSelectedNetwork?"Phone check needed":snapshot?.networks.length?"Choose connection":"No connection found";

  return <section className="panel local-network-panel">
    <div className="local-network-heading">
      <div><span className="eyebrow"><span className="eyebrow-line"/>SHOP CONNECTION</span><h2>Customer upload connection</h2><p>Choose the Wi-Fi or hotspot customers will use to send documents.</p></div>
      <Status label={loading?"Checking…":statusLabel} tone={loading?"slate":statusTone}/>
    </div>
    {loading&&!snapshot?<div className="local-network-loading"><span className="button-spinner"/> Checking available connections…</div>:error&&!snapshot
      ? <div className="local-network-empty"><Wifi size={20}/><div><strong>Could not check this computer</strong><p>SmartPrint is not responding yet. Make sure the desktop app is open, then try again.</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>Try again</button></div>
      : <>
      {snapshot?.networks.length?<>
        <label className="local-network-choice">Shop Wi-Fi or hotspot
          <select value={snapshot.selectedNetworkId??""} onChange={event=>void changeNetwork(event.target.value)} disabled={saving||!connected}>
            <option value="">Choose the connection customers use</option>
            {snapshot.networks.map(network=><option key={network.id} value={network.id}>{network.displayName}{network.kind&&network.displayName.toLowerCase()!==network.kind.toLowerCase()?" · "+network.kind:""}</option>)}
          </select>
        </label>
        {selected
          ? <div className="local-network-selected"><Wifi size={17}/><span><strong>{selected.displayName}</strong><small>{selected.kind} · Used for customer document uploads</small></span><Check size={17}/></div>
          : <p className="local-network-copy">Connect this computer to the Wi-Fi or hotspot customers will use, then select it here.</p>}
        {!connected?<p className="local-network-copy">Connect this computer to your shop first. The phone check will be available when setup is complete.</p>:!localApiReady?<p className="local-network-copy">SmartPrint is preparing the shop connection. The phone check will be available when the print service is ready.</p>:<>
          <div className="local-network-actions">
            <button className="primary-button" type="button" onClick={()=>void beginPhoneTest()} disabled={saving||!!test||!hasSelectedNetwork||!localApiReady}>
              {saving?<span className="button-spinner"/>:<><QrCode size={16}/> Check with another phone</>}
            </button>
            <button className="secondary-button" type="button" onClick={()=>void refresh()} disabled={saving}><RotateCcw size={15}/> Refresh</button>
          </div>
          {test&&<div className="local-phone-test">
            <div className="local-phone-qr"><QRCode value={test.phoneTestUrl} size={190} level="M" bgColor="#ffffff" fgColor="#111827"/></div>
            <div><strong>Use a second phone</strong><p>Connect another phone to this same Wi-Fi or hotspot. In SmartPrint, open Your space → connected shop → Check shop Wi-Fi, then scan this QR.</p><small>The phone sharing the hotspot cannot join it. No SmartPrint? Scan with your camera and open the link.</small><p>This screen updates automatically when the phone connects.</p></div>
          </div>}
          {phoneTestPassed&&<div className="local-network-result success"><Check size={17}/><span><strong>Phone test passed</strong><small>A phone reached this shop connection recently. The check expires after ten minutes.</small></span></div>}
          {localApiReady&&readiness?.localUploadReady===true&&<div className="local-network-result success"><Check size={17}/><span><strong>Local print pipeline ready</strong><small>Protected queue, print worker, cloud heartbeat and printer inventory are healthy. The assigned printer is checked separately for each QR station upload.</small></span></div>}
          {localApiReady&&!readiness&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Agent update required</strong><small>This Agent version does not report queue and print-worker readiness. Update the Windows Print Agent to verify local upload readiness.</small></span></div>}
          {localApiReady&&readiness&&!readiness.localUploadReady&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Print pipeline recovering</strong><small>{[
            readiness.queueInitialized?"":"Protected document queue is not ready",
            readiness.printWorkerRunning?"":"Print worker is not running",
            readiness.heartbeatFresh?"":"Cloud heartbeat is stale",
            readiness.printerInventoryFresh?"":"Printer inventory is stale"
          ].filter(Boolean).join(" · ")} {readiness.lastFailureCode?"(Last issue: "+readiness.lastFailureCode+") ":""}Retry after the shop computer/Agent has recovered.</small></span></div>}
          {expired&&<div className="local-network-result warning"><Clock3 size={17}/><span><strong>Phone not connected yet</strong><small>Check that the second phone is on the selected Wi-Fi or hotspot. Some hotspots block devices from communicating; try shop Wi-Fi instead.</small></span></div>}
        </>}
      </>:<div className="local-network-empty"><Wifi size={20}/><div><strong>No shop connection found</strong><p>Connect this computer to the shop Wi-Fi or hotspot, then refresh.</p></div><button className="secondary-button" type="button" onClick={()=>void refresh()}>Refresh</button></div>}
    </>}
    {error&&<div className="local-network-error" role="alert">{error}</div>}
  </section>;
}

function PricingPage({ pricing, onSave, busy }: {
  pricing: ShopPricingProfile;
  onSave: (values: {
    monoPerSideMinor: number;
    colourPerSideMinor: number;
    monoDuplexPerSheetMinor: number;
    colourDuplexPerSheetMinor: number;
    minimumChargeMinor: number;
    photo35x45Minor:number; photo25x35Minor:number; photo2x2Minor:number; photo4x6Minor:number;
  }) => void;
  busy: boolean;
}) {
  const [mono,setMono]=useState((pricing.monoPerSideMinor/100).toFixed(2));
  const [colour,setColour]=useState((pricing.colourPerSideMinor/100).toFixed(2));
  const [monoDuplex,setMonoDuplex]=useState((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
  const [colourDuplex,setColourDuplex]=useState((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
  const [minimum,setMinimum]=useState((pricing.minimumChargeMinor/100).toFixed(2));
  const [photo35x45,setPhoto35x45]=useState((pricing.photo35x45Minor/100).toFixed(2));const [photo25x35,setPhoto25x35]=useState((pricing.photo25x35Minor/100).toFixed(2));const [photo2x2,setPhoto2x2]=useState((pricing.photo2x2Minor/100).toFixed(2));const [photo4x6,setPhoto4x6]=useState((pricing.photo4x6Minor/100).toFixed(2));
  useEffect(()=>{
    setMono((pricing.monoPerSideMinor/100).toFixed(2));
    setColour((pricing.colourPerSideMinor/100).toFixed(2));
    setMonoDuplex((pricing.monoDuplexPerSheetMinor/100).toFixed(2));
    setColourDuplex((pricing.colourDuplexPerSheetMinor/100).toFixed(2));
    setMinimum((pricing.minimumChargeMinor/100).toFixed(2));
    setPhoto35x45((pricing.photo35x45Minor/100).toFixed(2));setPhoto25x35((pricing.photo25x35Minor/100).toFixed(2));setPhoto2x2((pricing.photo2x2Minor/100).toFixed(2));setPhoto4x6((pricing.photo4x6Minor/100).toFixed(2));
  },[pricing]);
  const toMinor=(value:string)=>Math.round(Number(value)*100);
  const valid=[mono,colour,monoDuplex,colourDuplex,minimum,photo35x45,photo25x35,photo2x2,photo4x6].every(value=>value.trim()!==""&&Number.isFinite(Number(value))&&Number(value)>=0);
  return <section className="panel settings-panel pricing-page">
    <div className="settings-intro"><div className="settings-icon"><Wallet size={20}/></div><div><h2>Print rate card</h2><p>Set the prices your customers see before they authorise a job.</p></div><span className={pricing.isConfigured?"pricing-status configured":"pricing-status"}>{pricing.isConfigured?"Active rate card":"Not configured"}</span></div>
    <div className="pricing-disclaimer"><ShieldCheck size={16}/><span>Prices are stored in paise and calculated by the server. Updating this rate card affects new quotes; existing quotes retain their saved price and pricing version.</span></div>
    <form className="settings-form" onSubmit={event=>{
      event.preventDefault();
      if(!valid||toMinor(mono)<=0||toMinor(colour)<=0)return;
      onSave({
        monoPerSideMinor:toMinor(mono),
        colourPerSideMinor:toMinor(colour),
        monoDuplexPerSheetMinor:toMinor(monoDuplex),
        colourDuplexPerSheetMinor:toMinor(colourDuplex),
        minimumChargeMinor:toMinor(minimum),photo35x45Minor:toMinor(photo35x45),photo25x35Minor:toMinor(photo25x35),photo2x2Minor:toMinor(photo2x2),photo4x6Minor:toMinor(photo4x6)
      });
    }}>
      <div className="pricing-group-heading"><strong>Single-sided printing</strong><span>Price per A4 printed side</span></div>
      <div className="pricing-input-grid">
        <label htmlFor="pricing-mono-per-side">Black & white <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0.01" max="1000" step="0.01" aria-label="Black & white" id="pricing-mono-per-side" value={mono} onChange={e=>setMono(e.target.value)} required/></span></label>
        <label htmlFor="pricing-colour-per-side">Colour <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0.01" max="1000" step="0.01" aria-label="Colour" id="pricing-colour-per-side" value={colour} onChange={e=>setColour(e.target.value)} required/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Double-sided printing</strong><span>Price per physical A4 sheet (two sides)</span></div>
      <div className="pricing-input-grid">
        <label htmlFor="pricing-mono-duplex">Black & white duplex <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" max="1000" step="0.01" aria-label="Black & white duplex" id="pricing-mono-duplex" value={monoDuplex} onChange={e=>setMonoDuplex(e.target.value)}/></span></label>
        <label htmlFor="pricing-colour-duplex">Colour duplex <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" max="1000" step="0.01" aria-label="Colour duplex" id="pricing-colour-duplex" value={colourDuplex} onChange={e=>setColourDuplex(e.target.value)}/></span></label>
      </div>
      <div className="pricing-group-heading"><strong>Minimum order</strong><span>Applied if the calculated print total is lower</span></div>
      <label htmlFor="pricing-minimum-charge">Minimum job charge <span className="price-input-wrap minimum-price"><span aria-hidden="true">₹</span><input type="number" min="0" max="100000" step="0.01" aria-label="Minimum job charge" id="pricing-minimum-charge" value={minimum} onChange={e=>setMinimum(e.target.value)}/></span></label>
      <div className="pricing-group-heading"><strong>Photo printing</strong><span>Price per finished photo · ₹0 disables a size</span></div><div className="pricing-input-grid photo-pricing-grid"><label htmlFor="pricing-photo-35x45">35 × 45 mm <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label="35 × 45 mm" id="pricing-photo-35x45" value={photo35x45} onChange={e=>setPhoto35x45(e.target.value)}/></span></label><label htmlFor="pricing-photo-25x35">25 × 35 mm <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label="25 × 35 mm" id="pricing-photo-25x35" value={photo25x35} onChange={e=>setPhoto25x35(e.target.value)}/></span></label><label htmlFor="pricing-photo-2x2">2 × 2 inch <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label="2 × 2 inch" id="pricing-photo-2x2" value={photo2x2} onChange={e=>setPhoto2x2(e.target.value)}/></span></label><label htmlFor="pricing-photo-4x6">4 × 6 inch <span className="price-input-wrap"><span aria-hidden="true">₹</span><input type="number" min="0" step="0.01" aria-label="4 × 6 inch" id="pricing-photo-4x6" value={photo4x6} onChange={e=>setPhoto4x6(e.target.value)}/></span></label></div><div className="settings-summary"><ShieldCheck size={16}/><span>Photo rates are per finished photo, not per A4 sheet. The quote records the rate used.</span></div>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Customer payment gateway setup is separate and is not active yet. This screen configures print quotations only; no money is collected here.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy||!valid||toMinor(mono)<=0||toMinor(colour)<=0}>{busy?<span className="button-spinner"/>:"Save print prices"}</button></div>
    </form>
    <div className="pricing-version-row"><span>Rate card version</span><strong>v{pricing.version}</strong><span>Last updated</span><strong>{formatIstDateTime(pricing.updatedUtc)}</strong></div>
  </section>;
}


function PaymentsPage({settings,payments,refunds,onSave,onVerify,onReject,busy,busyJobId}:{
  settings:ShopPaymentSettings;
  payments:ShopPayment[];
  refunds:ShopRefund[];
  onSave:(values:{upiEnabled:boolean;upiId:string|null;payeeName:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean})=>void;
  onVerify:(payment:ShopPayment)=>void;
  onReject:(payment:ShopPayment)=>void;
  busy:boolean;
  busyJobId:string|null;
}) {
  const [upiEnabled,setUpiEnabled]=useState(settings.upiEnabled);
  const [upiId,setUpiId]=useState(settings.upiId||"");
  const [payeeName,setPayeeName]=useState(settings.payeeName||"");
  const [cashEnabled,setCashEnabled]=useState(settings.cashEnabled);
  const [instantPaymentEnabled,setInstantPaymentEnabled]=useState(settings.instantPaymentEnabled);
  useEffect(()=>{
    setUpiEnabled(settings.upiEnabled);
    setUpiId(settings.upiId||"");
    setPayeeName(settings.payeeName||"");
    setCashEnabled(settings.cashEnabled);
    setInstantPaymentEnabled(settings.instantPaymentEnabled);
  },[settings.upiEnabled,settings.upiId,settings.payeeName,settings.cashEnabled,settings.instantPaymentEnabled]);

  const pending=payments.filter(payment=>payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT");
  return <div className="payments-workspace">
    <section className="panel payment-settings-panel">
      <div className="collection-header"><div><h2>Shop payment methods</h2><p>Customer print payments go directly to your own UPI account or are received at your counter.</p></div><span className="setup-count">{(upiEnabled?1:0)+(cashEnabled?1:0)} enabled</span></div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Wallet size={19}/></div><div><strong>UPI · QR and payment app</strong><small>Configure your shop's own UPI ID. Payments are not auto-verified.</small></div><label className="switch"><input id="payment-upi-enabled" type="checkbox" aria-label="Enable UPI QR and payment app" checked={upiEnabled} onChange={event=>setUpiEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
        {upiEnabled&&<div className="payment-settings-fields">
          <label>Shop UPI ID<input value={upiId} onChange={event=>setUpiId(event.target.value)} placeholder="yourshop@bank"/></label>
          <label>Payee / business name<input value={payeeName} onChange={event=>setPayeeName(event.target.value)} maxLength={120} placeholder="Name shown in customer's UPI app"/></label>
        </div>}
      </div>
      <div className="payment-method-card">
        <div className="payment-method-heading"><div className="payment-method-icon"><Printer size={19}/></div><div><strong>Cash at counter</strong><small>{cashEnabled?"Enabled · customers can pay at the counter.":"Disabled · customers will not see cash at the counter."}</small></div><label className="switch"><input id="payment-cash-enabled" type="checkbox" aria-label="Enable cash at counter" checked={cashEnabled} onChange={event=>setCashEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
      </div>
      <div className="payment-method-card instant-payment-setting">
        <div className="payment-method-heading"><div className="payment-method-icon"><Zap size={19}/></div><div><strong>Instant payment after upload</strong><small>Customers can pay as soon as the rate-card quote is calculated, without a separate shop pre-approval. Once payment starts, file decisions are locked; after payment is verified, refund rejected files before printer handoff.</small></div><label className="switch"><input id="payment-instant-enabled" type="checkbox" aria-label="Enable instant payment after upload" checked={instantPaymentEnabled} onChange={event=>setInstantPaymentEnabled(event.target.checked)}/><span aria-hidden="true"/></label></div>
        <p className="instant-payment-setting-note">Requires your print rate card and at least one payment method. UPI references and cash are still verified by the shop before printing. Leave this off when you want to review and approve every quote before customers can pay.</p>
      </div>
      <div className="payment-security-note"><ShieldCheck size={16}/><span>SmartPrint by QuantaEdge does not receive, hold or route your customer print revenue. Verify every UPI reference against your own bank/UPI app; cash must be physically received. SmartPrint platform subscription charges are billed separately.</span></div>
      <div className="payment-settings-footer"><span>Last updated {formatIstDateTime(settings.updatedUtc)}</span><button className="primary-button" disabled={busy||(!upiEnabled&&!cashEnabled)||(upiEnabled&&(!upiId.trim()||!payeeName.trim()))} onClick={()=>onSave({upiEnabled,upiId:upiEnabled?upiId.trim():null,payeeName:upiEnabled?payeeName.trim():null,cashEnabled,instantPaymentEnabled})}>{busy?<span className="button-spinner"/>:"Save payment methods"}</button></div>
    </section>
    <section className="panel payment-review-panel">
      <div className="collection-header"><div><h2>Payment verification</h2><p>Review UPI references and confirm cash collected before a job becomes payment-authorised.</p></div><span className="setup-count">{pending.length} need review</span></div>
      {payments.length===0?<EmptyModule icon={<Wallet size={24}/>} title="No print payments yet" text="When customers choose UPI or cash for a shop-approved job, the payment attempt will appear here."/>:
      <div className="payment-record-list">{payments.map(payment=><article className="payment-record" key={payment.id}>
        <div className="payment-record-head"><div><strong>{payment.batchPaymentId?(`Batch checkout · ${payment.batchFileCount??1} files`):payment.jobNumber}</strong><small>{payment.documentName}</small></div><div className="payment-record-amount-wrap"><div className="payment-record-amount">{formatMoney(payment.amountMinor,payment.currency)}</div>{(payment.refundedAmountMinor??0)>0&&<small className="payment-record-refund-summary">Refund recorded: {formatMoney(payment.refundedAmountMinor??0,payment.currency)} · Net retained: {formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</small>}</div></div>
        {payment.origin==="SHOP_OWNER"&&<div className="payment-reference-details"><span>Shop-created order recipient</span><strong>{payment.recipientName||"Walk-in"}{payment.recipientPhone?" · "+payment.recipientPhone:""}</strong><small>This photo order was created in the shop workspace, not uploaded through a customer QR session.</small></div>}
        <div className="payment-record-meta"><span>{payment.method}</span><span>{formatIstDateTime(payment.createdUtc)}</span><Status label={payment.status.replaceAll("_"," ")} tone={payment.status==="VERIFIED"?"green":payment.status==="REJECTED"?"red":payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT"?"amber":"slate"}/></div>
        {payment.method==="UPI"&&<div className="payment-reference-details"><span>Transaction reference</span><strong>{payment.paymentReference||"Customer has not submitted a reference"}</strong><small>Payee snapshot: {payment.payeeNameSnapshot||"Not recorded"} · {payment.upiIdSnapshot||"No UPI ID snapshot"}</small></div>}
        {payment.rejectionReason&&<div className="payment-rejection-note">Reason: {payment.rejectionReason}</div>}
        {(payment.status==="AWAITING_CUSTOMER_PAYMENT"||payment.status==="CUSTOMER_REPORTED"||payment.status==="CASH_AWAITING_PAYMENT")&&<div className="payment-review-actions">{payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>Cancel attempt</button>}{payment.status==="CASH_AWAITING_PAYMENT"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Cancel batch cash attempt":"Cancel cash attempt"}</button>}{payment.status==="CUSTOMER_REPORTED"&&<button className="secondary-button reject-action" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onReject(payment)}>{payment.batchPaymentId?"Reject batch report":"Reject report"}</button>}<button className="primary-button" disabled={busy||busyJobId===payment.printJobId} onClick={()=>onVerify(payment)}>{busyJobId===payment.printJobId?<span className="button-spinner"/>:payment.method==="UPI"?(payment.batchPaymentId?"Verify full batch in bank app":"Verify in bank app"):(payment.batchPaymentId?"Confirm full batch cash":"Confirm cash received")}</button></div>}
        {payment.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Verified {payment.verifiedUtc?formatIstDateTime(payment.verifiedUtc):"by shop"}</div>}
      </article>)}</div>}
    </section>
    <section className="panel refund-audit-panel">
      <div className="collection-header"><div><h2>Refund audit log</h2><p>Every shopkeeper-recorded file refund, with its exact amount, return method, reason and operator.</p></div><span className="setup-count">{refunds.length} recorded</span></div>
      {refunds.length===0?<EmptyModule icon={<RotateCcw size={24}/>} title="No refunds recorded" text="When a shopkeeper rejects a paid file and records its return, the audit entry will appear here."/>:
      <div className="payment-record-list">{refunds.map(refund=><article className="payment-record refund-record" key={refund.id}>
        <div className="payment-record-head"><div><strong>{refund.jobNumber}</strong><small>{refund.documentName}</small></div><div className="payment-record-amount">{formatMoney(refund.amountMinor,refund.currency)}</div></div>
        <div className="payment-record-meta"><span>{refund.method} refund</span><span>{formatIstDateTime(refund.refundedUtc)}</span><Status label="Recorded as sent" tone="green"/></div>
        <div className="refund-record-details"><span>Shopkeeper</span><strong>{refund.refundedByName}</strong><span>Reason</span><strong>{refund.reason}</strong>{refund.reference&&<><span>Refund reference</span><strong>{refund.reference}</strong></>}</div>
      </article>)}</div>}
    </section>
  </div>;
}

function PrintQueuePage({ jobs, printers, agentDevices, searchQuery, loadingBatchId, onCreatePhotoOrder, onSearch, onPreview, onPreviewOutput, onApprove, onReject, onRefund, onReviewBatch, onHistory, onAssignPrinter, onUpdateSettings, onDispatch, onConfirmOutput, onRetryPdfExport, onRefresh, onLoadOlder, hasMoreJobs, loadingOlderJobs, busyJobId }: {
  jobs: ShopPrintJob[];
  printers: PrinterDevice[];
  agentDevices: AgentDevice[];
  searchQuery: string;
  loadingBatchId: string | null;
  onCreatePhotoOrder:()=>void;
  onSearch: (query: string) => Promise<void>;
  onPreview: (job: ShopPrintJob) => void;
  onPreviewOutput: (job: ShopPrintJob) => void;
  onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void;
  onRefund: (job: ShopPrintJob) => void;
  onReviewBatch: (batchId: string) => void;
  onHistory: (job: ShopPrintJob) => void;
  onAssignPrinter: (job: ShopPrintJob, printerDeviceId: string) => void;
  onUpdateSettings: (job:ShopPrintJob,settings:CustomerPrintRequestSettings) => Promise<void> | void;
  onDispatch: (job: ShopPrintJob) => void;
  onConfirmOutput: (job: ShopPrintJob) => void;
  onRetryPdfExport: (job: ShopPrintJob) => void;
  onRefresh: () => Promise<void> | void;
  onLoadOlder: () => Promise<void> | void;
  hasMoreJobs: boolean;
  loadingOlderJobs: boolean;
  busyJobId: string | null;
}) {
  const [autoRefresh,setAutoRefresh]=useState(true);
  const [refreshSeconds,setRefreshSeconds]=useState(10);
  const [searchInput,setSearchInput]=useState(searchQuery);
  const [expandedBatchKeys,setExpandedBatchKeys]=useState<Set<string>>(()=>new Set());
  const [editingSettingsJobId,setEditingSettingsJobId]=useState<string|null>(null);
  const [editingPrintSettings,setEditingPrintSettings]=useState<CustomerPrintRequestSettings|null>(null);
  useEffect(()=>setSearchInput(searchQuery),[searchQuery]);
  const refreshRunning=useRef(false);
  const refreshCallback=useRef(onRefresh);
  refreshCallback.current=onRefresh;

  async function refreshQueue() {
    if(refreshRunning.current)return;
    refreshRunning.current=true;
    try { await refreshCallback.current(); }
    finally { refreshRunning.current=false; }
  }

  useEffect(()=>{
    if(!autoRefresh)return;
    const timer=window.setInterval(()=>void refreshQueue(),Math.max(5,refreshSeconds)*1000);
    return ()=>window.clearInterval(timer);
  },[autoRefresh,refreshSeconds]);

  const reviewableBatchFileStatuses=new Set(["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED","REJECTED"]);
  const reviewBatches=Array.from(new Map(jobs.filter(job=>job.batchId&&reviewableBatchFileStatuses.has(job.status)).map(job=>[job.batchId!,job])).keys());
  const onlineAgentIds = new Set(agentDevices.filter(agent =>
    !agent.revokedUtc && agent.status === "Online" && !!agent.lastSeenUtc &&
    Date.parse(agent.lastSeenUtc) > Date.now() - 2 * 60 * 1000
  ).map(agent => agent.id));
  const dispatchablePrinters = printers.filter(printer =>
    isPrinterAvailable(printer, onlineAgentIds)
  );
  const batchMap=new Map<string,{key:string;batchId:string|null;jobs:ShopPrintJob[]}>();
  for(const job of jobs) {
    const key=job.batchId??`single:${job.id}`;
    const group=batchMap.get(key)??{key,batchId:job.batchId??null,jobs:[]};
    group.jobs.push(job);
    batchMap.set(key,group);
  }
  const queueBatches=Array.from(batchMap.values());

  return <section className="panel queue-panel live-queue-panel">
    <div className="collection-header"><div><h2>Print jobs</h2><p>Customer QR uploads and shop-created photo orders use the same review, payment and printing queue.</p></div><div className="queue-header-actions"><span className="setup-count">{queueBatches.length} batches shown</span><button className="primary-button" type="button" onClick={onCreatePhotoOrder}><ImageIcon size={14}/> New photo order</button><form className="queue-search-form" onSubmit={event=>{event.preventDefault();void onSearch(searchInput);}}><input aria-label="Search all print jobs" type="search" maxLength={120} placeholder="Job, customer, file or printer" value={searchInput} onChange={event=>setSearchInput(event.target.value)}/><button className="secondary-button" type="submit"><Search size={13}/> Search</button></form><button className="secondary-button queue-refresh-button" onClick={()=>void refreshQueue()} disabled={refreshRunning.current}><Activity size={13}/> Refresh queue</button></div></div>
    <div className="queue-refresh-controls">
      <label className="queue-auto-toggle"><input type="checkbox" checked={autoRefresh} onChange={event=>setAutoRefresh(event.target.checked)}/><span className="queue-toggle-track"/><strong>Auto refresh</strong><small>{autoRefresh?"On":"Off"}</small></label>
      <label className="queue-refresh-interval">Refresh every <input type="number" min={5} max={3600} step={1} value={refreshSeconds} onChange={event=>setRefreshSeconds(Math.max(5,Math.min(3600,Number(event.target.value)||5)))}/> seconds</label>
      <span className="queue-refresh-note">{autoRefresh?"Queue refreshes automatically while this page is open.":"Automatic refresh is off. Use Refresh queue whenever needed."}</span>
    </div>
    {reviewBatches.length>0&&<div className="batch-review-strip"><div><strong>{reviewBatches.length} {reviewBatches.length===1?"batch":"batches"} need review</strong><span>Review includes every file in the batch. New files start accepted; rejected files stay excluded until restored.</span></div>{reviewBatches.map(batchId=><button key={batchId} className="secondary-button" disabled={loadingBatchId===batchId||!!loadingBatchId} onClick={()=>onReviewBatch(batchId)}>{loadingBatchId===batchId?<><span className="button-spinner"/>Loading full batch…</>:<>Review batch · {jobs.filter(job=>job.batchId===batchId&&reviewableBatchFileStatuses.has(job.status)).length} shown</>}</button>)}</div>}
    {jobs.length===0 ? <EmptyModule icon={<FileText size={24}/>} title="Your queue is clear" text="When a customer uploads a document through one of your QR stations, the job and its current state will appear here."/> :
      <div className="table-scroll queue-table-scroll"><table className="queue-jobs-table"><thead><tr><th>JOB</th><th>DOCUMENT</th><th>PRINT SETTINGS</th><th>QUOTE</th><th>PRINTER / LOCAL QUEUE</th><th>STATUS</th><th>CREATED</th><th>ACTIONS</th></tr></thead><tbody>
        {queueBatches.map(batch=>{
          const first=batch.jobs[0];
          const photoJobs=batch.jobs.filter(job=>job.printKind==="PHOTO");
          const documentJobs=batch.jobs.filter(job=>job.printKind!=="PHOTO");
          const waiting=batch.jobs.filter(job=>reviewableBatchFileStatuses.has(job.status));
          const folders=Array.from(new Set(batch.jobs.map(job=>job.batchGroupName??"Loose files")));
          const fileCountLabel=`${batch.jobs.length} ${batch.jobs.length===1?"file":"files"}`;
          const folderGroupLabel=`${folders.length} ${folders.length===1?"folder group":"folder groups"}`;
          const statusLabels=Array.from(new Set(batch.jobs.map(job=>job.status)));
          const payableJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const totalRequestedPages=payableJobs.reduce((sum,job)=>sum+job.pageCount*job.copies,0);
          const monoRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?job.pageCount*job.copies:0),0);
          const colourRequestedPages=payableJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?job.pageCount*job.copies:0),0);
          const totalAmount=payableJobs.reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
          const activeBatchJobs=batch.jobs.filter(job=>job.status!=="REJECTED"&&job.status!=="REFUNDED");
          const localStorageJobs=activeBatchJobs.filter(job=>job.storageMode==="LOCAL_AGENT");
          const localAgentIds=Array.from(new Set(localStorageJobs.map(job=>job.documentAgentDeviceId).filter((id):id is string=>Boolean(id))));
          const hasUnidentifiedLocalStorage=localStorageJobs.some(job=>!job.documentAgentDeviceId);
          const requiredLocalAgentId=localAgentIds.length===1&&!hasUnidentifiedLocalStorage?localAgentIds[0]:null;
          const localComputerName=localStorageJobs.find(job=>job.documentAgentDeviceName)?.documentAgentDeviceName;
          const assignedPrinterIds=Array.from(new Set(activeBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchHasUnassignedFiles=activeBatchJobs.some(job=>!job.printerDeviceId);
          const lockedBatchJobs=activeBatchJobs.filter(job=>
            ["PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"].includes(job.status) ||
            (job.localQueueReceivedUtc!=null && job.status!=="LOCAL_QUEUE_READY") ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!!job.agentLeaseExpiresUtc&&Date.parse(job.agentLeaseExpiresUtc)>Date.now())
          );
          const lockedPrinterIds=Array.from(new Set(lockedBatchJobs.map(job=>job.printerDeviceId).filter((id):id is string=>Boolean(id))));
          const batchPrinterConflict=lockedPrinterIds.length>1;
          const lockedBatchPrinter=lockedPrinterIds.length===1?printers.find(printer=>printer.id===lockedPrinterIds[0]):undefined;
          const multipleOrUnknownLocalComputers=localAgentIds.length>1||hasUnidentifiedLocalStorage;
          const lockedPrinterOnWrongComputer=Boolean(requiredLocalAgentId&&lockedBatchPrinter&&lockedBatchPrinter.agentDeviceId!==requiredLocalAgentId);
          const localComputerIdentityConflict=multipleOrUnknownLocalComputers||lockedPrinterOnWrongComputer;
          const batchAssignmentIncomplete=assignedPrinterIds.length!==1||batchHasUnassignedFiles;
          const currentBatchPrinterId=!batchAssignmentIncomplete&&assignedPrinterIds.length===1?assignedPrinterIds[0]:"";
          const batchPrinter=printers.find(printer=>printer.id===currentBatchPrinterId);
          const batchPrinterDisplayName=batchPrinter?.outputMode==="PDF_EXPORT"?"Save as PDF":batchPrinter?.displayName||activeBatchJobs.find(job=>job.printerDeviceId===currentBatchPrinterId)?.printerName||"Assigned printer";
          const assignmentJob=batch.jobs.find(job=>
            job.status==="PAID_AUTHORISED" ||
            (job.status==="LOCAL_QUEUE_CLAIMED"&&!job.localQueueReceivedUtc&&
              (!job.agentLeaseExpiresUtc||Date.parse(job.agentLeaseExpiresUtc)<=Date.now())) ||
            (job.status==="LOCAL_QUEUE_READY"&&!!job.localQueueReceivedUtc&&!job.spoolerJobId&&!job.spoolerJobName)
          );
          const availableBatchPrinters=localComputerIdentityConflict
            ?[]
            :lockedPrinterIds.length===1
              ?dispatchablePrinters.filter(printer=>printer.id===lockedPrinterIds[0])
              :requiredLocalAgentId
                ?dispatchablePrinters.filter(printer=>printer.agentDeviceId===requiredLocalAgentId)
                :dispatchablePrinters;
          const expanded=expandedBatchKeys.has(batch.key);
          const customerBatchName=batch.jobs.find(job=>job.batchName?.trim())?.batchName?.trim()??null;
          const allPdfsSaved = batch.jobs.length > 0 && batch.jobs.every(job => !!verifiedPdfExportPath(job));
          const singleSavedPdfPath = batch.jobs.length === 1 ? verifiedPdfExportPath(first) : null;
          const label=statusLabels.length===1?(allPdfsSaved?"PDF saved":prettyStatus(statusLabels[0])):`${statusLabels.length} states`;
          const tone=statusLabels.every(status=>status==="COMPLETED"||status==="LOCAL_QUEUE_READY")?"green":statusLabels.some(status=>reviewableBatchFileStatuses.has(status)||status==="AWAITING_CUSTOMER_APPROVAL"||status==="OUTPUT_UNCONFIRMED")?"amber":"blue";
          return <Fragment key={batch.key}>
            <tr className="queue-batch-summary-row">
              <td><strong className="job-id">{customerBatchName??(batch.batchId?"Batch · "+first.jobNumber:first.jobNumber)}</strong><small className="batch-summary-sub">{batch.batchId?"First job · "+first.jobNumber+" · "+fileCountLabel:fileCountLabel}</small><div className="queue-print-kind-summary">{photoJobs.length>0&&<PrintKindTag printKind="PHOTO" count={photoJobs.length}/>}{documentJobs.length>0&&<PrintKindTag printKind="DOCUMENT" count={documentJobs.length}/>}</div></td>
              <td><div className="customer-cell"><strong>{folderGroupLabel}</strong><span>{folders.slice(0,3).join(" · ")}{folders.length>3?" · +"+(folders.length-3):""}</span></div></td>
              <td><div className="detail-cell"><strong>{totalRequestedPages} pages requested</strong><span>{monoRequestedPages} B&W · {colourRequestedPages} Colour</span></div></td>
              <td className="amount-cell">{totalAmount>0?formatMoney(totalAmount,first.currency):"Quote pending"}</td>
              <td><div className="queue-printer-cell">
                {singleSavedPdfPath ? <>
                  <strong>PDF saved</strong>
                  <small className="queue-pdf-path">{singleSavedPdfPath}</small>
                </> : <>
                  <strong>{batchPrinterConflict?"Multiple printer assignments":batchAssignmentIncomplete?(assignedPrinterIds.length?"Choose one printer":"Printer not assigned"):batchPrinterDisplayName}</strong>
                  <small>{lockedPrinterIds.length===1?"Locked to the printer already used for this batch":waiting.length===0?"No files awaiting review":`${waiting.length} ${waiting.length===1?"file":"files"} awaiting review`}</small>
                  {batchPrinter?.outputMode==="PDF_EXPORT"&&<small>Saves a PDF, not paper</small>}
                </>}
                {assignmentJob&&<select
                  aria-label="Assign one printer for all files in batch"
                  value={currentBatchPrinterId}
                  disabled={!!busyJobId||batchPrinterConflict||availableBatchPrinters.length===0}
                  onChange={event=>onAssignPrinter(assignmentJob,event.target.value)}>
                  <option value="">{availableBatchPrinters.length===0?"No available printer":"Choose one printer for this batch"}</option>
                  {availableBatchPrinters.map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save as PDF · ":""}{printer.displayName} · {agentDevices.find(agent=>agent.id===printer.agentDeviceId)?.deviceName||"Shop computer"} · {printer.status}</option>)}
                </select>}
                {assignmentJob&&<small className="queue-batch-assignment-note">One printer is used for all active files.</small>}
                {requiredLocalAgentId&&!localComputerIdentityConflict&&<small className="queue-batch-assignment-note"><span>Stored on {localComputerName||"the original shop computer"}.</span> <InfoTooltip label="Shop computer storage details"><span>Files stay on this computer. Reconnect it to use its printers.</span><span>To switch computers, upload a new Cloud-assisted batch.</span></InfoTooltip></small>}
                {multipleOrUnknownLocalComputers&&<small className="queue-batch-printer-lock">Files are stored on different or unknown computers. Do not start output. Check for existing output, then upload all files in a new Cloud-assisted batch.</small>}
                {lockedPrinterOnWrongComputer&&<small className="queue-batch-printer-lock">Files are on {localComputerName||"the original shop computer"}, but this printer is on another computer. Stop; do not retry or switch printers. Check existing output before resubmitting.</small>}
                {batchPrinterConflict&&<small className="queue-batch-printer-lock">This batch uses multiple printers. New output is blocked. Review the affected files.</small>}
              </div></td>
              <td><Status label={label} tone={tone}/></td>
              <td className="time-cell">{formatIstDateTime(first.createdUtc)}</td>
              <td><div className="queue-actions">
                {waiting.length>0&&batch.batchId&&<button className="subtle-button approve-action" disabled={busyJobId==="batch-review"||!!loadingBatchId} onClick={()=>onReviewBatch(batch.batchId!)}>{loadingBatchId===batch.batchId?"Loading…":"Preview & review"}</button>}
                <button className="subtle-button" onClick={()=>setExpandedBatchKeys(current=>{const next=new Set(current);if(next.has(batch.key))next.delete(batch.key);else next.add(batch.key);return next;})}>{expanded?"Hide files":"Files"}</button>
              </div></td>
            </tr>
            {expanded&&<tr className="queue-batch-detail-row"><td colSpan={8}><div className="queue-batch-files">{batch.jobs.map(job=><article className="queue-batch-file-card" key={job.id}>
              <div className="queue-batch-file-main">
                <div><div className="queue-batch-file-title"><strong>{job.document.originalFileName}</strong>{job.privatePrinting&&<span className="private-printing-tag" title="The paired shop computer erases the private working copy after physical output is confirmed. The original file on the customer device is not deleted.">Private · auto-delete</span>}<PrintKindTag printKind={job.printKind}/>{job.origin==="SHOP_OWNER"&&<span className="print-kind-tag print-kind-tag--photo">Shop-created</span>}</div><small>{job.origin==="SHOP_OWNER"?`Recipient: ${job.recipientName||"Walk-in"}${job.recipientPhone?" · "+job.recipientPhone:""} · Shop-originated order`:"Customer QR order"} · {job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.pageCount} A4 pages` : `${job.batchGroupName??"Loose files"} · ${job.pageCount} pages × ${job.copies} copies = ${job.pageCount*job.copies} pages requested · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"} · ${job.paperSize||"A4"} · ${job.orientation??"AUTO"}`}</small></div>
                <Status label={prettyStatus(job.status)} tone={job.status==="REJECTED"?"red":job.status==="COMPLETED"||job.status==="LOCAL_QUEUE_READY"?"green":job.status==="AWAITING_SHOP_APPROVAL"?"amber":"blue"}/>
                <strong className="amount-cell">{job.quotedAmountMinor>0?formatMoney(job.quotedAmountMinor,job.currency):"—"}</strong>
              </div>
              {job.printerName&&<div className="queue-batch-file-printer">
                <Printer size={13}/>
                <strong>{job.outputMode==="PDF_EXPORT"?"Save as PDF":job.outputConfirmedUtc||job.status==="COMPLETED"?"Printed on":job.spoolerJobId?"Sent to":"Assigned printer"}</strong>
                <span>{job.outputMode==="PDF_EXPORT"?job.printerAgentName||"Shop computer":job.printerName}</span>
                {job.outputMode==="PDF_EXPORT"&&<small>Virtual printer: {job.printerName}</small>}
                {job.queueName&&job.queueName!==job.printerName&&<small>Windows queue: {job.queueName}</small>}
                {job.printerAgentName&&job.outputMode!=="PDF_EXPORT"&&<small>Shop computer: {job.printerAgentName}</small>}
                {job.printerSnapshotUtc&&<small>Output destination recorded: {formatIstDateTime(job.printerSnapshotUtc)}</small>}
                {job.outputConfirmedUtc&&job.outputType!=="PDF_EXPORT"&&<small>Paper output manually confirmed by {job.outputConfirmedByName||"shop operator"} at {formatIstDateTime(job.outputConfirmedUtc)}</small>}
              </div>}
              {!job.printerName&&job.storageMode==="LOCAL_AGENT"&&job.documentAgentDeviceName&&<div className="queue-batch-file-printer"><Printer size={13}/><strong>Stored on</strong><span>{job.documentAgentDeviceName}</span></div>}
              {batch.jobs.length > 1 && verifiedPdfExportPath(job) && <div className="queue-batch-file-output">
                <span>Saved PDF location</span>
                <code>{verifiedPdfExportPath(job)}</code>
              </div>}
              {job.status==="LOCAL_QUEUE_READY"&&job.failureCode&&<div className="printer-duplicate-warning" role="status">
                 <strong>Printing paused before submission</strong>
                 <span>{printBlockMessage(job.failureCode)} The original document and requested settings remain unchanged. Reassign this batch to a compatible printer, or resolve the issue with the customer before changing any setting. A settings change must be sent as a revised quote and explicitly approved before output.</span>
               </div>}
               <div className="queue-batch-file-actions">
                {job.printKind==="DOCUMENT"&&["AWAITING_CONFIGURATION","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","QUOTE_EXPIRED","PAYMENT_REJECTED"].includes(job.status)&&<button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>{
                  setEditingSettingsJobId(job.id);
                  setEditingPrintSettings({
                    copies:job.copies,
                    colourMode:job.colourMode==="Colour"?"Colour":"Mono",
                    paperSize:(["A4","A3","A5","LETTER","LEGAL"].includes(job.paperSize)?job.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"],
                    isDuplex:job.isDuplex,
                    orientation:(["AUTO","PORTRAIT","LANDSCAPE"].includes(job.orientation??"AUTO")?job.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"]
                  });
                }}>Adjust options</button>}
                <button className="subtle-button" disabled={busyJobId===job.id||job.privatePrinting} onClick={()=>onPreview(job)}>{job.privatePrinting?"Private source · preview disabled":"Preview original"}</button>
                {job.status==="COMPLETED"&&job.outputType==="PDF_EXPORT"&&verifiedPdfExportPath(job)&&job.outputFileName&&job.outputSha256?.length===64&&(job.outputSizeBytes??0)>0&&(job.outputSizeBytes??0)<=100*1024*1024&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onPreviewOutput(job)}>Preview saved PDF</button>}
                <button className="subtle-button" disabled={busyJobId===job.id} onClick={()=>onHistory(job)}>History</button>
                {job.status==="AWAITING_SHOP_APPROVAL"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onApprove(job)}>Accept</button>}
                {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onReject(job)}>Reject file</button>}
                {(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="subtle-button reject-action" disabled={busyJobId===job.id} onClick={()=>onRefund(job)}>{job.status==="REJECTED"?"Refund rejected file":"Review pages / refund"}</button>}
                {job.status==="LOCAL_QUEUE_READY"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onDispatch(job)}>{job.outputMode==="PDF_EXPORT"?"Save PDF":"Start printing"}</button>}
                {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button className="subtle-button approve-action" disabled={busyJobId===job.id} onClick={()=>onRetryPdfExport(job)}>Save PDF again</button>}
                {(job.status==="SPOOLER_ACCEPTED"||job.status==="PRINTING"||(job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode!=="PDF_EXPORT"))&&<button className="subtle-button approve-action" disabled={busyJobId===job.id||(job.status!=="OUTPUT_UNCONFIRMED"&&!!spoolerStatusWarning(job.spoolerStatus,job.printerStatus))} onClick={()=>onConfirmOutput(job)}>Confirm output</button>}
              </div>
              {editingSettingsJobId===job.id&&editingPrintSettings&&<div className="queue-settings-editor">
                <div><strong>Review print options</strong><small>The revised quote must be approved by the customer before payment or printing.</small></div>
                <label>Colour <select value={editingPrintSettings.colourMode} onChange={event=>{
                  const colourMode=event.currentTarget.value as CustomerPrintRequestSettings["colourMode"];
                  setEditingPrintSettings(current=>current?{...current,colourMode}:current);
                }}><option value="Mono">B&W</option><option value="Colour">Colour</option></select></label>
                <label>Sides <select value={editingPrintSettings.isDuplex?"DOUBLE":"SINGLE"} onChange={event=>{
                  const isDuplex=event.currentTarget.value==="DOUBLE";
                  setEditingPrintSettings(current=>current?{...current,isDuplex}:current);
                }}><option value="SINGLE">Single-sided</option><option value="DOUBLE">Double-sided</option></select></label>
                <label>Copies <input type="number" min={1} max={100} value={editingPrintSettings.copies} onChange={event=>{
                  const copies=Math.max(1,Math.min(100,Number(event.currentTarget.value)||1));
                  setEditingPrintSettings(current=>current?{...current,copies}:current);
                }}/></label>
                <label>Paper size <select value={editingPrintSettings.paperSize} onChange={event=>{
                  const paperSize=event.currentTarget.value as CustomerPrintRequestSettings["paperSize"];
                  setEditingPrintSettings(current=>current?{...current,paperSize}:current);
                }}><option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option></select></label>
                <label>Orientation <select value={editingPrintSettings.orientation} onChange={event=>{
                  const orientation=event.currentTarget.value as CustomerPrintRequestSettings["orientation"];
                  setEditingPrintSettings(current=>current?{...current,orientation}:current);
                }}><option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option></select></label>
                <div className="queue-settings-actions">
                  <button className="secondary-button" type="button" onClick={()=>{setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>Cancel</button>
                  <button className="primary-button" type="button" disabled={busyJobId===job.id||!editingPrintSettings} onClick={()=>{if(editingPrintSettings)void onUpdateSettings(job,editingPrintSettings);setEditingSettingsJobId(null);setEditingPrintSettings(null);}}>Send revised quote</button>
                </div>
              </div>}
            </article>)}</div></td></tr>}
          </Fragment>;
        })}
      </tbody></table></div>}
    {hasMoreJobs && <div className="queue-load-older"><button className="secondary-button" type="button" onClick={() => void onLoadOlder()} disabled={loadingOlderJobs}>{loadingOlderJobs ? <span className="button-spinner"/> : <ArrowRight size={14}/>} Load older jobs</button></div>}
  </section>;
}



function BatchReviewModal({jobs,busy,decisions,retrying,onDecision,onClose,onPreview,onApply}:{
  jobs:ShopPrintJob[];
  busy:boolean;
  decisions:Record<string,boolean>;
  retrying:boolean;
  onDecision:(jobId:string,accepted:boolean)=>void;
  onClose:()=>void;
  onPreview:(job:ShopPrintJob)=>void;
  onApply:(acceptedJobIds:string[],pdfBrandingEnabled:boolean)=>void;
}) {
  const hasIdDocument=jobs.some(job=>job.printKind==="ID_DOCUMENT");
  const [pdfBrandingEnabled,setPdfBrandingEnabled]=useState(()=>!hasIdDocument&&jobs.some(job=>job.pdfBrandingEnabled===true));
  const acceptedIds=jobs.filter(job=>decisions[job.id]??true).map(job=>job.id);
  const acceptedCount=acceptedIds.length;
  const rejectedCount=jobs.length-acceptedCount;
  const acceptedJobs=jobs.filter(job=>decisions[job.id]??true);
  const quoteExpired=(job:ShopPrintJob)=>!job.quoteExpiresUtc||
    !Number.isFinite(Date.parse(job.quoteExpiresUtc))||Date.parse(job.quoteExpiresUtc)<=Date.now();
  const quoteRefreshRequiredCount=acceptedJobs.filter(quoteExpired).length;
  const acceptedTotal=acceptedJobs.filter(job=>!quoteExpired(job))
    .reduce((sum,job)=>sum+Math.max(0,job.quotedAmountMinor),0);
  const acceptedRequestedPages=acceptedJobs.reduce((sum,job)=>sum+job.pageCount*job.copies,0);
  const acceptedMonoPages=acceptedJobs.reduce((sum,job)=>sum+(job.colourMode==="Mono"?job.pageCount*job.copies:0),0);
  const acceptedColourPages=acceptedJobs.reduce((sum,job)=>sum+(job.colourMode==="Colour"?job.pageCount*job.copies:0),0);
  const folders=Array.from(new Set(jobs.map(job=>job.batchGroupName??"Loose files")));
  return <Modal title={retrying?"Retry batch review":"Review batch"} subtitle={retrying?"Retry review. Rejected files stay excluded until restored.":"Files start accepted. Uncheck a file to exclude it; recheck it to restore it."} onClose={onClose}>
    <div className="batch-review-list">{folders.map(folderName=><section className="batch-review-folder" key={folderName}>
      <div className="batch-review-folder-head"><FolderOpen size={15}/><strong>{folderName}</strong><small>{jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).length} files</small></div>
      {jobs.filter(job=>(job.batchGroupName??"Loose files")===folderName).map((job,index)=>{
        const accepted=decisions[job.id]??true;
        const needsFreshQuote=accepted&&quoteExpired(job);
        return <div className={accepted?"batch-review-row accepted":"batch-review-row rejected"} key={job.id}>
          <label className="batch-review-checkbox"><input type="checkbox" checked={accepted} disabled={busy} onChange={event=>onDecision(job.id,event.target.checked)}/></label>
          <span className="batch-review-index">{index+1}</span>
          <div className="batch-review-file"><div className="queue-batch-file-title"><strong>{job.document.originalFileName}</strong>{job.privatePrinting&&<span className="private-printing-tag">Private · auto-delete</span>}<PrintKindTag printKind={job.printKind}/></div><small>{job.printKind==="PHOTO" ? `${job.photoCount??0} photos · ${job.photoSizeCode??""} · ${job.pageCount} A4 pages` : `${job.pageCount} pages × ${job.copies} copies = ${job.pageCount*job.copies} pages requested · ${job.colourMode} · ${job.isDuplex?"Double-sided":"Single-sided"}`} · {needsFreshQuote?"Previous quote expired · fresh quote required":formatMoney(job.quotedAmountMinor,job.currency)}</small><Status label={!accepted?"Marked for rejection":needsFreshQuote?"Accepted · re-quote needed":"Accepted"} tone={!accepted?"red":needsFreshQuote?"amber":"green"}/></div>
          <button className="subtle-button" type="button" disabled={busy||job.privatePrinting} onClick={()=>onPreview(job)}>{job.privatePrinting?"Private source · preview disabled":"Preview"}</button>
        </div>;
      })}
    </section>)}</div>
    <div className="batch-branding-setting">
      <div className="batch-branding-copy">
        <strong>SmartPrint PDF footer <InfoTooltip label="PDF footer details"><span>Footer only appears on PDFs with a verified blank bottom margin.</span><span>Digitally signed or interactive PDFs remain unchanged.</span><span>Original uploads and physical prints are never altered.</span><span>Image-to-PDF exports currently remain unbranded.</span><span>Branding off preserves a single-copy full-document PDF byte-for-byte.</span><span>Multi-copy or reformatted exports may be flattened.</span></InfoTooltip></strong>
        <small>Add a footer only to eligible PDFs with clear bottom margins. New batches start off.</small>
      </div>
      <label className="batch-branding-toggle">
        <input type="checkbox" checked={pdfBrandingEnabled&&!hasIdDocument} disabled={busy||hasIdDocument} onChange={event=>setPdfBrandingEnabled(event.currentTarget.checked)} aria-label="Add SmartPrint footer to PDF exports in this batch"/>
        <span>{hasIdDocument?"Unavailable for ID card sheets":pdfBrandingEnabled?"On for this batch":"Off · default"}</span>
      </label>
    </div>
    <div className="batch-review-summary"><span><strong>{acceptedCount}</strong> accepted</span><span><strong>{rejectedCount}</strong> rejected</span><span><strong>{acceptedRequestedPages}</strong> pages requested</span><span><strong>{acceptedMonoPages}</strong> B&W · <strong>{acceptedColourPages}</strong> Colour</span><span><strong>{formatMoney(acceptedTotal,jobs[0]?.currency??"INR")}</strong> accepted value with valid quotes</span>{quoteRefreshRequiredCount>0&&<span><strong>{quoteRefreshRequiredCount}</strong> need a fresh quote</span>}<small>Page count = source pages × copies. Refresh expired quotes before checkout. Reject files before checkout; after payment starts, use the file-level refund action.</small></div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose} disabled={busy}>Keep reviewing</button><button className="primary-button" disabled={busy||jobs.length===0} onClick={()=>onApply(acceptedIds,pdfBrandingEnabled&&!hasIdDocument)}>{busy?<span className="button-spinner"/>:"Apply review"}</button></div>
  </Modal>;
}

function JobHistoryModal({job,events,onClose}:{job:ShopPrintJob;events:ShopJobEvent[];onClose:()=>void}) {
  return <Modal title={job.jobNumber} subtitle={`Audit history · ${job.document.originalFileName}`} onClose={onClose}>
    <div className="shop-history-list">{events.length===0?<p>No recorded events yet.</p>:events.map(event=><div key={event.id}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"Customer":"System"} · {formatIstDateTime(event.createdUtc)}</small>{event.reasonCode&&<small>{event.reasonCode.replaceAll("_"," ").toLowerCase()}</small>}</span></div>)}</div>
    <div className="modal-actions"><button className="secondary-button" onClick={onClose}>Close history</button></div>
  </Modal>;
}

function DocumentPreviewModal({name,url,mediaType,job,readOnly=false,isSavedOutput=false,focusEditOnMount=false,onClose,onApprove,onReject,onRefund,onEditPhoto}:{
  name:string;url:string;mediaType:string;job:ShopPrintJob;readOnly?:boolean;isSavedOutput?:boolean;focusEditOnMount?:boolean;onClose:()=>void;onApprove:()=>void;onReject:()=>void;onRefund:()=>void;onEditPhoto?:()=>void;
}) {
  const editPhotoButtonRef=useRef<HTMLButtonElement|null>(null);
  const isPdf=mediaType==="application/pdf";
  const isImage=mediaType==="image/jpeg"||mediaType==="image/png";
  useEffect(()=>{
    if(focusEditOnMount)editPhotoButtonRef.current?.focus();
  },[focusEditOnMount]);
  useEffect(()=>{
    if(!isDesktopApp()||(!isImage&&job.printKind!=="PHOTO"))return;
    void setDesktopContentProtection(true);
    return ()=>{void setDesktopContentProtection(false);};
  },[isImage,job.printKind]);
  const photoDimensions:Record<string,string>={"35x45":"35 × 45 mm","25x35":"25 × 35 mm","2x2":"50.8 × 50.8 mm","4x6":"101.6 × 152.4 mm"};
  const previewSummary = isSavedOutput
    ? "Final PDF export saved by this shop. This is the actual output file; the uploaded original remains separate and unchanged."
    : job.printKind==="PHOTO"
      ? `${job.photoCount??0} ${photoDimensions[job.photoSizeCode??""]??job.photoSizeCode??""} photos · ${job.photosPerPage??"Layout density not recorded"} per A4 page · ${job.pageCount} A4 PDF page(s). Preview displays the complete prepared file.`
      : `${job.pageCount} source page(s) × ${job.copies} copy/copies = ${job.pageCount*job.copies} printed page(s). Preview displays the uploaded original once.`;
  const subtitle = isSavedOutput ? "Saved PDF output · Read-only preview" : readOnly ? `Read-only preview · ${previewSummary}` : job.batchId ? `File review · Batch ${job.batchId.slice(0,8).toUpperCase()} · ${previewSummary}` : previewSummary;
  return <Modal title={name} subtitle={subtitle} onClose={onClose}>
    <div className="shop-document-preview" onContextMenu={event=>{if(isImage||job.printKind==="PHOTO")event.preventDefault();}} onDragStart={event=>{if(isImage||job.printKind==="PHOTO")event.preventDefault();}}>
      {isPdf&&job.printKind==="PHOTO"?<><iframe src={url} title={name}/><span className="shop-photo-preview-watermark" aria-hidden="true">SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW</span></>:isPdf?<iframe src={url} title={name}/>:isImage?<><img src={url} alt={name} draggable={false}/><span className="shop-photo-preview-watermark" aria-hidden="true">SMARTPRINT · PRIVATE SHOP PREVIEW</span></>:<div className="shop-document-preview-unavailable">Preview format is not supported. <a href={url} download={name}>Download the original file</a></div>}
    </div>
    {onEditPhoto&&<div className="file-review-note"><ImageIcon size={16}/><span>Open the same Photo Studio used by customers to crop, improve the background, add form details and prepare this photo for shop printing.</span><button ref={editPhotoButtonRef} className="secondary-button" onClick={onEditPhoto}>Enhance photo for printing</button></div>}
    {!readOnly&&isImage&&job.status==="AWAITING_SHOP_APPROVAL"&&job.storageMode==="LOCAL_AGENT"&&<div className="file-review-note"><ShieldCheck size={16}/><span>This original is stored only on the paired shop computer. It remains local; this version does not copy it to cloud storage for editing.</span></div>}
    {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<div className="file-review-note"><Check size={16}/><span>{readOnly ? "This preview is part of the batch review. Keep or change this file's decision in the batch panel after closing the preview." : "Review this file independently. Accepting or rejecting it will not change any other file in the batch."}</span></div>}
    {job.status==="PAID_AUTHORISED"&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&<div className="file-refund-note"><Wallet size={16}/><span>Paid amount: <strong>{formatMoney(job.payment.amountMinor,job.payment.currency)}</strong>. A refund can be recorded while this file has not yet been handed to the shop computer.</span></div>}
    <div className="modal-actions">
      {!readOnly&&job.status==="AWAITING_SHOP_APPROVAL"&&<button className="primary-button" onClick={onApprove}><Check size={15}/> Accept this file</button>}
      {!readOnly&&["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&<button className="secondary-button reject-action" onClick={onReject}><X size={15}/> Reject this file</button>}
      {!readOnly&&(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&(job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId&&<button className="secondary-button reject-action" onClick={onRefund}><RotateCcw size={15}/> {job.status==="REJECTED"?"Refund rejected file":"Review pages & refund"}</button>}
      <button className="secondary-button" onClick={onClose}>Close preview</button>
    </div>
  </Modal>;
}

function RefundModal({job,token,busy,onClose,onSubmit}:{
  job:ShopPrintJob; token:string; busy:boolean; onClose:()=>void;
  onSubmit:(values:{refundMethod:"UPI"|"CASH";refundReference:string|null;reason:string;rejectedPageNumbers:number[];idempotencyKey:string})=>void;
}) {
  const [rejectedPages,setRejectedPages]=useState<Set<number>>(()=>{
    if(job.status==="REJECTED")return new Set<number>(Array.from({length:job.pageCount},(_,index)=>index+1));
    try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}
  });
  const [estimate,setEstimate]=useState<Awaited<ReturnType<typeof previewShopPrintRefund>>|null>(null);
  const [estimating,setEstimating]=useState(false);
  const [estimateError,setEstimateError]=useState("");
  const [method,setMethod]=useState<"UPI"|"CASH">(job.payment?.method==="CASH"?"CASH":"UPI");
  const [reference,setReference]=useState("");
  const [reason,setReason]=useState(job.status==="REJECTED"?"Shop rejected the entire file after preview.":"Customer rejected selected pages after shop preview.");
  const [confirmed,setConfirmed]=useState(false);
  const refundRequest=useRef<{signature:string;key:string}|null>(null);
  const selectedPages=Array.from(rejectedPages).sort((a,b)=>a-b);
  const selectionKey=selectedPages.join(",");
  const alreadyRejected=(()=>{try{return new Set<number>(JSON.parse(job.rejectedPageNumbersJson||"[]") as number[]);}catch{return new Set<number>();}})();

  useEffect(()=>{
    let active=true;
    setEstimating(true);setEstimateError("");setEstimate(null);
    void previewShopPrintRefund(token,job.id,selectedPages).then(value=>{if(active)setEstimate(value);})
      .catch(error=>{if(active)setEstimateError(error instanceof Error?error.message:"Could not calculate the refund.");})
      .finally(()=>{if(active)setEstimating(false);});
    return ()=>{active=false;};
  },[token,job.id,selectionKey]);

  const additional=estimate?.additionalRefundAmountMinor??0;
  const hasNewPages=selectedPages.some(page=>!alreadyRejected.has(page));
  const canSubmit=!!estimate&&hasNewPages&&reason.trim().length>=3&&confirmed&&
    (method==="CASH"||reference.trim().length>=4);
  const togglePage=(page:number,checked:boolean)=>{
    if(alreadyRejected.has(page))return;
    setRejectedPages(current=>{const next=new Set(current);if(checked)next.add(page);else next.delete(page);return next;});
  };

  return <Modal title={job.status==="REJECTED"?"Refund rejected file":"Review pages and refund"} subtitle={job.status==="REJECTED"?"All pages are selected because the shop rejected this entire file. Confirm the full-file refund before handoff.":"Every page starts accepted. Tick only pages to reject; selections can be undone before recording. Already refunded pages stay excluded."} onClose={onClose}>
    <div className="refund-summary-card">
      <div><span>FILE</span><strong>{job.document.originalFileName}</strong></div>
      <div><span>VERIFIED PAYMENT</span><strong>{formatMoney(job.payment?.amountMinor||job.quotedAmountMinor,job.payment?.currency||job.currency)}</strong></div>
    </div>
    <div className="refund-selection-actions">
      <span>Reject specific pages or the entire file.</span>
      <button type="button" className="secondary-button reject-action" disabled={busy} onClick={()=>setRejectedPages(new Set(Array.from({length:job.pageCount},(_,index)=>index+1)))}>Reject entire file · refund all pages</button>
    </div>
    <div className="page-refund-grid" role="group" aria-label="Pages to reject">
      {Array.from({length:job.pageCount},(_,index)=>index+1).map(page=>{
        const rejected=rejectedPages.has(page), locked=alreadyRejected.has(page);
        return <label className={locked?"page-refund-chip already-rejected":rejected?"page-refund-chip selected":"page-refund-chip"} key={page}>
          <input type="checkbox" checked={rejected} disabled={locked||busy} onChange={event=>togglePage(page,event.target.checked)}/>
          <span>Page {page}{locked?" · refunded":rejected?" · reject":""}</span>
        </label>;
      })}
    </div>
    <div className="page-refund-estimate">
      {estimating?<span>Calculating the exact refund from the original quote…</span>:
       estimateError?<span className="inline-error">{estimateError}</span>:
       estimate?<><span>{estimate.acceptedPageCount} pages will remain in the print job.</span><strong>Return {formatMoney(additional,estimate.currency)}</strong><small>Cumulative returned: {formatMoney(estimate.cumulativeRefundAmountMinor,estimate.currency)} · remaining payment: {formatMoney(estimate.remainingPaidAmountMinor,estimate.currency)}</small></>:<span>Select page(s) to see the refund amount.</span>}
    </div>
    {estimate&&additional>0&&<form className="refund-form" onSubmit={event=>{
      event.preventDefault();
      if(!canSubmit)return;
      const payload={refundMethod:method,refundReference:method==="UPI"?reference.trim():null,reason:reason.trim(),rejectedPageNumbers:selectedPages};
      const signature=JSON.stringify(payload);
      if(refundRequest.current?.signature!==signature)refundRequest.current={signature,key:crypto.randomUUID()};
      onSubmit({...payload,idempotencyKey:refundRequest.current.key});
    }}>
      <label>Refund returned by<select value={method} onChange={event=>setMethod(event.target.value as "UPI"|"CASH")}><option value="UPI">UPI transfer</option><option value="CASH">Cash returned at counter</option></select></label>
      {method==="UPI"&&<label>Refund transaction reference<input value={reference} onChange={event=>setReference(event.target.value)} maxLength={100} placeholder="Enter the refund UPI reference" required/></label>}
      <label>Reason for rejecting these pages<textarea rows={3} maxLength={300} value={reason} onChange={event=>setReason(event.target.value)} required/></label>
      <label className="refund-confirm-line"><input type="checkbox" checked={confirmed} onChange={event=>setConfirmed(event.target.checked)}/><span>I confirm that I have already returned the exact additional amount shown above. SmartPrint records the refund; it does not send money automatically.</span></label>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||estimating||!canSubmit}>{busy?<span className="button-spinner"/>:"Record refund and reject selected pages"}</button></div>
    </form>}
    {estimate&&additional===0&&<div className="refund-form"><p>Select at least one additional page that produces a positive refund from the original quote. No page is rejected until a refund can be recorded.</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Close</button></div></div>}
  </Modal>;
}

function rejectedPageList(json?:string) {
  try {
    const pages=JSON.parse(json||"[]");
    return Array.isArray(pages)&&pages.length>0?pages.join(", "):"selected pages";
  } catch {
    return "selected pages";
  }
}

function spoolerStatusWarning(status?: string | null, printerStatus?: string | null) {
  const spooler = (status || "").toUpperCase();
  const printer = (printerStatus || "").toUpperCase();
  if (spooler === "OFFLINE" || printer === "OFFLINE")
    return "Printer is offline. Reconnect it; Windows may resume this same queued job. Do not start a duplicate.";
  if (spooler === "PAPEROUT")
    return "The printer reports no paper. Refill it and check the same Windows queue before continuing.";
  if (spooler === "PAUSED" || printer === "PAUSED")
    return "The printer or Windows print queue is paused. Resume the existing job in Windows; do not submit another copy.";
  if (spooler === "PRINTER_UNAVAILABLE")
    return "The Agent cannot currently open this printer queue. Reconnect or restore the queue, then refresh this job.";
  if (spooler === "ERROR")
    return "Windows reports a printer error. Resolve it in the printer queue and inspect any pages already produced.";
  if (spooler === "USERINTERVENTION")
    return "The printer requires attention. Resolve the prompt at the device before checking the same queued job.";
  return "";
}
function prettyStatus(status:string) {
  const names:Record<string,string>={
    AWAITING_CONFIGURATION:"Configure options",
    AWAITING_CUSTOMER_APPROVAL:"Quote awaiting customer",
    QUOTE_EXPIRED:"Quote expired",
    AWAITING_SHOP_APPROVAL:"Awaiting shop approval",
    REJECTED:"Rejected",
    REFUNDED:"Fully refunded",
    PARTIALLY_REFUNDED:"Partially refunded",

    UPLOADED:"Uploaded",
    VALIDATING:"Validating",
    ANALYSING:"Analysing",
    AWAITING_PAYMENT:"Awaiting payment",
    AWAITING_COUNTER_PAYMENT:"Pay at counter",
    AUTHORISED:"Authorised",
    PAID_AUTHORISED:"Paid · authorised",
    LOCAL_QUEUE_CLAIMED:"Transferring locally",
    LOCAL_QUEUE_READY:"Local copy secured",
    PRINT_AUTHORISED:"Ready to print",
    PRINT_SUBMITTING:"Sending to printer",
    SPOOLER_ACCEPTED:"Accepted by spooler",
    PRINTING:"Printing",
    OUTPUT_UNCONFIRMED:"Output unconfirmed",
    PDF_EXPORTING:"Creating PDF",
    PDF_EXPORTED:"PDF saved",
    QUEUED:"Queued",
    SUBMITTING_TO_SPOOLER:"Sending to printer",
    COMPLETED:"Completed",
    FAILED:"Failed",
    CANCELLED:"Cancelled"
  };
  return names[status] || status.replaceAll("_"," ").toLowerCase().replace(/\\b\\w/g,letter=>letter.toUpperCase());
}
function printBlockMessage(code:string):string {
  switch(code) {
    case "PAPER_SIZE_UNSUPPORTED":
    case "PAPER_SIZE_REJECTED":
      return "The selected printer did not confirm the requested paper size.";
    case "LANDSCAPE_UNSUPPORTED":
    case "ORIENTATION_UNCONFIRMED":
      return "The selected printer did not confirm the requested page orientation.";
    case "COLOUR_UNSUPPORTED":
    case "COLOUR_REJECTED":
      return "The selected printer did not confirm colour output.";
    case "DUPLEX_UNSUPPORTED":
    case "DUPLEX_REJECTED":
      return "The selected printer did not confirm double-sided output.";
    default:
      return "The Windows Agent could not confirm the requested settings with this output device.";
  }
}

function isPrinterAvailable(printer: PrinterDevice, onlineAgentIds: ReadonlySet<string>, now = Date.now()) {
  if (!printer.isActive || !printer.agentDeviceId || !onlineAgentIds.has(printer.agentDeviceId)) return false;
  if (printer.outputMode === "PDF_EXPORT") return true;
  const lastSeen = printer.lastSeenUtc ? Date.parse(printer.lastSeenUtc) : Number.NaN;
  return Number.isFinite(lastSeen) && lastSeen > now - 2 * 60 * 1000 &&
    (printer.status === "Ready" || printer.status === "Busy");
}

function formatMoney(minor:number,currency:string) {
  return new Intl.NumberFormat("en-IN",{style:"currency",currency,minimumFractionDigits:2}).format(minor/100);
}
function formatBytes(bytes:number) {
  return bytes<1024*1024?`${(bytes/1024).toFixed(0)} KB`:`${(bytes/(1024*1024)).toFixed(2)} MB`;
}

function SettingsPage({ profile, onSave, busy }: { profile:ShopProfile; onSave:(v:{displayName:string;logoUrl:string|null;accentColor:string})=>void; busy:boolean }) {
  const [displayName,setDisplayName]=useState(profile.displayName);
  const [logoUrl,setLogoUrl]=useState(profile.logoUrl || "");
  const [accentColor,setAccentColor]=useState(profile.accentColor || "#7662E8");
  useEffect(()=>{setDisplayName(profile.displayName);setLogoUrl(profile.logoUrl||"");setAccentColor(profile.accentColor||"#7662E8");},[profile]);
  return <section className="panel settings-panel"><div className="settings-intro"><div className="settings-icon"><Settings size={20}/></div><div><h2>Shop profile &amp; branding</h2><p>Update the shop name and brand customers see when they connect through your QR stations.</p></div></div>
    <form className="settings-form" onSubmit={event=>{event.preventDefault();onSave({displayName:displayName.trim(),logoUrl:logoUrl.trim()||null,accentColor});}}>
      <label>Customer-facing shop name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} required maxLength={180}/></label>
      <label>Logo URL <span className="field-hint">Optional · secure HTTPS image URL</span><input type="url" value={logoUrl} onChange={e=>setLogoUrl(e.target.value)} placeholder="https://example.com/logo.png"/></label>
      <label>Brand accent colour<div className="colour-picker-row"><input type="color" value={accentColor} onChange={e=>setAccentColor(e.target.value)}/><input value={accentColor} onChange={e=>setAccentColor(e.target.value)} pattern="^#[0-9A-Fa-f]{6}$" required/></div></label>
      <div className="settings-summary"><ShieldCheck size={16}/><span>Standard Smart Print branding is active. Full white-label branding is a paid plan upgrade.</span></div>
      <div className="settings-actions"><button className="primary-button" disabled={busy}>{busy?<span className="button-spinner"/>:"Save shop identity"}</button></div>
    </form>
    <div className="settings-meta"><span>Shop slug</span><strong>{profile.slug}</strong><span>Currency</span><strong>{profile.currency}</strong><span>Time zone</span><strong>{profile.timeZone}</strong></div>
  </section>;
}



function ShopOwnerAccountModal({ profile, busy, onClose, onSave }: {
  profile: ShopProfile;
  busy: boolean;
  onClose: () => void;
  onSave: (values: { ownerName: string; email: string; currentPassword?: string }) => Promise<void>;
}) {
  const [ownerName, setOwnerName] = useState(profile.ownerName);
  const [email, setEmail] = useState(profile.ownerEmail);
  const [currentPassword, setCurrentPassword] = useState("");
  const [saveError, setSaveError] = useState("");
  const emailChanged = email.trim().toLowerCase() !== profile.ownerEmail.trim().toLowerCase();
  return (
    <Modal title="Personal details" subtitle="Update the name and sign-in email for your shop owner account." onClose={onClose}>
      <form className="modal-form account-edit-form" onSubmit={async event => {
        event.preventDefault();
        setSaveError("");
        try {
          await onSave({
            ownerName: ownerName.trim(),
            email: email.trim(),
            currentPassword: emailChanged ? currentPassword : undefined
          });
          onClose();
        } catch (exception) {
          setSaveError(exception instanceof Error ? exception.message : "Could not update your personal details.");
        }
      }}>
        <label>Your name
          <input value={ownerName} onChange={event => setOwnerName(event.target.value)}
            autoComplete="name" maxLength={160} required/>
        </label>
        <label>Sign-in email
          <input type="email" value={email}
            onChange={event => setEmail(event.currentTarget.value)}
            onInput={event => setEmail(event.currentTarget.value)}
            autoComplete="email" maxLength={256} required/>
        </label>
        {emailChanged && <>
          <div className="modal-note account-email-change-note">
            <ShieldCheck size={16}/>
            <span>This address becomes your sign-in email immediately. Make sure it is correct. Your current password is required to confirm the change.</span>
          </div>
          <label>Current password
            <input type="password" value={currentPassword}
              onChange={event => setCurrentPassword(event.target.value)}
              autoComplete="current-password" required/>
          </label>
        </>}
        {saveError && <div className="inline-error account-edit-error" role="alert">{saveError}</div>}
        <div className="modal-actions">
          <button type="button" className="secondary-button" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="primary-button" disabled={busy || !ownerName.trim() || !email.trim() || (emailChanged && !currentPassword)}>
            {busy ? <span className="button-spinner"/> : "Save personal details"}
          </button>
        </div>
      </form>
    </Modal>
  );
}

function HelpSupportModal({ onClose, onNavigate }: { onClose: () => void; onNavigate: (section: Section) => void }) {
  const topics: { section: Section; title: string; description: string; icon: typeof FileText }[] = [
    { section: "Print Queue", title: "Print jobs and approvals", description: "Review incoming jobs, decisions, print status and refunds.", icon: FileText },
    { section: "Printers", title: "Printers and connections", description: "Check printer setup, availability and device details.", icon: Printer },
    { section: "Print Agent", title: "Shop computer and Print Agent", description: "Pair or reconnect the Windows computer that runs printing.", icon: Monitor },
    { section: "QR Stations", title: "QR stations", description: "Manage counter QR codes and their assigned printers.", icon: QrCode },
    { section: "Pricing", title: "Pricing", description: "Review the rates customers see before they submit a job.", icon: Wallet }
  ];

  return (
    <Modal title="Help & support" subtitle="Choose the part of your shop workspace you need help with." onClose={onClose}>
      <div className="help-topic-list">
        {topics.map(({ section, title, description, icon: Icon }) => (
          <button type="button" className="help-topic" key={section} onClick={() => onNavigate(section)}>
            <span className="help-topic-icon"><Icon size={17}/></span>
            <span className="help-topic-copy"><strong>{title}</strong><small>{description}</small></span>
            <ArrowRight size={16}/>
          </button>
        ))}
      </div>
      <p className="help-modal-note">These shortcuts open the existing shop tools. They do not change your settings or print jobs.</p>
    </Modal>
  );
}

function PrinterModal({ busy, onClose, onSubmit }: {
  busy:boolean; onClose:()=>void; onSubmit:(v:{displayName:string;manufacturer:string;model:string;connectionType:string;connectionAddress?:string;queueName?:string})=>void;
}) {
  const [displayName,setDisplayName]=useState("");
  const [manufacturer,setManufacturer]=useState("HP");
  const [model,setModel]=useState("");
  const [connectionType,setConnectionType]=useState("WindowsQueue");
  const [connectionAddress,setConnectionAddress]=useState("");
  const [queueName,setQueueName]=useState("");
  return <Modal title="Add a printer" subtitle="Register its identity first. Live discovery will be handled by the local Print Agent." onClose={onClose}>
    <form className="modal-form" onSubmit={e=>{e.preventDefault();onSubmit({displayName,manufacturer,model,connectionType,connectionAddress:connectionAddress||undefined,queueName:queueName||undefined});}}>
      <label>Display name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} placeholder="e.g. Front counter printer" required maxLength={160}/></label>
      <div className="form-two-col"><label>Manufacturer<select value={manufacturer} onChange={e=>setManufacturer(e.target.value)}>{["HP","Canon","Ricoh","Epson","Brother","Kyocera","Xerox","Other"].map(item=><option key={item}>{item}</option>)}</select></label><label>Model<input value={model} onChange={e=>setModel(e.target.value)} placeholder="Model name" required maxLength={120}/></label></div>
      <label>Connection type<select value={connectionType} onChange={e=>setConnectionType(e.target.value)}><option value="WindowsQueue">Windows installed queue</option><option value="USB">USB</option><option value="LAN">LAN / Network</option><option value="IPP">IPP</option></select></label>
      {(connectionType==="LAN"||connectionType==="IPP")&&<label>Network address<input value={connectionAddress} onChange={e=>setConnectionAddress(e.target.value)} placeholder="192.168.1.100 or ipp://printer/ipp/print"/></label>}
      {connectionType==="WindowsQueue"&&<label>Windows queue name <span className="field-hint">Optional until the agent discovers installed queues</span><input value={queueName} onChange={e=>setQueueName(e.target.value)} placeholder="Exact Windows printer queue name"/></label>}
      <div className="modal-note"><ShieldCheck size={15}/> The printer will be listed as Unknown until a trusted local agent confirms its status and capabilities.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||!displayName.trim()||!model.trim()}>{busy?<span className="button-spinner"/>:"Add printer"}</button></div>
    </form>
  </Modal>;
}

function StationModal({ busy, printers, onClose, onSubmit }: {
  busy:boolean; printers:PrinterDevice[]; onClose:()=>void; onSubmit:(v:{displayName:string;printerDeviceId?:string})=>void;
}) {
  const [displayName,setDisplayName]=useState("Front counter");
  const [printerDeviceId,setPrinterDeviceId]=useState("");
  return <Modal title="Create a QR station" subtitle="Give customers a clear entry point for this counter or printer." onClose={onClose}>
    <form className="modal-form" onSubmit={e=>{e.preventDefault();onSubmit({displayName,printerDeviceId:printerDeviceId||undefined});}}>
      <label>Station name<input value={displayName} onChange={e=>setDisplayName(e.target.value)} placeholder="e.g. Front counter" required maxLength={120}/></label>
      <label>Default printer <span className="field-hint">Optional · can be changed before a job is printed</span><select value={printerDeviceId} onChange={e=>setPrinterDeviceId(e.target.value)}><option value="">No default printer</option>{printers.filter(printer=>printer.isActive).map(printer=><option key={printer.id} value={printer.id}>{printer.displayName} · {printer.manufacturer}</option>)}</select></label>
      <div className="modal-note"><QrCode size={15}/> The QR contains a public station identifier, not a permanent print authorisation. Every scan creates an expiring customer session. Only enabled printers are offered as defaults; you can also assign a printer to each paid job in the queue.</div>
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>Cancel</button><button className="primary-button" disabled={busy||!displayName.trim()}>{busy?<span className="button-spinner"/>:"Create station"}</button></div>
    </form>
  </Modal>;
}

function Modal({title,subtitle,children,onClose}:{title:string;subtitle:string;children:ReactNode;onClose:()=>void}) {
  return <div className="modal-scrim" onMouseDown={e=>{if(e.target===e.currentTarget)onClose();}}><section className="modal-card" role="dialog" aria-modal="true" aria-label={title}><div className="modal-heading"><div><h2>{title}</h2><p>{subtitle}</p></div><button className="icon-button" onClick={onClose} aria-label="Close"><X size={18}/></button></div>{children}</section></div>;
}

function EmptyModule({icon,title,text,actionLabel,onAction}:{icon:ReactNode;title:string;text:string;actionLabel?:string;onAction?:()=>void}) {
  return <section className="panel empty-module"><div className="empty-module-icon">{icon}</div><h2>{title}</h2><p>{text}</p>{actionLabel&&onAction&&<button className="primary-button" onClick={onAction}>{actionLabel}<ArrowRight size={15}/></button>}</section>;
}

function SetupCheck({complete,title,detail,onClick}:{complete:boolean;title:string;detail:string;onClick:()=>void}) {
  return <button className="setup-check" onClick={onClick}><span className={complete?"check-circle complete":"check-circle"}>{complete?<Check size={14}/>:null}</span><span className="setup-check-copy"><strong>{title}</strong><small>{detail}</small></span><ArrowRight size={16}/></button>;
}
function Metric({icon,label,value,note,accent}:{icon:ReactNode;label:string;value:string;note:string;accent:string}) {
  return <section className="metric-card"><div className="metric-top"><div className={"metric-icon "+accent}>{icon}</div><span className="metric-dots"><i/><i/><i/></span></div><div className="metric-label">{label}</div><div className="metric-value">{value}</div><div className="metric-foot"><span>{note}</span></div></section>;
}
function Device({name,detail,state,tone}:{name:string;detail:string;state:string;tone:string}) {
  return <div className="device-row"><div className="device-glyph"><Printer size={16}/></div><div className="device-copy"><strong>{name}</strong><span>{detail}</span></div><Status label={state} tone={tone}/></div>;
}
function Status({label,tone}:{label:string;tone:string}) { return <span className={"status-badge "+tone}><i/>{label}</span>; }
function PrintKindTag({printKind,count}:{printKind?:string|null;count?:number}) {
  const isPhoto=printKind==="PHOTO";
  const isIdDocument=printKind==="ID_DOCUMENT";
  const label=isPhoto?"Photo":isIdDocument?"ID card":"Document";
  const countLabel=typeof count==="number"&&count>1?` · ${count}`:"";
  return <span className={`print-kind-tag print-kind-tag--${isPhoto?"photo":"document"}`} title={isPhoto?"Photo printing job":isIdDocument?"Private ID card print job":"Document printing job"}>{label}{countLabel}</span>;
}
function initials(value:string) { return value.trim().split(/\s+/).slice(0,2).map(part=>part[0]?.toUpperCase()||"").join("") || "SP"; }
function SubscriptionPage({ summary, token, onUpdated }: { summary: ShopSubscriptionSummary; token: string; onUpdated: (summary: ShopSubscriptionSummary) => void }) {
  const { subscription, plan, entitlements, usage } = summary;
  const status = subscription.status.toLowerCase();
  const statusTone = status === "active" ? "tone-success" : status === "trial" ? "tone-warning" : "tone-neutral";
  const planName = plan?.name ?? (subscription.planCode === "TRIAL" ? "Trial plan" : subscription.planCode);
  const periodStart = formatIstDate(subscription.periodStartUtc);
  const periodEnd = formatIstDate(subscription.periodEndUtc);
  const price = (amount: number | null | undefined, currency: string) =>
    amount == null ? "Not configured" : `${currency} ${(amount / 100).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  return (
    <section className="subscription-workspace">
      <div className="subscription-hero-grid">
        <section className="panel subscription-current-panel">
          <div className="subscription-panel-top">
            <span className="subscription-eyebrow">CURRENT SUBSCRIPTION</span>
            <span className={`subscription-status ${statusTone}`}>{subscription.status}</span>
          </div>
          <h2>{planName}</h2>
          <p className="subscription-plan-meta">
            {plan ? `Plan ${plan.code} · version ${plan.version} · ${plan.status}` : "A versioned plan has not yet been assigned to this shop."}
          </p>
          <div className="subscription-period">
            <div><span>Billing interval</span><strong>{subscription.billingInterval}</strong></div>
            <div><span>Current period</span><strong>{periodStart} – {periodEnd}</strong></div>
          </div>
          <div className="subscription-prices">
            <div><span>Monthly plan price</span><strong>{price(plan?.monthlyPriceMinor, plan?.currency ?? "INR")}</strong></div>
            <div><span>Annual plan price</span><strong>{price(plan?.annualPriceMinor, plan?.currency ?? "INR")}</strong></div>
          </div>
          {!entitlements.isAccessActive && (
            <div className="subscription-legacy-note subscription-expired-note"><ShieldCheck size={15}/><span>This trial or subscription is no longer active. New print requests are paused. Contact SmartPrint by QuantaEdge support to restore access.</span></div>
          )}
          {entitlements.isLegacyUnconfigured && entitlements.isAccessActive && (
            <div className="subscription-legacy-note"><ShieldCheck size={15}/><span>This subscription predates plan assignment. Existing shop access is being preserved until its terms are explicitly assigned.</span></div>
          )}
        </section>

        <section className="panel subscription-feature-panel">
          <div className="panel-heading"><div><h2>Included capabilities</h2><p>Resolved from the version assigned to your subscription</p></div></div>
          <div className="subscription-feature-list">
            <FeatureLine label="Cloud-assisted printing" enabled={entitlements.cloudAssistedPrintingEnabled}/>
            <FeatureLine label="White-label experience" enabled={entitlements.whiteLabelEnabled}/>
            <FeatureLine label="Advanced reports" enabled={entitlements.advancedReportsEnabled}/>
          </div>
          <div className="subscription-upgrade-note">
            <CreditCard size={15}/>
            <span>Manage your SmartPrint subscription securely with Razorpay. Customer print payments remain separate and continue to be handled by your shop.</span>
          </div>
        </section>
      </div>

      <ShopSubscriptionCheckout token={token} onSubscriptionUpdated={onUpdated}/>

      <section className="panel subscription-usage-panel">
        <div className="subscription-usage-heading">
          <div><h2>Usage and limits</h2><p>Live counts for this shop. Monthly print jobs use the UTC calendar month.</p></div>
          <span className="subscription-updated">Updated {formatIstTime(summary.generatedUtc)}</span>
        </div>
        <div className="subscription-usage-grid">
          <UsageMeter label="Registered printers" used={usage.printerCount} maximum={entitlements.maxPrinters}/>
          <UsageMeter label="Paired computers" used={usage.agentDeviceCount} maximum={entitlements.maxAgentDevices}/>
          <UsageMeter label="QR stations" used={usage.qrStationCount} maximum={entitlements.maxQrStations}/>
          <UsageMeter label="Print jobs this month" used={usage.monthlyPrintJobCount} maximum={entitlements.maxMonthlyPrintJobs}/>
        </div>
        <div className="subscription-document-limit">
          <span>Maximum cloud document size</span>
          <strong>{entitlements.maxDocumentBytes == null ? "No configured cap" : `${(entitlements.maxDocumentBytes / (1024 * 1024)).toLocaleString("en-IN", { maximumFractionDigits: 2 })} MB`}</strong>
        </div>
        <div className="subscription-usage-footnote">Disabled printers, QR stations and offline agents still count as registered resources unless removed or revoked. Revoked agent computers are excluded.</div>
      </section>
    </section>
  );
}

function UsageMeter({ label, used, maximum }: { label: string; used: number; maximum: number | null }) {
  const boundedMaximum = maximum == null ? null : Math.max(0, maximum);
  const percentage = boundedMaximum == null ? 0 : boundedMaximum > 0 ? Math.min(100, used / boundedMaximum * 100) : 100;
  return (
    <div className="subscription-usage-item">
      <div className="subscription-usage-item-top"><span>{label}</span><strong>{used.toLocaleString("en-IN")} <small>{boundedMaximum == null ? "/ unlimited" : `/ ${boundedMaximum.toLocaleString("en-IN")}`}</small></strong></div>
      <div className="subscription-progress-track"><span style={{ width: boundedMaximum == null ? "0%" : `${percentage}%` }}/></div>
      <small className="subscription-usage-caption">{boundedMaximum == null ? "No configured plan limit" : boundedMaximum === 0 ? "Not available on this plan" : `${Math.max(0, boundedMaximum - used).toLocaleString("en-IN")} remaining`}</small>
    </div>
  );
}

function FeatureLine({ label, enabled }: { label: string; enabled: boolean }) {
  return (
    <div className="subscription-feature-line">
      <span className={enabled ? "subscription-feature-indicator enabled" : "subscription-feature-indicator"}>{enabled ? <Check size={13}/> : <X size={13}/>}</span>
      <span>{label}</span>
      <strong>{enabled ? "Included" : "Not included"}</strong>
    </div>
  );
}

function pageDescription(section:Section) {
  const descriptions:Record<Section,string> = {
    "Order Journey":"See every customer order move from upload through review, payment and printing to handover.",
    "Overview":"Your shop setup and operations, all in one place.",
    "Print Queue":"Review real customer uploads and server-calculated print quotes.",
    "Printers":"Manage connected devices, capabilities and health.",
    "QR Stations":"Create secure customer entry points for each counter.",
    "Print Agent":"Pair the Windows computer that hosts your installed printers.",
    "Pricing":"Set your mono, colour and duplex rates.",
    "Subscription":"Review your assigned plan, entitlement limits and current usage.",
    "Payments":"Configure your shop's payment methods and verify customer print payments.",
    "Customers":"View customer activity and print history.",
    "Reports":"Understand print volume, shop revenue and performance.",
    "Settings":"Update the shop name and branding customers see when they connect."
  };
  return descriptions[section];
}
async function copyText(value:string) {
  try {
    if(navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return;
    }
  } catch {
    // Fall through to the in-page clipboard fallback.
  }
  const helper=document.createElement("textarea");
  helper.value=value;
  helper.setAttribute("readonly","");
  helper.style.position="fixed";
  helper.style.opacity="0";
  document.body.appendChild(helper);
  helper.select();
  try { document.execCommand("copy"); } finally { helper.remove(); }
}

function InfoTooltip({label,children}:{label:string;children:ReactNode}) {
  const tooltipId=useId();
  const rootRef=useRef<HTMLSpanElement>(null);
  const [open,setOpen]=useState(false);
  useEffect(()=>{
    if(!open)return;
    const dismissOutside=(event:PointerEvent)=>{
      if(!rootRef.current?.contains(event.target as Node))setOpen(false);
    };
    document.addEventListener("pointerdown",dismissOutside);
    return ()=>document.removeEventListener("pointerdown",dismissOutside);
  },[open]);
  return <span ref={rootRef}
    className={open?"context-info context-info-open":"context-info"}
    onMouseEnter={()=>setOpen(true)}
    onMouseLeave={()=>setOpen(false)}
    onBlur={event=>{
      const nextTarget=event.relatedTarget;
      if(!(nextTarget instanceof Node)||!event.currentTarget.contains(nextTarget))setOpen(false);
    }}
  >
    <button type="button" className="context-info-trigger" aria-label={label}
      aria-expanded={open} aria-describedby={tooltipId}
      onFocus={()=>setOpen(true)} onClick={()=>setOpen(true)}
      onKeyDown={event=>{if(event.key==="Escape"){setOpen(false);event.currentTarget.blur();}}}
    ><Info size={14} strokeWidth={2}/></button>
    <span id={tooltipId} className="context-info-tooltip" role="tooltip" aria-hidden={!open}>{children}</span>
  </span>;
}

function CustomerEntry({publicCode,dark,onToggleTheme}:{publicCode:string;dark:boolean;onToggleTheme:()=>void}) {
  const [result,setResult]=useState<Awaited<ReturnType<typeof createCustomerSession>>|null>(null);
  const [error,setError]=useState("");
  const [loading,setLoading]=useState(true);
  const [localReady,setLocalReady]=useState(false);
  const [localFallbackSuggested,setLocalFallbackSuggested]=useState(false);
  const handleLocalReady=useCallback((ready:boolean)=>{
    setLocalReady(ready);
    if(ready)setLocalFallbackSuggested(false);
  },[]);
  const handleLocalUnavailable=useCallback(()=>setLocalFallbackSuggested(true),[]);
  useEffect(()=>{
    setLocalReady(false);
    setLocalFallbackSuggested(false);
  },[result?.localAgent?.agentDeviceId]);
  useEffect(()=>{
    let mounted=true;
    const stationKey=`qrtoprint.customer-session:${publicCode}`;
    const restore=async()=>{
      const storedToken=sessionStorage.getItem(stationKey) ||
        await getNativeCustomerPrintSession(publicCode);
      let existing:Awaited<ReturnType<typeof createCustomerSession>>|null=null;
      if(storedToken){
        try {
          existing=await resumeCustomerSession(storedToken,publicCode);
        } catch(exception) {
          if(exception instanceof ApiError&&exception.status===401){
            sessionStorage.removeItem(stationKey);
            sessionStorage.removeItem("qrtoprint.customer-session");
            await clearNativeCustomerPrintSession(publicCode);
          } else throw exception;
        }
      }
      const value=existing??await createCustomerSession(publicCode);
      // Wait for native encrypted persistence before presenting the connected session.
      // In a regular browser the bridge is absent and this safely becomes a no-op.
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      await saveNativeCustomerShopProfile(publicCode,value.shop);
      if(!mounted)return;
      setResult(value);
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
    };
    void restore().catch(exception=>{
      if(mounted)setError(exception instanceof Error?exception.message:"This QR station is unavailable.");
    }).finally(()=>{if(mounted)setLoading(false);});
    return ()=>{mounted=false;};
  },[publicCode]);

  const startNewSession=useCallback(async()=>{
    setLoading(true);
    setError("");
    setLocalReady(false);
    setLocalFallbackSuggested(false);
    try{
      const value=await createCustomerSession(publicCode);
      const storedNatively=await saveNativeCustomerPrintSession(publicCode,value.sessionToken);
      await saveNativeCustomerShopProfile(publicCode,value.shop);
      const stationKey=`qrtoprint.customer-session:${publicCode}`;
      if(storedNatively)sessionStorage.removeItem(stationKey);
      else sessionStorage.setItem(stationKey,value.sessionToken);
      sessionStorage.removeItem("qrtoprint.customer-session");
      setResult(value);
    }catch(exception){
      setError(exception instanceof Error?exception.message:"Could not start a new print session.");
    }finally{
      setLoading(false);
    }
  },[publicCode]);

  useEffect(()=>{
    if(!result)return;
    let active=true;
    let renewing=false;
    const renew=()=>{
      if(renewing)return;
      renewing=true;
      void renewCustomerPrintSession(result.sessionToken)
        .then(session=>{
          if(active){
            setResult(current=>current?.sessionToken===result.sessionToken
              ? {...current,expiresUtc:session.expiresUtc,localAgent:session.localAgent,paymentOptions:session.paymentOptions}
              : current);
          }
        })
        .catch(()=>{/* A temporary offline period is retried on the next foreground check. */})
        .finally(()=>{renewing=false;});
    };
    const checkExpiry=()=>{
      const expiry=Date.parse(result.expiresUtc);
      if(!Number.isFinite(expiry)||expiry<=Date.now()+10*60*1000)renew();
    };
    const timer=window.setInterval(renew,5*60*1000);
    const onVisibility=()=>{if(document.visibilityState==="visible")checkExpiry();};
    window.addEventListener("focus",checkExpiry);
    document.addEventListener("visibilitychange",onVisibility);
    return ()=>{
      active=false;
      window.clearInterval(timer);
      window.removeEventListener("focus",checkExpiry);
      document.removeEventListener("visibilitychange",onVisibility);
    };
  },[result?.sessionToken,result?.expiresUtc]);

  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  return <div className={nativeAppMode ? "customer-entry smartprint-native" : "customer-entry"}><div className="customer-entry-glow"/>
    <header className="customer-entry-brand"><BrandLockup className="customer-entry-brand-lockup" /><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/></header>
    <main className={result ? "customer-entry-card customer-entry-card--upload" : "customer-entry-card"}>
      {loading?<><div className="customer-entry-spinner"><span className="button-spinner"/></div><h1>{nativeAppMode?"Connecting…":"Connecting to your print shop"}</h1>{!nativeAppMode&&<p>Creating a secure, time-limited session for this QR station.</p>}</>:
      error?<><div className="entry-error-icon"><X size={23}/></div><div className="entry-eyebrow">STATION UNAVAILABLE</div><h1>{nativeAppMode?"Shop unavailable":"This QR station can't be reached"}</h1>{!nativeAppMode&&<p>{error}</p>}<button className="primary-button" onClick={()=>window.location.reload()}>Try again</button></>:
      result?<><div className="entry-success-icon"><Check size={23}/></div><div className="entry-eyebrow">SECURE SESSION CREATED</div><h1>{nativeAppMode?result.shop.displayName:"Welcome to "+result.shop.displayName}</h1><p>{nativeAppMode?"Shop connected.":"Your phone is connected to this shop's print station."}</p>
        <div className="entry-printer-card"><div className="entry-printer-icon"><Printer size={20}/></div><div><strong>{result.printer?.displayName||"Shop print counter"}</strong><span>{nativeAppMode?(result.printer?.status||"Printer not selected"):result.printer?result.printer.manufacturer+" · "+result.printer.model:"The shop will select a printer for your job."}</span></div><Status label={result.printer?.status||"Session ready"} tone="blue"/></div>
        <div className="entry-session-note"><ShieldCheck size={16}/><span>{nativeAppMode?"Secure session · Expires "+formatIstTime(result.expiresUtc):"This session expires at "+formatIstTime(result.expiresUtc)+". Your document access stays limited to this shop session."}</span></div>
        <CustomerLocalConnection localAgent={result.localAgent} onReady={handleLocalReady} onUnavailable={handleLocalUnavailable} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled}/>
        <CustomerPrintFlow dark={dark} publicCode={publicCode} sessionToken={result.sessionToken} paymentOptions={result.paymentOptions} localAgent={result.localAgent} localReady={localReady} localFallbackSuggested={localFallbackSuggested} cloudAssistedPrintingEnabled={result.cloudAssistedPrintingEnabled} photoPricing={result.photoPricing} hasAssignedPrinter={Boolean(result.printer)} printerCapabilitiesJson={result.printer?.capabilitiesJson} key={result.sessionToken} onStartNewSession={()=>void startNewSession()}/>
      </>:null}
    </main>
    <footer className="customer-entry-footer">© 2026 SmartPrint by QuantaEdge · Secure customer printing</footer>
  </div>;
}


type CustomerLocalAgent = NonNullable<Awaited<ReturnType<typeof createCustomerSession>>["localAgent"]>;
type LocalAgentCheckStatus = "idle" | "checking" | "connected" | "unverified" | "recovering" | "unavailable" | "denied" | "insecure";

function isPrivateIpv4Hostname(hostname:string):boolean {
  const octets=hostname.split(".").map(Number);
  return octets.length===4 &&
    octets.every(part=>Number.isInteger(part)&&part>=0&&part<=255) &&
    (octets[0]===10 ||
     (octets[0]===172&&octets[1]>=16&&octets[1]<=31) ||
     (octets[0]===192&&octets[1]===168));
}

function isLocalOrLoopbackPortalOrigin(hostname:string):boolean {
  const value=hostname.toLowerCase().replace(/^\[|\]$/g,"");
  return value==="localhost" ||
    value.endsWith(".localhost") ||
    value==="127.0.0.1" ||
    value==="::1" ||
    isPrivateIpv4Hostname(value) ||
    value.endsWith(".local");
}

function buildSecureLocalPortalUrl():string|null {
  try {
    const url=new URL(window.location.href);
    if(url.protocol!=="http:"||url.port!=="3000"||!isPrivateIpv4Hostname(url.hostname))return null;
    url.protocol="https:";
    url.port="3443";
    return url.toString();
  } catch {
    return null;
  }
}

async function requestSmartPrintNativeLocalNetworkAccess():Promise<"granted"|"denied"|"not_required"|"unavailable"> {
  const bridge=(window as Window & {
    SmartPrintNative?: {
      postMessage:(message:string)=>void;
      addEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
      removeEventListener:(type:string,listener:(event:MessageEvent)=>void)=>void;
    };
  }).SmartPrintNative;
  if(!bridge?.postMessage||!bridge.addEventListener||!bridge.removeEventListener)return "unavailable";

  return new Promise(resolve=>{
    let settled=false;
    let timeout:number|undefined;
    const finish=(result:"granted"|"denied"|"not_required"|"unavailable")=>{
      if(settled)return;
      settled=true;
      if(timeout!==undefined)window.clearTimeout(timeout);
      bridge.removeEventListener("message",handleReply);
      resolve(result);
    };
    const handleReply=(event:MessageEvent)=>{
      const value=typeof event.data==="string"?event.data:"";
      if(value==="localNetworkAccess:granted")finish("granted");
      else if(value==="localNetworkAccess:denied")finish("denied");
      else if(value==="localNetworkAccess:not_required")finish("not_required");
    };
    timeout=window.setTimeout(()=>finish("unavailable"),30000);
    bridge.addEventListener("message",handleReply);
    try{bridge.postMessage("requestLocalNetworkAccess");}
    catch{finish("unavailable");}
  });
}

function CustomerLocalConnection({localAgent,onReady,onUnavailable,cloudAssistedPrintingEnabled}:{localAgent:CustomerLocalAgent|null;onReady:(ready:boolean)=>void;onUnavailable:()=>void;cloudAssistedPrintingEnabled:boolean}) {
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  const [permissionState,setPermissionState]=useState<PermissionState|"unknown">("unknown");
  const [checkStatus,setCheckStatus]=useState<LocalAgentCheckStatus>("idle");
  const [message,setMessage]=useState("");
  const checkGeneration=useRef(0);
  const securePortalUrl=buildSecureLocalPortalUrl();
  const localOriginDoesNotNeedLnaPermission=isLocalOrLoopbackPortalOrigin(window.location.hostname);

  function requireSecureContext() {
    setCheckStatus("insecure");
    onReady(false);
    if(cloudAssistedPrintingEnabled)onUnavailable();
    setMessage(nativeAppMode ? "Secure HTTPS required." : securePortalUrl
      ? "This shop page is open over HTTP. Chrome blocks local-network requests from insecure pages without showing a permission prompt. Open the secure shop link below; the certificate must be trusted on this phone."
      : "This shop page is not a secure HTTPS context. Open the shop's HTTPS customer portal before checking local printing.");
  }

  async function checkConnection() {
    const generation=++checkGeneration.current;
    if(!localAgent)return;
    if(!window.isSecureContext) {
      requireSecureContext();
      return;
    }
    setCheckStatus("checking");
    onReady(false);
    setMessage(nativeAppMode ? "Checking shop connection." : "Checking the shop computer. No document is sent by this check.");
    let nativeLocalNetworkPermissionDenied=false;
    if(nativeAppMode) {
      const access=await requestSmartPrintNativeLocalNetworkAccess();
      if(generation!==checkGeneration.current)return;
      // Android 16 only enforces local-network permission when its protection is
      // active. A denied Nearby devices prompt must not block a connection on
      // devices where the OS still grants local access implicitly.
      nativeLocalNetworkPermissionDenied=access==="denied";
    }

    let validEndpointCount=0;
    for(const value of localAgent.endpoints.slice(0,8)) {
      let endpoint:URL;
      try {
        endpoint=new URL(value);
        if(endpoint.protocol!=="http:" ||
           endpoint.port!=="17842" ||
           endpoint.username!=="" ||
           endpoint.password!=="" ||
           endpoint.pathname!=="/" ||
           endpoint.search!=="" ||
           endpoint.hash!=="" ||
           !isPrivateIpv4Hostname(endpoint.hostname))continue;
      } catch {
        continue;
      }

      validEndpointCount++;
      const controller=new AbortController();
      const timeout=window.setTimeout(()=>controller.abort(),3500);
      try {
        // Start from the user's click when a browser permission prompt is required.
        const response=await fetch(`${endpoint.origin}/api/v1/local/status`,{
          method:"GET",
          mode:"cors",
          cache:"no-store",
          credentials:"omit",
          signal:controller.signal,
          targetAddressSpace:"local"
        } as RequestInit & {targetAddressSpace:"local"});
        if(generation!==checkGeneration.current)return;
        if(!response.ok)continue;

        const status=await response.json() as {
          agentDeviceId?:string;
          protocolVersion?:number;
          status?:string;
          localUploadReady?:boolean;
          readiness?: {
            queueInitialized?:boolean;
            printWorkerRunning?:boolean;
            heartbeatFresh?:boolean;
            printerInventoryFresh?:boolean;
            discoveredPrinterCount?:number;
            localUploadReady?:boolean;
          };
        };
        if(generation!==checkGeneration.current)return;
        if(status.agentDeviceId===localAgent.agentDeviceId &&
           status.protocolVersion===1 &&
           status.status==="LOCAL_ENDPOINT_READY") {
          if(!status.readiness) {
            // Existing installed Agents do not yet publish the detailed readiness
            // payload. Keep their working local route available; the ticket API
            // and Agent must still validate the assigned printer and queue.
            setCheckStatus("unverified");
            onReady(true);
            setMessage("This shop computer is reachable, but its Agent has not reported detailed queue health yet. SmartPrint will verify the assigned printer and upload with the shop before confirming the file.");
            return;
          }
          if(status.localUploadReady!==true || status.readiness?.localUploadReady!==true) {
            setCheckStatus("recovering");
            onReady(false);
            const readiness=status.readiness;
            const agentHeartbeatOffline=readiness?.heartbeatFresh!==true;
            if(cloudAssistedPrintingEnabled||agentHeartbeatOffline)onUnavailable();
            const blockers=[
              readiness?.queueInitialized!==true?"protected document queue is not ready":null,
              readiness?.printWorkerRunning!==true?"print worker is not running":null,
              readiness?.heartbeatFresh!==true?"cloud heartbeat is not fresh":null,
              readiness?.printerInventoryFresh!==true?"printer inventory is not fresh":null
            ].filter((value):value is string=>value!==null);
            const detail=blockers.length?blockers.join(", "):"the Agent has not reported full pipeline readiness";
            setMessage("The shop computer is reachable, but local printing is recovering ("+detail+"). "+
              (cloudAssistedPrintingEnabled
                ? "Choose Cloud-assisted upload while the shop operator restores the local print pipeline."
                : agentHeartbeatOffline
                  ? "The Agent cloud heartbeat is offline. Choose Secure queue · HTTPS to save this request while the shop reconnects."
                  : "Ask the shop operator to restore the protected local queue, then check again."));
            return;
          }
          setCheckStatus("connected");
          onReady(true);
          setMessage(nativeAppMode ? "Shop connection ready. Standard local uploads use HTTP; Private Printing encrypts document contents end-to-end." : "This phone can reach the shop computer. Standard local uploads use HTTP and are not encrypted in transit; Private Printing encrypts document contents end-to-end.");
          return;
        }
      } catch {
        // Continue across the Agent's advertised private addresses.
      } finally {
        window.clearTimeout(timeout);
      }
    }

    if(generation!==checkGeneration.current)return;
    setCheckStatus("unavailable");
    onReady(false);
    // A customer session can outlive the Agent heartbeat. Surface the existing
    // HTTPS queue path after the local Agent endpoint is confirmed unreachable;
    // the API still verifies the live Agent/plan boundary before accepting bytes.
    onUnavailable();
    setMessage(nativeAppMode
      ? nativeLocalNetworkPermissionDenied
        ? "Could not reach the shop. Make sure this phone is on the same Wi-Fi or hotspot as the shop computer. If Android 16 local-network access is restricted, allow SmartPrint under Android Settings → Permissions → Nearby devices. Cloud-assisted upload remains available."
        : "Shop offline · Secure HTTPS queue available while the shop Agent is offline."
      : validEndpointCount===0
        ? "The shop Agent has not announced a usable local address. Secure HTTPS queue fallback is available only while the shop Agent is offline."
        : "Could not reach the shop computer. Connect this phone to the same shop Wi-Fi or hotspot, then ask the shop owner to check the connection. Secure HTTPS queue fallback is available only while the shop Agent is offline.");
  }

  useEffect(()=>{
    // Invalidate an older request immediately when the shop endpoint list changes.
    checkGeneration.current++;
    if(!localAgent?.endpoints.length) {
      onReady(false);
      onUnavailable();
      setPermissionState("unknown");
      setCheckStatus("unavailable");
      setMessage("The shop's paired print computer has not reported a reachable local connection. Direct shop Wi-Fi or hotspot transfer is temporarily unavailable. Use Secure queue · HTTPS below to submit your request when available.");
      return ()=>{checkGeneration.current++;};
    }
    onReady(false);
    setCheckStatus("idle");
    setMessage("");
    if(!window.isSecureContext) {
      requireSecureContext();
      return ()=>{checkGeneration.current++;};
    }

    // Android WebView does not expose Chrome's site-level Local Network Access
    // prompt. Keep this check user-initiated and let the native app request the
    // Android 16 transitional permission only when the customer chooses Shop Wi-Fi.
    if(nativeAppMode) {
      setPermissionState("unknown");
      setCheckStatus("idle");
      setMessage("Tap Check Wi-Fi to connect to this shop.");
      return ()=>{checkGeneration.current++;};
    }

    if(localOriginDoesNotNeedLnaPermission) {
      setPermissionState("unknown");
      return ()=>{checkGeneration.current++;};
    }
    if(!navigator.permissions?.query)return ()=>{checkGeneration.current++;};

    let active=true;
    let permissionStatus:PermissionStatus|null=null;
    const updatePermission=()=>{
      if(!active||!permissionStatus)return;
      setPermissionState(permissionStatus.state);
      if(permissionStatus.state==="denied") {
        setCheckStatus("denied");
        onReady(false);
        if(cloudAssistedPrintingEnabled)onUnavailable();
        setMessage(nativeAppMode ? "Allow local network access." : "Chrome has blocked Local network access for this public HTTPS site. On Android, open the site information icon beside the address bar → Permissions → Local network access → Allow, then reload this QR page.");
      } else if(permissionStatus.state==="prompt") {
        setCheckStatus("idle");
        setMessage(nativeAppMode ? "Tap Check Wi-Fi." : "Tap Check shop Wi-Fi to request local-network access from Chrome.");
      }
    };

    void navigator.permissions.query({name:"local-network" as PermissionName})
      .then(status=>{
        if(!active)return;
        permissionStatus=status;
        status.addEventListener("change",updatePermission);
        updatePermission();
        if(status.state==="granted")void checkConnection();
      })
      .catch(()=>{if(active)setPermissionState("unknown");});

    return ()=>{
      active=false;
      checkGeneration.current++;
      permissionStatus?.removeEventListener("change",updatePermission);
    };
  },[localAgent?.agentDeviceId,localAgent?.endpoints.join("|"),onReady,onUnavailable]);

  const label=!localAgent?.endpoints.length?"Temporarily unavailable":
    checkStatus==="checking"?"Checking…":
    checkStatus==="connected"?(nativeAppMode?"Connected":"Shop computer reachable"):
    checkStatus==="unverified"?"Connected · health details unavailable":
    checkStatus==="recovering"?"Print pipeline recovering":
    checkStatus==="denied"?(nativeAppMode?"Access blocked":"Browser permission blocked"):
    checkStatus==="insecure"?"HTTPS required":
    checkStatus==="unavailable"?"Not reachable":"Not checked";
  const tone=checkStatus==="connected"?"green":
    checkStatus==="unverified"?"amber":
    checkStatus==="recovering"?"amber":
    checkStatus==="unavailable"||checkStatus==="denied"?"red":"blue";

  return <section className="entry-local-agent">
    <div className="entry-local-agent-head">
      <div><strong>{nativeAppMode?"Shop Wi-Fi":"Shop Wi-Fi connection"}</strong><span>Optional local connection check</span></div>
      <Status label={label} tone={tone}/>
    </div>
    {!nativeAppMode&&<p>Check whether this phone can reach the shop's local print computer. This check does not upload your document.</p>}
    {!localAgent?.endpoints.length
      ? <div className="entry-local-agent-message" role="status"><strong>Direct transfer is temporarily unavailable.</strong> {message}</div>
      : checkStatus==="insecure"
      ? <div className="entry-local-agent-message">
          <small>{message}</small>
          {securePortalUrl&&<a className="customer-flow-button" href={securePortalUrl}>{nativeAppMode?"Open HTTPS":"Open secure HTTPS shop page"}</a>}
        </div>
      : permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission
        ? <small className="entry-local-agent-message">{message}</small>
        : <button type="button" className="customer-flow-button" disabled={checkStatus==="checking"} onClick={()=>void checkConnection()}>
            {checkStatus==="checking"?"Checking…":checkStatus==="connected"?"Check again":nativeAppMode?"Check Wi-Fi":"Check shop Wi-Fi"}
          </button>}
    {message&&checkStatus!=="insecure"&&!(permissionState==="denied"&&!localOriginDoesNotNeedLnaPermission)&&<small className="entry-local-agent-message">{message}</small>}
  </section>;
}


type CustomerGroupSettings = {
  copies: string;
  colourMode: "Mono" | "Colour";
  isDuplex: boolean;
  advancedEnabled: boolean;
  paperSize: CustomerPrintRequestSettings["paperSize"];
  orientation: CustomerPrintRequestSettings["orientation"];
};
type CustomerFileSettings = CustomerGroupSettings & { custom: boolean };

function consumeSmartPrintNativePickerGroups():string[]|undefined {
  const target=window as Window & {__smartPrintNativePickerGroups?:string[]};
  const groups=target.__smartPrintNativePickerGroups;
  delete target.__smartPrintNativePickerGroups;
  return Array.isArray(groups)?groups:undefined;
}
type CustomerSavedPrintSettings = CustomerPrintRequestSettings & { advancedEnabled?: boolean };
type CustomerPhotoSettings = { sizeCode: string; photoCount: number; photosPerPage?: number };
function customerPhotoSizeLabel(sizeCode?:string|null):string {
  switch(sizeCode){
    case "35x45": return "35 × 45 mm";
    case "25x35": return "25 × 35 mm";
    case "2x2": return "2 × 2 in";
    case "4x6": return "4 × 6 in";
    default: return "photo";
  }
}

type StoredCustomerDraft = {
  sessionMarker: string;
  printKindsByJobId?: Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">;
  uploaded?: UploadedPrintJob;
  batchName?: string;
  uploadedBatch?: UploadedPrintJob[];
  uploadedFileFingerprints?: string[];
  batchId?: string;
  quote: PrintQuoteResult | null;
  fileGroupNames?: Record<string,string>;
  groupSettings?: Record<string,CustomerGroupSettings>;
  fileSettings?: Record<string,CustomerFileSettings>;
  printSettingsByJobId?: Record<string,CustomerSavedPrintSettings>;
  quotesByJobId?: Record<string,PrintQuoteResult>;
  photoSettingsByJobId?: Record<string,CustomerPhotoSettings>;
};

function isCustomerImage(file:File):boolean {
  return file.type.toLowerCase().startsWith("image/") || /\.(jpe?g|png)$/i.test(file.name);
}

function customerFileFingerprint(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const nativeGroup=(file as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup??"";
  return `${relativePath||nativeGroup}\u0000${file.name}\u0000${file.size}\u0000${file.lastModified}\u0000${file.type}`;
}

function isSmartPrintIdDocument(file:File):boolean {
  return (file as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="ID_DOCUMENT";
}

function isSmartPrintPhotoDocument(file:File):boolean {
  return (file as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="PHOTO";
}
function isValidNativePhotoSettings(value:unknown):value is CustomerPhotoSettings {
  if(!value||typeof value!=="object")return false;
  const settings=value as CustomerPhotoSettings;
  const maximumBySize:Record<string,number>={"35x45":25,"25x35":49,"2x2":15,"4x6":1};
  const maximum=maximumBySize[settings.sizeCode];
  return maximum!==undefined&&Number.isInteger(settings.photoCount)&&settings.photoCount>=1&&settings.photoCount<=100&&
    Number.isInteger(settings.photosPerPage)&&settings.photosPerPage!>=1&&settings.photosPerPage!<=maximum;
}

function isCustomerPdf(file:File):boolean {
  return file.type.toLowerCase()==="application/pdf"||/\.pdf$/i.test(file.name);
}

const smartPrintNativePdfPreviewTokens=new WeakMap<File,string>();

function postSmartPrintNativeMessage(message:string):boolean {
  const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
  if(!bridge?.postMessage)return false;
  try {bridge.postMessage(message);return true;}
  catch {return false;}
}

function consumeSmartPrintNativePrintKinds():Array<string|null>|undefined {
  const target=window as Window & {__smartPrintNativePrintKinds?:Array<string|null>};
  const kinds=target.__smartPrintNativePrintKinds;
  delete target.__smartPrintNativePrintKinds;
  return Array.isArray(kinds)?kinds:undefined;
}

function consumeSmartPrintNativePhotoSettings():Array<CustomerPhotoSettings|null>|undefined {
  const target=window as Window & {__smartPrintNativePhotoSettings?:Array<CustomerPhotoSettings|null>};
  const settings=target.__smartPrintNativePhotoSettings;
  delete target.__smartPrintNativePhotoSettings;
  return Array.isArray(settings)?settings:undefined;
}

function consumeSmartPrintNativePdfPreviews():Array<string|null>|undefined {
  const target=window as Window & {__smartPrintNativePdfPreviews?:Array<string|null>};
  const previews=target.__smartPrintNativePdfPreviews;
  delete target.__smartPrintNativePdfPreviews;
  return Array.isArray(previews)?previews:undefined;
}

function customerBatchGroupName(file:File):string {
  const relativePath=(file as File & {webkitRelativePath?:string}).webkitRelativePath??"";
  const pathParts=relativePath.split("/").filter(Boolean);
  return pathParts.length>1?pathParts[0]:"Loose files";
}

function normalizeCustomerCopies(value:string):string {
  const trimmed=value.trim();
  if(!/^\d+$/.test(trimmed))return "1";
  const copies=Number(trimmed);
  if(!Number.isFinite(copies))return "100";
  return String(Math.min(100,Math.max(1,Math.trunc(copies))));
}

function readStoredCustomerDraft(publicCode:string,sessionToken:string):StoredCustomerDraft|null {
  try {
    const raw=localStorage.getItem(`qrtoprint.customer-active-job:${publicCode}`);
    if(!raw)return null;
    const draft=JSON.parse(raw) as StoredCustomerDraft;
    return draft.sessionMarker===sessionToken.slice(-16)&&
      (draft.uploaded?.job?.id||draft.uploadedBatch?.length||draft.batchName?.trim())?draft:null;
  } catch {
    return null;
  }
}

function smartPrintNativeHeadline(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop review";
    case "AWAITING_PAYMENT": return "Payment required";
    case "AWAITING_CUSTOMER_PAYMENT": return "Complete UPI payment";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at counter";
    case "PAYMENT_REPORTED": return "Verification pending";
    case "PAYMENT_REJECTED": return "Payment not verified";
    case "PAID_AUTHORISED": return "Payment verified";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop";
    case "LOCAL_QUEUE_READY": return "Queued at shop";
    case "PRINT_AUTHORISED": return "Printing authorised";
    case "PRINT_SUBMITTING": return "Sending to printer";
    case "SPOOLER_ACCEPTED": return "Printer queue accepted";
    case "PRINTING": return "Printing";
    case "OUTPUT_UNCONFIRMED": return "Output check pending";
    case "COMPLETED": return "Ready to collect";
    case "PDF_EXPORTED": return "PDF ready";
    case "REFUNDED": return "Fully refunded";
    case "CANCELLED": return "Cancelled";
    case "FAILED": return "Failed";
    case "REJECTED": return "Request declined";
    case "QUOTE_EXPIRED": return "Quote expired";
    default: return "Request in progress";
  }
}

function smartPrintNativeStatus(status:string):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Awaiting shop approval.";
    case "AWAITING_PAYMENT": return "Quote ready · Payment required.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Pay shop · Submit UTR.";
    case "AWAITING_COUNTER_PAYMENT": return "Pay at the counter.";
    case "PAYMENT_REPORTED": return "UTR sent · Verify pending.";
    case "PAYMENT_REJECTED": return "Payment not verified.";
    case "PAID_AUTHORISED": return "Payment verified · Print pending.";
    case "LOCAL_QUEUE_CLAIMED": return "Transferring to shop.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator pending.";
    case "PRINT_AUTHORISED": return "Printing authorised.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing in progress.";
    case "OUTPUT_UNCONFIRMED": return "Shop confirming print output.";
    case "COMPLETED": return "Ready for collection.";
    case "PDF_EXPORTED": return "Your PDF was saved and verified.";
    case "REFUNDED": return "The file was excluded and its full payment was returned.";
    case "CANCELLED": return "This request was cancelled. No further print work will be produced.";
    case "FAILED": return "This request could not be completed. Contact the shop before submitting again.";
    case "REJECTED": return "Shop rejected request."
    case "QUOTE_EXPIRED": return "Quote expired.";
    default: return "Status updated.";
  }
}

function smartPrintNativeBoundary(status:string|undefined,hasQuote:boolean):string {
  switch(status) {
    case "AWAITING_SHOP_APPROVAL": return "Shop review · No payment.";
    case "AWAITING_PAYMENT": return "Pay shop · Verification required.";
    case "AWAITING_COUNTER_PAYMENT": return "Counter payment · Await confirmation.";
    case "AWAITING_CUSTOMER_PAYMENT": return "Submit UTR · Shop verifies.";
    case "PAYMENT_REPORTED": return "UTR pending · Shop verifies.";
    case "PAID_AUTHORISED": return "Payment verified · Printing not started.";
    case "LOCAL_QUEUE_CLAIMED": return "Secure transfer · Not printing.";
    case "LOCAL_QUEUE_READY": return "Queued · Operator approval needed.";
    case "PRINT_AUTHORISED": return "Shop approved physical printing.";
    case "PRINT_SUBMITTING": return "Sending to printer.";
    case "SPOOLER_ACCEPTED": return "Queue accepted · Paper unconfirmed.";
    case "PRINTING": return "Printing · Output unconfirmed.";
    case "OUTPUT_UNCONFIRMED": return "Output unconfirmed · Shop checks pages.";
    case "COMPLETED": return "Output confirmed · Collect print.";
    case "PDF_EXPORTED": return "PDF saved · Ready to use.";
    case "REFUNDED": return "Fully refunded · No printing will take place.";
    case "CANCELLED": return "Cancelled · No further print work.";
    case "FAILED": return "Failed · Contact the shop before retrying.";
    case "QUOTE_EXPIRED": return "Quote expired · Request another."
    case "REJECTED": return "Request rejected · Ask shop.";
    default: return hasQuote ? "Quote only · No payment." : "No payment yet.";
  }
}

function CustomerPrintFlow({dark,publicCode,sessionToken,paymentOptions,localAgent,localReady,localFallbackSuggested,cloudAssistedPrintingEnabled,photoPricing,hasAssignedPrinter,printerCapabilitiesJson,onStartNewSession}:{dark:boolean;publicCode:string;sessionToken:string;paymentOptions:{upiEnabled:boolean;upiId?:string|null;payeeName?:string|null;cashEnabled:boolean;instantPaymentEnabled:boolean};localAgent:CustomerLocalAgent|null;localReady:boolean;localFallbackSuggested:boolean;cloudAssistedPrintingEnabled:boolean;photoPricing:CustomerPhotoPricing|null;hasAssignedPrinter:boolean;printerCapabilitiesJson?:string|null;onStartNewSession:()=>void}) {
  const nativeAppMode = navigator.userAgent.includes("SmartPrintAndroid/1");
  // Customer requests are captured independently of current hardware reports.
  // The shop may wait, reassign, or request an approved change before dispatch.
  void hasAssignedPrinter;
  void printerCapabilitiesJson;
  const photoRatesAvailable=Boolean(photoPricing&&[photoPricing.photo35x45Minor,photoPricing.photo25x35Minor,photoPricing.photo2x2Minor,photoPricing.photo4x6Minor].some(rate=>Number.isFinite(rate)&&rate>0));
  const initialDraft=readStoredCustomerDraft(publicCode,sessionToken);
  const [files,setFiles]=useState<File[]>([]);
  const [selectedFileIndex,setSelectedFileIndex]=useState(0);
  const file=files[selectedFileIndex]??null;
  const [previewUrl,setPreviewUrl]=useState<string|null>(null);
  const [uploaded,setUploaded]=useState<UploadedPrintJob|null>(()=>initialDraft?.uploaded??initialDraft?.uploadedBatch?.[0]??null);
  const [uploadedBatch,setUploadedBatch]=useState<UploadedPrintJob[]>(()=>initialDraft?.uploadedBatch??(initialDraft?.uploaded?[initialDraft.uploaded]:[]));
  const [uploadedFileFingerprints,setUploadedFileFingerprints]=useState<string[]>(()=>initialDraft?.uploadedFileFingerprints??[]);
  const [batchId,setBatchId]=useState(()=>initialDraft?.batchId??"");
  const [batchName,setBatchName]=useState(()=>initialDraft?.batchName??initialDraft?.uploadedBatch?.[0]?.job?.batchName??initialDraft?.uploaded?.job?.batchName??"");
  const [fileGroupNames,setFileGroupNames]=useState<Record<string,string>>(()=>initialDraft?.fileGroupNames??{});
  const [groupSettings,setGroupSettings]=useState<Record<string,CustomerGroupSettings>>(()=>initialDraft?.groupSettings??{});
  const [fileSettings,setFileSettings]=useState<Record<string,CustomerFileSettings>>(()=>initialDraft?.fileSettings??{});
  const [printSettingsByJobId,setPrintSettingsByJobId]=useState<Record<string,CustomerSavedPrintSettings>>(()=>initialDraft?.printSettingsByJobId??{});
  const [printKindsByJobId,setPrintKindsByJobId]=useState<Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">>(()=>initialDraft?.printKindsByJobId??{});
  const [quotesByJobId,setQuotesByJobId]=useState<Record<string,PrintQuoteResult>>(()=>initialDraft?.quotesByJobId??(initialDraft?.quote&&initialDraft?.uploaded?{[initialDraft.uploaded.job.id]:initialDraft.quote}:{}));
  const [photoSettingsByFileFingerprint,setPhotoSettingsByFileFingerprint]=useState<Record<string,CustomerPhotoSettings>>({});
  const [photoPreviewPagesByFingerprint,setPhotoPreviewPagesByFingerprint]=useState<Record<string,string[]>>({});
  const [selectedPreparedPhotoPage,setSelectedPreparedPhotoPage]=useState(0);
  const photoPreviewUrlsRef=useRef<Set<string>>(new Set());
  const [photoSettingsByJobId,setPhotoSettingsByJobId]=useState<Record<string,CustomerPhotoSettings>>(()=>initialDraft?.photoSettingsByJobId??{});
  const [photoStudioSource,setPhotoStudioSource]=useState<File|null>(null);
  const [photoStudioSourceFingerprint,setPhotoStudioSourceFingerprint]=useState<string|null>(null);
  const [uploadProgress,setUploadProgress]=useState<{completed:number;total:number;currentFile:string|null}|null>(null);
  const [quote,setQuote]=useState<PrintQuoteResult|null>(()=>initialDraft?.quote??null);
  const [jobStatus,setJobStatus]=useState<CustomerJobStatus|null>(null);
  const [batchStatuses,setBatchStatuses]=useState<Record<string,CustomerJobStatus>>({});
  const [payment,setPayment]=useState<CustomerPayment|null>(null);
  const [batchPayment,setBatchPayment]=useState<CustomerBatchPayment|null>(null);

  function paymentTimestamp(payment: { updatedUtc?: string | null; createdUtc?: string | null }): number {
    const timestamp=Date.parse(payment.updatedUtc??payment.createdUtc??"");
    return Number.isFinite(timestamp)?timestamp:0;
  }

  function rememberBatchPayment(candidate?:CustomerBatchPayment|null) {
    if(!candidate)return;
    setBatchPayment(current=>
      !current||paymentTimestamp(candidate)>=paymentTimestamp(current)
        ?candidate:current);
  }

  function rememberCustomerPayment(candidate?:CustomerPayment|null) {
    if(!candidate)return;
    setPayment(current=>
      !current||current.id!==candidate.id||paymentTimestamp(candidate)>=paymentTimestamp(current)
        ?candidate:current);
  }

  function rememberBatchStatuses(candidates:Record<string,CustomerJobStatus>) {
    setBatchStatuses(current=>{
      const next={...current};
      for(const [id,candidate] of Object.entries(candidates)){
        const existing=current[id];
        if(!existing||Date.parse(candidate.updatedUtc)>=Date.parse(existing.updatedUtc))next[id]=candidate;
      }
      return next;
    });
  }

  function rememberCustomerStatus(candidate:CustomerJobStatus) {
    rememberBatchStatuses({[candidate.id]:candidate});
    setJobStatus(current=>
      !current||current.id!==candidate.id||Date.parse(candidate.updatedUtc)>=Date.parse(current.updatedUtc)
        ?candidate:current);
    if(candidate.pricingVersion&&candidate.quotedAmountMinor>0) {
      const paperSize=(["A4","A3","A5","LETTER","LEGAL"].includes(candidate.paperSize)
        ?candidate.paperSize:"A4") as CustomerPrintRequestSettings["paperSize"];
      const orientation=(["AUTO","PORTRAIT","LANDSCAPE"].includes(candidate.orientation??"AUTO")
        ?candidate.orientation??"AUTO":"AUTO") as CustomerPrintRequestSettings["orientation"];
      // A shop-side quote revision is authoritative. Keep the settings shown
      // to the customer in step with the revised amount before approval.
      setPrintSettingsByJobId(current=>{
        const previous=current[candidate.id];
        return {
          ...current,
          [candidate.id]:{
            ...previous,
            copies:candidate.copies,
            colourMode:candidate.colourMode==="Colour"?"Colour":"Mono",
            paperSize,
            isDuplex:candidate.isDuplex,
            orientation,
            advancedEnabled:Boolean(previous?.advancedEnabled)||paperSize!=="A4"||orientation!=="AUTO"
          }
        };
      });
      const revisedQuote:PrintQuoteResult={
        id:candidate.id,
        batchId:uploadedBatch.find(item=>item.job.id===candidate.id)?.job.batchId??uploaded?.job.batchId??null,
        jobNumber:candidate.jobNumber,
        status:candidate.status,
        pageCount:candidate.pageCount,
        copies:candidate.copies,
        colourMode:candidate.colourMode as "Mono"|"Colour",
        paperSize:candidate.paperSize as CustomerPrintRequestSettings["paperSize"],
        isDuplex:candidate.isDuplex,
        orientation:(candidate.orientation??"AUTO") as CustomerPrintRequestSettings["orientation"],
        printKind:(candidate.printKind??"DOCUMENT") as "DOCUMENT"|"PHOTO"|"ID_DOCUMENT",
        photoSizeCode:candidate.photoSizeCode??null,
        photoCount:candidate.photoCount,
        photoUnitPriceMinor:candidate.photoUnitPriceMinorSnapshot,
        currency:candidate.currency,
        quotedAmountMinor:candidate.quotedAmountMinor,
        amountMajor:candidate.quotedAmountMinor/100,
        pricingVersion:candidate.pricingVersion,
        quoteExpiresUtc:candidate.quoteExpiresUtc??""
      };
      setQuotesByJobId(current=>({...current,[candidate.id]:revisedQuote}));
      setQuote(revisedQuote);
    }
    rememberCustomerPayment(candidate.payment);
    rememberBatchPayment(candidate.payment?.batchPayment);
  }
  const [paymentReference,setPaymentReference]=useState("");
  const [paymentMethod,setPaymentMethod]=useState<"UPI"|"CASH">(paymentOptions.upiEnabled?"UPI":"CASH");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [uploadMode,setUploadMode]=useState<"LOCAL"|"CLOUD">(
    localAgent?.endpoints.length ? "LOCAL" : "CLOUD"
  );
  const [uploadModeManuallySelected,setUploadModeManuallySelected]=useState(false);
  const privatePrintingStorageKey="smartprint.private-printing:"+sessionToken;
  const privacyChoiceManualStorageKey="smartprint.privacy-choice-manual:"+sessionToken;
  const [privatePrinting,setPrivatePrinting]=useState(()=>sessionStorage.getItem("smartprint.private-printing:"+sessionToken)==="true");
  const [privacyChoiceManuallySelected,setPrivacyChoiceManuallySelected]=useState(
    ()=>sessionStorage.getItem("smartprint.privacy-choice-manual:"+sessionToken)==="true"
  );
  const canOfferPrivatePrinting=Boolean(localAgent?.agentDeviceId && localAgent.documentEncryptionPublicKey);

  useEffect(()=>{
    if(privatePrinting)sessionStorage.setItem(privatePrintingStorageKey,"true");
    else sessionStorage.removeItem(privatePrintingStorageKey);
  },[privatePrinting,privatePrintingStorageKey]);

  useEffect(()=>{
    if(privacyChoiceManuallySelected)sessionStorage.setItem(privacyChoiceManualStorageKey,"true");
    else sessionStorage.removeItem(privacyChoiceManualStorageKey);
  },[privacyChoiceManuallySelected,privacyChoiceManualStorageKey]);

  const secureQueueFallbackEnabled = cloudAssistedPrintingEnabled || !localAgent?.endpoints.length || localFallbackSuggested;

  useEffect(()=>{
    if(privatePrinting){setUploadMode("LOCAL");return;}
    setUploadMode(current=>resolveCustomerUploadMode(
      current,
      Boolean(localAgent?.endpoints?.length),
      secureQueueFallbackEnabled,
      uploadModeManuallySelected
    ));
  },[secureQueueFallbackEnabled,localAgent?.endpoints.join("|"),uploadModeManuallySelected,privatePrinting]);

  useEffect(()=>{
    if(privatePrinting){setUploadMode("LOCAL");return;}
    if(uploadModeManuallySelected)return;
    if((localFallbackSuggested||!localReady)&&secureQueueFallbackEnabled) {
      setUploadMode("CLOUD");
    } else if(!localFallbackSuggested&&localReady&&localAgent?.endpoints.length) {
      setUploadMode("LOCAL");
    }
  },[localFallbackSuggested,localReady,localAgent?.endpoints.length,secureQueueFallbackEnabled,uploadModeManuallySelected,privatePrinting]);

  const customerUploadUnavailable = privatePrinting
    ? !canOfferPrivatePrinting
    : !secureQueueFallbackEnabled && !localAgent?.endpoints.length;

  useEffect(()=>{
    if(!paymentOptions.upiEnabled&&paymentOptions.cashEnabled)setPaymentMethod("CASH");
    else if(paymentOptions.upiEnabled)setPaymentMethod("UPI");
  },[paymentOptions.upiEnabled,paymentOptions.cashEnabled]);

  useEffect(()=>{
    const storageKey=`qrtoprint.customer-active-job:${publicCode}`;
    if(!uploaded&&uploadedBatch.length===0&&!batchName.trim()){
      localStorage.removeItem(storageKey);
      return;
    }
    // Store only job metadata, never the bearer session capability.
    localStorage.setItem(storageKey,JSON.stringify({
      sessionMarker:sessionToken.slice(-16),
      uploaded: uploaded??uploadedBatch[0],
      uploadedBatch,
      uploadedFileFingerprints,
      batchId,
      batchName,
      quote,
      fileGroupNames,
      groupSettings,
      fileSettings,
      printSettingsByJobId,
      printKindsByJobId,
      quotesByJobId,
      photoSettingsByJobId
    } satisfies StoredCustomerDraft));
  },[publicCode,sessionToken,uploaded,uploadedBatch,uploadedFileFingerprints,batchId,batchName,quote,fileGroupNames,groupSettings,fileSettings,printSettingsByJobId,printKindsByJobId,quotesByJobId,photoSettingsByJobId]);

  const batchItemsForStatus=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchStatusKey=batchItemsForStatus.map(item=>item.job.id).join("|");
  const canStartNewSession=!!uploaded&&isCustomerBatchFinished(batchItemsForStatus,batchStatuses,uploaded.job.id,jobStatus?.status);
  const groupNameForFile=(candidate:File)=>fileGroupNames[customerFileFingerprint(candidate)]??customerBatchGroupName(candidate);
  const defaultsForGroup=(name:string):CustomerGroupSettings=>{
    const baseline:CustomerGroupSettings={
      copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"
    };
    return {...baseline,...(groupSettings[name]??{})};
  };
  const settingsForFile=(candidate:File):CustomerSavedPrintSettings=> {
    if(isSmartPrintIdDocument(candidate)) return {
      copies:1,colourMode:"Colour",isDuplex:false,paperSize:"A4",orientation:"PORTRAIT",advancedEnabled:false
    };
    const fingerprint=customerFileFingerprint(candidate);
    const defaults=defaultsForGroup(groupNameForFile(candidate));
    const own=fileSettings[fingerprint];
    if(photoSettingsByFileFingerprint[fingerprint])return {
      copies:1,colourMode:"Colour",paperSize:"A4",isDuplex:false,orientation:"AUTO",advancedEnabled:false
    };
    const selected=own?.custom?{...defaults,...own}:defaults;
    return {
      copies:Number(normalizeCustomerCopies(selected.copies)),
      colourMode:selected.colourMode,
      isDuplex:selected.isDuplex,
      paperSize:selected.advancedEnabled?selected.paperSize:"A4",
      orientation:selected.advancedEnabled?selected.orientation:"AUTO",
      advancedEnabled:selected.advancedEnabled
    };
  };

  useEffect(()=>{
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    if(items.length===0)return;
    let active=true;
    let refreshing=false;
    const refreshAll=async()=>{
      if(refreshing)return;
      refreshing=true;
      try{
        const results=await Promise.all(items.map(async item=>{
          try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
          catch{return null;}
        }));
        if(!active)return;
        const fresh=results.reduce<Record<string,Awaited<ReturnType<typeof getCustomerJobStatus>>>>((map,item)=>{
          if(item)map[item[0]]=item[1];
          return map;
        },{});
        rememberBatchStatuses(fresh);
        const latestBatchPayment=results
          .map(item=>item?.[1].payment?.batchPayment??null)
          .filter((item):item is CustomerBatchPayment=>item!==null)
          .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
        rememberBatchPayment(latestBatchPayment);
        const selectedId=uploaded?.job.id;
        if(selectedId&&fresh[selectedId]){
          const selected=fresh[selectedId];
          if(selected.status==="Uploaded"||selected.status==="UPLOADED"||selected.status==="AWAITING_CONFIGURATION"){
            setJobStatus(null);
            setPayment(null);
          } else {
            rememberCustomerStatus(selected);
          }
        }
      } finally {refreshing=false;}
    };
    void refreshAll();
    const awaitingPrivatePurge=jobStatus?.privatePrinting===true&&
      jobStatus.status==="COMPLETED"&&!jobStatus.privateDocumentPurgedUtc;
    const timer=window.setInterval(()=>void refreshAll(),awaitingPrivatePurge?3000:30000);
    return ()=>{active=false;window.clearInterval(timer);};
  },[sessionToken,batchStatusKey,uploaded?.job.id,jobStatus?.privatePrinting,jobStatus?.status,jobStatus?.privateDocumentPurgedUtc]);

  useEffect(()=>{
    if(!uploaded)return;
    let active=true;
    void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
      if(!active)return;
      // A newly uploaded job starts in AWAITING_CONFIGURATION on the API.
      // Treat that state like the legacy Uploaded value so the customer can
      // choose print settings and request the first quote after a reload.
      if(refreshed.status==="Uploaded"||refreshed.status==="UPLOADED"||refreshed.status==="AWAITING_CONFIGURATION"){
        setJobStatus(null);
        setPayment(null);
        return;
      }
      rememberCustomerStatus(refreshed);
    }).catch(exception=>{
      if(active)setError(exception instanceof ApiError?exception.message:"Could not restore this print request.");
    });
    return ()=>{active=false;};
  },[sessionToken,uploaded?.job.id]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    const tokens=files
      .map(candidate=>smartPrintNativePdfPreviewTokens.get(candidate))
      .filter((token):token is string=>Boolean(token));
    postSmartPrintNativeMessage("retainCustomerPdfPreviews|"+tokens.join(","));
  },[files,nativeAppMode]);

  useEffect(()=>{
    if(!nativeAppMode)return;
    return ()=>{postSmartPrintNativeMessage("retainCustomerPdfPreviews|none");};
  },[nativeAppMode]);

  useEffect(()=>()=>{photoPreviewUrlsRef.current.forEach(url=>URL.revokeObjectURL(url));photoPreviewUrlsRef.current.clear();},[]);

  useEffect(()=>{
    if(!file){setPreviewUrl(null);return;}
    // Some Android document providers expose PDFs with an empty or generic
    // MIME type. Preserve the existing blob preview with the correct PDF type.
    const previewSource=isCustomerPdf(file)&&file.type.toLowerCase()!=="application/pdf"
      ?new Blob([file],{type:"application/pdf"})
      :file;
    const url=URL.createObjectURL(previewSource);
    setPreviewUrl(url);
    return ()=>URL.revokeObjectURL(url);
  },[file]);

  async function upload() {
    if(batchLockedForPayment){
      setError("This batch already has a payment attempt. Start a new batch for additional files.");
      return;
    }
    if(files.length===0)return;
    const containsIdDocument=files.some(isSmartPrintIdDocument);
    const effectivePrivatePrinting=privatePrinting||containsIdDocument;
    if(containsIdDocument&&uploadedBatch.length>0&&!uploadedBatch.every(item=>item.job.privatePrinting===true)) {
      setError("Prepared ID cards require Private Printing. Finish the current Standard batch, then start a new private batch for this ID sheet.");
      return;
    }
    if(containsIdDocument&&!privatePrinting)setPrivatePrinting(true);
    const effectiveUploadMode: "LOCAL"|"CLOUD"=effectivePrivatePrinting?"LOCAL":uploadMode;
    if(effectivePrivatePrinting&&(!localAgent||!canOfferPrivatePrinting)) {
      setError("Private Printing is not ready at this shop yet. No document has been uploaded. Ask the shop operator to reconnect the paired computer.");
      return;
    }
    if(effectivePrivatePrinting&&uploadMode!=="LOCAL")setUploadMode("LOCAL");
    if(effectiveUploadMode==="LOCAL"&&!effectivePrivatePrinting&&!localReady) {
      setError(nativeAppMode ? "Check shop Wi-Fi." : "Check shop Wi-Fi access above before starting a direct local upload.");
      return;
    }
    let currentBatchId=batchId||crypto.randomUUID();
    let currentBatchName=batchName.trim();
    setBatchId(currentBatchId);
    const completedFingerprints=new Set(uploadedFileFingerprints);
    const results=[...uploadedBatch];
    const savedSettings={...printSettingsByJobId};
    const savedPhotoSettings={...photoSettingsByJobId};
    const savedPrintKinds={...printKindsByJobId};
    const pending=files.filter(candidate=>!completedFingerprints.has(customerFileFingerprint(candidate)));
    if(pending.length===0){setError(nativeAppMode ? "Files already uploaded. Choose more." : "All selected files are already uploaded. Choose another document or folder.");return;}
    setBusy(true);setError("");setQuote(null);setQuotesByJobId({});setJobStatus(null);setUploadProgress({completed:0,total:pending.length,currentFile:pending[0]?.name??null});
    try {
      for(let index=0;index<pending.length;index++){
        const candidate=pending[index];
        setUploadProgress({completed:index,total:pending.length,currentFile:candidate.name});
        const fingerprint=customerFileFingerprint(candidate);
        const batchGroupName=groupNameForFile(candidate);
        const settings=settingsForFile(candidate);
        const idDocument=isSmartPrintIdDocument(candidate);
        const photoSettings=photoSettingsByFileFingerprint[fingerprint];
        const printKind=idDocument?"ID_DOCUMENT":photoSettings?"PHOTO":"DOCUMENT";
        // ID Studio sheets remain Private Printing only. Saved Photo Studio sheets
        // follow the customer's selected batch privacy mode (Private by default).
        if(idDocument&&!effectivePrivatePrinting) throw new ApiError("Prepared ID cards must use Private Printing. No normal cloud upload is allowed.",409);
        const created=effectiveUploadMode==="LOCAL"&&localAgent
          ? await uploadCustomerDocumentLocally(sessionToken,candidate,localAgent,currentBatchId,batchGroupName,currentBatchName||undefined,cloudAssistedPrintingEnabled,settings,effectivePrivatePrinting,localReady,printKind,photoSettings)
          : idDocument
            ? (()=>{throw new ApiError("The paired shop computer's encrypted transfer is not ready. No ID document was uploaded or sent to cloud storage.",409);})()
            : await uploadCustomerDocument(sessionToken,candidate,currentBatchId,batchGroupName,currentBatchName||undefined,settings,printKind,photoSettings);
        if(created.job.batchId&&created.job.batchId!==currentBatchId) {
          if(results.length>0) {
            throw new ApiError("This file belongs to an earlier batch. Refresh the original print request before adding more files; SmartPrint will not mix two batches.",409);
          }
          currentBatchId=created.job.batchId;
          setBatchId(currentBatchId);
        }
        if(created.job.batchName) {
          currentBatchName=created.job.batchName;
          setBatchName(currentBatchName);
        }
        if(!results.some(item=>item.job.id===created.job.id))results.push(created);
        savedSettings[created.job.id]=settings;
        if(photoSettings)savedPhotoSettings[created.job.id]=photoSettings;
        savedPrintKinds[created.job.id]=idDocument?"ID_DOCUMENT":photoSettings?"PHOTO":"DOCUMENT";
        setPrintKindsByJobId({...savedPrintKinds});
        completedFingerprints.add(fingerprint);
        setUploadedBatch([...results]);
        setUploadedFileFingerprints([...completedFingerprints]);
        setUploadProgress({completed:index+1,total:pending.length,currentFile:pending[index+1]?.name??null});
      }
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setPrintKindsByJobId(savedPrintKinds);
      setUploadedBatch(results);
      setUploaded(results[0]??null);
      setQuote(null);setJobStatus(null);setPayment(null);setPaymentReference("");
      setSelectedFileIndex(0);
      setFiles([]);
      // Upload succeeded; release only the temporary decrypted picker copies.
      // The saved encrypted Photo Studio document remains available in the app library.
      postSmartPrintNativeMessage("releasePhotoStudioUploadFiles");
      if(paymentOptions.instantPaymentEnabled)await getQuote(results,savedSettings,savedPhotoSettings,savedPrintKinds);
    } catch(exception) {
      setPrintSettingsByJobId(savedSettings);
      setPhotoSettingsByJobId(savedPhotoSettings);
      setPrintKindsByJobId(savedPrintKinds);
      setUploadedBatch(results);
      setUploadedFileFingerprints([...completedFingerprints]);
      if(results.length>0)setUploaded(results[0]);
      const confirmedPreTicketRejection=effectiveUploadMode==="LOCAL"&&exception instanceof LocalUploadPreflightError;
      const agentUnavailable=exception instanceof LocalUploadPreflightError&&
        ["local_print_agent_unavailable","local_print_printer_unavailable"].includes(exception.code??"");
      const batchCheckoutLocked=exception instanceof LocalUploadPreflightError&&
        exception.code==="batch_upload_locked_after_payment_start";
      const canFallbackToCloud=!privatePrinting&&!batchCheckoutLocked&&canFallbackToCloudFromLocalFailure(
        confirmedPreTicketRejection,
        cloudAssistedPrintingEnabled||agentUnavailable
      );
      if(canFallbackToCloud){setUploadModeManuallySelected(true);setUploadMode("CLOUD");}
      if(exception instanceof LocalUploadOutcomeUnknownError&&effectiveUploadMode==="LOCAL"&&!privatePrinting&&secureQueueFallbackEnabled) {
        // Local ticket and grant are preserved. The next retry will resume that
        // same PrintJob over HTTPS rather than creating a second cloud upload.
        setUploadModeManuallySelected(true);
        setUploadMode("CLOUD");
      }
      const uploadError=exception instanceof LocalUploadOutcomeUnknownError
        ? privatePrinting
          ? "Your file is encrypted and attached to the same private print request. The shop computer is still checking it; tap Retry to refresh the status without creating a duplicate."
          : cloudAssistedPrintingEnabled
            ? "Shop Wi-Fi was interrupted. Cloud-assisted upload is now selected for secure HTTPS continuation. SmartPrint has kept this file attached to its original job; tap Retry to resume that same upload without creating another print job."
            : "Shop Wi-Fi was interrupted. SmartPrint has kept this file attached to its original job. Reconnect to the shop computer and tap Retry; the same upload will resume without creating a second print job."
        : batchCheckoutLocked
          ? "Checkout has already started for this batch, so this unfinished file was not included. Start a new batch for this file."
          : confirmedPreTicketRejection
          ? exception instanceof LocalUploadPreflightError && exception.code==="local_upload_ticket_expired"
            ? canFallbackToCloud
              ? "The earlier shop Wi-Fi attempt expired before a job was created. Cloud-assisted HTTPS is now selected; tap Retry to start a fresh transfer."
              : "The earlier shop Wi-Fi attempt expired before a job was created. Reconnect to the shop computer, check Wi-Fi, then tap Retry to start a fresh transfer."
            : exception instanceof LocalUploadPreflightError && exception.code==="batch_local_agent_mismatch"
              ? canFallbackToCloud
                ? "Some files in this batch are stored on another shop computer. Cloud-assisted upload is now selected; retry the remaining files in this same batch. The shop can route the complete batch to the computer holding the existing files."
                : "Some files in this batch are on another shop computer. Cloud-assisted upload is not included in this shop's plan. Ask the shop to restore the original computer before retrying the same batch."
              : canFallbackToCloud
                ? "The API rejected local upload before creating a ticket. Cloud-assisted HTTPS upload is now selected; retry the remaining files in this same batch."
                : "The shop cannot accept a local upload right now, and Cloud-assisted upload is not included in its plan. Ask the shop operator to restore the local Agent/printer connection, then retry the same batch."
          : exception instanceof ApiError ? exception.message
          : "Upload stopped after "+completedFingerprints.size+" file(s). Retry the selected files to continue; completed files will be skipped.";
      setError(uploadError);
    } finally {setBusy(false);setUploadProgress(null);}
  }

  function selectUploadedFile(item:UploadedPrintJob) {
    setUploaded(item);setQuote(quotesByJobId[item.job.id]??null);setJobStatus(null);setPayment(null);setPaymentReference("");setError("");
  }

  async function getQuote(itemsOverride?:UploadedPrintJob[],settingsOverride?:Record<string,CustomerSavedPrintSettings>,photoSettingsOverride?:Record<string,CustomerPhotoSettings>,printKindsOverride?:Record<string,"DOCUMENT"|"PHOTO"|"ID_DOCUMENT">) {
    const items=itemsOverride??(uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]));
    if(items.length===0)return;
    setBusy(true);setError("");setJobStatus(null);
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of items) {
        const existingQuote=nextQuotes[item.job.id];
        const status=uploaded?.job.id===item.job.id&&jobStatus?.status
          ?jobStatus.status
          :batchStatuses[item.job.id]?.status??item.job.status;
        const quoteExpired=quoteExpiredForBatchItem(item);
        if(!quoteExpired&&existingQuote&&existingQuote.status!=="QUOTE_EXPIRED")continue;
        if(quoteExpired
          ? !["QUOTE_EXPIRED","AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status)
          : !["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status))continue;
        const requestedSettings=settingsOverride?.[item.job.id]??printSettingsByJobId[item.job.id];
        const settings:CustomerSavedPrintSettings={
          copies:requestedSettings?.copies??1,
          colourMode:requestedSettings?.colourMode??"Mono",
          isDuplex:requestedSettings?.isDuplex??false,
          paperSize:requestedSettings?.paperSize??"A4",
          orientation:requestedSettings?.orientation??"AUTO",
          advancedEnabled:requestedSettings?.advancedEnabled??false
        };
        const photoSettings=photoSettingsOverride?.[item.job.id]??photoSettingsByJobId[item.job.id];
        const idDocument=(printKindsOverride?.[item.job.id]??printKindsByJobId[item.job.id]??item.job.printKind)==="ID_DOCUMENT";
        try {
          nextQuotes[item.job.id]=await calculateCustomerQuote(sessionToken,item.job.id,{
            colourMode:photoSettings||idDocument?"Colour":settings.colourMode,
            paperSize:photoSettings||idDocument?"A4":settings.paperSize,
            isDuplex:photoSettings||idDocument?false:settings.isDuplex,
            copies:photoSettings?1:idDocument?1:settings.copies,
            orientation:photoSettings?"AUTO":idDocument?"PORTRAIT":settings.orientation,
            ...(photoSettings
              ?{printKind:"PHOTO" as const,photoSizeCode:photoSettings.sizeCode,photoCount:photoSettings.photoCount,photosPerPage:photoSettings.photosPerPage??photoSettings.photoCount}
              :idDocument?{printKind:"ID_DOCUMENT" as const}:{printKind:"DOCUMENT" as const})
          });
        } catch(exception) {
          failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      const statusResults=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      const freshStatuses=statusResults.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
        if(item)map[item[0]]=item[1];
        return map;
      },{});
      rememberBatchStatuses(freshStatuses);
      if(uploaded&&freshStatuses[uploaded.job.id]){
        const selected=freshStatuses[uploaded.job.id];
        rememberCustomerStatus(selected);
      }
      const selectedQuote=uploaded?nextQuotes[uploaded.job.id]??null:null;
      setQuote(selectedQuote??Object.values(nextQuotes)[0]??null);
      if(failures.length) setError(`Could not quote: ${failures.join(", ")}. Retry the batch quote.`);
      else if(!Object.keys(nextQuotes).length) setError("No files are waiting for a quote.");
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The shop could not calculate the batch quote.");
    } finally {setBusy(false);}
  }

  async function acceptQuote() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const pending=items.filter(item=>
      !["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&
      quotesByJobId[item.job.id]?.status==="AWAITING_CUSTOMER_APPROVAL");
    if(pending.length===0)return;
    setBusy(true);setError("");
    const nextQuotes={...quotesByJobId};
    const failures:string[]=[];
    try {
      for(const item of pending) {
        try {
          const accepted=await acceptCustomerQuote(sessionToken,item.job.id);
          const current=nextQuotes[item.job.id];
          if(current)nextQuotes[item.job.id]={...current,status:accepted.status};
        } catch {
          const fresh=await getCustomerJobStatus(sessionToken,item.job.id).catch(()=>null);
          if(fresh&&fresh.status!=="AWAITING_CUSTOMER_APPROVAL") {
            rememberBatchStatuses({[item.job.id]:fresh});
          } else failures.push(item.document.originalFileName);
        }
      }
      setQuotesByJobId(nextQuotes);
      if(uploaded) {
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
      const refreshedBatch=await Promise.all(items.map(async item=>{
        try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
        catch{return null;}
      }));
      rememberBatchStatuses(Object.fromEntries(refreshedBatch.filter((entry):entry is NonNullable<typeof entry>=>entry!==null)));
      setQuote(uploaded?nextQuotes[uploaded.job.id]??null:null);
      if(failures.length)setError(`Could not send: ${failures.join(", ")}. Refresh and retry.`);
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The batch could not be sent to the shop.");
    } finally {setBusy(false);}
  }

  async function refreshJobStatus() {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
      rememberCustomerStatus(refreshed);
    }
    catch(exception){setError(exception instanceof ApiError?exception.message:"Could not refresh job status.");}
    finally {setBusy(false);}
  }


  // Keep the customer informed as the shop reviews, verifies payment and prints.
  // Private jobs keep polling after print completion until shop-computer deletion is acknowledged.
  useEffect(()=>{
    if(!uploaded||!jobStatus)return;
    const liveStates=new Set([
      "AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","AWAITING_COUNTER_PAYMENT",
      "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","PAYMENT_REJECTED",
      "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY",
      "PRINT_AUTHORISED","PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING",
      "OUTPUT_UNCONFIRMED"
    ]);
    const waitingForPrivatePurge =
      jobStatus.status==="COMPLETED" &&
      Boolean(jobStatus.privatePrinting) &&
      !jobStatus.privateDocumentPurgedUtc;
    if(!liveStates.has(jobStatus.status)&&!waitingForPrivatePurge)return;
    let active=true;
    const timer=window.setInterval(()=>{
      void getCustomerJobStatus(sessionToken,uploaded.job.id).then(refreshed=>{
        if(!active)return;
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }).catch(()=>{/* Retry on the next interval without interrupting the flow. */});
    },8000);
    return ()=>{
      active=false;
      window.clearInterval(timer);
    };
  },[sessionToken,uploaded?.job.id,jobStatus?.status,jobStatus?.privatePrinting,jobStatus?.privateDocumentPurgedUtc]);

  async function refreshBatchStatuses() {
    const items=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
    const results=await Promise.all(items.map(async item=>{
      try{return [item.job.id,await getCustomerJobStatus(sessionToken,item.job.id)] as const;}
      catch{return null;}
    }));
    const fresh=results.reduce<Record<string,CustomerJobStatus>>((map,item)=>{
      if(item)map[item[0]]=item[1];
      return map;
    },{});
    rememberBatchStatuses(fresh);
    const latestBatchPayment=results
      .map(item=>item?.[1].payment?.batchPayment??null)
      .filter((item):item is CustomerBatchPayment=>item!==null)
      .sort((left,right)=>paymentTimestamp(right)-paymentTimestamp(left))[0];
    rememberBatchPayment(latestBatchPayment);
    const selected=uploaded?fresh[uploaded.job.id]:null;
    if(selected){
      rememberCustomerStatus(selected);
    }
  }

  async function beginPayment(method:"UPI"|"CASH",forceBatchCheckout=false) {
    if(!uploaded)return;
    setBusy(true);setError("");
    try {
      if(batchId&&(forceBatchCheckout||!hasLegacyIndividualPayment)){
        const attempt=await startCustomerBatchPayment(sessionToken,batchId,method);
        setBatchPayment(attempt);
        setPaymentMethod(method);
        await refreshBatchStatuses();
      } else {
        const attempt=await startCustomerPayment(sessionToken,uploaded.job.id,method);
        rememberCustomerPayment(attempt);
        setPaymentMethod(method);
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
      }
    } catch(exception) {
      if(batchId)await refreshBatchStatuses();
      setError(exception instanceof ApiError?exception.message:"Could not start this payment method.");
    } finally {setBusy(false);}
  }

  async function submitPaymentReference() {
    setBusy(true);setError("");
    try {
      if(batchPayment&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"){
        await reportCustomerBatchPayment(sessionToken,batchPayment.id,paymentReference.trim());
        setPaymentReference("");
        await refreshBatchStatuses();
      } else {
        if(!payment||!uploaded)return;
        await reportCustomerUpiPayment(sessionToken,payment.id,paymentReference.trim());
        setPaymentReference("");
        const refreshed=await getCustomerJobStatus(sessionToken,uploaded.job.id);
        rememberCustomerStatus(refreshed);
        rememberBatchPayment(refreshed.payment?.batchPayment);
      }
    } catch(exception) {
      setError(exception instanceof ApiError?exception.message:"The payment reference could not be submitted.");
    } finally {setBusy(false);}
  }

  function applyPhotoSheets(sheets:PreparedPhotoSheet[]) {
    if(!photoStudioSourceFingerprint||!sheets.length)return;
    const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===photoStudioSourceFingerprint);
    if(index<0){setError("The source photo is no longer in this batch. Select it again.");setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const generated=sheets.map(sheet=>sheet.file),nextFiles=[...files.slice(0,index),...generated,...files.slice(index+1)];
    const pending=nextFiles.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate)));
    const bytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0)+pending.reduce((sum,file)=>sum+file.size,0);
    if(uploadedBatch.length+pending.length>UPLOAD_LIMITS.maxBatchFiles||bytes>UPLOAD_LIMITS.maxBatchBytes){setError("Prepared photo sheets exceed the batch limit. Reduce the copies or start a new batch.");setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);return;}
    const group=groupNameForFile(files[index]),groups={...fileGroupNames},settings={...fileSettings},photos={...photoSettingsByFileFingerprint},previews={...photoPreviewPagesByFingerprint};
    delete groups[photoStudioSourceFingerprint];delete settings[photoStudioSourceFingerprint];delete photos[photoStudioSourceFingerprint];delete previews[photoStudioSourceFingerprint];
    for(const sheet of sheets){
      const fp=customerFileFingerprint(sheet.file);groups[fp]=group;
      settings[fp]={copies:"1",custom:true,colourMode:"Colour",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"};
      photos[fp]={sizeCode:sheet.sizeCode,photoCount:sheet.photoCount,photosPerPage:sheet.photosPerPage};
      const urls=(sheet.previewPages??[]).map(page=>{const url=URL.createObjectURL(page);photoPreviewUrlsRef.current.add(url);return url;});
      if(urls.length)previews[fp]=urls;
    }
    setFiles(nextFiles);setSelectedFileIndex(index);setSelectedPreparedPhotoPage(0);setFileGroupNames(groups);setFileSettings(settings);setPhotoSettingsByFileFingerprint(photos);setPhotoPreviewPagesByFingerprint(previews);setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);setError("");
  }
  function openPhotoStudio(candidate:File){
    const fingerprint=customerFileFingerprint(candidate);
    if(uploadedFileFingerprints.includes(fingerprint)){
      setError("This photo has already been sent to the shop. The shop can prepare it from the review queue; reopening it here would create a duplicate print request.");
      return;
    }
    if(!photoRatesAvailable){setError("Photo printing is not configured at this shop yet. Please ask the shop owner to enable photo pricing.");return;}
    setPhotoStudioSource(candidate);
    setPhotoStudioSourceFingerprint(fingerprint);
    setError("");
  }
  function openCustomerPdfPreview(candidate:File) {
    const token=smartPrintNativePdfPreviewTokens.get(candidate);
    if(!token||!postSmartPrintNativeMessage("previewCustomerPdf|"+token)) {
      setError("Please select this PDF again before opening its preview.");
      return;
    }
    setError("");
  }
  function chooseFiles(next:File[], nativeGroupNames?:string[], nativePreviewTokens?:Array<string|null>, nativePrintKinds?:Array<string|null>, nativePhotoSettings?:Array<CustomerPhotoSettings|null>) {
    if(batchLockedForPayment){
      setError("This batch already has a payment attempt. Start a new batch for additional files.");
      return;
    }
    if(nativePreviewTokens?.length===next.length) {
      next.forEach((candidate,index)=>{
        const token=nativePreviewTokens[index];
        if(token&&isCustomerPdf(candidate))smartPrintNativePdfPreviewTokens.set(candidate,token);
      });
    }
    if(nativePrintKinds?.length===next.length) {
      next.forEach((candidate,index)=>{
        if(nativePrintKinds[index]==="ID_DOCUMENT"||nativePrintKinds[index]==="PHOTO") {
          (candidate as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind=nativePrintKinds[index]!;
        }
      });
    }
    if(nativePhotoSettings?.length===next.length) {
      next.forEach((candidate,index)=>{
        const settings=nativePhotoSettings[index];
        if(settings&&isValidNativePhotoSettings(settings)) {
          (candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings=settings;
        }
      });
    }
    let nestedSkipped=0;
    const rootFiles=next.filter(candidate=>{
      const relativePath=(candidate as File & {webkitRelativePath?:string}).webkitRelativePath??"";
      const parts=relativePath.split("/").filter(Boolean);
      if(relativePath&&parts.length>2){nestedSkipped++;return false;}
      return true;
    });
    const supported=rootFiles.filter(candidate=>/\.(pdf|jpe?g|png)$/i.test(candidate.name));
    const malformedPhotoDocument=supported.some(candidate=>
      (candidate as File & {__smartPrintPrintKind?:string}).__smartPrintPrintKind==="PHOTO"&&!isValidNativePhotoSettings(
        (candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings
      )
    );
    if(malformedPhotoDocument){setError("This saved Photo Studio sheet is missing its print details. Refresh the private document library and select it again.");return;}
    const incomingIdDocument=supported.some(isSmartPrintIdDocument);
    const incomingPhotoDocument=supported.some(isSmartPrintPhotoDocument);
    if(incomingIdDocument&&uploadedBatch.length>0&&!uploadedBatch.every(item=>item.job.privatePrinting===true)) {
      setError("Prepared ID cards require Private Printing. Finish the current Standard batch, then start a new private batch for this ID sheet.");
      return;
    }
    if(incomingIdDocument) {
      // ID Studio remains Private Printing only.
      setPrivatePrinting(true);
      setUploadMode("LOCAL");
      setUploadModeManuallySelected(true);
    } else if(incomingPhotoDocument&&uploadedBatch.length===0&&!privacyChoiceManuallySelected) {
      // A saved Photo Studio sheet defaults to Private Printing, but the
      // customer can change the batch choice to Standard before uploading.
      setPrivatePrinting(true);
    }
    for(const candidate of supported){
      const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim().slice(0,80);
      if(nativeGroup)(candidate as File & {__smartPrintBatchGroup?:string}).__smartPrintBatchGroup=nativeGroup;
    }
    if(incomingPhotoDocument){
      setPhotoSettingsByFileFingerprint(current=>{
        const updated={...current};
        supported.forEach(candidate=>{
          const settings=(candidate as File & {__smartPrintPhotoSettings?:CustomerPhotoSettings}).__smartPrintPhotoSettings;
          if(settings)updated[customerFileFingerprint(candidate)]=settings;
        });
        return updated;
      });
    }
    const unsupportedSkipped=rootFiles.length-supported.length;
    if(supported.length===0){setError("Choose printable PDF, JPG or PNG files.");return;}
    const oversized=supported.find(candidate=>candidate.size<=0||candidate.size>UPLOAD_LIMITS.maxFileBytes);
    if(oversized){setError(`“${oversized.name}” exceeds the ${UPLOAD_LIMITS.maxFileBytes / (1024 * 1024)} MB per-file limit.`);return;}
    const merged=new Map(files.map(candidate=>[customerFileFingerprint(candidate),candidate]));
    for(const candidate of supported)merged.set(customerFileFingerprint(candidate),candidate);
    const selected=Array.from(merged.values());
    const alreadyUploaded=new Set(uploadedFileFingerprints);
    const pending=selected.filter(candidate=>!alreadyUploaded.has(customerFileFingerprint(candidate)));
    const totalBytes=uploadedBatch.reduce((sum,item)=>sum+item.document.sizeBytes,0)+pending.reduce((sum,candidate)=>sum+candidate.size,0);
    if(uploadedBatch.length+pending.length>UPLOAD_LIMITS.maxBatchFiles||totalBytes>UPLOAD_LIMITS.maxBatchBytes){
      setError(nativeAppMode ? `${UPLOAD_LIMITS.maxBatchFiles} files · ${UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024)} MB max.` : `This batch can contain up to ${UPLOAD_LIMITS.maxBatchFiles} files and ${UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024)} MB in total.`);
      return;
    }
    setFileGroupNames(current=>{
      const updated={...current};
      for(const candidate of supported){
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        updated[customerFileFingerprint(candidate)]=nativeGroup||customerBatchGroupName(candidate);
      }
      return updated;
    });
    setGroupSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const nativeGroup=nativeGroupNames?.[next.indexOf(candidate)]?.trim();
        const group=nativeGroup||customerBatchGroupName(candidate);
        if(!updated[group])updated[group]={copies:"1",colourMode:"Mono",isDuplex:false,advancedEnabled:false,paperSize:"A4",orientation:"AUTO"};
      }
      return updated;
    });
    setFileSettings(current=>{
      const updated={...current};
      for(const candidate of supported) {
        const key=customerFileFingerprint(candidate);
        if(!updated[key])updated[key]={...defaultsForGroup(fileGroupNames[key]??customerBatchGroupName(candidate)),custom:false};
      }
      return updated;
    });
    setFiles(selected);setSelectedFileIndex(0);
    setError(nestedSkipped? "Nested folders are skipped; only files directly inside each selected folder are included.":unsupportedSkipped? "Unsupported file types were skipped.": "");
  }

  function removeSelectedFile(fingerprint: string) {
    if (busy) return;
    const index = files.findIndex(candidate => customerFileFingerprint(candidate) === fingerprint);
    if (index < 0) return;
    const nextFiles = files.filter(candidate => customerFileFingerprint(candidate) !== fingerprint);
    setFiles(nextFiles);
    setSelectedFileIndex(current => current === index
      ? Math.min(index, Math.max(0, nextFiles.length - 1))
      : current > index ? current - 1 : current);
    setFileGroupNames(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setFileSettings(current => {
      const next = { ...current };
      delete next[fingerprint];
      return next;
    });
    setPhotoSettingsByFileFingerprint(current=>{const next={...current};delete next[fingerprint];return next;});
    setError("");
  }

  const pendingFilesCount=files.filter(candidate=>!uploadedFileFingerprints.includes(customerFileFingerprint(candidate))).length;
  const uploadGroups=Array.from(new Set(files.map(candidate=>groupNameForFile(candidate)))).map(name=>({
    name, files:files.filter(candidate=>groupNameForFile(candidate)===name)
  }));
  const quoteItems=uploadedBatch.length?uploadedBatch:(uploaded?[uploaded]:[]);
  const batchLockedForPayment=(batchPayment!==null&&batchPayment.status!=="REJECTED")||
    (payment!==null&&!payment.batchPayment&&["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status));
  const statusForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&jobStatus?.status
      ? jobStatus.status
      : batchStatuses[item.job.id]?.status??item.job.status;
  const quoteExpiredForBatchItem=(item:UploadedPrintJob)=>{
    const status=statusForBatchItem(item);
    if(status==="QUOTE_EXPIRED")return true;
    const expiry=quotesByJobId[item.job.id]?.quoteExpiresUtc??batchStatuses[item.job.id]?.quoteExpiresUtc;
    if(!expiry)return ["AWAITING_CUSTOMER_APPROVAL","AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(status);
    const expiresAt=Date.parse(expiry);
    return !Number.isFinite(expiresAt)||expiresAt<=Date.now();
  };
  const batchCheckoutStatuses=new Set([
    "REJECTED","REFUNDED","AWAITING_PAYMENT","PAYMENT_REJECTED",
    "AWAITING_CUSTOMER_PAYMENT","PAYMENT_REPORTED","AWAITING_COUNTER_PAYMENT",
    "PAID_AUTHORISED","LOCAL_QUEUE_CLAIMED","LOCAL_QUEUE_READY","PRINT_AUTHORISED",
    "PRINT_SUBMITTING","SPOOLER_ACCEPTED","PRINTING","OUTPUT_UNCONFIRMED","COMPLETED"
  ]);
  const batchCheckoutReady=quoteItems.length>0&&quoteItems.every(item=>batchCheckoutStatuses.has(statusForBatchItem(item)));
  const batchPayableItems=quoteItems.filter(item=>["AWAITING_PAYMENT","PAYMENT_REJECTED"].includes(statusForBatchItem(item)));
  const batchPayableTotal=batchPayableItems.reduce((total,item)=>
    total+(batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0),0);
  const hasLegacyIndividualPayment=quoteItems.some(item=>{
    const itemPayment=uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
    return Boolean(itemPayment&&!itemPayment.batchPayment&&
      ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(itemPayment.status));
  });
  const hasSelectedLegacyIndividualPayment=payment!==null&&!payment.batchPayment&&
    ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED","PARTIALLY_REFUNDED"].includes(payment.status);
  const batchPaymentStarted=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const acceptedQuoteExpired=shouldBlockBatchCheckoutForExpiredQuote(
    batchPayment?.status,
    quoteItems.some(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))&&quoteExpiredForBatchItem(item))
  );
  const batchCheckoutVisible=Boolean(batchId)&&!hasLegacyIndividualPayment&&
    (batchPaymentStarted||(!acceptedQuoteExpired&&batchCheckoutReady&&batchPayableItems.length>0));
  const batchCheckoutUsesPayment=batchPayment!==null&&batchPayment.status!=="REJECTED";
  const legacyIndividualPayment=!batchId||hasSelectedLegacyIndividualPayment;
  const batchCheckoutAmount=batchCheckoutUsesPayment?batchPayment!.amountMinor:batchPayableTotal;
  const batchCheckoutFileCount=batchCheckoutUsesPayment?batchPayment!.fileCount:batchPayableItems.length;
  const batchCheckoutCurrency=batchCheckoutUsesPayment?batchPayment!.currency:(batchPayableItems[0]?.job.currency??"INR");
  const customerPaymentForBatchItem=(item:UploadedPrintJob)=>
    uploaded?.job.id===item.job.id&&payment
      ?payment
      :batchStatuses[item.job.id]?.payment??null;
  const batchPaymentItems=batchCheckoutUsesPayment
    ?quoteItems.filter(item=>customerPaymentForBatchItem(item)?.batchPaymentId===batchPayment!.id)
    :batchPayableItems;
  const batchCheckoutLineItems=batchCheckoutUsesPayment?batchPaymentItems:batchPayableItems;
  const batchPaymentAllocatedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.amountMinor??0),0);
  const batchPaymentRefundedTotal=batchPaymentItems.reduce((total,item)=>
    total+(customerPaymentForBatchItem(item)?.refundedAmountMinor??0),0);
  const batchPaymentAllocationComplete=batchCheckoutUsesPayment&&
    batchPaymentItems.length===batchPayment!.fileCount&&
    batchPaymentAllocatedTotal===batchPayment!.amountMinor;
  const batchCheckoutHeading=batchCheckoutUsesPayment
    ?batchPayment!.status==="VERIFIED"?"BATCH PAYMENT VERIFIED"
      :batchPayment!.status==="CUSTOMER_REPORTED"?"PAYMENT REFERENCE SUBMITTED"
      :batchPayment!.status==="CASH_AWAITING_PAYMENT"?"PAY AT SHOP COUNTER"
      :"PAYMENT FOR ACCEPTED FILES"
    :"PAY FOR ACCEPTED FILES";
  const quoteEntries=quoteItems.map(item=>({item,quote:quotesByJobId[item.job.id]}))
    .filter(entry=>!!entry.quote&&!quoteExpiredForBatchItem(entry.item));
  const acceptedQuoteEntries=quoteEntries.filter(entry=>
    !["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))
  );
  const batchQuoteTotal=acceptedQuoteEntries.reduce((total,entry)=>
    total+(entry.quote?.quotedAmountMinor??0),0);
  const acceptedDocumentEntries=acceptedQuoteEntries.filter(entry=>entry.quote?.printKind!=="PHOTO");
  const acceptedPhotoEntries=acceptedQuoteEntries.filter(entry=>entry.quote?.printKind==="PHOTO");
  const batchDocumentSourcePages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.pageCount??entry.item.job.pageCount),0);
  const batchPhotoCount=acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.photoCount??0),0);
  const batchPhotoAmount=acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.quotedAmountMinor??0),0);
  const batchMonoPrintedPages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Mono"?(entry.quote!.pageCount*entry.quote!.copies):0),0);
  const batchColourPrintedPages=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Colour"?(entry.quote!.pageCount*entry.quote!.copies):0),0);
  const batchPrintedPages=batchMonoPrintedPages+batchColourPrintedPages+
    acceptedPhotoEntries.reduce((total,entry)=>total+(entry.quote?.pageCount??0),0);
  const batchMonoAmount=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Mono"?(entry.quote!.quotedAmountMinor??0):0),0);
  const batchColourAmount=acceptedDocumentEntries.reduce((total,entry)=>
    total+(entry.quote?.colourMode==="Colour"?(entry.quote!.quotedAmountMinor??0):0),0);
  const quoteEligibleItems=quoteItems.filter(item=>!["REJECTED","REFUNDED"].includes(statusForBatchItem(item)));
  const canSendBatch=quoteEligibleItems.length>0&&
    acceptedQuoteEntries.length===quoteEligibleItems.length&&
    acceptedQuoteEntries.some(entry=>entry.quote?.status==="AWAITING_CUSTOMER_APPROVAL");
  const canCalculateBatch=!batchLockedForPayment&&!batchPaymentStarted&&quoteItems.some(item=>{
    const status=statusForBatchItem(item);
    if(shouldBlockQuoteRefreshForStatus(status))return false;
    if(quoteExpiredForBatchItem(item))return true;
    if(quotesByJobId[item.job.id]&&quotesByJobId[item.job.id].status!=="QUOTE_EXPIRED")return false;
    return !quotesByJobId[item.job.id]||quotesByJobId[item.job.id].status==="QUOTE_EXPIRED"||
      ["AWAITING_CONFIGURATION","UPLOADED","Uploaded"].includes(status);
  });

  const photoPriceMap={"35x45":photoPricing?.photo35x45Minor??0,"25x35":photoPricing?.photo25x35Minor??0,"2x2":photoPricing?.photo2x2Minor??0,"4x6":photoPricing?.photo4x6Minor??0};
  const photoAlreadyUploaded=Boolean(file&&uploadedFileFingerprints.includes(customerFileFingerprint(file)));
  return <>{photoStudioSource&&<PhotoStudio source={photoStudioSource} mode="customer" dark={dark} photoPrices={photoPriceMap} onCancel={()=>{setPhotoStudioSource(null);setPhotoStudioSourceFingerprint(null);}} onApply={applyPhotoSheets}/>}<section className="customer-print-flow">
    <div className="customer-flow-heading"><div><span>STEP 1</span><h2>{nativeAppMode?"Add files":"Add documents or images"}</h2></div><span className="upload-limit">PDF/JPG/PNG · Up to {UPLOAD_LIMITS.maxBatchFiles} files / {UPLOAD_LIMITS.maxBatchBytes / (1024 * 1024)} MB</span></div>
    <section className="customer-privacy-choice" aria-label="Document privacy choice">
      <div className="customer-privacy-choice-heading"><div><span>{nativeAppMode?"PRIVACY":"CHOOSE YOUR PRIVACY"}</span><strong>Choose how files are handled</strong></div><small>Applies to all files</small></div>
      <div className="customer-privacy-choice-grid">
        <button type="button" aria-pressed={privatePrinting} disabled={busy||uploadedBatch.length>0||!canOfferPrivatePrinting} className={privatePrinting?"customer-privacy-option private selected":"customer-privacy-option private"} onClick={()=>{setPrivacyChoiceManuallySelected(true);setPrivatePrinting(true);setUploadMode("LOCAL");setUploadModeManuallySelected(true);}}>
          <span className="customer-privacy-option-icon"><ShieldCheck size={22}/></span><span className="customer-privacy-option-copy"><strong>Private Printing</strong><small>Encrypted. Erased after printing.</small></span><span className="customer-privacy-option-state">{privatePrinting?"Selected":canOfferPrivatePrinting?"Choose private":"Shop setup needed"}</span>
        </button>
        <button type="button" aria-pressed={!privatePrinting} disabled={busy||uploadedBatch.length>0} className={!privatePrinting?"customer-privacy-option standard selected":"customer-privacy-option standard"} onClick={()=>{setPrivacyChoiceManuallySelected(true);setPrivatePrinting(false);}}>
          <span className="customer-privacy-option-icon"><FileText size={22}/></span><span className="customer-privacy-option-copy"><strong>Standard Printing</strong><small>Normal print queue. 7-day auto-delete.</small></span><span className="customer-privacy-option-state">{privatePrinting?"Choose standard":"Selected"}</span>
        </button>
      </div>
      <div className="customer-upload-local-note"><InfoTooltip label="Private Printing details"><span>Files are encrypted before leaving your device. They travel over shop Wi-Fi or a temporary encrypted relay. The paired computer erases its working copy after physical printing is confirmed. Your original stays on your device.</span></InfoTooltip> Private Printing details <InfoTooltip label="Standard Printing details"><span>Standard Printing uses the shop's normal print queue. Completed source copies are deleted automatically 7 days after confirmed output. Your original stays on your device.</span></InfoTooltip> Standard Printing details</div>
      {!canOfferPrivatePrinting&&<small className="customer-privacy-unavailable">Private Printing unavailable. <InfoTooltip label="Why Private Printing is unavailable"><span>The paired shop computer must be connected and have its secure document key enabled.</span></InfoTooltip></small>}
      {privatePrinting&&<div className="customer-private-confirmation"><ShieldCheck size={16}/><span><strong>Private Printing is on.</strong></span></div>}
      {files.some(isSmartPrintPhotoDocument)&&<div className="customer-private-confirmation" role="status">
        <ShieldCheck size={16}/>
        <span>{privatePrinting
          ? <><strong>Private Printing preselected.</strong> <InfoTooltip label="Change printing method"><span>You can still choose Standard Printing above before uploading.</span></InfoTooltip></>
          : <strong>Standard Printing selected.</strong>}</span>
      </div>}
    </section>
    {privatePrinting
      ? <div className="customer-private-transfer-note"><Lock size={16}/><span>End-to-end encrypted · Physical printing only</span></div>
      : <div className="customer-upload-modes">
      {localAgent?.endpoints.length
        ? <button type="button" aria-pressed={uploadMode==="LOCAL"} className={uploadMode==="LOCAL"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("LOCAL");}}><strong>Shop Wi-Fi · local transfer</strong><small>Local transfer · not encrypted</small></button>
        : <button type="button" className="customer-upload-mode customer-upload-mode-unavailable" disabled aria-disabled="true"><strong>Shop Wi-Fi · local transfer</strong><small>Unavailable</small></button>}
      {secureQueueFallbackEnabled&&<button type="button" aria-pressed={uploadMode==="CLOUD"} className={uploadMode==="CLOUD"?"customer-upload-mode selected":"customer-upload-mode"} onClick={()=>{setUploadModeManuallySelected(true);setUploadMode("CLOUD");}}><strong>{cloudAssistedPrintingEnabled?"Cloud-assisted · HTTPS":"Secure queue · HTTPS"}</strong><small>Secure upload · print when ready</small></button>}
    </div>}
    {!localAgent?.endpoints.length&&<small className="customer-upload-local-note">Shop connection details <InfoTooltip label="Shop connection unavailable"><span>The paired shop computer has not reported a reachable local connection.</span></InfoTooltip></small>}
    {customerUploadUnavailable&&<div className="customer-upload-local-note" role="status">Secure upload unavailable. <InfoTooltip label="Upload unavailable"><span>No document was uploaded. Ask the shop to reconnect its print service, then try again.</span></InfoTooltip></div>}
    {privatePrinting&&!localReady&&canOfferPrivatePrinting&&<div className="customer-upload-local-note" role="status">Encrypted online relay in use. <InfoTooltip label="Secure relay details"><span>Shop Wi-Fi is not verified. SmartPrint uses the encrypted online relay automatically; no Wi-Fi setup is needed.</span></InfoTooltip></div>}
    <div className="customer-upload-picker-grid"
      onDragOver={event=>{if(!batchLockedForPayment)event.preventDefault();}}
      onDrop={event=>{event.preventDefault();if(batchLockedForPayment)return;chooseFiles(Array.from(event.dataTransfer.files||[]));}}>
      <label className="customer-upload-zone">
        <input type="file" multiple disabled={batchLockedForPayment} accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png" onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews(),consumeSmartPrintNativePrintKinds(),consumeSmartPrintNativePhotoSettings());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FileUp size={21}/></span>
        <strong>{nativeAppMode?"Select files":"Select documents or images"}</strong>
        <small>PDF, JPG or PNG</small>
      </label>
      <label className="customer-upload-zone folder-upload-zone">
        <input type="file" multiple disabled={batchLockedForPayment} accept={nativeAppMode?"application/x-smartprint-folder-selection":".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"} {...({webkitdirectory:"true",directory:"true"} as any)} onChange={event=>{const selected=Array.from(event.currentTarget.files||[]);chooseFiles(selected,consumeSmartPrintNativePickerGroups(),consumeSmartPrintNativePdfPreviews(),consumeSmartPrintNativePrintKinds(),consumeSmartPrintNativePhotoSettings());event.currentTarget.value="";}}/>
        <span className="customer-upload-icon"><FolderOpen size={21}/></span>
        <strong>{nativeAppMode?"Choose folders":"Add a folder"}</strong>
        <small>{nativeAppMode?"Select multiple folders":"One folder at a time"}</small>
      </label>
    </div>
    {files.length>0&&<div className="customer-selected-files">
      <div className="customer-selected-files-head"><strong>{files.length} selected</strong><span>{formatBytes(files.reduce((total,item)=>total+item.size,0))} in this selection</span></div>
      <div className="customer-batch-groups">{uploadGroups.map(group=>{
        const defaults=defaultsForGroup(group.name);
        const isIdStudioGroup=group.files.length>0&&group.files.every(isSmartPrintIdDocument);
        return <section className="customer-folder-group" key={group.name}>
          <div className="customer-folder-group-head">
            <div className="customer-folder-title"><FolderOpen size={16}/><span><strong>{group.name}</strong><small>{group.files.length} file(s)</small></span></div>
            {isIdStudioGroup
              ? <small className="customer-upload-local-note">ID sheet · fixed settings. <InfoTooltip label="ID sheet settings"><span>One colour copy, A4 portrait and single-sided printing preserve the card's physical dimensions.</span></InfoTooltip></small>
              : <div className="customer-folder-defaults">
              <div><span>Colour</span><div className="customer-choice-row">
                <button type="button" className={defaults.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Mono"}}))}>B&W</button>
                <button type="button" className={defaults.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,colourMode:"Colour"}}))}>Colour</button>
              </div></div>
              <div><span>Sides</span><div className="customer-choice-row">
                <button type="button" className={!defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:false}}))}>Single</button>
                <button type="button" className={defaults.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setGroupSettings(current=>({...current,[group.name]:{...defaults,isDuplex:true}}))}>Double</button>
              </div></div>
              <label className="customer-folder-copies"><span>Copies · all files</span><input type="text" inputMode="numeric" pattern="[0-9]*" maxLength={3} value={defaults.copies}
                 onChange={event=>{
                   const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}
                 onBlur={event=>{
                   const copies=normalizeCustomerCopies(event.currentTarget.value);
                   setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),copies}}));
                 }}/></label>
              <div><span>Paper</span><strong className="customer-paper-default">{defaults.advancedEnabled?defaults.paperSize:"A4"}</strong></div>
              <label className="customer-advanced-toggle"><input type="checkbox" checked={defaults.advancedEnabled} onChange={event=>{
                 const advancedEnabled=event.currentTarget.checked;
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),advancedEnabled}}));
               }}/><span>Advanced settings</span></label>
            </div>}
            {!isIdStudioGroup&&defaults.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
              <label><span>Paper size</span><select value={defaults.paperSize} onChange={event=>{
                 const paperSize=event.currentTarget.value as CustomerGroupSettings["paperSize"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),paperSize}}));
               }}>
                <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option>
              </select></label>
              <label><span>Orientation</span><select value={defaults.orientation} onChange={event=>{
                 const orientation=event.currentTarget.value as CustomerGroupSettings["orientation"];
                 setGroupSettings(current=>({...current,[group.name]:{...(current[group.name]??defaults),orientation}}));
               }}>
                <option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option>
              </select></label>
            </div>}
          </div>
          <div className="customer-folder-file-list">{group.files.map(selected=>{
            const fingerprint=customerFileFingerprint(selected);
            const savedOwn=fileSettings[fingerprint]??{...defaults,custom:false};
            const own={...defaults,...savedOwn};
            const index=files.findIndex(candidate=>customerFileFingerprint(candidate)===fingerprint);
            return <div className={selectedFileIndex===index?"customer-folder-file-row selected":"customer-folder-file-row"} key={fingerprint}>
              <button type="button" className="customer-folder-file-name" onClick={()=>setSelectedFileIndex(index)}><FileText size={15}/><span><strong>{selected.name}</strong><small>{formatBytes(selected.size)}</small></span><PrintKindTag printKind={photoSettingsByFileFingerprint[fingerprint]?"PHOTO":isSmartPrintIdDocument(selected)?"ID_DOCUMENT":"DOCUMENT"}/></button>
              {!isSmartPrintIdDocument(selected)&&<button type="button" className={own.custom?"customer-file-custom active":"customer-file-custom"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...defaults,...(current[fingerprint]??{}),custom:!own.custom,...(!own.custom?{copies:defaults.copies,colourMode:defaults.colourMode,isDuplex:defaults.isDuplex,advancedEnabled:defaults.advancedEnabled,paperSize:defaults.paperSize,orientation:defaults.orientation}:{})}}))}>{own.custom?"Custom":"Folder"}</button>}
              <button type="button" className="customer-file-remove" aria-label={`Remove ${selected.name} from this selection`} title="Remove from this selection" disabled={busy} onClick={()=>removeSelectedFile(fingerprint)}><X size={13}/><span>Remove</span></button>
              {own.custom&&!isSmartPrintIdDocument(selected)&&<div className="customer-file-overrides">
                <label className="customer-folder-copies"><span>Copies</span><input type="text" inputMode="numeric" pattern="[0-9]*" maxLength={3} value={own.copies}
                   onChange={event=>{
                     const copies=event.currentTarget.value.replace(/\D/g,"").slice(0,3);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}
                   onBlur={event=>{
                     const copies=normalizeCustomerCopies(event.currentTarget.value);
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,copies}}));
                   }}/></label>
                <div className="customer-choice-row">
                  <button type="button" className={own.colourMode==="Mono"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Mono"}}))}>B&W</button>
                  <button type="button" className={own.colourMode==="Colour"?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,colourMode:"Colour"}}))}>Colour</button>
                </div>
                <div className="customer-choice-row">
                  <button type="button" className={!own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:false}}))}>Single</button>
                  <button type="button" className={own.isDuplex?"customer-choice selected":"customer-choice"} onClick={()=>setFileSettings(current=>({...current,[fingerprint]:{...own,custom:true,isDuplex:true}}))}>Double</button>
                </div>
                <div><span>Paper</span><strong className="customer-paper-default">{own.advancedEnabled?own.paperSize:"A4"}</strong></div>
                <label className="customer-advanced-toggle"><input type="checkbox" checked={own.advancedEnabled} onChange={event=>{
                   const advancedEnabled=event.currentTarget.checked;
                   setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,advancedEnabled}}));
                 }}/><span>Advanced settings</span></label>
                {own.advancedEnabled&&<div className="customer-file-overrides customer-folder-advanced">
                  <label><span>Paper size</span><select value={own.paperSize} onChange={event=>{
                     const paperSize=event.currentTarget.value as CustomerFileSettings["paperSize"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,paperSize}}));
                   }}>
                    <option value="A4">A4</option><option value="A3">A3</option><option value="A5">A5</option><option value="LETTER">Letter</option><option value="LEGAL">Legal</option>
                  </select></label>
                  <label><span>Orientation</span><select value={own.orientation} onChange={event=>{
                     const orientation=event.currentTarget.value as CustomerFileSettings["orientation"];
                     setFileSettings(current=>({...current,[fingerprint]:{...(current[fingerprint]??own),custom:true,orientation}}));
                   }}>
                    <option value="AUTO">Auto · follow document</option><option value="PORTRAIT">Portrait</option><option value="LANDSCAPE">Landscape</option>
                  </select></label>
                </div>}
              </div>}
            </div>;
          })}</div>
        </section>;
      })}</div>
    </div>}
    {files.length>0&&!privatePrinting&&<label className="customer-batch-name-field">
  <span>Give this batch a name <small>Optional · shown to the shop</small></span>
  <input type="text" value={batchName} maxLength={50}
    placeholder="e.g. Rahul – College Notes"
    disabled={busy||uploadedBatch.length>0}
    onChange={event=>setBatchName(event.target.value)}
  />
</label>}
{files.length>0&&<small className="customer-upload-local-note">Folder defaults apply. <InfoTooltip label="Folder settings"><span>Settings apply to every file in this folder. Use Custom on a file to change its settings.</span></InfoTooltip></small>}
    {previewUrl&&file&&<div className="customer-file-preview"><div className="customer-file-label"><FileText size={15}/><span>{photoSettingsByFileFingerprint[customerFileFingerprint(file)]?"Prepared photo sheet":"Selected document"} · {file.name}</span>{isCustomerImage(file)&&photoRatesAvailable&&<button type="button" className="customer-file-studio-button" disabled={busy||batchLockedForPayment||photoAlreadyUploaded} title={photoAlreadyUploaded?"This photo has already been submitted to the shop.":"Open Photo Studio"} onClick={()=>openPhotoStudio(file)}><ImageIcon size={14}/> {photoAlreadyUploaded?"Already sent to shop":"Open Photo Studio"}</button>}</div>{isCustomerImage(file)&&!photoRatesAvailable&&<small className="customer-upload-local-note">Photo printing unavailable. Contact shop. <InfoTooltip label="Photo printing details"><span>This shop must enable photo printing and configure its photo sizes and rates.</span></InfoTooltip></small>}{isCustomerImage(file)&&photoAlreadyUploaded&&photoRatesAvailable&&<small className="customer-upload-local-note">Photo already sent. <InfoTooltip label="Photo already sent"><span>The shop owner can prepare this photo from the review queue without creating another print job.</span></InfoTooltip></small>}{isCustomerPdf(file)?photoSettingsByFileFingerprint[customerFileFingerprint(file)]?(()=>{const pages=photoPreviewPagesByFingerprint[customerFileFingerprint(file)]??[];return pages.length?<div className="customer-edited-photo-preview"><div className="customer-edited-photo-frame"><img src={pages[Math.min(selectedPreparedPhotoPage,pages.length-1)]} alt={"Prepared photo sheet page "+(selectedPreparedPhotoPage+1)}/><span>SmartPrint by QuantaEdge</span></div>{pages.length>1&&<div className="customer-edited-photo-pages" aria-label="Prepared A4 pages">{pages.map((url,index)=><button type="button" key={url} aria-pressed={selectedPreparedPhotoPage===index} onClick={()=>setSelectedPreparedPhotoPage(index)}><img src={url} alt={"Page "+(index+1)+" thumbnail"}/><span>Page {index+1}</span></button>)}</div>}<small>Preview only. <InfoTooltip label="Prepared file"><span>The final prepared file stays in your print workflow.</span></InfoTooltip></small></div>:<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>Photo sheet prepared for this shop</strong><span>Preview unavailable. <InfoTooltip label="Prepared PDF"><span>The prepared PDF stays in your print workflow.</span></InfoTooltip></span></div>})():nativeAppMode?smartPrintNativePdfPreviewTokens.has(file)?<button type="button" className="customer-flow-button" onClick={()=>openCustomerPdfPreview(file)}><FileText size={16}/> Preview PDF</button>:<small className="customer-upload-local-note">Preview unavailable. <InfoTooltip label="Upload status"><span>You can still upload this file normally.</span></InfoTooltip></small>:<iframe src={previewUrl} title={file.name+" preview"}/>:isCustomerImage(file)?<img className="customer-original-image-preview" src={previewUrl} alt={file.name}/>:<div className="customer-private-photo-preview"><ShieldCheck size={20}/><strong>Preview unavailable</strong><span>This file remains attached to the print workflow.</span></div>}</div>}
    {files.length>0&&<button className="customer-flow-button" disabled={busy||customerUploadUnavailable||pendingFilesCount===0||(!privatePrinting&&uploadMode==="LOCAL"&&!localReady)} onClick={()=>void upload()}>{busy?<span className="button-spinner"/>:<><UploadIcon/>{privatePrinting?"Upload "+pendingFilesCount+" file(s) securely":uploadMode==="LOCAL"&&localAgent?.endpoints?.length?"Upload "+pendingFilesCount+" file(s) to shop":"Upload "+pendingFilesCount+" file(s)"}</>}</button>}
    {uploadProgress&&<div className="customer-upload-progress"><span>{uploadProgress.currentFile?`Uploading ${Math.min(uploadProgress.completed+1,uploadProgress.total)} of ${uploadProgress.total}: ${uploadProgress.currentFile}`:`${uploadProgress.completed} of ${uploadProgress.total} files uploaded`}</span><div><i style={{width:`${uploadProgress.total?uploadProgress.completed/uploadProgress.total*100:0}%`}}/>{uploadProgress.currentFile&&<b aria-hidden="true"/>}</div>{!nativeAppMode&&<small>Updates after each file.</small>}</div>}
    {files.length>0&&!privatePrinting&&uploadMode==="LOCAL"&&localAgent&&<small className="customer-upload-local-note">{localReady?<>Local transfer is not encrypted. <InfoTooltip label="Local transfer security"><span>Shop Wi-Fi uses local HTTP. Choose Cloud-assisted for encrypted HTTPS transfer.</span></InfoTooltip></>:<>Check shop Wi-Fi first. <InfoTooltip label="Shop Wi-Fi access"><span>In a browser, tap Check shop Wi-Fi above to grant local network access.</span></InfoTooltip></>}</small>}
    {files.length>0&&uploadMode==="CLOUD"&&<small className="customer-upload-local-note">Encrypted HTTPS upload. <InfoTooltip label="Secure upload details"><span>Files are encrypted in transit and stored in the existing print queue until the shop computer is ready.</span></InfoTooltip></small>}
    {uploaded&&<>
      {uploadedBatch.length>0&&<div className="customer-uploaded-batch">
        <div className="customer-uploaded-batch-head"><strong>{batchName.trim()?"Batch · "+batchName.trim():"Batch · "+uploadedBatch.length+" files"}</strong><span>{uploadedBatch[0]?.job.jobNumber?"First job · "+uploadedBatch[0].job.jobNumber+" · ":""}Select a file to view its status. Pay once for all accepted files.</span></div>
        {batchLockedForPayment&&<p className="customer-payment-model-note">This batch is locked because a payment attempt has started. Add more documents in a new batch to keep each checkout total accurate.</p>}
        {Array.from(new Set(uploadedBatch.map(item=>item.job.batchGroupName??"Loose files"))).map(groupName=><section className="customer-uploaded-folder" key={groupName}>
          <strong>{groupName}</strong>
          {uploadedBatch.filter(item=>(item.job.batchGroupName??"Loose files")===groupName).map(item=><button type="button" key={item.job.id} className={uploaded.job.id===item.job.id?"customer-uploaded-batch-item active":"customer-uploaded-batch-item"} onClick={()=>selectUploadedFile(item)}>
            <span className="batch-file-index"><FileText size={14}/></span><span className="batch-file-copy"><span className="batch-file-title-row"><strong>{item.document.originalFileName}</strong><PrintKindTag printKind={(photoSettingsByJobId[item.job.id]||batchStatuses[item.job.id]?.printKind==="PHOTO"||item.job.printKind==="PHOTO")?"PHOTO":"DOCUMENT"}/></span><small>{(photoSettingsByJobId[item.job.id]||batchStatuses[item.job.id]?.printKind==="PHOTO"||item.job.printKind==="PHOTO")?`${photoSettingsByJobId[item.job.id]?.photoCount??batchStatuses[item.job.id]?.photoCount??item.job.photoCount??0} photos · ${photoSettingsByJobId[item.job.id]?.sizeCode??batchStatuses[item.job.id]?.photoSizeCode??item.job.photoSizeCode??""} · ${photoSettingsByJobId[item.job.id]?.photosPerPage??batchStatuses[item.job.id]?.photosPerPage??item.job.photosPerPage??"layout not recorded"} per A4 page · ${batchStatuses[item.job.id]?.pageCount??item.job.pageCount} A4 pages`:`${item.job.pageCount} pages × ${printSettingsByJobId[item.job.id]?.copies??item.job.copies} copies = ${item.job.pageCount*(printSettingsByJobId[item.job.id]?.copies??item.job.copies)} pages requested · ${printSettingsByJobId[item.job.id]?.colourMode??"Mono"} · ${(printSettingsByJobId[item.job.id]?.isDuplex??false)?"Double-sided":"Single-sided"} · ${printSettingsByJobId[item.job.id]?.paperSize??item.job.paperSize??"A4"} · ${(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="LANDSCAPE"?"Landscape":(printSettingsByJobId[item.job.id]?.orientation??item.job.orientation??"AUTO")==="PORTRAIT"?"Portrait":"Auto orientation"}`} · {(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor)>0?formatMoney(batchStatuses[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor,item.job.currency):"Awaiting quote"}</small></span>
            <Status
              label={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"Quote expired":prettyStatus(batchStatuses[item.job.id]?.status??item.job.status??"UPLOADED")}
              tone={quoteExpiredForBatchItem(item)&&!["REJECTED","REFUNDED"].includes(statusForBatchItem(item))?"amber":
                ["REJECTED","PAYMENT_REJECTED"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"red":
                ["COMPLETED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[item.job.id]?.status??item.job.status??"").toUpperCase())?"green":"amber"}
            />
          </button>)}
        </section>)}
      </div>}
      <div className="uploaded-document-card"><div className="uploaded-document-icon"><FileText size={18}/></div><div><div className="uploaded-document-title"><strong>{uploaded.document.originalFileName}</strong><PrintKindTag printKind={(photoSettingsByJobId[uploaded.job.id]||batchStatuses[uploaded.job.id]?.printKind==="PHOTO"||uploaded.job.printKind==="PHOTO")?"PHOTO":"DOCUMENT"}/></div><span>{(photoSettingsByJobId[uploaded.job.id]||batchStatuses[uploaded.job.id]?.printKind==="PHOTO"||uploaded.job.printKind==="PHOTO")?`${photoSettingsByJobId[uploaded.job.id]?.photoCount??batchStatuses[uploaded.job.id]?.photoCount??uploaded.job.photoCount??0} photos · ${photoSettingsByJobId[uploaded.job.id]?.sizeCode??batchStatuses[uploaded.job.id]?.photoSizeCode??uploaded.job.photoSizeCode??""} · ${uploaded.job.pageCount} A4 pages`:`${uploaded.job.pageCount} ${uploaded.job.pageCount===1?"page":"pages"}`} · {formatBytes(uploaded.document.sizeBytes)} · Validated</span></div><Status label={prettyStatus(batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"UPLOADED")} tone={["REJECTED","PAYMENT_REJECTED","REFUNDED","CANCELLED","FAILED"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"red":["COMPLETED","PDF_EXPORTED","PAID_AUTHORISED","AWAITING_PAYMENT"].includes((batchStatuses[uploaded.job.id]?.status??jobStatus?.status??uploaded.job.status??"").toUpperCase())?"green":"amber"}/></div>
      {batchId&&hasLegacyIndividualPayment&&<p className="customer-payment-warning">A file in this batch already has a separate individual payment. Complete or resolve that attempt before starting another payment for this batch. A second collection path is blocked to prevent duplicate charges.</p>}
      {canCalculateBatch&&<div className="customer-batch-quote-action">
        <div className="customer-flow-heading customer-config-heading"><div><span>STEP 2</span><h2>Review batch settings</h2></div><small>{quoteItems.length} files</small></div>
        <button className="customer-flow-button" disabled={busy} onClick={()=>void getQuote()}>{busy?<span className="button-spinner"/>:"Calculate batch quote"}</button>
      </div>}
    </>}
    {error&&<div className="customer-flow-error" role="alert">{error}</div>}
    {quoteEntries.length>0&&<div className="customer-quote-card customer-batch-quote-card">
      <div><span>BATCH QUOTE</span><strong>{formatMoney(batchQuoteTotal,quoteEntries[0].quote!.currency)}</strong><small>{quoteEntries.length} files · {batchDocumentSourcePages} document pages · {batchPhotoCount} photos · {batchPrintedPages} output pages/sheets</small></div>
      <div className="customer-batch-quote-lines">{quoteEntries.map(entry=><div key={entry.item.job.id}><span><span className="customer-quote-line-title">{entry.item.document.originalFileName}<PrintKindTag printKind={entry.quote!.printKind}/>{["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))?" · Rejected":""}</span><small>{entry.quote!.printKind==="PHOTO"?(entry.quote!.photoCount??0)+" × "+customerPhotoSizeLabel(entry.quote!.photoSizeCode)+" · "+(entry.quote!.photosPerPage??"layout not recorded")+" per A4 page · "+entry.quote!.pageCount+" A4 pages · "+formatMoney(entry.quote!.photoUnitPriceMinor??0,entry.quote!.currency)+" per photo":entry.quote!.pageCount+" pages × "+entry.quote!.copies+" copies · "+entry.quote!.colourMode+" · "+(entry.quote!.isDuplex?"Double-sided":"Single-sided")+" · "+entry.quote!.paperSize+" · "+(entry.quote!.orientation==="LANDSCAPE"?"Landscape":entry.quote!.orientation==="PORTRAIT"?"Portrait":"Auto orientation")}</small></span><strong>{["REJECTED","REFUNDED"].includes(statusForBatchItem(entry.item))?"Excluded":formatMoney(entry.quote!.quotedAmountMinor,entry.quote!.currency)}</strong></div>)}</div>
      <div className="customer-batch-print-breakdown">
        {batchMonoPrintedPages>0&&<div><span>B&W · {batchMonoPrintedPages} document pages</span><strong>{formatMoney(batchMonoAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        {batchColourPrintedPages>0&&<div><span>Colour · {batchColourPrintedPages} document pages</span><strong>{formatMoney(batchColourAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        {batchPhotoCount>0&&<div><span>Photo printing · {batchPhotoCount} finished photos</span><strong>{formatMoney(batchPhotoAmount,quoteEntries[0].quote!.currency)}</strong></div>}
        <div className="customer-batch-print-total"><span>Total output pages/sheets</span><strong>{batchPrintedPages}</strong></div>
        <div className="customer-batch-print-total"><span>Total payable</span><strong>{formatMoney(batchQuoteTotal,quoteEntries[0].quote!.currency)}</strong></div>
      </div>
      <p className="customer-payment-model-note">Document pages use the shop's B&W/Colour rate card. Photo sheets use the saved per-photo rate for the selected size, shown on each line. Rejected files are excluded. After review, pay once for all accepted files.</p>
    </div>}
    {canSendBatch&&<button className="customer-flow-button" disabled={busy} onClick={()=>void acceptQuote()}>{busy?<span className="button-spinner"/>:"Send batch to shop"}</button>}
      {jobStatus?.status==="QUOTE_EXPIRED"&&!batchLockedForPayment&&!hasSelectedLegacyIndividualPayment&&<button className="customer-flow-button" disabled={busy} onClick={()=>{setQuote(null);setQuotesByJobId(current=>{const next={...current};if(uploaded)delete next[uploaded.job.id];return next;});void getQuote(uploadedBatch.length?uploadedBatch:uploaded?[uploaded]:[]);}}>Request a fresh quote</button>}
    {jobStatus&&<div className="customer-job-state"><div className="customer-job-state-top"><Status label={prettyStatus(jobStatus.status)} tone={["REJECTED","REFUNDED","CANCELLED","FAILED"].includes(jobStatus.status)?"red":jobStatus.status==="AWAITING_PAYMENT"?"green":"amber"}/><button className="customer-refresh-status" disabled={busy} onClick={()=>void refreshJobStatus()}>{busy?<span className="button-spinner"/>:"Refresh status"}</button></div>
      <strong>{jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"&&jobStatus.privateDocumentPurgedUtc?"Your print is ready — your document has been removed from the shop computer":jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"?"Your print is ready — removing your document from the shop computer":nativeAppMode?smartPrintNativeHeadline(jobStatus.status):jobStatus.status==="AWAITING_SHOP_APPROVAL"?"Your request is with the shop":jobStatus.status==="AWAITING_PAYMENT"?"Your quote is ready for payment":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your document is reaching the shop":jobStatus.status==="LOCAL_QUEUE_READY"?"Your document is secured at the shop":jobStatus.status==="PRINT_AUTHORISED"?"The shop started printing":jobStatus.status==="PRINT_SUBMITTING"?"Your document is being sent to the printer":jobStatus.status==="SPOOLER_ACCEPTED"?"The printer queue accepted your document":jobStatus.status==="PRINTING"?"Your document is printing":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The shop is checking your print":jobStatus.status==="COMPLETED"?"Your print is ready":jobStatus.status==="PDF_EXPORTED"?"Your PDF is ready":jobStatus.status==="REFUNDED"?"This file was fully refunded":jobStatus.status==="CANCELLED"?"This request was cancelled":jobStatus.status==="FAILED"?"This request could not be completed":jobStatus.status==="PAID_AUTHORISED"?"Your payment is verified":jobStatus.status==="REJECTED"?"The shop could not accept this request":jobStatus.status==="QUOTE_EXPIRED"?"Your quote has expired":"Your request is being reviewed"}</strong>
      <p>{jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"&&jobStatus.privateDocumentPurgedUtc?"The paired shop computer confirmed that its private working copy was removed. Your original file on this device is unchanged.":jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"?"The shop confirmed the physical pages. SmartPrint is removing the private working copy from the paired shop computer and will confirm here when it is done.":nativeAppMode ? smartPrintNativeStatus(jobStatus.status) : jobStatus.status==="AWAITING_SHOP_APPROVAL"?"The shop will review your request next. No payment has been collected.":jobStatus.status==="AWAITING_PAYMENT"?"Your quote is ready. Choose one of the payment methods enabled by the shop; the shop must verify payment before authorising the job.":jobStatus.status==="LOCAL_QUEUE_CLAIMED"?"Your paid document is being transferred securely to the shop's Windows computer. It has not been sent to the printer.":jobStatus.status==="LOCAL_QUEUE_READY"?"The document is safely stored in the shop's local queue and is waiting for the print operator. Physical printing has not started.":jobStatus.status==="PRINT_AUTHORISED"?"The shop has authorised printing. The Windows agent will submit the document once.":jobStatus.status==="PRINT_SUBMITTING"?"The Windows agent is submitting your document to the printer.":jobStatus.status==="SPOOLER_ACCEPTED"?"Windows accepted the job into the printer queue. This does not yet confirm paper output.":jobStatus.status==="PRINTING"?"The printer driver reports that the job is printing. The shop will confirm the pages before marking it complete.":jobStatus.status==="OUTPUT_UNCONFIRMED"?"The printer queue could not confirm the final output. The shop operator is checking the pages before any retry.":jobStatus.status==="COMPLETED"?"The shop has checked the physical pages and marked your print ready for collection.":jobStatus.status==="PDF_EXPORTED"?"The shop saved and verified your PDF export.":jobStatus.status==="REFUNDED"?"The file was excluded and the full payment was returned.":jobStatus.status==="CANCELLED"?"This request was cancelled and will not be produced.":jobStatus.status==="FAILED"?"This request could not be completed. Contact the shop before trying again.":jobStatus.status==="AWAITING_COUNTER_PAYMENT"?"Please pay the shop at the counter. The job is not authorised until the shop confirms receipt.":jobStatus.status==="AWAITING_CUSTOMER_PAYMENT"?"Complete the UPI payment and submit its transaction reference. The shop must independently verify it.":jobStatus.status==="PAYMENT_REPORTED"?"Your UPI reference was sent to the shop. It is not marked paid until the shop verifies the transaction.":jobStatus.status==="PAID_AUTHORISED"?"The shop has verified your payment. The print queue is being prepared; physical printing has not yet started.":jobStatus.status==="PAYMENT_REJECTED"?"The shop could not verify that payment. Check with the shop before trying another method.":jobStatus.status==="REJECTED"?"Please speak with the shop operator if you need help with this request.":jobStatus.status==="QUOTE_EXPIRED"?"This quote is no longer valid. Please ask the shop for a fresh quote.":"Check back for the latest request status."}</p>
      {jobStatus.privatePrinting&&jobStatus.status==="COMPLETED"&&!jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-pending" role="status" aria-live="polite"><Clock3 size={16}/><span><strong>Removing your document from the shop computer</strong>The shop confirmed the physical print. SmartPrint is removing the shop-side working copy and will confirm when deletion is complete.</span></div>}
      {jobStatus.privatePrinting&&jobStatus.privateDocumentPurgedUtc&&<div className="customer-private-purge-status customer-private-purge-complete" role="status" aria-live="polite"><ShieldCheck size={16}/><span><strong>Document removed from the shop computer</strong>The paired computer confirmed deletion of its private working copy. Your original file on this device has not been deleted.</span></div>}
      {payment&&!!payment.refundedAmountMinor&&payment.refundedAmountMinor>0&&<div className="customer-refund-note"><Wallet size={16}/><span><strong>{formatMoney(payment.refundedAmountMinor,payment.currency)} returned</strong> for rejected pages ({rejectedPageList(jobStatus.rejectedPageNumbersJson)}). {formatMoney(Math.max(0,payment.amountMinor-payment.refundedAmountMinor),payment.currency)} remains paid; only accepted pages proceed to printing.</span></div>}
      {jobStatus.pdfBrandingEnabled&&<div className="customer-branding-note">
        <ShieldCheck size={16}/>
        <span><strong>PDF footer selected by the shop.</strong> <InfoTooltip label="PDF footer details"><span>Footer only appears on PDFs with a verified blank bottom margin.</span><span>Digitally signed or interactive PDFs remain unchanged.</span><span>Original uploads and physical prints are never altered.</span><span>Image-to-PDF exports currently remain unbranded.</span><span>Branding off preserves a single-copy full-document PDF byte-for-byte.</span><span>Reformatted or multi-copy exports may be flattened.</span><span>Ask for an unbranded single-copy export when PDF structure matters.</span></InfoTooltip></span>
      </div>}
      {!!jobStatus.timeline?.length&&<div className="customer-state-timeline">{jobStatus.timeline.map((event,index)=><div key={index}><i/><span><strong>{prettyStatus(event.newStatus)}</strong><small>{event.actorType==="ShopUser"?"Shop team":event.actorType==="Customer"?"You":"System"} · {formatIstDateTime(event.createdUtc)}</small></span></div>)}</div>}
      {canStartNewSession&&<div className="customer-session-complete"><div><strong>Order finished</strong><span>Your order history is saved. You can start another print session when ready.</span></div><button className="customer-flow-button" onClick={onStartNewSession}><RefreshCw size={16}/> Start new session</button></div>}

      {legacyIndividualPayment&&(jobStatus.status==="AWAITING_PAYMENT"||jobStatus.status==="PAYMENT_REJECTED")&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>PAY THE SHOP</span><strong>{formatMoney(jobStatus.quotedAmountMinor,jobStatus.currency)}</strong></div>
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<small className="customer-payment-method-note">Cash at counter unavailable. <InfoTooltip label="Why cash is unavailable"><span>The shop must enable Cash at counter in Payments settings.</span></InfoTooltip></small>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<small className="customer-payment-method-note">The shop has not enabled UPI or cash payment. Please contact the shop to arrange payment.</small>}
        {paymentOptions.upiEnabled&&<button className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>UPI payment</button>}
        {paymentOptions.cashEnabled&&<button className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>Cash at counter</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)} onClick={()=>void beginPayment(paymentMethod)}>{busy?<span className="button-spinner"/>:nativeAppMode?"Continue":"Continue with "+(paymentMethod==="UPI"?"UPI":"cash at counter")}</button>
        {!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled&&<p className="customer-flow-error">{nativeAppMode?"No payment method. Ask shop.":"The shop has not configured a payment method yet. Please speak to the shop operator."}</p>}
      </div>}
      {legacyIndividualPayment&&payment&&payment.status!=="REJECTED"&&<div className="customer-pay-panel">
        <div className="customer-pay-heading"><span>{payment.method==="UPI"?"UPI PAYMENT":"CASH AT COUNTER"}</span><strong>{formatMoney(Math.max(0,payment.amountMinor-(payment.refundedAmountMinor??0)),payment.currency)}</strong></div>
        {payment.method==="UPI"&&payment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
          <CustomerUpiQr
            uri={buildCustomerUpiIntent({
              serverUri:payment.upiUri,upiId:payment.upiIdSnapshot||paymentOptions.upiId,
              payeeName:payment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:payment.amountMinor,
              currency:payment.currency,transactionReference:payment.id,
              description:"SmartPrint "+(uploaded?.job.jobNumber||"print payment")
            })}
            payeeName={payment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
            upiId={payment.upiIdSnapshot||paymentOptions.upiId||""}
            amount={formatMoney(payment.amountMinor,payment.currency)}
            fileName={"smartprint-payment-"+payment.id.slice(0,12)+".png"}
          />
          <div className="customer-payment-reference">
            <label className="customer-reference-field">{nativeAppMode?"UTR":"UPI transaction reference / UTR"}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={nativeAppMode?"Enter UTR":"Enter UTR after paying"}/></label>
            <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/>:nativeAppMode?"Submit UTR":"Submit payment reference"}</button>
            <small className="customer-payment-warning">UPI transfers can still be declined by the bank or UPI provider. A downloaded QR offers a scan-from-image route; it does not bypass payment security checks.</small>
          </div>
        </>}
        {payment.method==="CASH"&&payment.status==="CASH_AWAITING_PAYMENT"&&<p>{nativeAppMode?"Pay at counter · Await confirmation.":"Pay at the shop counter. The shop will confirm receipt before authorising the job."}</p>}
        {payment.status==="CUSTOMER_REPORTED"&&<p>{nativeAppMode?"Reference received · Verification pending.":"Your payment reference was received. The shop still needs to verify the transaction."}</p>}
        {payment.status==="VERIFIED"&&<p>{nativeAppMode?"Payment verified · Printing pending.":"Payment verified by the shop. The print job is authorised, but not yet physically printed."}</p>}
        {payment.status==="PARTIALLY_REFUNDED"&&<p>{nativeAppMode?"Partial refund · Accepted pages only.":"A partial return has been recorded. The remaining paid amount is for the pages the shop accepted."}</p>}
      </div>}
    </div>}
    {batchCheckoutVisible&&<section className="customer-pay-panel customer-batch-checkout-panel">
      <div className="customer-pay-heading">
        <span>{batchCheckoutHeading} · {batchCheckoutFileCount} FILE(S)</span>
        <strong>{formatMoney(batchCheckoutAmount,batchCheckoutCurrency)}</strong>
      </div>
      <div className="customer-batch-checkout-lines">
        {batchCheckoutLineItems.map(item=>{
          const allocation=customerPaymentForBatchItem(item);
          const amount=batchCheckoutUsesPayment
            ?allocation?.amountMinor??0
            :batchStatuses[item.job.id]?.quotedAmountMinor??quotesByJobId[item.job.id]?.quotedAmountMinor??item.job.quotedAmountMinor??0;
          const refunded=batchCheckoutUsesPayment?(allocation?.refundedAmountMinor??0):0;
          const lineStatus=batchCheckoutUsesPayment?allocation?.status:"Included in this checkout";
          return <div key={item.job.id}>
            <span>{item.document.originalFileName}<small>{lineStatus?String(lineStatus).replaceAll("_"," "):"Allocation loading"}{refunded>0? ` · ${formatMoney(refunded,item.job.currency)} refunded` : ""}</small></span>
            <strong>{formatMoney(amount,item.job.currency)}</strong>
          </div>;
        })}
      </div>
      {batchCheckoutUsesPayment&&!batchPaymentAllocationComplete&&<p className="customer-payment-warning">Refreshing the per-file payment breakdown. Check the complete file list before paying or submitting a reference.</p>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPaymentRefundedTotal>0&&<p className="customer-payment-warning">Recorded file refunds: {formatMoney(batchPaymentRefundedTotal,batchCheckoutCurrency)}. Remaining amount after those returns: {formatMoney(Math.max(0,batchPayment!.amountMinor-batchPaymentRefundedTotal),batchCheckoutCurrency)}.</p>}
      {!batchCheckoutUsesPayment&&<>
        {batchPayment?.status==="REJECTED"&&<p className="customer-flow-error">The previous batch payment was rejected. {batchPayment.rejectionReason||"Check with the shop before trying again."}</p>}
        {!paymentOptions.cashEnabled&&paymentOptions.upiEnabled&&<small className="customer-payment-method-note">Cash at counter unavailable. <InfoTooltip label="Why cash is unavailable"><span>The shop must enable Cash at counter in Payments settings.</span></InfoTooltip></small>}
        {!paymentOptions.cashEnabled&&!paymentOptions.upiEnabled&&<small className="customer-payment-method-note">The shop has not enabled UPI or cash payment. Please contact the shop to arrange payment.</small>}
        {paymentOptions.upiEnabled&&<button className={paymentMethod==="UPI"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("UPI")}>UPI payment</button>}
        {paymentOptions.cashEnabled&&<button className={paymentMethod==="CASH"?"customer-choice selected":"customer-choice"} onClick={()=>setPaymentMethod("CASH")}>Cash at counter</button>}
        <button className="customer-flow-button" disabled={busy||(!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled)||batchPayableItems.length===0} onClick={()=>void beginPayment(paymentMethod,true)}>
          {busy?<span className="button-spinner"/>:nativeAppMode?"Pay accepted files":"Pay accepted files · "+formatMoney(batchPayableTotal,batchCheckoutCurrency)}
        </button>
        {!paymentOptions.upiEnabled&&!paymentOptions.cashEnabled&&<p className="customer-flow-error">{nativeAppMode?"No payment method. Ask shop.":"The shop has not configured a payment method yet. Please speak to the shop operator."}</p>}
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="UPI"&&batchPayment.status==="AWAITING_CUSTOMER_PAYMENT"&&<>
        <CustomerUpiQr
          uri={buildCustomerUpiIntent({
            serverUri:batchPayment.upiUri,upiId:batchPayment.upiIdSnapshot||paymentOptions.upiId,
            payeeName:batchPayment.payeeNameSnapshot||paymentOptions.payeeName,amountMinor:batchPayment.amountMinor,
            currency:batchPayment.currency,transactionReference:batchPayment.id,
            description:"SmartPrint batch "+batchPayment.batchId.slice(0,8).toUpperCase()
          })}
          payeeName={batchPayment.payeeNameSnapshot||paymentOptions.payeeName||"Shop"}
          upiId={batchPayment.upiIdSnapshot||paymentOptions.upiId||""}
          amount={formatMoney(batchPayment.amountMinor,batchPayment.currency)}
          fileName={"smartprint-batch-payment-"+batchPayment.id.slice(0,12)+".png"}
        />
        <div className="customer-payment-reference">
          <label className="customer-reference-field">{nativeAppMode?"UTR":"UPI transaction reference / UTR"}<input value={paymentReference} maxLength={100} onChange={event=>setPaymentReference(event.target.value)} placeholder={nativeAppMode?"Enter UTR":"Enter UTR after paying"}/></label>
          <button className="customer-flow-button" disabled={busy||paymentReference.trim().length<4} onClick={()=>void submitPaymentReference()}>{busy?<span className="button-spinner"/>:nativeAppMode?"Submit UTR":"Submit batch payment reference"}</button>
          <small className="customer-payment-warning">The shop verifies the actual UPI credit. A downloaded QR offers an alternative scan route; it cannot override a bank or UPI security decline.</small>
        </div>
      </>}
      {batchCheckoutUsesPayment&&batchPaymentAllocationComplete&&batchPayment?.method==="CASH"&&batchPayment.status==="CASH_AWAITING_PAYMENT"&&<p>Pay the displayed batch total at the shop counter. The shop will confirm receipt before authorising the accepted files.</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="CUSTOMER_REPORTED"&&<p>Your batch payment reference was received. The shop must verify the full amount before any accepted file is authorised.</p>}
      {batchCheckoutUsesPayment&&batchPayment?.status==="VERIFIED"&&<div className="payment-verified-note"><Check size={14}/> Batch payment verified. Accepted files are authorised for the shop's print queue.</div>}
    </section>}
    <div className="customer-payment-boundary"><ShieldCheck size={15}/><span>{nativeAppMode ? smartPrintNativeBoundary(jobStatus?.status,Boolean(quote)) : jobStatus?.status==="PAID_AUTHORISED"?"The shop has verified your payment. It still needs to assign and start the local printer.":jobStatus?.status==="PRINT_AUTHORISED"?"The shop has authorised physical printing.":jobStatus?.status==="PRINT_SUBMITTING"?"Your document is being sent to the Windows printer queue.":jobStatus?.status==="SPOOLER_ACCEPTED"?"Windows accepted the job. This is not proof that paper has come out.":jobStatus?.status==="PRINTING"?"The Windows driver reports that printing is in progress.":jobStatus?.status==="OUTPUT_UNCONFIRMED"?"The shop is checking the printer and actual pages before any retry.":jobStatus?.status==="COMPLETED"?"The shop confirmed the requested pages are printed and ready for collection.":jobStatus?.status==="PDF_EXPORTED"?"The PDF export was saved and verified.":jobStatus?.status==="REFUNDED"?"The file was fully refunded and will not be printed.":jobStatus?.status==="CANCELLED"?"The request was cancelled.":jobStatus?.status==="FAILED"?"The request failed; contact the shop before submitting again.":jobStatus?.status==="LOCAL_QUEUE_CLAIMED"?"The shop's paired Windows computer is receiving the encrypted document. It is not being printed yet.":jobStatus?.status==="LOCAL_QUEUE_READY"?"The encrypted local copy is verified on the shop computer. Physical printing still requires the print operator.":jobStatus?.status==="AWAITING_PAYMENT"?"Your quote is ready for payment. The shop collects payment directly and must verify it before the job is authorised.":jobStatus?.status==="PAYMENT_REPORTED"?"Your UPI reference is awaiting the shop's independent bank verification; submitting a reference does not confirm payment.":quote?"This quote is calculated by the shop's saved rate card. Accepting it sends a request to the shop; it does not collect payment or start printing.":"Your document is validated before pricing. You will see the shop's calculated price before any payment or print approval."}</span></div>
  </section></>;
}

function UploadIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 16V4m0 0L7 9m5-5 5 5"/><path d="M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3"/></svg>;
}
