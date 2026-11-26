import { useMemo, useState } from "react";
import { useLocale, translateUiText } from "./i18n";
import { AlertTriangle, Check, ChevronDown, Clock3, CreditCard, Eye, FileText, History, Image as ImageIcon, Inbox, PackageCheck, Printer, RefreshCw, RotateCcw, ShieldCheck, Wallet, Wifi, X } from "lucide-react";
import type { AgentDevice, PrinterDevice, ShopPayment, ShopPrintJob } from "./api";
import { formatIstDateTime } from "./date-time";
import { buildShopOrderJourneys, filterShopOrderJourneys, isActiveOrderWork, orderJourneyStageLabel, orderJourneyStatusLabel, type OrderJourneyStage } from "./order-journey";
import styles from "./OrderJourney.module.css";
import { canConfirmPhysicalOutput, canManuallyReconcileUncertainOutput, canResumeUnprintedPhysicalJob, hasIncompleteSpoolerPageProgress, hasVerifiedAgentSpoolerObservation, isPhysicalOutputConfirmationStatus, isRetainedSpoolerJob } from "./spooler-output-confirmation";
import { getPrinterAvailabilityReason, isAgentOnlineForShop, isPrinterAvailableForShop } from "./printer-availability";

type ShopDestination = "Print Queue" | "Payments" | "Print Agent";
interface Props {
  jobs: ShopPrintJob[]; payments: ShopPayment[]; printers: PrinterDevice[]; agents: AgentDevice[];
  refreshing: boolean; onRefresh: () => void; onNavigate: (section: ShopDestination) => void;
  onReviewBatch: (batchId: string) => void; onVerifyPayment: (payment: ShopPayment) => void;
  onPreview: (job: ShopPrintJob) => void; onApprove: (job: ShopPrintJob) => void;
  onReject: (job: ShopPrintJob) => void; onRefund: (job: ShopPrintJob) => void;
  onAssignPrinter: (job: ShopPrintJob, printerDeviceId: string) => void;
  onDispatch: (job: ShopPrintJob) => void; onConfirmOutput: (job: ShopPrintJob) => void;
  onRetryPdfExport: (job: ShopPrintJob) => void; onHistory: (job: ShopPrintJob) => void;
  onOpenPhotoStudio: (job: ShopPrintJob) => void;
  onResumeUnprintedBatch: (batchId: string, jobIds: string[]) => Promise<boolean>;
}
const readyStatuses = new Set(["COMPLETED","PDF_EXPORTED"]);
const flowSteps: Array<{id:OrderJourneyStage;title:string;detail:string;icon:typeof Inbox}> = [
  {id:"received",title:"Received",detail:"Files arrive safely",icon:Inbox},
  {id:"review",title:"Review",detail:"Accept or exclude files",icon:FileText},
  {id:"payment",title:"Payment",detail:"Confirm payment received",icon:Wallet},
  {id:"printing",title:"Printing",detail:"Shop computer prints",icon:Printer},
  {id:"ready",title:"Ready",detail:"Output checked and complete",icon:PackageCheck}
];
function tx(locale: string, value: string){return translateUiText(locale,value);}
function money(value:number,currency:string){return new Intl.NumberFormat("en-IN",{style:"currency",currency:currency||"INR",maximumFractionDigits:2}).format(Math.max(0,value)/100);}
function paymentLabel(payment:ShopPayment|null,stage:OrderJourneyStage,locale:string){
  if(!payment)return tx(locale,stage==="payment"?"Checkout not started":"Not due yet");
  switch(payment.status){
    case "AWAITING_CUSTOMER_PAYMENT":return tx(locale,"Waiting for customer payment");
    case "CUSTOMER_REPORTED":return tx(locale,"Customer says paid · confirm receipt");
    case "CASH_AWAITING_PAYMENT":return tx(locale,"Cash due at counter");
    case "VERIFIED":return tx(locale,"Payment received");
    case "PARTIALLY_REFUNDED":return tx(locale,"Partly refunded");
    case "REJECTED":return tx(locale,"Payment not confirmed · customer may retry");
    case "REFUNDED":return tx(locale,"Payment returned");
    default:return tx(locale,"Payment update");
  }
}
function printerSummary(jobs:ShopPrintJob[],printers:PrinterDevice[],agents:AgentDevice[],locale:string){
  const active=jobs.filter(job=>!["REJECTED","REFUNDED","CANCELLED"].includes(job.status));
  const ids=new Set(active.map(job=>job.printerDeviceId).filter((id):id is string=>!!id));
  if(!ids.size)return {name:tx(locale,"Printer not assigned"),detail:tx(locale,"Assign one before dispatch"),ready:false};
  if(ids.size>1)return {name:tx(locale,"Multiple printers assigned"),detail:tx(locale,"Use one printer for this batch before dispatch"),ready:false};
  const assigned=printers.filter(printer=>ids.has(printer.id));
  const connected=assigned.filter(printer=>isPrinterAvailableForShop(printer,agents));
  const names=Array.from(new Set(assigned.map(printer=>printer.displayName).filter(Boolean)));
  if(connected.length)
    return {name:names.join(", ")||tx(locale,"Assigned printer"),detail:tx(locale,"Windows queue ready · shop computer online"),ready:true};
  const unavailable=assigned[0];
  const reason=unavailable?getPrinterAvailabilityReason(unavailable,agents):tx(locale,"Assigned printer is no longer registered");
  return {name:names.join(", ")||"Assigned printer",detail:reason??tx(locale,"Printer availability is not confirmed"),ready:false};
}
function FileActions({job,printers,agents,locale,onPreview,onApprove,onReject,onRefund,onAssignPrinter,onDispatch,onConfirmOutput,onRetryPdfExport,onHistory,onOpenPhotoStudio}:{
  job:ShopPrintJob;printers:PrinterDevice[];agents:AgentDevice[];locale:string;
  onPreview:(job:ShopPrintJob)=>void;onApprove:(job:ShopPrintJob)=>void;onReject:(job:ShopPrintJob)=>void;
  onRefund:(job:ShopPrintJob)=>void;onAssignPrinter:(job:ShopPrintJob,printerId:string)=>void;
  onDispatch:(job:ShopPrintJob)=>void;onConfirmOutput:(job:ShopPrintJob)=>void;
  onRetryPdfExport:(job:ShopPrintJob)=>void;onHistory:(job:ShopPrintJob)=>void;onOpenPhotoStudio:(job:ShopPrintJob)=>void;
}){
  const t=(value:string)=>tx(locale,value);
  const canEditPhoto=job.photoSourceAvailable===true&&!job.privatePrinting&&job.status==="AWAITING_SHOP_APPROVAL";
  const canRefund=(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&["VERIFIED","PARTIALLY_REFUNDED"].includes(job.payment?.status??"")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId;
  const expiredAgentLease = job.status==="LOCAL_QUEUE_CLAIMED" &&
    (!job.agentLeaseExpiresUtc || Date.parse(job.agentLeaseExpiresUtc)<=Date.now());
  const awaitingAgentDispatchCanBeReassigned =
    job.status==="PRINT_AUTHORISED" &&
    job.spoolerStatus==="AWAITING_AGENT" &&
    job.spoolerJobId==null &&
    !job.printDispatchLeaseExpiresUtc &&
    !job.spoolerSubmittedUtc &&
    !job.spoolerObservedUtc &&
    !job.outputConfirmedUtc;
  const assignable=["PAID_AUTHORISED","LOCAL_QUEUE_READY"].includes(job.status) ||
    expiredAgentLease ||
    awaitingAgentDispatchCanBeReassigned;
  const currentPrinter=printers.find(printer=>printer.id===job.printerDeviceId);
  const localDocumentNeedsOriginalAgent=job.storageMode==="LOCAL_AGENT";
  const localDocumentAgentId=job.documentAgentDeviceId??job.claimedByAgentDeviceId??null;
  const assignablePrinters=printers.filter(printer=>
    isPrinterAvailableForShop(printer,agents)&&
    (printer.outputMode!=="PDF_EXPORT"||!job.privatePrinting)&&
    (!localDocumentNeedsOriginalAgent||
      (!!localDocumentAgentId&&printer.agentDeviceId===localDocumentAgentId))
  );
  const confirmable=job.outputType!=="TEST_VIRTUAL"&&isPhysicalOutputConfirmationStatus(job.status)&&(job.status!=="OUTPUT_UNCONFIRMED"||job.outputMode!=="PDF_EXPORT");
  const spoolerRetained=isRetainedSpoolerJob(job.spoolerStatus);
  const manualQueueReconciliationAvailable=canManuallyReconcileUncertainOutput(
    job.status,job.spoolerStatus,job.failureCode,job.spoolerJobId,job.queueName,job.spoolerJobName
  );
  const canConfirmOutput=canConfirmPhysicalOutput(
    job.status,job.spoolerStatus,job.spoolerSubmittedUtc,job.spoolerObservedUtc,
    job.printerStatus,job.failureCode,job.spoolerJobId,Date.now(),
    job.spoolerPagesPrinted??0,job.spoolerTotalPages??0
  )||manualQueueReconciliationAvailable;
  const incompletePageProgress=hasIncompleteSpoolerPageProgress(job.spoolerPagesPrinted,job.spoolerTotalPages);
  const canFinishOutput=canConfirmOutput&&!incompletePageProgress;
  const confirmationBlockedMessage=!canFinishOutput
    ?incompletePageProgress
      ?`Windows recorded ${job.spoolerPagesPrinted??0} of ${job.spoolerTotalPages} pages. Do not mark this file complete; finish and inspect the remaining pages separately.`
      :spoolerRetained?"Windows still holds this job. Confirmation remains blocked until the Agent verifies that it has cleared."
      :!hasVerifiedAgentSpoolerObservation(job.spoolerStatus,job.spoolerSubmittedUtc,job.spoolerObservedUtc,job.failureCode,job.spoolerJobId)?"Waiting for a fresh Agent observation from after submission before confirmation."
      :["QUEUED","SPOOLING","PRINTING"].includes((job.spoolerStatus??"").toUpperCase())?"Windows still reports this job queued or printing. Wait for a terminal state."
      :"Confirm output only after Windows reports printed output or verified queue absence."
    :undefined;
  return <div className={styles.fileActionPanel}>
    {!job.privatePrinting&&<button type="button" className={styles.miniAction} onClick={()=>onPreview(job)}><Eye size={12}/> Preview</button>}
    <button type="button" className={styles.miniAction} onClick={()=>onHistory(job)}><History size={12}/> History</button>
    {canEditPhoto&&<button type="button" className={styles.miniAction} onClick={()=>onOpenPhotoStudio(job)}><ImageIcon size={12}/> Edit photo</button>}
    {job.status==="AWAITING_SHOP_APPROVAL"&&!job.batchId&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onApprove(job)}><Check size={12}/> Accept</button>}
    {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&!job.batchId&&<button type="button" className={styles.miniAction+" "+styles.miniReject} onClick={()=>onReject(job)}><X size={12}/> Reject</button>}
    {assignable&&<label className={styles.printerAssignment}>{t("Printer")}
      <select aria-label={t("Assign printer for ") + job.jobNumber} value={job.printerDeviceId??""} onChange={event=>onAssignPrinter(job,event.currentTarget.value)}>
        <option value="">{t("Choose printer")}</option>
        {currentPrinter&&!assignablePrinters.some(printer=>printer.id===currentPrinter.id)&&<option value={currentPrinter.id} disabled>{currentPrinter.displayName} · currently unavailable</option>}
        {assignablePrinters.map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save as PDF · ":""}{printer.displayName}</option>)}
      </select>
    </label>}
    {expiredAgentLease&&<div className={styles.attentionLine} role="status">
      <AlertTriangle size={13}/>
      <span>{t("The previous computer's Agent lease expired before it finished receiving this file. Choose another ready printer to release the paid job; the file remains saved and does not need to be uploaded again.")}</span>
    </div>}
    {awaitingAgentDispatchCanBeReassigned&&<div className={styles.attentionLine} role="status">
      <AlertTriangle size={13}/>
      <span>{t("This job has not been submitted to Windows yet. Choose another ready printer to safely reroute it; no pages have been sent to the old queue.")}</span>
    </div>}
    {assignable&&assignablePrinters.length===0&&<div className={styles.attentionLine} role="status"><AlertTriangle size={13}/><span>{localDocumentNeedsOriginalAgent
      ?localDocumentAgentId
        ?"This file is stored only on its original Windows computer. Reconnect that computer or ready another printer on the same PC; printers on other computers cannot receive this local-only file."
        :"This file is stored locally, but its original Windows computer is not identified. Reconnect the computer that received it before changing its printer."
      :"No selected printer is ready right now. This file stays in its order; reconnect a Windows computer or select another ready queue in Printers. You do not need to upload the file again."}</span></div>}
    {job.status==="LOCAL_QUEUE_READY"&&job.printerDeviceId&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onDispatch(job)}><Printer size={12}/>{job.outputMode==="PDF_EXPORT"?"Save PDF":job.outputMode==="UNSUPPORTED_VIRTUAL"?"Run virtual test":"Start printing"}</button>}
    {confirmable&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} disabled={!canFinishOutput} title={manualQueueReconciliationAvailable?"The Agent restarted before Windows returned a spooler ID. Check the exact job in the selected Windows queue and inspect the paper output before confirming.":confirmationBlockedMessage} onClick={()=>onConfirmOutput(job)}><Check size={12}/> {manualQueueReconciliationAvailable?"Reconcile & confirm":"Confirm output"}</button>}
    {confirmable&&incompletePageProgress&&<div className={styles.attentionLine+" "+styles.confirmationBlocked} role="alert"><AlertTriangle size={13}/><span>{confirmationBlockedMessage}</span></div>}
    {confirmable&&job.failureCode==="SPOOLER_JOB_NO_LONGER_VISIBLE"&&(job.spoolerTotalPages??0)<=0&&<div className={styles.attentionLine+" "+styles.confirmationBlocked} role="status"><AlertTriangle size={13}/><span>{t("Windows no longer lists this job, but the page count is unknown. Automatic retry is blocked; inspect the full paper output before resolving.")}</span></div>}
    {confirmable&&manualQueueReconciliationAvailable&&<div className={styles.attentionLine} role="status"><AlertTriangle size={13}/><span>The Agent could not identify this Windows job. Manually check the {job.queueName} queue for {job.spoolerJobName}, then inspect every printed page before reconciling.</span></div>}
    {confirmable&&spoolerRetained&&<div className={styles.attentionLine+" "+styles.confirmationBlocked} role="status"><AlertTriangle size={13}/><span>{t("Windows is still holding this print job. SmartPrint will enable confirmation after the Agent verifies that the held entry has cleared.")}</span></div>}
    {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onRetryPdfExport(job)}><RotateCcw size={12}/> Save PDF again</button>}
    {canRefund&&<button type="button" className={styles.miniAction+" "+styles.miniReject} onClick={()=>onRefund(job)}><Wallet size={12}/> Refund</button>}
  </div>;
}
function OrderCard({journey,printers,agents,locale,onReviewBatch,onVerifyPayment,...actions}:{
  journey:ReturnType<typeof buildShopOrderJourneys>[number];printers:PrinterDevice[];agents:AgentDevice[];locale:string;
  onReviewBatch:(batchId:string)=>void;onVerifyPayment:(payment:ShopPayment)=>void;
  onPreview:(job:ShopPrintJob)=>void;onApprove:(job:ShopPrintJob)=>void;onReject:(job:ShopPrintJob)=>void;
  onRefund:(job:ShopPrintJob)=>void;onAssignPrinter:(job:ShopPrintJob,printerId:string)=>void;
  onDispatch:(job:ShopPrintJob)=>void;onConfirmOutput:(job:ShopPrintJob)=>void;
  onRetryPdfExport:(job:ShopPrintJob)=>void;onHistory:(job:ShopPrintJob)=>void;onOpenPhotoStudio:(job:ShopPrintJob)=>void;
  onResumeUnprintedBatch:(batchId:string,jobIds:string[])=>Promise<boolean>;
}){
  const t=(value:string)=>tx(locale,value);
  const printer=printerSummary(journey.jobs,printers,agents,locale);
  const canReview=journey.canReview&&!!journey.batchId;
  const singleFileReviewJob=journey.jobs.length===1&&!journey.batchId?journey.jobs[0]:null;
  const allOutputsReady=journey.jobs.length>0&&journey.jobs.every(job=>readyStatuses.has(job.status));
  const allVirtualTestsReady=journey.jobs.length>0&&journey.jobs.every(job=>job.status==="TEST_OUTPUT_READY"&&job.outputType==="TEST_VIRTUAL"&&job.outputMode==="UNSUPPORTED_VIRTUAL");
  const headline=t(journey.isClosed?"No files moving forward":journey.attentionJobs.length?"Needs a quick check":allVirtualTestsReady?"Virtual test completed · not for collection":journey.stage==="received"?"New order received":journey.stage==="review"?"Your review is next":journey.stage==="payment"?"Waiting for payment":journey.stage==="printing"?"Order is being prepared":allOutputsReady?"Ready for collection":"Print output saved");
  const stageIndex=journey.isClosed?1:journey.stageIndex;
  const [filesExpanded,setFilesExpanded]=useState(false);
  const [selectedResumeJobIds,setSelectedResumeJobIds]=useState<string[]>([]);
  const [resumeBusy,setResumeBusy]=useState(false);
  const unprintedResumeCandidates=journey.batchId?journey.jobs.filter(job=>{
    const originalPrinter=printers.find(printer=>printer.id===job.printerDeviceId);
    const originalQueueReady=!!originalPrinter&&
      originalPrinter.agentDeviceId===job.claimedByAgentDeviceId&&
      ((originalPrinter.outputMode==="PHYSICAL"&&job.outputType==="PHYSICAL")||
       (originalPrinter.outputMode==="UNSUPPORTED_VIRTUAL"&&job.outputType==="TEST_VIRTUAL"&&originalPrinter.virtualTestGrantActive===true))&&
      isPrinterAvailableForShop(originalPrinter,agents);
    const verifiedPayment=job.payment?.status==="VERIFIED"||job.payment?.status==="PARTIALLY_REFUNDED";
    return verifiedPayment&&canResumeUnprintedPhysicalJob(
      job.status,job.outputMode,job.outputType,job.spoolerStatus,job.failureCode,
      job.spoolerJobId,job.spoolerPagesPrinted??0,job.spoolerTotalPages??0,
      job.spoolerSubmittedUtc,job.spoolerObservedUtc,job.outputConfirmedUtc,
      job.printDispatchLeaseExpiresUtc,job.localQueueReceivedUtc,originalQueueReady
    );
  }):[];
  const effectiveSelectedResumeJobIds=selectedResumeJobIds.filter(id=>
    unprintedResumeCandidates.some(job=>job.id===id)
  );
  const virtualTestResume=unprintedResumeCandidates.some(job=>job.outputType==="TEST_VIRTUAL"&&job.outputMode==="UNSUPPORTED_VIRTUAL");
  async function resumeSelectedUnprintedFiles(){
    if(!journey.batchId||effectiveSelectedResumeJobIds.length===0||resumeBusy)return;
    setResumeBusy(true);
    try{
      const succeeded=await actions.onResumeUnprintedBatch(journey.batchId,effectiveSelectedResumeJobIds);
      if(succeeded)setSelectedResumeJobIds([]);
    }finally{setResumeBusy(false);}
  }
  return <article className={styles.orderCard}>
    <div className={styles.orderCardTop}><div className={styles.orderIdentity}>
      <div className={styles.orderEyebrow}><span>{t("ORDER")}</span><span className={styles.orderReference}>{journey.reference}</span>{journey.batchId&&<span className={styles.batchBadge}>Batch · {journey.batchId.slice(0,8).toUpperCase()}</span>}{journey.privatePrinting&&<span className={styles.privateModeBadge}>{t("Private · content hidden")}</span>}</div>
      <h3>{journey.name}</h3><p><Clock3 size={13}/> {t("Received")} {formatIstDateTime(journey.createdUtc)} · {journey.jobs.length} file(s) · {journey.acceptedCount} included · {journey.rejectedCount} excluded</p>
    </div><div className={styles.currentStatus+" "+styles["tone"+journey.stage[0].toUpperCase()+journey.stage.slice(1)]}><span/><strong>{headline}</strong></div></div>
    <div className={styles.miniProgress} aria-label={t("Order progress: ")+t(orderJourneyStageLabel(journey.stage))}>
      {flowSteps.map((step,index)=><span key={step.id} className={styles.progressSegment+(index<=stageIndex?" "+styles.progressComplete:"")+(index===stageIndex?" "+styles.progressCurrent:"")}/>)}
    </div>
    <div className={styles.orderFacts}>
      <div><strong>{journey.blackWhitePageCount}</strong><span>{t("B&W pages")}</span></div>
      <div><strong>{journey.colourPageCount}</strong><span>{t("Colour pages")}</span></div>
      <div><strong>{journey.pageCount}</strong><span>{t("Total output pages")}</span></div>
      <div><strong>{money(journey.payment?.amountMinor??journey.quotedAmountMinor,journey.currency)}</strong><span>{t(journey.payment?"payment total":"quoted value")}</span></div>
      {journey.photoCount>0&&<div><strong>{journey.photoCount}</strong><span>{t("Photos")}</span></div>}
      {journey.photoPageCount>0&&<div><strong>{journey.photoPageCount}</strong><span>{t("Photo layout pages")}</span></div>}
    </div>
    <div className={styles.orderSignals}>
      <div className={styles.signalRow}><span className={styles.signalIcon+(journey.payment?.status==="VERIFIED"||journey.payment?.status==="PARTIALLY_REFUNDED"?" "+styles.signalGood:"")}><CreditCard size={15}/></span><div><strong>{t("Payment")}</strong><small>{paymentLabel(journey.payment,journey.stage,locale)} · {money(journey.payment?.amountMinor??journey.quotedAmountMinor,journey.currency)}</small></div></div>
      <div className={styles.signalRow}><span className={styles.signalIcon+(printer.ready?" "+styles.signalGood:"")}><Printer size={15}/></span><div><strong>{printer.name}</strong><small>{printer.detail}</small></div></div>
      {journey.attentionJobs.length>0&&<div className={styles.attentionLine}><AlertTriangle size={14}/><span>{journey.attentionJobs.length} {t("file(s) need attention. Check the original output destination before retrying.")}</span></div>}
    </div>
    <div className={styles.cardWorkflowActions}>
      {canReview&&<button type="button" className={styles.actionButton} onClick={()=>onReviewBatch(journey.batchId!)}><FileText size={14}/> {t("Review all files")}</button>}
      {singleFileReviewJob?.status==="AWAITING_SHOP_APPROVAL"&&<>
        <button type="button" className={styles.actionButton} onClick={()=>actions.onApprove(singleFileReviewJob)}><Check size={14}/> {t("Accept file")}</button>
        <button type="button" className={styles.actionButton} onClick={()=>actions.onReject(singleFileReviewJob)}><X size={14}/> {t("Reject file")}</button>
      </>}
      {singleFileReviewJob?.status==="AWAITING_PAYMENT"&&<button type="button" className={styles.actionButton} onClick={()=>actions.onReject(singleFileReviewJob)}><X size={14}/> {t("Reject file")}</button>}
      {journey.canVerifyPayment&&journey.payment&&<button type="button" className={styles.actionButton} onClick={()=>onVerifyPayment(journey.payment!)}><ShieldCheck size={14}/> {t("Verify payment")}</button>}
      {journey.isClosed&&<span className={styles.closedHint}>All files have a recorded final decision.</span>}
    </div>
    <div className={styles.orderFileList}>
      <button type="button" className={styles.fileDetailsToggle} aria-expanded={filesExpanded} aria-controls={"order-files-"+journey.key} onClick={()=>setFilesExpanded(value=>!value)}>
        <span><FileText size={13}/> File details &amp; actions</span>
        <span>{journey.jobs.length} {t(journey.jobs.length===1?"file":"files")} <ChevronDown size={14} className={filesExpanded?styles.chevronOpen:""}/></span>
      </button>
      {filesExpanded&&<div className={styles.orderFileDetails} id={"order-files-"+journey.key}>
        {journey.batchId&&unprintedResumeCandidates.length>0&&<section className={styles.resumePanel} aria-label={t("Resume files verified as unprinted")}>
          <div className={styles.resumePanelHeading}>
            <strong>{t(virtualTestResume?"Retry virtual tests with zero output":"Resume files with zero pages printed")}</strong>
            <span>Only files with a fresh Windows queue-absence check, a known page total and a secured copy on the original computer appear here.</span>
          </div>
          <div className={styles.resumeCandidateList}>
            {unprintedResumeCandidates.map(job=>{
              const originalIndex=journey.jobs.findIndex(item=>item.id===job.id);
              const privateIndex=journey.jobs.slice(0,originalIndex+1).filter(item=>item.privatePrinting).length;
              const fileLabel=job.privatePrinting?"Private file "+privateIndex:job.document.originalFileName;
              const checked=effectiveSelectedResumeJobIds.includes(job.id);
              return <label className={styles.resumeCandidate} key={job.id}>
                <input type="checkbox" checked={checked} disabled={resumeBusy} onChange={event=>{
                  const isChecked=event.currentTarget.checked;
                  setSelectedResumeJobIds(current=>
                    isChecked?[...current.filter(id=>id!==job.id),job.id]:current.filter(id=>id!==job.id)
                  );
                }}/>
                <span><strong>{fileLabel}</strong><small>0 of {job.spoolerTotalPages} pages recorded</small></span>
              </label>;
            })}
          </div>
          <p>{virtualTestResume
            ?"Before continuing, verify that each exact job is absent from the original virtual Windows queue and that no test output artifact was created. Files with any recorded pages or unknown page counts cannot be retried."
            :"Before continuing, check the original Windows queue and paper tray for each selected file. Files with any printed pages or unknown page counts cannot be retried here."}</p>
          <button type="button" className={styles.resumeButton} disabled={resumeBusy||effectiveSelectedResumeJobIds.length===0} onClick={()=>void resumeSelectedUnprintedFiles()}>
            {resumeBusy?"Authorising selected files":virtualTestResume?`Retry ${effectiveSelectedResumeJobIds.length} virtual test(s)`:`Resume ${effectiveSelectedResumeJobIds.length} selected file(s)`}
          </button>
        </section>}
        {journey.jobs.map((job,index)=><div className={styles.orderFileItem} key={job.id}>
          <div className={styles.orderFileMeta}>
            <strong title={job.privatePrinting?undefined:job.document.originalFileName}>
              {job.privatePrinting?"Private file "+journey.jobs.slice(0,index+1).filter(item=>item.privatePrinting).length:job.document.originalFileName}
            </strong>
            <span>{job.pageCount} page(s) · {job.copies} {job.copies===1?"copy":"copies"} · {t(orderJourneyStatusLabel(job.status))}</span>
          </div>
          <FileActions job={job} printers={printers} agents={agents} locale={locale} {...actions}/>
        </div>)}
      </div>}
    </div>
  </article>;
}
export default function OrderJourneyPage({jobs,payments,printers,agents,refreshing,onRefresh,onNavigate,onReviewBatch,onVerifyPayment,...actions}:Props){
  const { locale } = useLocale();
  const t=(value:string)=>tx(locale,value);
  const [stageFilter,setStageFilter]=useState<OrderJourneyStage|"all">("all");
  const [query,setQuery]=useState("");
  const journeys=useMemo(()=>buildShopOrderJourneys(jobs,payments),[jobs,payments]);
  const filteredJourneys=useMemo(()=>filterShopOrderJourneys(journeys,stageFilter,query),[journeys,stageFilter,query]);
  const counts=useMemo(()=>({
    received:journeys.filter(j=>j.stage==="received"&&!j.isClosed).length,
    review:journeys.filter(j=>j.stage==="review"&&!j.isClosed).length,
    payment:journeys.filter(j=>j.stage==="payment"&&!j.isClosed).length,
    printing:journeys.filter(j=>j.stage==="printing"&&!j.isClosed).length,
    ready:journeys.filter(j=>j.stage==="ready").length
  }),[journeys]);
  const agentsOnline=agents.filter(agent=>isAgentOnlineForShop(agent)).length;
  const reachable=printers.filter(printer=>isPrinterAvailableForShop(printer,agents)).length;
  const inMotion=journeys.filter(isActiveOrderWork).length;
  const readyForCollection=journeys.filter(j=>j.stage==="ready").length;
  const paymentsToVerify=journeys.filter(j=>j.canVerifyPayment).length;

  return <div className={styles.journeyPage}>
    <header className={styles.pageHero}>
      <div><div className={styles.eyebrow}><span className={styles.livePulse}/> LIVE ORDER CONTROL</div><p>{t("Workload, payments and print status at a glance.")}</p></div>
      <div className={styles.heroActions}><span className={styles.liveLabel}><span/> {t("Live shop view")}</span><button type="button" className={styles.refreshButton} onClick={onRefresh} disabled={refreshing}><RefreshCw size={15} className={refreshing?styles.spinning:""}/>{refreshing?t("Refreshing"):t("Refresh")}</button></div>
    </header>

    <section className={styles.stageFilters} aria-label={t("Filter orders by stage")}>
      <button type="button" aria-pressed={stageFilter==="all"} className={styles.stageFilterButton+(stageFilter==="all"?" "+styles.stageFilterSelected:"")} onClick={()=>setStageFilter("all")}>
        <span>{t("All stages")}</span><strong>{journeys.length}</strong>
      </button>
      {flowSteps.map(step=>{const Icon=step.icon,count=counts[step.id],selected=stageFilter===step.id;return <button type="button" key={step.id} aria-pressed={selected} className={styles.stageFilterButton+(selected?" "+styles.stageFilterSelected:"")} onClick={()=>setStageFilter(current=>current===step.id?"all":step.id)}>
        <Icon size={14}/><span>{t(step.title)}</span><strong>{count}</strong>
      </button>;})}
    </section>

    <section className={styles.operationsStrip} aria-label={t("Shop operations summary")}>
      <div className={styles.operationsLabel}><span className={styles.operationsGlyph}><Wifi size={15}/></span><div><strong>{inMotion} active orders</strong><small>{readyForCollection} ready for collection · {paymentsToVerify} payments to verify</small></div></div>
      <div className={styles.operationMetric}><strong>{agentsOnline}</strong><span>{t("Agents online")}</span></div>
      <div className={styles.operationMetric}><strong>{reachable}</strong><span>{t("Printers reachable")}</span></div>
      <button type="button" className={styles.subtleLink} onClick={()=>onNavigate("Print Agent")}>{t("Shop computer")}</button>
    </section>

    <section className={styles.ordersSection}>
      <div className={styles.ordersHeading}><div><span className={styles.panelEyebrow}>{t("YOUR CUSTOMERS")}</span><h3>{t("Order journeys")}</h3><p>{t("Search by batch ID, order ID, file name or folder name.")}</p></div><span className={styles.ordersCount}>{filteredJourneys.length} of {journeys.length} orders</span></div>
      <div className={styles.orderFilters}>
        <label className={styles.orderSearch}><span>{t("Find an order")}</span><input value={query} onChange={event=>setQuery(event.currentTarget.value)} placeholder={t("Batch ID, order ID, file or folder name")} aria-label={t("Search order journey")}/></label>
        <button type="button" className={styles.clearFilters} disabled={stageFilter==="all"&&!query} onClick={()=>{setStageFilter("all");setQuery("");}}>{t("Clear filters")}</button>
      </div>
      {journeys.length===0?<div className={styles.emptyState}><h3>{t("Your first customer order will appear here")}</h3><p>{t("Share a QR station with your customers. Their orders will appear here as one easy-to-follow journey.")}</p></div>
      :filteredJourneys.length===0?<div className={styles.emptyState}><h3>{t("No matching orders")}</h3><p>{t("Try another batch, order, file or folder name, or clear the current filters.")}</p><button type="button" className={styles.actionButton} onClick={()=>{setStageFilter("all");setQuery("");}}>Clear filters</button></div>
      :<div className={styles.orderList}>{filteredJourneys.map(j=><OrderCard key={j.key} journey={j} printers={printers} agents={agents} locale={locale} onReviewBatch={onReviewBatch} onVerifyPayment={onVerifyPayment} {...actions}/>)}</div>}
    </section>
    <footer className={styles.journeyFooter}><span>{t("Output accuracy matters.")}</span> A printer accepting a job does not prove paper was produced. Physical orders become complete only after the shop confirms the actual pages.</footer>
  </div>;
}
