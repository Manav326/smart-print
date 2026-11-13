import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import {
  Check, ChevronLeft, ChevronRight, Crop, Image as ImageIcon, RotateCcw, SlidersHorizontal,
  Sparkles, X, ZoomIn, ZoomOut, Type, PaintBucket, Pipette
} from "lucide-react";
import styles from "./PhotoStudio.module.css";
import { UPLOAD_LIMITS } from "./upload-limits.generated";
import { isDesktopApp, setDesktopContentProtection } from "./desktop-bridge";
import { A4_HEIGHT_MM, A4_WIDTH_MM, A4_HEIGHT_PX, A4_WIDTH_PX, buildPhotoPdf, getPhotoSheetLayout, getPhotoSheetPhotoPositions, hasSufficientPhotoResolution } from "./photo-studio-layout";

export type PhotoStudioEditorSettings = {
  sizeId: string;
  look: LookPreset;
  adjustments: Adjustments;
  copiesInput: string;
  photosPerPageInput: string;
  zoom: number;
  focusX: number;
  focusY: number;
  includeFormDetails: boolean;
  photoName: string;
  dateOfBirth: string;
  captionFontSizePt: number;
  captionColor: string;
  textLayers: PhotoTextLayer[];
  targetBackgroundColor: string;
  sourceBackgroundColor: string | null;
  backgroundTolerance: number;
};
export type PreparedPhotoSheet = {
  file: File;
  sizeCode: string;
  photoCount: number;
  photosPerPage: number;
  captionHeightMm: number;
  resolutionDpi?: number;
  editorSettings?: PhotoStudioEditorSettings;
  previewPages?: Blob[];
};

type PhotoSize = {
  id: string;
  label: string;
  detail: string;
  widthMm: number;
  heightMm: number;
};

type LookPreset = "natural" | "warm" | "crisp" | "mono" | "custom";
type Tool = "crop" | "look" | "sheet" | "text" | "background";

type PhotoTextLayer = {
  id: string;
  text: string;
  xPercent: number;
  yPercent: number;
  fontSizePt: number;
  color: string;
  bold: boolean;
};

type Adjustments = {
  brightness: number;
  contrast: number;
  saturation: number;
};

function formatPhotoRate(minor: number) {
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 2 }).format(minor / 100);
}
const MAX_FILE_BYTES = UPLOAD_LIMITS.maxFileBytes;
const MAX_PHOTO_COUNT = UPLOAD_LIMITS.maxPhotoCount;

const PHOTO_SIZES: PhotoSize[] = [
  { id: "35x45", label: "35 × 45 mm", detail: "Portrait ID photo", widthMm: 35, heightMm: 45 },
  { id: "25x35", label: "25 × 35 mm", detail: "Compact ID photo", widthMm: 25, heightMm: 35 },
  { id: "2x2", label: "2 × 2 inch", detail: "Square photo", widthMm: 50.8, heightMm: 50.8 },
  { id: "4x6", label: "4 × 6 inch · Portrait", detail: "Portrait tile on an A4 sheet", widthMm: 101.6, heightMm: 152.4 }
];

const LOOKS: Array<{ id: LookPreset; label: string; note: string; adjustments: Adjustments }> = [
  { id: "natural", label: "Natural", note: "Balanced, true-to-life colour", adjustments: { brightness: 100, contrast: 100, saturation: 100 } },
  { id: "warm", label: "Warm", note: "A gentle warmer tone", adjustments: { brightness: 102, contrast: 102, saturation: 108 } },
  { id: "crisp", label: "Crisp", note: "Slightly clearer and brighter", adjustments: { brightness: 104, contrast: 112, saturation: 104 } },
  { id: "mono", label: "Black & white", note: "Monochrome photo", adjustments: { brightness: 102, contrast: 108, saturation: 0 } }
];

function parseHexColor(value: string): [number, number, number] | null {
  const match=/^#([0-9a-f]{6})$/i.exec(value);
  if(!match)return null;
  return [Number.parseInt(match[1].slice(0,2),16),Number.parseInt(match[1].slice(2,4),16),Number.parseInt(match[1].slice(4,6),16)];
}
function replaceEdgeBackground(canvas: HTMLCanvasElement, sourceHex: string, targetHex: string, tolerance: number) {
  const source=parseHexColor(sourceHex),target=parseHexColor(targetHex);
  if(!source||!target||tolerance<1)return;
  const context=canvas.getContext("2d");if(!context)return;
  const {width,height}=canvas,pixels=context.getImageData(0,0,width,height),data=pixels.data;
  const visited=new Uint8Array(width*height),queue=new Int32Array(width*height);
  let head=0,tail=0;const limit=tolerance*tolerance;
  const candidate=(i:number)=>{const p=i*4,r=data[p]-source[0],g=data[p+1]-source[1],b=data[p+2]-source[2];return r*r+g*g+b*b<=limit;};
  const add=(i:number)=>{if(visited[i]||!candidate(i))return;visited[i]=1;queue[tail++]=i;};
  for(let x=0;x<width;x++){add(x);add((height-1)*width+x);}
  for(let y=1;y<height-1;y++){add(y*width);add(y*width+width-1);}
  while(head<tail){const i=queue[head++],x=i%width,y=Math.floor(i/width);if(x>0)add(i-1);if(x+1<width)add(i+1);if(y>0)add(i-width);if(y+1<height)add(i+width);}
  for(let n=0;n<tail;n++){
    const p=queue[n]*4,r=data[p]-source[0],g=data[p+1]-source[1],b=data[p+2]-source[2];
    const distance=Math.sqrt(r*r+g*g+b*b),blend=Math.max(0,Math.min(1,(tolerance-distance+5)/10));
    data[p]=Math.round(data[p]*(1-blend)+target[0]*blend);
    data[p+1]=Math.round(data[p+1]*(1-blend)+target[1]*blend);
    data[p+2]=Math.round(data[p+2]*(1-blend)+target[2]*blend);
  }
  context.putImageData(pixels,0,0);
}
function formatDobForPrint(value:string){
  const match=/^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return match?match[3]+"/"+match[2]+"/"+match[1]:value.trim().slice(0,24);
}

function createPhotoTile(
  image:HTMLImageElement,size:PhotoSize,zoom:number,focusX:number,focusY:number,adjustments:Adjustments,
  background:{sourceColor:string|null;targetColor:string;tolerance:number}
){
  const source=cropSource(image,size,zoom,focusX,focusY);
  const filter=`brightness(${adjustments.brightness}%) contrast(${adjustments.contrast}%) saturate(${adjustments.saturation}%)`;
  const photoW=Math.max(1,Math.round(size.widthMm/25.4*300)),photoH=Math.max(1,Math.round(size.heightMm/25.4*300));
  const photoTile=document.createElement("canvas");
  photoTile.width=photoW;photoTile.height=photoH;
  const tileContext=photoTile.getContext("2d");
  if(!tileContext)throw new Error("A photo crop could not be prepared.");
  tileContext.filter=filter;
  tileContext.drawImage(image,source.x,source.y,source.width,source.height,0,0,photoW,photoH);
  tileContext.filter="none";
  if(background.sourceColor)replaceEdgeBackground(photoTile,background.sourceColor,background.targetColor,background.tolerance);
  return photoTile;
}

function drawPhotoSheetPage(
  context:CanvasRenderingContext2D,image:HTMLImageElement,canvasWidth:number,size:PhotoSize,copies:number,photosPerPage:number,pageIndex:number,
  zoom:number,focusX:number,focusY:number,adjustments:Adjustments,
  caption:{name:string;dob:string;fontSizePt:number;color:string},
  background:{sourceColor:string|null;targetColor:string;tolerance:number},captionHeightMm:number,
  textLayers: readonly PhotoTextLayer[] = [],sharedPhotoTile?:HTMLCanvasElement
){
  const scaleX=canvasWidth/A4_WIDTH_MM,scaleY=context.canvas.height/A4_HEIGHT_MM;
  const pageStart=pageIndex*photosPerPage;
  const pageCopies=Math.max(0,Math.min(photosPerPage,copies-pageStart));
  const positions=getPhotoSheetPhotoPositions(size,captionHeightMm,photosPerPage,pageCopies);
  context.save();context.setTransform(1,0,0,1,0,0);context.fillStyle="#fff";
  context.fillRect(0,0,canvasWidth,context.canvas.height);context.scale(scaleX,scaleY);
  // A multi-page export passes one processed tile to every page. Live preview
  // creates and releases its own tile because it renders only the visible page.
  const photoTile=sharedPhotoTile??createPhotoTile(image,size,zoom,focusX,focusY,adjustments,background);

  for(const position of positions){
    const x=position.xMm,y=position.yMm;
    context.drawImage(photoTile,x,y,size.widthMm,size.heightMm);
    context.save();context.strokeStyle="#7b8491";context.lineWidth=.18;context.setLineDash([.8,.8]);context.strokeRect(x,y,size.widthMm,size.heightMm);context.restore();
    if(captionHeightMm>0){
      const lines=[caption.name.trim().slice(0,48),caption.dob.trim()?("DOB: "+formatDobForPrint(caption.dob)):""].filter(Boolean);
      const fontMm=caption.fontSizePt*25.4/72;context.save();context.fillStyle=caption.color;
      context.font=`600 ${fontMm}px Arial, sans-serif`;context.textAlign="center";context.textBaseline="top";
      const lineHeight=Math.max(fontMm+.35,1.7);
      lines.forEach((line,i)=>context.fillText(line,x+size.widthMm/2,y+size.heightMm+.45+i*lineHeight,size.widthMm-1.2));
      context.restore();
    }
  }
  if(!sharedPhotoTile){
    photoTile.width=0;
    photoTile.height=0;
  }

  // Text layers are placed in page-relative coordinates so the preview and the
  // final 300-DPI A4 raster use the same position, font size and colour.
  for (const layer of textLayers) {
    const value = layer.text.trim().slice(0, 120);
    if (!value) continue;
    const xMm = layer.xPercent / 100 * A4_WIDTH_MM;
    const yMm = layer.yPercent / 100 * A4_HEIGHT_MM;
    const fontMm = layer.fontSizePt * 25.4 / 72;
    context.save();
    context.fillStyle = layer.color;
    context.font = `${layer.bold ? "700" : "500"} ${fontMm}px Arial, sans-serif`;
    context.textAlign = "center";
    context.textBaseline = "middle";
    context.fillText(value, xMm, yMm, A4_WIDTH_MM - 16);
    context.restore();
  }
  context.restore();
}

function cropSource(image: HTMLImageElement, size: PhotoSize, zoom: number, focusX: number, focusY: number) {
  const targetRatio = size.widthMm / size.heightMm;
  let width = image.naturalWidth;
  let height = image.naturalHeight;
  if (width / height > targetRatio) {
    width = height * targetRatio;
  } else {
    height = width / targetRatio;
  }
  width /= zoom;
  height /= zoom;
  const x = Math.max(0, (image.naturalWidth - width) * (focusX / 100));
  const y = Math.max(0, (image.naturalHeight - height) * (focusY / 100));
  return { x, y, width, height };
}

function canvasToBlob(canvas: HTMLCanvasElement, type: string, quality?: number) {
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error("The photo sheet could not be rendered.")), type, quality);
  });
}

function safeBaseName(fileName: string) {
  const withoutExtension = fileName.replace(/\.[^.]+$/, "");
  return withoutExtension.replace(/[^a-zA-Z0-9_-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48) || "photo";
}

function sourceResolutionNote(image: HTMLImageElement, size: PhotoSize, zoom: number) {
  return hasSufficientPhotoResolution(image.naturalWidth,image.naturalHeight,size,zoom)
    ? "Source resolution is suitable for this print size and crop."
    : "This crop may look soft at the selected size. Use a sharper original or reduce the zoom.";
}

export default function PhotoStudio({
  source,
  onCancel,
  onApply,
  photoPrices,
  mode = "customer",
  dark = false,
  initialPhotoCount,
  initialSettings,
  onDraftChange
}: {
  source: File;
  onCancel: () => void;
  photoPrices: Record<string, number>;
  onApply: (sheets: PreparedPhotoSheet[]) => void | Promise<void>;
  mode?: "customer" | "shop" | "shop-create" | "private-library";
  dark?: boolean;
  initialPhotoCount?: number;
  initialSettings?: Partial<PhotoStudioEditorSettings>;
  onDraftChange?: (settings: PhotoStudioEditorSettings) => void;
}) {
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const [image, setImage] = useState<HTMLImageElement | null>(null);
  const [imageError, setImageError] = useState("");
  const [activeTool, setActiveTool] = useState<Tool>("sheet");
  const [sizeId, setSizeId] = useState(() =>
    initialSettings?.sizeId && PHOTO_SIZES.some(item => item.id === initialSettings.sizeId)
      ? initialSettings.sizeId : "35x45"
  );
  const [look, setLook] = useState<LookPreset>(() =>
    initialSettings?.look && LOOKS.some(item => item.id === initialSettings.look) ? initialSettings.look : "natural"
  );
  const [adjustments, setAdjustments] = useState<Adjustments>(() => ({
    brightness: Math.min(200, Math.max(0, initialSettings?.adjustments?.brightness ?? LOOKS[0].adjustments.brightness)),
    contrast: Math.min(200, Math.max(0, initialSettings?.adjustments?.contrast ?? LOOKS[0].adjustments.contrast)),
    saturation: Math.min(200, Math.max(0, initialSettings?.adjustments?.saturation ?? LOOKS[0].adjustments.saturation))
  }));
  const [copiesInput, setCopiesInput] = useState(() => {
    const initialCount = Number(initialSettings?.copiesInput ?? initialPhotoCount);
    return Number.isInteger(initialCount) && initialCount >= 1 && initialCount <= MAX_PHOTO_COUNT
      ? String(initialCount)
      : "8";
  });
  const [photosPerPageInput,setPhotosPerPageInput]=useState(() =>
    initialSettings?.photosPerPageInput === "auto" ||
    (Number.isInteger(Number(initialSettings?.photosPerPageInput)) && Number(initialSettings?.photosPerPageInput) >= 1)
      ? String(initialSettings?.photosPerPageInput) : "auto"
  );
  const [previewPage,setPreviewPage]=useState(0);
  const [zoom, setZoom] = useState(() => Math.min(3, Math.max(1, initialSettings?.zoom ?? 1)));
  const [focusX, setFocusX] = useState(() => Math.min(100, Math.max(0, initialSettings?.focusX ?? 50)));
  const [focusY, setFocusY] = useState(() => Math.min(100, Math.max(0, initialSettings?.focusY ?? 50)));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const busyRef = useRef(busy);
  const onCancelRef = useRef(onCancel);
  const onDraftChangeRef = useRef(onDraftChange);
  busyRef.current = busy;
  onCancelRef.current = onCancel;
  onDraftChangeRef.current = onDraftChange;
  const [includeFormDetails,setIncludeFormDetails]=useState(initialSettings?.includeFormDetails ?? false);
  const [photoName,setPhotoName]=useState((initialSettings?.photoName ?? "").slice(0,48));
  const [dateOfBirth,setDateOfBirth]=useState((initialSettings?.dateOfBirth ?? "").slice(0,24));
  const [captionFontSizePt,setCaptionFontSizePt]=useState(Math.min(20,Math.max(4,initialSettings?.captionFontSizePt ?? 6)));
  const [captionColor,setCaptionColor]=useState(/^#[0-9a-f]{6}$/i.test(initialSettings?.captionColor ?? "") ? initialSettings!.captionColor! : "#202020");
  const [textLayers,setTextLayers]=useState<PhotoTextLayer[]>(() =>
    (initialSettings?.textLayers ?? []).slice(0,20).map((layer,index)=>({
      id: typeof layer.id === "string" ? layer.id.slice(0,64) : "photo-text-"+index,
      text: String(layer.text ?? "").slice(0,120),
      xPercent: Math.min(98,Math.max(2,Number(layer.xPercent)||50)),
      yPercent: Math.min(98,Math.max(2,Number(layer.yPercent)||50)),
      fontSizePt: Math.min(48,Math.max(4,Number(layer.fontSizePt)||16)),
      color: /^#[0-9a-f]{6}$/i.test(layer.color ?? "") ? layer.color : "#202020",
      bold: Boolean(layer.bold)
    }))
  );
  const [selectedTextLayerId,setSelectedTextLayerId]=useState<string|null>(null);
  const textLayerIdRef=useRef(0);
  const textDragRef=useRef<{
    id:string; pointerId:number; startX:number; startY:number;
    initialX:number; initialY:number; width:number; height:number;
  }|null>(null);
  const [targetBackgroundColor,setTargetBackgroundColor]=useState(
    /^#[0-9a-f]{6}$/i.test(initialSettings?.targetBackgroundColor ?? "") ? initialSettings!.targetBackgroundColor! : "#ffffff"
  );
  const [sourceBackgroundColor,setSourceBackgroundColor]=useState<string|null>(
    initialSettings?.sourceBackgroundColor && /^#[0-9a-f]{6}$/i.test(initialSettings.sourceBackgroundColor)
      ? initialSettings.sourceBackgroundColor : null
  );
  const [backgroundTolerance,setBackgroundTolerance]=useState(Math.min(100,Math.max(1,initialSettings?.backgroundTolerance ?? 30)));
  const [pickingBackground,setPickingBackground]=useState(false);

  const editorSettingsSnapshot = useMemo<PhotoStudioEditorSettings>(()=>({
    sizeId,
    look,
    adjustments:{...adjustments},
    copiesInput,
    photosPerPageInput,
    zoom,
    focusX,
    focusY,
    includeFormDetails,
    photoName,
    dateOfBirth,
    captionFontSizePt,
    captionColor,
    textLayers:textLayers.map(layer=>({...layer})),
    targetBackgroundColor,
    sourceBackgroundColor,
    backgroundTolerance
  }),[sizeId,look,adjustments,copiesInput,photosPerPageInput,zoom,focusX,focusY,includeFormDetails,photoName,dateOfBirth,captionFontSizePt,captionColor,textLayers,targetBackgroundColor,sourceBackgroundColor,backgroundTolerance]);

  useEffect(()=>{
    if(!onDraftChangeRef.current)return;
    const timeout=window.setTimeout(()=>onDraftChangeRef.current?.(editorSettingsSnapshot),120);
    return ()=>window.clearTimeout(timeout);
  },[editorSettingsSnapshot]);

  const availableSizes = mode === "private-library"
    ? PHOTO_SIZES
    : PHOTO_SIZES.filter(item => (photoPrices[item.id] ?? 0) > 0);
  const size = availableSizes.find(item => item.id === sizeId) ?? availableSizes[0] ?? PHOTO_SIZES[0];
  const hasCaption=includeFormDetails&&Boolean(photoName.trim()||dateOfBirth.trim());
  const selectedTextLayer=textLayers.find(layer=>layer.id===selectedTextLayerId)??null;
  const captionFontMm=captionFontSizePt*25.4/72;
  const captionHeightMm=hasCaption?Math.max(7,captionFontMm*2+1.2):0;
  const maximumLayout=useMemo(()=>getPhotoSheetLayout(size,captionHeightMm),[size,captionHeightMm]);
  const requestedPhotosPerPage=photosPerPageInput==="auto"?maximumLayout.perSheet:Number(photosPerPageInput);
  const photosPerPage=Number.isInteger(requestedPhotosPerPage)&&requestedPhotosPerPage>=1&&requestedPhotosPerPage<=maximumLayout.perSheet
    ? requestedPhotosPerPage : maximumLayout.perSheet;
  const layout=useMemo(()=>getPhotoSheetLayout(size,captionHeightMm,photosPerPage),[size,captionHeightMm,photosPerPage]);
  const maxCopies=MAX_PHOTO_COUNT;
  const parsedCopiesInput = Number(copiesInput);
  const copiesInputValid = Number.isInteger(parsedCopiesInput) && parsedCopiesInput >= 1 && parsedCopiesInput <= maxCopies;
  // Keep the live preview on a valid integer grid even while the editable field
  // contains an invalid fraction. Validation below still blocks preparation until
  // the owner/customer enters a whole number from 1 to 100.
  const copies = Number.isFinite(parsedCopiesInput)
    ? Math.max(1, Math.min(maxCopies, Math.floor(parsedCopiesInput) || 1))
    : 1;
  const sheetCount = Math.ceil(copies / photosPerPage);
  const previewPageIndex=Math.max(0,Math.min(previewPage,sheetCount-1));
  const previewPhotoCount=Math.min(layout.perSheet,copies-previewPageIndex*layout.perSheet);
  useEffect(()=>{setPreviewPage(current=>Math.min(current,sheetCount-1));},[sheetCount]);
  useEffect(()=>{
    if(photosPerPageInput!=="auto"&&(!Number.isInteger(Number(photosPerPageInput))||Number(photosPerPageInput)>maximumLayout.perSheet))
      setPhotosPerPageInput("auto");
  },[photosPerPageInput,maximumLayout.perSheet]);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.style.overflow = "hidden";
    const focusableSelector = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const focusableElements = () => Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(focusableSelector) ?? []);
    const handleDialogKeydown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        if (!busyRef.current) {
          event.preventDefault();
          onCancelRef.current();
        }
        return;
      }
      if (event.key !== "Tab") return;
      const elements = focusableElements();
      if (!elements.length) {
        event.preventDefault();
        dialogRef.current?.focus();
        return;
      }
      const first = elements[0];
      const last = elements[elements.length - 1];
      if (event.shiftKey && (document.activeElement === first || !dialogRef.current?.contains(document.activeElement))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (document.activeElement === last || !dialogRef.current?.contains(document.activeElement))) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleDialogKeydown, true);
    focusableElements()[0]?.focus();
    return () => {
      document.removeEventListener("keydown", handleDialogKeydown, true);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
  }, []);

  useEffect(() => {
    if (availableSizes.length && !availableSizes.some(item => item.id === sizeId)) setSizeId(availableSizes[0].id);
  }, [availableSizes, sizeId]);

  useEffect(()=>{
    if(!isDesktopApp())return;
    const protect = mode !== "private-library";
    void setDesktopContentProtection(protect);
    return ()=>{void setDesktopContentProtection(false);};
  },[mode]);

  useEffect(()=>{
    const bridge=(window as Window & {SmartPrintNative?:{postMessage:(value:string)=>void}}).SmartPrintNative;
    try {
      bridge?.postMessage("setPhotoStudioOpen|on");
      if (mode === "private-library") {
        bridge?.postMessage("setSensitivePreview|off");
      } else {
        bridge?.postMessage("setSensitivePreview|on");
      }
    } catch {/* Native modal state and screenshot protection are best-effort outside supported Android builds. */}
    return ()=>{
      try {
        bridge?.postMessage("setSensitivePreview|off");
        bridge?.postMessage("setPhotoStudioOpen|off");
      } catch {/* The WebView may already be closing. */}
    };
  },[mode]);

  useEffect(()=>{
    const protectContextMenu=(event:globalThis.MouseEvent)=>event.preventDefault();
    const protectShortcuts=(event:KeyboardEvent)=>{
      const key=event.key.toLowerCase();
      if((mode !== "private-library" && key==="printscreen")||((event.ctrlKey||event.metaKey)&&["s","p"].includes(key)))event.preventDefault();
    };
    document.addEventListener("contextmenu",protectContextMenu,true);
    window.addEventListener("keydown",protectShortcuts,true);
    return ()=>{
      document.removeEventListener("contextmenu",protectContextMenu,true);
      window.removeEventListener("keydown",protectShortcuts,true);
    };
  },[]);

  useEffect(() => {
    const url = URL.createObjectURL(source);
    const nextImage = new Image();
    let active = true;
    nextImage.onload = () => {
      if (active) {
        setImage(nextImage);
        setImageError("");
      }
    };
    nextImage.onerror = () => {
      if (active) {
        setImage(null);
        setImageError("This image could not be opened. Choose a JPG or PNG photo.");
      }
    };
    nextImage.src = url;
    return () => {
      active = false;
      URL.revokeObjectURL(url);
    };
  }, [source]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !image) return;
    const context = canvas.getContext("2d");
    if (!context) {
      setImageError("Photo preview is not available in this browser.");
      return;
    }
    drawPhotoSheetPage(
      context,image,canvas.width,size,copies,photosPerPage,previewPageIndex,zoom,focusX,focusY,adjustments,
      {name:photoName,dob:dateOfBirth,fontSizePt:captionFontSizePt,color:captionColor},
      {sourceColor:sourceBackgroundColor,targetColor:targetBackgroundColor,tolerance:backgroundTolerance},captionHeightMm,textLayers
    );
  },[image,size,copies,photosPerPage,previewPageIndex,zoom,focusX,focusY,adjustments,photoName,dateOfBirth,captionFontSizePt,captionColor,sourceBackgroundColor,targetBackgroundColor,backgroundTolerance,captionHeightMm,textLayers]);

  function addTextLayer(){
    const id="photo-text-"+(++textLayerIdRef.current);
    setTextLayers(current=>[...current,{id,text:"Your text",xPercent:50,yPercent:50,fontSizePt:16,color:"#202020",bold:false}]);
    setSelectedTextLayerId(id);
    setActiveTool("text");
    setError("");
  }

  function updateSelectedTextLayer(patch:Partial<Pick<PhotoTextLayer,"text"|"fontSizePt"|"color"|"bold">>){
    if(!selectedTextLayerId)return;
    setTextLayers(current=>current.map(layer=>layer.id===selectedTextLayerId?{...layer,...patch}:layer));
  }

  function removeSelectedTextLayer(){
    if(!selectedTextLayerId)return;
    setTextLayers(current=>current.filter(layer=>layer.id!==selectedTextLayerId));
    setSelectedTextLayerId(null);
    textDragRef.current=null;
  }

  function handleTextLayerPointerDown(layer:PhotoTextLayer,event:ReactPointerEvent<HTMLButtonElement>){
    if(!textPanel||event.button!==0)return;
    const stage=event.currentTarget.closest<HTMLElement>("[data-photo-preview-stage]");
    const bounds=stage?.getBoundingClientRect();
    if(!bounds||!bounds.width||!bounds.height)return;
    event.preventDefault();
    event.stopPropagation();
    setSelectedTextLayerId(layer.id);
    textDragRef.current={
      id:layer.id,pointerId:event.pointerId,startX:event.clientX,startY:event.clientY,
      initialX:layer.xPercent,initialY:layer.yPercent,width:bounds.width,height:bounds.height
    };
    try{event.currentTarget.setPointerCapture(event.pointerId);}catch{/* Pointer capture is best-effort on older WebViews. */}
  }

  function handleTextLayerPointerMove(event:ReactPointerEvent<HTMLButtonElement>){
    const drag=textDragRef.current;
    if(!drag||drag.pointerId!==event.pointerId)return;
    const x=Math.max(2,Math.min(98,drag.initialX+(event.clientX-drag.startX)/drag.width*100));
    const y=Math.max(2,Math.min(98,drag.initialY+(event.clientY-drag.startY)/drag.height*100));
    setTextLayers(current=>current.map(layer=>layer.id===drag.id?{...layer,xPercent:x,yPercent:y}:layer));
  }

  function handleTextLayerPointerUp(event:ReactPointerEvent<HTMLButtonElement>){
    if(textDragRef.current?.pointerId===event.pointerId)textDragRef.current=null;
  }

  function handleBackgroundPick(event:ReactPointerEvent<HTMLCanvasElement>){
    if(!pickingBackground||!image)return;
    const canvas=event.currentTarget,bounds=canvas.getBoundingClientRect();
    if(!bounds.width||!bounds.height)return;
    const px=(event.clientX-bounds.left)/bounds.width*canvas.width,py=(event.clientY-bounds.top)/bounds.height*canvas.height;
    const xMm=px/(canvas.width/A4_WIDTH_MM),yMm=py/(canvas.height/A4_HEIGHT_MM);
    const positions=getPhotoSheetPhotoPositions(size,captionHeightMm,photosPerPage,previewPhotoCount);
    const position=positions.find(item=>xMm>=item.xMm&&xMm<=item.xMm+size.widthMm&&yMm>=item.yMm&&yMm<=item.yMm+size.heightMm);
    if(!position)return;
    const localX=xMm-position.xMm,localY=yMm-position.yMm;
    const crop=cropSource(image,size,zoom,focusX,focusY);
    const sx=Math.max(0,Math.min(image.naturalWidth-1,Math.floor(crop.x+localX/size.widthMm*crop.width)));
    const sy=Math.max(0,Math.min(image.naturalHeight-1,Math.floor(crop.y+localY/size.heightMm*crop.height)));
    const sampler=document.createElement("canvas");sampler.width=1;sampler.height=1;
    const ctx=sampler.getContext("2d",{willReadFrequently:true});if(!ctx){setError("The background colour could not be sampled.");return;}
    ctx.filter=`brightness(${adjustments.brightness}%) contrast(${adjustments.contrast}%) saturate(${adjustments.saturation}%)`;
    ctx.drawImage(image,sx,sy,1,1,0,0,1,1);
    ctx.filter="none";
    const [r,g,b]=ctx.getImageData(0,0,1,1).data;
    setSourceBackgroundColor("#"+[r,g,b].map(v=>v.toString(16).padStart(2,"0")).join(""));
    setPickingBackground(false);
  }

  function selectLook(nextLook: LookPreset) {
    const preset = LOOKS.find(item => item.id === nextLook) ?? LOOKS[0];
    setLook(nextLook);
    setAdjustments(preset.adjustments);
  }

  async function createPrintSheets(){
    if(!image||busyRef.current)return;
    const parsedCopies=Number(copiesInput);
    if(!Number.isInteger(parsedCopies)||parsedCopies<1||parsedCopies>maxCopies){setError("Choose between 1 and "+maxCopies+" photos. The A4 page count is calculated automatically.");return;}
    busyRef.current=true;setBusy(true);setError("");
    try{
      const pages=Math.ceil(parsedCopies/photosPerPage),jpegPages:Uint8Array[]=[];
      let jpegByteLength=0;
      const pdfStructureAllowance=4096+pages*1024;
      const sharedPhotoTile=createPhotoTile(image,size,zoom,focusX,focusY,adjustments,
        {sourceColor:sourceBackgroundColor,targetColor:targetBackgroundColor,tolerance:backgroundTolerance});
      try{
        for(let page=0;page<pages;page++){
          const canvas=document.createElement("canvas");canvas.width=A4_WIDTH_PX;canvas.height=A4_HEIGHT_PX;
          const context=canvas.getContext("2d");if(!context)throw new Error("A print canvas could not be created.");
          drawPhotoSheetPage(context,image,canvas.width,size,parsedCopies,photosPerPage,page,zoom,focusX,focusY,adjustments,
            {name:photoName,dob:dateOfBirth,fontSizePt:captionFontSizePt,color:captionColor},
            {sourceColor:sourceBackgroundColor,targetColor:targetBackgroundColor,tolerance:backgroundTolerance},captionHeightMm,textLayers,sharedPhotoTile);
          let blob=await canvasToBlob(canvas,"image/jpeg",.9);
          if(mode!=="private-library"&&blob.size>MAX_FILE_BYTES)blob=await canvasToBlob(canvas,"image/jpeg",.82);
          jpegByteLength+=blob.size;
          if(mode!=="private-library"&&jpegByteLength+pdfStructureAllowance>MAX_FILE_BYTES)
            throw new Error(`The prepared photo PDF would exceed SmartPrint's ${MAX_FILE_BYTES / (1024 * 1024)} MB file limit. Use a smaller original photo, choose fewer photos per job, or split the order into smaller batches.`);
          jpegPages.push(new Uint8Array(await blob.arrayBuffer()));
        }
      }finally{
        sharedPhotoTile.width=0;
        sharedPhotoTile.height=0;
      }
      const bytes=buildPhotoPdf(jpegPages);
      const buffer=bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength) as ArrayBuffer;
      const pdf=new Blob([buffer],{type:"application/pdf"});
      if(mode!=="private-library"&&pdf.size>MAX_FILE_BYTES)throw new Error(`The prepared photo PDF exceeds SmartPrint's ${MAX_FILE_BYTES / (1024 * 1024)} MB file limit. Use a smaller original photo or reduce the number of copies.`);
      const output=new File([pdf],safeBaseName(source.name)+"-"+size.id+"-photos.pdf",{type:"application/pdf",lastModified:Date.now()});
      const editorSettings: PhotoStudioEditorSettings = {
        sizeId: size.id,
        look,
        adjustments: { ...adjustments },
        copiesInput: String(parsedCopies),
        photosPerPageInput,
        zoom,
        focusX,
        focusY,
        includeFormDetails,
        photoName,
        dateOfBirth,
        captionFontSizePt,
        captionColor,
        textLayers: textLayers.map(layer => ({ ...layer })),
        targetBackgroundColor,
        sourceBackgroundColor,
        backgroundTolerance
      };
      await onApply([{file:output,sizeCode:size.id,photoCount:parsedCopies,photosPerPage,captionHeightMm,resolutionDpi:300,editorSettings,previewPages:jpegPages.map(jpeg=>new Blob([jpeg],{type:"image/jpeg"}))}]);
    }catch(cause){setError(cause instanceof Error?cause.message:"Photo sheet creation failed. Try again with this photo.");}
    finally{busyRef.current=false;setBusy(false);}
  }

  const resolutionMessage = image ? sourceResolutionNote(image, size, zoom) : "";
  const footerHint = mode === "private-library"
    ? "Original stays private · A4 sheet saved here."
    : mode === "shop-create"
      ? "New order · Verify payment before printing."
      : mode === "shop"
        ? "Replace the unpaid job photo PDF; approval and dispatch still apply."
        : "Prepared A4 replaces this upload; original stays untouched.";
  const actionLabel = mode === "private-library" ? "Save private sheet" : mode === "shop-create" ? "Create photo order" : mode === "shop" ? "Save sheet to this job" : "Prepare A4 sheet";
  const busyLabel = mode === "private-library" ? "Saving privately…" : mode === "shop-create" ? "Creating photo order…" : mode === "shop" ? "Saving to the existing job…" : "Preparing your A4 sheet…";
  const cropPanel=activeTool==="crop",lookPanel=activeTool==="look",textPanel=activeTool==="text",backgroundPanel=activeTool==="background";

  return (
    <div ref={dialogRef} className={styles.overlay + (dark ? " " + styles.dark : "")} role="dialog" aria-modal="true" aria-labelledby="photo-studio-title" aria-busy={busy} tabIndex={-1}>
      <header className={styles.topbar}>
        <div className={styles.brand}>
          <span className={styles.brandMark}><ImageIcon size={17}/></span>
          <span><strong id="photo-studio-title">Photo Studio</strong><small>{mode === "private-library" ? "PRIVATE · ON THIS DEVICE" : mode === "shop-create" ? "SHOP · NEW PHOTO ORDER" : mode === "shop" ? "SHOP · EDIT EXISTING JOB" : "CUSTOMER · BEFORE UPLOAD"}</small></span>
        </div>
        <div className={styles.topbarMeta}><span>A4 · 210 × 297 mm</span><span>300 DPI</span></div>
        <button type="button" className={styles.iconButton} onClick={onCancel} disabled={busy} aria-label="Close Photo Studio" title="Close"><X size={18}/></button>
      </header>

      <div className={styles.editor}>
        <nav className={styles.toolRail} aria-label="Photo editing tools">
          <button type="button" id="photo-studio-tool-sheet" className={activeTool === "sheet" ? styles.toolActive : ""} onClick={() => setActiveTool("sheet")} aria-pressed={activeTool === "sheet"}><ImageIcon size={17}/><span>Sheet</span></button>
          <button type="button" id="photo-studio-tool-crop" className={activeTool === "crop" ? styles.toolActive : ""} onClick={() => setActiveTool("crop")} aria-pressed={activeTool === "crop"}><Crop size={17}/><span>Crop</span></button>
          <button type="button" id="photo-studio-tool-looks" className={activeTool === "look" ? styles.toolActive : ""} onClick={() => setActiveTool("look")} aria-pressed={activeTool === "look"}><Sparkles size={17}/><span>Looks</span></button>
          <button type="button" id="photo-studio-tool-text" className={activeTool==="text"?styles.toolActive:""} onClick={()=>setActiveTool("text")} aria-pressed={activeTool==="text"} title="Add, select and position text on the A4 sheet"><Type size={17}/><span>Text</span></button>
          <button type="button" id="photo-studio-tool-background" className={activeTool==="background"?styles.toolActive:""} onClick={()=>setActiveTool("background")} aria-pressed={activeTool==="background"}><PaintBucket size={17}/><span>Background</span></button>
        </nav>

        <aside className={styles.toolPanel} data-testid="photo-studio-tool-panel">
          <div className={styles.panelHeading}>
            <div><strong>{cropPanel?"Crop & position":lookPanel?"Photo look":textPanel?"Text layers":backgroundPanel?"Photo background":"Photo sheet & form details"}</strong><small>{cropPanel?"Frame the photo without changing facial features.":lookPanel?"Use subtle, print-friendly adjustments.":textPanel?"Add, select, edit and drag text directly on the A4 preview.":backgroundPanel?"Sample a flat background from the photo and replace only the edge-connected area.":"Set the ID photo size, form details and print quantity."}</small></div>
          </div>

          {cropPanel && <>
            <label className={styles.rangeControl}>Zoom <output>{zoom.toFixed(1)}×</output><input id="photo-studio-zoom" type="range" min="1" max="3" step="0.05" value={zoom} onChange={event => setZoom(Number(event.target.value))}/></label>
            <label className={styles.rangeControl}>Horizontal position <output>{focusX}%</output><input id="photo-studio-horizontal-position" type="range" min="0" max="100" value={focusX} onChange={event => setFocusX(Number(event.target.value))}/></label>
            <label className={styles.rangeControl}>Vertical position <output>{focusY}%</output><input id="photo-studio-vertical-position" type="range" min="0" max="100" value={focusY} onChange={event => setFocusY(Number(event.target.value))}/></label>
            <button type="button" id="photo-studio-reset-crop" className={styles.secondaryButton} onClick={() => { setZoom(1); setFocusX(50); setFocusY(50); }}><RotateCcw size={14}/> Reset crop</button>
          </>}

          {lookPanel && <>
            <div className={styles.lookGrid}>
              {LOOKS.map(item => <button type="button" key={item.id} id={"photo-studio-look-" + item.id} className={look === item.id ? styles.lookActive : styles.lookButton} onClick={() => selectLook(item.id)} aria-pressed={look === item.id}>
                <span className={styles.lookSwatch + " " + styles["look_" + item.id]}/><strong>{item.label}</strong><small>{item.note}</small>
              </button>)}
            </div>
            <label className={styles.rangeControl}>Brightness <output>{adjustments.brightness}%</output><input id="photo-studio-brightness" type="range" min="70" max="130" value={adjustments.brightness} onChange={event => { setLook("custom"); setAdjustments(current => ({ ...current, brightness: Number(event.target.value) })); }}/></label>
            <label className={styles.rangeControl}>Contrast <output>{adjustments.contrast}%</output><input id="photo-studio-contrast" type="range" min="70" max="140" value={adjustments.contrast} onChange={event => { setLook("custom"); setAdjustments(current => ({ ...current, contrast: Number(event.target.value) })); }}/></label>
            <label className={styles.rangeControl}>Colour <output>{adjustments.saturation}%</output><input id="photo-studio-colour" type="range" min="0" max="160" value={adjustments.saturation} onChange={event => { setLook("custom"); setAdjustments(current => ({ ...current, saturation: Number(event.target.value) })); }}/></label>
          </>}

          {textPanel && <>
            <button type="button" id="photo-studio-add-text" className={styles.secondaryButton} onClick={addTextLayer}><Type size={14}/> Add text</button>
            {selectedTextLayer ? <>
              <label className={styles.fieldLabel}>Text content
                <input id="photo-studio-text-content" type="text" maxLength={120} value={selectedTextLayer.text} onChange={event=>updateSelectedTextLayer({text:event.target.value})} placeholder="Enter text to print on the sheet"/>
              </label>
              <label className={styles.rangeControl}>Font size <output>{selectedTextLayer.fontSizePt} pt</output>
                <input id="photo-studio-text-font-size" type="range" min="6" max="36" step="1" value={selectedTextLayer.fontSizePt} onChange={event=>updateSelectedTextLayer({fontSizePt:Number(event.target.value)})}/>
              </label>
              <label className={styles.fieldLabel}>Text colour
                <input id="photo-studio-text-colour" type="color" value={selectedTextLayer.color} onChange={event=>updateSelectedTextLayer({color:event.target.value})}/>
              </label>
              <label className={styles.captionOptIn}>
                <input id="photo-studio-text-bold" type="checkbox" checked={selectedTextLayer.bold} onChange={event=>updateSelectedTextLayer({bold:event.target.checked})}/>
                <span><strong>Bold text</strong><small>Apply a stronger weight to the selected text.</small></span>
              </label>
              <button type="button" id="photo-studio-remove-text" className={styles.secondaryButton} onClick={removeSelectedTextLayer}><X size={14}/> Remove selected text</button>
              <div className={styles.editorHelp}>Drag the selected text on the page to position it. Text is included in the prepared A4 PDF.</div>
            </> : <div className={styles.editorHelp}>Add a text layer, then select it on the preview to edit its content, size and colour. You can add multiple text layers.</div>}
            {includeFormDetails && <>
              <div className={styles.editorHelp}>Photo caption styling</div>
              <label className={styles.fieldLabel}>Caption font size<select id="photo-studio-caption-font-size" value={captionFontSizePt} onChange={event=>setCaptionFontSizePt(Number(event.target.value))}><option value={5}>5 pt · Compact</option><option value={6}>6 pt · Standard</option><option value={7}>7 pt · Large</option></select></label>
              <label className={styles.fieldLabel}>Caption colour<input id="photo-studio-caption-colour" type="color" value={captionColor} onChange={event=>setCaptionColor(event.target.value)}/></label>
            </>}
          </>}

          {backgroundPanel && <>
            <div className={styles.backgroundPreviewRow} data-testid="photo-studio-background-state"><span>Sampled background</span><strong>{sourceBackgroundColor?sourceBackgroundColor.toUpperCase():"Not selected"}</strong>{sourceBackgroundColor&&<i style={{background:sourceBackgroundColor}}/>}</div>
            <button type="button" id="photo-studio-pick-background" className={styles.secondaryButton} onClick={()=>{setPickingBackground(true);setError("");}}><Pipette size={14}/>{pickingBackground?"Click the background in the preview…":"Pick background from photo"}</button>
            <label className={styles.fieldLabel}>Replacement background<input id="photo-studio-replacement-background" type="color" value={targetBackgroundColor} onChange={event=>setTargetBackgroundColor(event.target.value)}/></label>
            <div className={styles.backgroundPresets}>{["#ffffff","#f3f6fa","#dcecff","#dff3e7","#f6eadb"].map(color=><button key={color} type="button" style={{background:color}} aria-label={"Set background "+color} aria-pressed={targetBackgroundColor===color} onClick={()=>setTargetBackgroundColor(color)}/>)}</div>
            <label className={styles.rangeControl}>Background tolerance<output>{backgroundTolerance}</output><input id="photo-studio-background-tolerance" type="range" min="5" max="90" value={backgroundTolerance} onChange={event=>setBackgroundTolerance(Number(event.target.value))}/></label>
            <div className={styles.editorHelp}>This replaces sampled-colour pixels connected to the photo edges. Use a plain, evenly lit background and inspect hair/shoulders. This is a colour-key tool, not AI subject segmentation.</div>
            <button type="button" id="photo-studio-reset-background" className={styles.secondaryButton} onClick={()=>{setSourceBackgroundColor(null);setPickingBackground(false);setBackgroundTolerance(30);setTargetBackgroundColor("#ffffff");}}>Reset background</button>
          </>}

          {activeTool === "sheet" && <>
            <label className={styles.captionOptIn}>
              <input id="photo-studio-include-form-details" type="checkbox" checked={includeFormDetails} onChange={event=>{
                const enabled=event.target.checked;
                setIncludeFormDetails(enabled);
                if(!enabled){setPhotoName("");setDateOfBirth("");}
              }}/>
              <span><strong>Add name and DOB to each photo</strong><small>Optional. Nothing is printed below the photos unless you switch this on.</small></span>
            </label>
            {includeFormDetails&&<>
              <label className={styles.fieldLabel}>Name for form (optional)
                <input id="photo-studio-name" type="text" maxLength={48} value={photoName} onChange={event=>setPhotoName(event.target.value)} placeholder="Enter the name to print below each photo"/>
              </label>
              <label className={styles.fieldLabel}>Date of birth (optional)
                <input id="photo-studio-date-of-birth" type="date" min="1900-01-01" max={new Date().toISOString().slice(0,10)} value={dateOfBirth} onChange={event=>setDateOfBirth(event.target.value)}/>
              </label>
              <div className={styles.editorHelp}>Only the details you enter are printed below each photo. The photo dimensions stay unchanged.</div>
            </>}
            <label className={styles.fieldLabel}>Photo size
              <select id="photo-studio-photo-size" value={sizeId} onChange={event => setSizeId(event.target.value)}>
                {availableSizes.map(item => <option key={item.id} value={item.id}>{item.label} · {item.detail}{mode === "private-library" ? "" : " · "+formatPhotoRate(photoPrices[item.id])+"/photo"}</option>)}
              </select>
            </label>
            <label className={styles.fieldLabel}>Number of photos
              <input
                id="photo-studio-photo-count"
                type="number"
                aria-label="Number of photos"
                min="1"
                max={maxCopies}
                step="1"
                inputMode="numeric"
                value={copiesInput}
                onChange={event => setCopiesInput(event.target.value)}
                onBlur={event => {
                  // Read the live input before scheduling React state. A fast blur
                  // immediately after typing can run before the latest onChange has
                  // committed; using captured state here can revert 100 to 8.
                  const inputValue = event.currentTarget.value;
                  const parsedValue = Number(inputValue);
                  if (Number.isInteger(parsedValue)) {
                    setCopiesInput(String(Math.max(1, Math.min(maxCopies, parsedValue || 1))));
                  }
                }}
              />
            </label>
            {!copiesInputValid&&<div className={styles.editorHelp} role="status">Enter a whole number from 1 to {maxCopies}. The sheet count is calculated automatically.</div>}
            {availableSizes.length===0&&<div className={styles.editorHelp} role="alert">Photo printing is not enabled for any size at this shop. Ask the shop owner to configure a photo rate before preparing a sheet.</div>}
            <label className={styles.fieldLabel}>Photos per A4 page
              <select id="photo-studio-photos-per-page" value={photosPerPageInput} onChange={event=>setPhotosPerPageInput(event.target.value)}>
                <option value="auto">Fit as many as possible ({maximumLayout.perSheet})</option>
                {Array.from({length:maximumLayout.perSheet},(_,index)=>index+1).map(count=><option key={count} value={String(count)}>{count} per page</option>)}
              </select>
            </label>
            <div className={styles.sheetSummary} data-testid="photo-studio-sheet-summary">
              <div><span>Photo dimensions</span><strong>{size.widthMm} × {size.heightMm} mm</strong></div>
              <div><span>Photos per A4 page</span><strong>{layout.perSheet}</strong></div>
              <div><span>Grid alignment</span><strong>{layout.columns} × {layout.rows}</strong></div>
              <div><span>A4 pages in this job</span><strong>{sheetCount}</strong></div>
              <div><span>Preview page</span><strong>{previewPageIndex+1} of {sheetCount}</strong></div>
              {copies>photosPerPage&&copies%photosPerPage!==0&&<small>Final A4 page: {copies%photosPerPage} photos, with the remaining sheet area left blank.</small>}
              {mode !== "private-library"&&<small>Shop rate: {formatPhotoRate(photoPrices[size.id] ?? 0)} per photo. The server confirms the final total in your quote.</small>}
            </div>
          </>}

          <div className={styles.privacyNote}><SlidersHorizontal size={14}/><span>Your original photo is not changed. ID-safe tools adjust framing and print appearance only; they do not reshape or retouch facial features.</span></div>
        </aside>

        <main className={styles.canvasWorkspace}>
          <div className={styles.contextToolbar}>
            <span>PRINT SHEET PREVIEW</span>
            {sheetCount>1&&<div className={styles.pageNavigator}>
              <button type="button" id="photo-studio-previous-page" aria-label="Previous A4 page" onClick={()=>setPreviewPage(current=>Math.max(0,current-1))} disabled={previewPageIndex===0}><ChevronLeft size={14}/></button>
              <span>Page {previewPageIndex+1} of {sheetCount}</span>
              <button type="button" id="photo-studio-next-page" aria-label="Next A4 page" onClick={()=>setPreviewPage(current=>Math.min(sheetCount-1,current+1))} disabled={previewPageIndex>=sheetCount-1}><ChevronRight size={14}/></button>
            </div>}
            <span className={styles.toolbarDivider}/>
            <button type="button" onClick={() => setZoom(value => Math.max(1, Number((value - 0.1).toFixed(2))))} disabled={zoom <= 1} aria-label="Zoom out"><ZoomOut size={15}/></button>
            <span className={styles.zoomLabel}>{zoom.toFixed(1)}×</span>
            <button type="button" onClick={() => setZoom(value => Math.min(3, Number((value + 0.1).toFixed(2))))} disabled={zoom >= 3} aria-label="Zoom in"><ZoomIn size={15}/></button>
            <span className={styles.toolbarDivider}/>
            <button type="button" id="photo-studio-reset-look" onClick={() => { setZoom(1); setFocusX(50); setFocusY(50); selectLook("natural"); }}><RotateCcw size={14}/> Reset</button>
          </div>
          <div className={styles.canvasViewport}>
            {pickingBackground&&<div className={styles.pickInstruction}>Click a flat background area in the photo to sample its colour.</div>}
            <div className={styles.sheetFrame} onContextMenu={event=>event.preventDefault()} onDragStart={event=>event.preventDefault()}>
              {image ? <div className={styles.previewStage} data-photo-preview-stage>
                <canvas id="photo-studio-preview-canvas" ref={canvasRef} width={420} height={594} onPointerUp={handleBackgroundPick} onClick={handleBackgroundPick} style={{cursor:pickingBackground?"crosshair":"default"}} className={styles.previewCanvas} data-testid="photo-studio-preview-canvas" data-background-sample-x-mm={(getPhotoSheetPhotoPositions(size,captionHeightMm,photosPerPage,previewPhotoCount)[0]?.xMm ?? 0) + 2} data-background-sample-y-mm={(getPhotoSheetPhotoPositions(size,captionHeightMm,photosPerPage,previewPhotoCount)[0]?.yMm ?? 0) + 2} draggable={false} role="img" aria-label={"A4 page "+(previewPageIndex+1)+" of "+sheetCount+" with "+previewPhotoCount+" photos"}/>
                <div className={styles.previewProtection} aria-hidden="true"/>
                {textLayers.map(layer=><button
                  key={layer.id}
                  type="button"
                  className={styles.textLayer+(selectedTextLayerId===layer.id?" "+styles.textLayerSelected:"")}
                  style={{
                    left:layer.xPercent+"%",
                    top:layer.yPercent+"%",
                    color:layer.color,
                    fontWeight:layer.bold?700:500,
                    fontSize:`${(layer.fontSizePt*25.4/72/A4_WIDTH_MM*100).toFixed(4)}cqw`
                  }}
                  aria-label={"Select text: "+(layer.text||"Empty text")}
                  aria-pressed={selectedTextLayerId===layer.id}
                  data-testid="photo-studio-text-layer"
                  disabled={!textPanel}
                  onClick={()=>setSelectedTextLayerId(layer.id)}
                  onPointerDown={event=>handleTextLayerPointerDown(layer,event)}
                  onPointerMove={handleTextLayerPointerMove}
                  onPointerUp={handleTextLayerPointerUp}
                  onPointerCancel={handleTextLayerPointerUp}
                >{layer.text||" "}</button>)}
              </div> : <div className={styles.loadingState} role={imageError ? "alert" : "status"}>{imageError || "Preparing your photo…"}</div>}
            </div>
          </div>
          <div className={styles.canvasFooter}>
            <span>{size.label} · {size.widthMm} × {size.heightMm} mm</span>
            <span>{copies} photos · {sheetCount} A4 pages</span>
            <small>Page {previewPageIndex+1}: {previewPhotoCount} photos</small>
            {resolutionMessage && <small>{resolutionMessage}</small>}
          </div>
        </main>
      </div>

      <footer className={styles.footer}>
        <div className={styles.footerHint}><Check size={14}/> {footerHint}</div>
        <div className={styles.footerActions}>
          <button type="button" id="photo-studio-cancel" className={styles.cancelButton} onClick={onCancel} disabled={busy}>Cancel</button>
          <button type="button" id="photo-studio-apply" className={styles.applyButton} onClick={() => void createPrintSheets()} disabled={!image || busy || !!imageError || availableSizes.length === 0 || !copiesInputValid}>
            {busy ? busyLabel : actionLabel} <Check size={15}/>
          </button>
        </div>
      </footer>
      {error && <div className={styles.errorToast} role="alert">{error}</div>}
    </div>
  );
}
