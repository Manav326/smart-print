import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { A4_HEIGHT_POINTS, A4_WIDTH_POINTS, buildPhotoPdf, getPhotoSheetLayout, getPhotoSheetPhotoPositions, hasSufficientPhotoResolution } from "../src/photo-studio-layout.ts";

test("exact physical photo layout starts at the top safe margin on A4",()=>{
 const layout=getPhotoSheetLayout({widthMm:35,heightMm:45});
 assert.equal(layout.perSheet,25);
 assert.ok(Math.abs((210-layout.contentWidthMm)/2-layout.offsetXmm)<.001);
 assert.equal(layout.offsetYmm,8);
});
test("caption labels reserve their own area and never shrink photo dimensions",()=>{
 const plain=getPhotoSheetLayout({widthMm:35,heightMm:45});
 const labelled=getPhotoSheetLayout({widthMm:35,heightMm:45},7);
 assert.ok(labelled.perSheet<=plain.perSheet);
 assert.equal(labelled.offsetXmm,plain.offsetXmm);
 assert.equal(labelled.contentWidthMm,plain.contentWidthMm);
 assert.equal(labelled.contentHeightMm,labelled.rows*52+(labelled.rows-1)*3);
});
test("all configured photo sizes have positive safe layouts",()=>{
 for(const size of [{widthMm:35,heightMm:45},{widthMm:25,heightMm:35},{widthMm:50.8,heightMm:50.8},{widthMm:101.6,heightMm:152.4}]){
  const layout=getPhotoSheetLayout(size,7);
  assert.ok(layout.columns>=1&&layout.rows>=1&&layout.perSheet>=1);
  assert.ok(layout.offsetXmm>=8&&layout.offsetYmm>=8);
  assert.ok(layout.offsetXmm+layout.contentWidthMm<=202.001);
  assert.ok(layout.offsetYmm+layout.contentHeightMm<=289.001);
 }
});
test("multiple prepared A4 pages become one multi-page PDF",()=>{
 const jpeg=new Uint8Array([0xff,0xd8,0xff,0xd9]);
 const pdf=buildPhotoPdf([jpeg,jpeg,jpeg]);
 const value=new TextDecoder("latin1").decode(pdf);
 assert.ok(value.startsWith("%PDF-1.4"));
 assert.match(value,/\/Count 3/);
 assert.equal((value.match(/\/Type \/Page \/Parent 2 0 R/g)??[]).length,3);
 assert.match(value,/\/MediaBox \[0 0 595\.275590551 841\.88976378\]/);
 const xrefOffset=Number(value.match(/startxref\n(\d+)\n%%EOF/)?.[1]);
 assert.ok(Number.isInteger(xrefOffset));
 assert.ok(value.slice(xrefOffset).startsWith("xref\n"));
 let objectNumber=1;
 for(const match of value.slice(xrefOffset).matchAll(/(\d{10}) 00000 n/g)){
  const offset=Number(match[1]);
  assert.ok(value.slice(offset).startsWith(`${objectNumber} 0 obj`),`PDF object ${objectNumber} has a valid cross-reference offset`);
  objectNumber+=1;
 }
 assert.equal(objectNumber,12);
});
test("photo PDF generation rejects empty or incomplete JPEG pages",()=>{
 assert.throws(()=>buildPhotoPdf([]),/At least one/);
 assert.throws(()=>buildPhotoPdf([new Uint8Array([1,2,3])]),/complete JPEG/);
});

test("name and DOB are opt-in and are absent from the default photo sheet",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/const \[includeFormDetails,setIncludeFormDetails\]=useState\(false\)/);
 assert.match(studio,/const hasCaption=includeFormDetails&&Boolean\(photoName\.trim\(\)\|\|dateOfBirth\.trim\(\)\)/);
 assert.match(studio,/checked=\{includeFormDetails\}/);
 assert.match(studio,/\{includeFormDetails&&<>[\s\S]*?Name for form[\s\S]*?Date of birth/);
 assert.ok(studio.includes('if(!enabled){setPhotoName("");setDateOfBirth("");}'));
});


test("customer Photo Studio cannot turn an already uploaded original into a second print job",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/if\(uploadedFileFingerprints\.includes\(fingerprint\)\)/);
 assert.match(app,/const photoAlreadyUploaded=Boolean\(file&&uploadedFileFingerprints\.includes\(customerFileFingerprint\(file\)\)\)/);
 assert.match(app,/disabled=\{busy\|\|batchLockedForPayment\|\|photoAlreadyUploaded\}/);
 assert.match(app,/This photo is already with the shop[\s\S]*?without creating another print job/);
});

test("customer photo preparation depends on configured shop pricing, not the current printer capability",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.doesNotMatch(app,/photoStudioReady|isPhotoStudioPrinterReady/);
 assert.match(app,/if\(!photoRatesAvailable\)/);
 assert.match(app,/photoPricing=\{result\.photoPricing\}/);
});

test("the shared Photo Studio explains different save actions for customers and shop owners",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(studio,/mode = "customer"/);
 assert.match(studio,/mode === "shop"/);
 assert.match(studio,/Prepare A4 sheet/);
 assert.match(studio,/Save sheet to this job/);
 assert.match(app,/PhotoStudio source=\{photoStudioSource\} mode="customer"/);
 assert.match(app,/PhotoStudio source=\{shopPhotoStudio\.source\} mode="shop"/);
});

test("shop owners can inspect the actual prepared photo PDF before approving the job",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/isPdf&&job\.printKind==="PHOTO"[\s\S]*?<iframe src=\{url\} title=\{name\}\//);
 assert.match(app,/SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW/);
});

test("Photo Studio locks background scroll and supports accessible keyboard dismissal",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(studio,/aria-labelledby="photo-studio-title"/);
 assert.match(studio,/document\.body\.style\.overflow = "hidden"/);
 assert.match(studio,/event\.key === "Escape"/);
 assert.match(studio,/event\.key !== "Tab"/);
 assert.match(studio,/disabled=\{busy\} aria-label="Close Photo Studio"/);
 assert.match(css,/@media\(max-width:390px\)[\s\S]*?topbarMeta span:last-child\{display:none\}/);
});

test("a successful shop photo PDF save is not misreported as failed when refreshing the queue fails",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/The enhanced multi-page A4 photo PDF is saved to the same print job/);
 assert.match(app,/The A4 photo PDF was saved to the existing print job, but the queue could not refresh/);
});


test("short portrait phones keep Photo Studio preview, controls and save actions usable",()=>{
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(css,/@media\(max-width:720px\) and \(max-height:620px\)/);
 assert.match(css,/grid-template-rows:40px minmax\(160px,1fr\) auto/);
 assert.match(css,/\.toolPanel\{grid-row:3;[^}]*max-height:34vh;[^}]*overflow:auto/);
 assert.match(css,/\.toolPanel\{max-height:26vh/);
 assert.match(css,/\.canvasWorkspace\{grid-template-rows:32px minmax\(0,1fr\) 26px/);
 assert.match(css,/\.footerActions button\{min-height:34px\}/);
});


test("Android image providers with blank MIME types still expose Photo Studio by supported filename",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/function isCustomerImage\(file:File\):boolean/);
 assert.ok(app.includes(String.raw`|| /\.(jpe?g|png)$/i.test(file.name);`));
 assert.match(app,/isCustomerImage\(file\)&&photoRatesAvailable/);
});

test("shop cancellation returns to the original document preview",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/returnPreview:\{name:string;blob:Blob;mediaType:string;job:ShopPrintJob;readOnly:boolean\}/);
 assert.match(app,/function cancelShopPhotoStudio\(\)[\s\S]*?URL\.createObjectURL\(restored\.blob\)[\s\S]*?setPreview/);
 assert.match(app,/onCancel=\{cancelShopPhotoStudio\}/);
 assert.match(app,/focusEditOnMount=\{focusShopPhotoEdit\}/);
 assert.match(app,/setFocusShopPhotoEdit\(true\)/);
 assert.match(app,/editPhotoButtonRef\.current\?\.focus\(\)/);
 assert.match(app,/setPreview\(null\); setFocusShopPhotoEdit\(false\)/);
});

test("Photo Studio follows the saved light or dark appearance in both customer and shop routes",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const css=readFileSync(new URL("../src/PhotoStudio.module.css",import.meta.url),"utf8");
 assert.match(app,/<CustomerPrintFlow dark=\{dark\}/);
 assert.match(app,/PhotoStudio source=\{photoStudioSource\} mode="customer" dark=\{dark\}/);
 assert.match(app,/PhotoStudio source=\{shopPhotoStudio\.source\} mode="shop" dark=\{dark\}/);
 assert.match(studio,/dark = false/);
 assert.match(studio,/dark \? " " \+ styles\.dark/);
 for(const selector of [".dark .toolPanel",".dark .fieldLabel",".dark .canvasWorkspace",".dark .footer",".dark .captionOptIn"])
  assert.ok(css.includes(selector),"Missing dark Photo Studio styling: "+selector);
});

test("Photo Studio restores focus to the customer or shop control after it closes",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/const previouslyFocused = document\.activeElement instanceof HTMLElement/);
 assert.match(studio,/previouslyFocused\?\.focus\(\)/);
});

test("customers are told when the shop has not enabled photo printing",()=>{
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(app,/Photo printing is not enabled at this shop yet/);
 assert.match(app,/Ask the shop owner to configure photo sizes and rates/);
});

test("50 photos can be laid out as nine per A4 page with a partially filled final page",()=>{
 const layout=getPhotoSheetLayout({widthMm:35,heightMm:45},0,9);
 assert.equal(layout.perSheet,9);
 assert.equal(layout.columns,5);
 assert.equal(layout.rows,2);
 const photoCount=50;
 const pageCount=Math.ceil(photoCount/layout.perSheet);
 assert.equal(pageCount,6);
 assert.equal(photoCount-(pageCount-1)*layout.perSheet,5);
});

test("manual photo density validates against physical page capacity",()=>{
 assert.equal(getPhotoSheetLayout({widthMm:35,heightMm:45},0,9).perSheet,9);
 assert.throws(()=>getPhotoSheetLayout({widthMm:35,heightMm:45},0,26),/Photos per A4 page/);
 assert.throws(()=>getPhotoSheetLayout({widthMm:35,heightMm:45},0,0),/Photos per A4 page/);
});

test("50 photos at nine per page build one six-page A4 PDF",()=>{
 const jpeg=new Uint8Array([0xff,0xd8,0xff,0xd9]);
 const pdf=buildPhotoPdf(Array.from({length:6},()=>jpeg));
 const value=new TextDecoder("latin1").decode(pdf);
 assert.match(value,/\/Count 6/);
 assert.equal((value.match(/\/Type \/Page \/Parent 2 0 R/g)??[]).length,6);
});

test("changing photos per A4 page redraws the live preview",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/\},\[image,size,copies,photosPerPage,previewPageIndex,/);
});

test("Photo Studio returns preview-only JPEG pages and the customer UI brands prepared pages",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 const app=readFileSync(new URL("../src/App.tsx",import.meta.url),"utf8");
 assert.match(studio,/previewPages:jpegPages\.map\(jpeg=>new Blob\(\[jpeg\],\{type:"image\/jpeg"\}\)\)/);
 assert.match(app,/customer-original-image-preview/);
 assert.match(app,/customer-edited-photo-frame/);
 assert.match(app,/SmartPrint by QuantaEdge/);
});

test("live Photo Studio QA reads summary rows by accessible labels, not unhashed CSS Module names",()=>{
 const qa=readFileSync(new URL("../../../scripts/qa/smartprint-local-e2e.mjs",import.meta.url),"utf8");
 assert.match(qa,/getByText\("Photo dimensions", \{ exact: true \}\)/);
 assert.match(qa,/getByText\("A4 pages in this job", \{ exact: true \}\)/);
 assert.doesNotMatch(qa,/locator\(["']\.sheetSummary["']\)/);
});


test("photo slots use the declared fixed 8 mm origin and 3 mm cut gutter",()=>{
 const size={widthMm:35,heightMm:45};
 const layout=getPhotoSheetLayout(size,0,9);
 const positions=getPhotoSheetPhotoPositions(size,0,9,9);
 assert.equal(layout.offsetYmm,8);
 assert.equal(positions.length,9);
 assert.deepEqual(positions[0],{photoIndex:0,row:0,column:0,xMm:layout.offsetXmm,yMm:8});
 assert.equal(positions[1].xMm-positions[0].xMm,38);
 assert.equal(positions[5].yMm-positions[0].yMm,48);
 assert.equal(positions[5].xMm,positions[0].xMm);
});

test("partial final page keeps the exact same grid slots instead of recentering",()=>{
 const size={widthMm:35,heightMm:45};
 const firstPage=getPhotoSheetPhotoPositions(size,0,9,9);
 const lastPage=getPhotoSheetPhotoPositions(size,0,9,5);
 assert.deepEqual(lastPage,firstPage.slice(0,5));
 assert.equal(lastPage[0].yMm,8);
 assert.equal(lastPage[4].xMm,firstPage[4].xMm);
});

test("short rows are anchored to fixed grid columns rather than centred independently",()=>{
 const size={widthMm:35,heightMm:45};
 const positions=getPhotoSheetPhotoPositions(size,0,9,7);
 assert.deepEqual(positions.slice(5).map(item=>item.column),[0,1]);
 assert.equal(positions[5].xMm,positions[0].xMm);
 assert.equal(positions[6].xMm-positions[5].xMm,38);
});

test("fixed photo positions stay inside safe A4 margins for every supported size",()=>{
 const sizes=[
  {widthMm:35,heightMm:45},
  {widthMm:25,heightMm:35},
  {widthMm:50.8,heightMm:50.8},
  {widthMm:101.6,heightMm:152.4}
 ];
 const expectedCapacities=[25,49,15,1];
 sizes.forEach((size,index)=>{
  const layout=getPhotoSheetLayout(size);
  const positions=getPhotoSheetPhotoPositions(size,0,layout.perSheet,layout.perSheet);
  assert.equal(positions.length,expectedCapacities[index]);
  for(const position of positions){
   assert.ok(position.xMm>=8);
   assert.ok(position.yMm>=8);
   assert.ok(position.xMm+size.widthMm<=202.001);
   assert.ok(position.yMm+size.heightMm<=289.001);
  }
 });
});

test("caption space shifts only the next grid row and never shrinks the photo tile",()=>{
 const size={widthMm:35,heightMm:45};
 const plain=getPhotoSheetPhotoPositions(size,0,9,9);
 const labelled=getPhotoSheetPhotoPositions(size,7,9,9);
 assert.equal(labelled[0].xMm,plain[0].xMm);
 assert.equal(labelled[0].yMm,plain[0].yMm);
 assert.equal(labelled[5].yMm-plain[5].yMm,7);
 assert.equal(size.widthMm,35);
 assert.equal(size.heightMm,45);
});

test("source resolution checks both cropped axes at the selected physical size",()=>{
 const size={widthMm:35,heightMm:45};
 assert.equal(hasSufficientPhotoResolution(800,1100,size,1),true);
 assert.equal(hasSufficientPhotoResolution(900,500,size,1),false);
 assert.equal(hasSufficientPhotoResolution(800,1100,size,2),false);
 assert.equal(hasSufficientPhotoResolution(1800,2700,{widthMm:101.6,heightMm:152.4},1.5),true);
 assert.equal(hasSufficientPhotoResolution(1800,2700,{widthMm:101.6,heightMm:152.4},1.51),false);
});

test("source resolution helper rejects invalid dimensions and zoom values",()=>{
 const size={widthMm:35,heightMm:45};
 assert.equal(hasSufficientPhotoResolution(0,1000,size),false);
 assert.equal(hasSufficientPhotoResolution(1000,0,size),false);
 assert.equal(hasSufficientPhotoResolution(1000,1000,size,0),false);
 assert.equal(hasSufficientPhotoResolution(1000,1000,size,Number.NaN),false);
});

test("live Photo Studio uses the same deterministic slot helper for preview, PDF and background sampling",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/getPhotoSheetPhotoPositions\(size,captionHeightMm,photosPerPage,pageCopies\)/);
 assert.match(studio,/getPhotoSheetPhotoPositions\(size,captionHeightMm,photosPerPage,previewPhotoCount\)/);
 assert.match(studio,/const x=position\.xMm,y=position\.yMm/);
 assert.match(studio,/const localX=xMm-position\.xMm,localY=yMm-position\.yMm/);
 assert.doesNotMatch(studio,/const pageOffsetY=Math\.max\(8,\(A4_HEIGHT_MM-visibleContentHeightMm\)\/2\)/);
 assert.doesNotMatch(studio,/const rowOffset=\(layout\.contentWidthMm-rowWidth\)\/2/);
});

test("A4 raster uses independent physical x and y scales before exact-A4 PDF placement",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/scaleX=canvasWidth\/A4_WIDTH_MM,scaleY=context\.canvas\.height\/A4_HEIGHT_MM/);
 assert.match(studio,/context\.scale\(scaleX,scaleY\)/);
 const jpeg=new Uint8Array([0xff,0xd8,0xff,0xd9]);
 const value=new TextDecoder("latin1").decode(buildPhotoPdf([jpeg,jpeg]));
 const pageBox="/MediaBox [0 0 "+A4_WIDTH_POINTS+" "+A4_HEIGHT_POINTS+"]";
 const fullPageImage=A4_WIDTH_POINTS+" 0 0 "+A4_HEIGHT_POINTS+" 0 0 cm";
 assert.equal(value.split(pageBox).length-1,2);
 assert.equal(value.split(fullPageImage).length-1,2);
});

test("Photo Studio resolution warning is recomputed when crop zoom changes",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/sourceResolutionNote\(image, size, zoom\)/);
 assert.match(studio,/reduce the zoom/i);
});


test("automatic captioned A4 capacities stay within each photo size's printable bounds",()=>{
 const sizes=[
  {widthMm:35,heightMm:45},
  {widthMm:25,heightMm:35},
  {widthMm:50.8,heightMm:50.8},
  {widthMm:101.6,heightMm:152.4}
 ];
 const expected=[25,42,12,1];
 sizes.forEach((size,index)=>{
  const layout=getPhotoSheetLayout(size,7);
  assert.equal(layout.perSheet,expected[index]);
  assert.ok(layout.offsetYmm+layout.contentHeightMm<=289.001);
 });
});

test("4x6 option explicitly means a portrait 4-inch by 6-inch tile on A4",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.match(studio,/id: "4x6", label: "4 × 6 inch · Portrait", detail: "Portrait tile on an A4 sheet", widthMm: 101\.6, heightMm: 152\.4/);
});


test("all supported photo sizes expose every valid manual density and reject the next slot",()=>{
 const cases=[
  {size:{widthMm:35,heightMm:45},maximum:25},
  {size:{widthMm:25,heightMm:35},maximum:49},
  {size:{widthMm:50.8,heightMm:50.8},maximum:15},
  {size:{widthMm:101.6,heightMm:152.4},maximum:1}
 ];
 for(const item of cases){
  for(let density=1;density<=item.maximum;density++){
   const layout=getPhotoSheetLayout(item.size,0,density);
   assert.equal(layout.perSheet,density);
   assert.ok(layout.columns*layout.rows>=density);
   const full=getPhotoSheetPhotoPositions(item.size,0,density,density);
   assert.equal(full.length,density);
   assert.equal(new Set(full.map(position=>position.row+":"+position.column)).size,density);
  }
  assert.throws(()=>getPhotoSheetLayout(item.size,0,item.maximum+1),/Photos per A4 page/);
 }
});

test("page-count boundaries allocate every requested photo exactly once",()=>{
 const cases=[
  {size:{widthMm:35,heightMm:45},maximum:25},
  {size:{widthMm:25,heightMm:35},maximum:49},
  {size:{widthMm:50.8,heightMm:50.8},maximum:15},
  {size:{widthMm:101.6,heightMm:152.4},maximum:1}
 ];
 for(const item of cases){
  for(const density of [1,Math.max(1,Math.floor(item.maximum/2)),item.maximum]){
   for(const quantity of new Set([1,Math.max(1,density-1),density,Math.min(100,density+1),Math.min(100,2*density),Math.min(100,2*density+1),100])){
    const pages=Math.ceil(quantity/density);
    let allocated=0;
    for(let page=0;page<pages;page++){
     const countOnPage=Math.min(density,quantity-page*density);
     const positions=getPhotoSheetPhotoPositions(item.size,0,density,countOnPage);
     assert.equal(positions.length,countOnPage);
     allocated+=positions.length;
    }
    assert.equal(allocated,quantity);
   }
  }
 }
});

test("photo coordinate helper rejects invalid page counts and invalid density",()=>{
 const size={widthMm:35,heightMm:45};
 assert.throws(()=>getPhotoSheetPhotoPositions(size,0,9,-1),/Photos on this A4 page/);
 assert.throws(()=>getPhotoSheetPhotoPositions(size,0,9,10),/Photos on this A4 page/);
 assert.throws(()=>getPhotoSheetPhotoPositions(size,0,1.5,1),/Photos per A4 page/);
 assert.throws(()=>getPhotoSheetPhotoPositions(size,0,0,0),/Photos per A4 page/);
});

test("Photo Studio identifies custom manual image adjustments instead of marking Natural selected",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.ok(studio.includes('type LookPreset = "natural" | "warm" | "crisp" | "mono" | "custom"'));
 assert.ok(studio.includes('setLook("custom"); setAdjustments(current => ({ ...current, brightness:'));
 assert.ok(studio.includes('setLook("custom"); setAdjustments(current => ({ ...current, contrast:'));
 assert.ok(studio.includes('setLook("custom"); setAdjustments(current => ({ ...current, saturation:'));
 assert.ok(studio.includes("aria-pressed={look === item.id}"));
});

test("Photo Studio prevents apply without a whole-number quantity or any shop photo rates",()=>{
 const studio=readFileSync(new URL("../src/PhotoStudio.tsx",import.meta.url),"utf8");
 assert.ok(studio.includes("const copiesInputValid = Number.isInteger(parsedCopiesInput) && parsedCopiesInput >= 1 && parsedCopiesInput <= maxCopies"));
 assert.ok(studio.includes("Enter a whole number from 1 to {maxCopies}"));
 assert.match(studio,/Photo printing is not enabled for any size at this shop/);
 assert.ok(studio.includes("availableSizes.length === 0 || !copiesInputValid"));
});

test("Photo Studio browser fixture inspects generated PDF page boxes and rendered pixel evidence",()=>{
 const fixture=readFileSync(new URL("./photo-studio.browser.tsx",import.meta.url),"utf8");
 assert.match(fixture,/mediaBoxCount/);
 assert.match(fixture,/pdfImageSizes/);
 assert.match(fixture,/pixelProbes/);
 assert.match(fixture,/captionRedPixels/);
 assert.match(fixture,/oversizedRaster/);
});

test("Photo Studio browser acceptance covers every editor tool and failure recovery path",()=>{
 const browser=readFileSync(new URL("./theme-responsive.browser.mjs",import.meta.url),"utf8");
 for(const label of [
  "Crop","Looks","Background","Sheet","Text","Zoom in","Zoom out","Reset crop","Reset background",
  "Photo size","Number of photos","Photos per A4 page","Caption font size","Caption colour",
  "Replacement background","Background tolerance","Pick background from photo","Prepare A4 sheet"
 ]) assert.ok(browser.includes(label),"Missing browser coverage for Photo Studio control: "+label);
 assert.ok(browser.includes("exercisePhotoStudioFeatureMatrix()"));
 assert.ok(browser.includes("exercisePhotoStudioFailurePaths()"));
 assert.match(browser,/failRender=1/);
 assert.match(browser,/oversizeRaster=1/);
});
