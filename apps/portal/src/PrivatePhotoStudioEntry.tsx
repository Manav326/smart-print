import { useEffect, useState, type ChangeEvent, type FormEvent, type CSSProperties } from "react";
import PhotoStudio, { type PreparedPhotoSheet, type PhotoStudioEditorSettings } from "./PhotoStudio";
import { resolvePhotoSourceMimeType } from "./photo-source-policy";

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
          const mime=typeof metadata.sourceMime==="string"?metadata.sourceMime:"";
          const name=typeof metadata.sourceName==="string"?metadata.sourceName:"photo.jpg";
          const supportedMime=resolvePhotoSourceMimeType({type:mime,name});
          if(!supportedMime){
            setError("This saved original is not a JPG or PNG photo and cannot be reopened in Photo Studio.");
            return;
          }
          const bytes=base64ToBytes(payload.sourceBase64);
          setSource(new File([bytes],name,{type:supportedMime,lastModified:Date.now()}));
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
    const supportedMime=resolvePhotoSourceMimeType(selected);
    if(!supportedMime){setError("Choose one JPG or PNG photo.");event.currentTarget.value="";return;}
    if(selected.size<=0){setError("Choose a non-empty photo.");event.currentTarget.value="";return;}
    const normalized=selected.type===supportedMime?selected:new File([selected],selected.name,{type:supportedMime,lastModified:selected.lastModified});
    setSource(normalized);setEditingDocumentId(null);setInitialSettings(undefined);setStatus("");setError("");
  }

  async function savePreparedSheets(sheets:PreparedPhotoSheet[]){
    const sheet=sheets[0];
    if(!source||!sheet||saving)return;
    const bridge=nativeBridge();
    if(!bridge){setError("Open Photo Studio from the SmartPrint Android app to save a private sheet.");return;}
    if(source.size<=0||sheet.file.size<=0){
      setError("The original photo or prepared sheet is empty. Choose the photo again and retry.");
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

export default PrivatePhotoStudioEntry;
