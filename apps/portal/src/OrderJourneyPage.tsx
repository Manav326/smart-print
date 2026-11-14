import { useMemo, useState } from "react";
import { Activity, AlertTriangle, ArrowRight, Check, Clock3, CreditCard, Eye, FileText, History, Image as ImageIcon, Inbox, PackageCheck, Printer, RefreshCw, RotateCcw, ShieldCheck, Wallet, Wifi, X } from "lucide-react";
import type { AgentDevice, PrinterDevice, ShopPayment, ShopPrintJob } from "./api";
import { formatIstDateTime } from "./date-time";
import { buildShopOrderJourneys, filterShopOrderJourneys, orderJourneyStageLabel, orderJourneyStatusLabel, type OrderJourneyStage } from "./order-journey";
import styles from "./OrderJourney.module.css";

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
}
const readyStatuses = new Set(["COMPLETED","PDF_EXPORTED"]);
const flowSteps: Array<{id:OrderJourneyStage;title:string;detail:string;icon:typeof Inbox}> = [
  {id:"received",title:"Received",detail:"Files arrive safely",icon:Inbox},
  {id:"review",title:"Review",detail:"Accept or exclude files",icon:FileText},
  {id:"payment",title:"Payment",detail:"Confirm payment received",icon:Wallet},
  {id:"printing",title:"Printing",detail:"Shop computer prints",icon:Printer},
  {id:"ready",title:"Ready",detail:"Output checked and complete",icon:PackageCheck}
];
function money(value:number,currency:string){return new Intl.NumberFormat("en-IN",{style:"currency",currency:currency||"INR",maximumFractionDigits:2}).format(Math.max(0,value)/100);}
function paymentLabel(payment:ShopPayment|null,stage:OrderJourneyStage){
  if(!payment)return stage==="payment"?"Checkout not started":"Not due yet";
  switch(payment.status){
    case "AWAITING_CUSTOMER_PAYMENT":return "Waiting for customer payment";
    case "CUSTOMER_REPORTED":return "Customer says paid · confirm receipt";
    case "CASH_AWAITING_PAYMENT":return "Cash due at counter";
    case "VERIFIED":return "Payment received";
    case "PARTIALLY_REFUNDED":return "Partly refunded";
    case "REJECTED":return "Payment not confirmed · customer may retry";
    case "REFUNDED":return "Payment returned";
    default:return "Payment update";
  }
}
function isAgentOnline(agent:AgentDevice){return !agent.revokedUtc&&agent.status==="Online"&&!!agent.lastSeenUtc&&Date.parse(agent.lastSeenUtc)>Date.now()-120000;}
function printerSummary(jobs:ShopPrintJob[],printers:PrinterDevice[],agents:AgentDevice[]){
  const active=jobs.filter(job=>!["REJECTED","REFUNDED","CANCELLED"].includes(job.status));
  const ids=new Set(active.map(job=>job.printerDeviceId).filter((id):id is string=>!!id));
  if(!ids.size)return {name:"Printer not assigned",detail:"Assign one before dispatch",ready:false};
  if(ids.size>1)return {name:"Multiple printers assigned",detail:"Use one printer for this batch before dispatch",ready:false};
  const assigned=printers.filter(printer=>ids.has(printer.id));
  const online=new Set(agents.filter(isAgentOnline).map(agent=>agent.id));
  const connected=assigned.filter(printer=>printer.isActive&&!!printer.agentDeviceId&&online.has(printer.agentDeviceId));
  const names=Array.from(new Set(assigned.map(printer=>printer.displayName).filter(Boolean)));
  return connected.length
    ? {name:names.join(", ")||"Assigned printer",detail:"Connected · shop computer online",ready:true}
    : {name:names.join(", ")||"Assigned printer",detail:"Assigned · waiting for shop computer",ready:false};
}
function PrinterVisual({stage,attention}:{stage:OrderJourneyStage;attention:boolean}){
  const printing=stage==="printing"&&!attention,ready=stage==="ready";
  return <div role="img" className={styles.machineVisual+(printing?" "+styles.machinePrinting:"")+(ready?" "+styles.machineReady:"")} aria-label={attention?"Printing needs attention":ready?"Print output complete":printing?"Print job in progress":"Print workflow illustration"}>
    <div className={styles.paper}><span/><span/><span/><span/><i>{ready?<Check size={13}/>:<FileText size={13}/>}</i></div>
    <div className={styles.printerTop}><div className={styles.printerTopLine}/><div className={styles.printerStatusDot}/></div>
    <div className={styles.printerBody}><div className={styles.printerDisplay}><span/>{attention?"!":ready?"OK":printing?"RUN":"READY"}</div><div className={styles.printerSlot}/><div className={styles.printerTray}><span/><span/><span/></div></div><div className={styles.printerBase}/>
  </div>;
}
function FileActions({job,printers,onPreview,onApprove,onReject,onRefund,onAssignPrinter,onDispatch,onConfirmOutput,onRetryPdfExport,onHistory,onOpenPhotoStudio}:{
  job:ShopPrintJob;printers:PrinterDevice[];
  onPreview:(job:ShopPrintJob)=>void;onApprove:(job:ShopPrintJob)=>void;onReject:(job:ShopPrintJob)=>void;
  onRefund:(job:ShopPrintJob)=>void;onAssignPrinter:(job:ShopPrintJob,printerId:string)=>void;
  onDispatch:(job:ShopPrintJob)=>void;onConfirmOutput:(job:ShopPrintJob)=>void;
  onRetryPdfExport:(job:ShopPrintJob)=>void;onHistory:(job:ShopPrintJob)=>void;onOpenPhotoStudio:(job:ShopPrintJob)=>void;
}){
  const canEditPhoto=job.photoSourceAvailable===true&&!job.privatePrinting&&job.status==="AWAITING_SHOP_APPROVAL";
  const canRefund=(job.status==="PAID_AUTHORISED"||job.status==="REJECTED")&&["VERIFIED","PARTIALLY_REFUNDED"].includes(job.payment?.status??"")&&!job.localQueueReceivedUtc&&!job.claimedByAgentDeviceId;
  const assignable=["PAID_AUTHORISED","LOCAL_QUEUE_READY"].includes(job.status);
  const confirmable=job.status==="SPOOLER_ACCEPTED"||job.status==="PRINTING"||(job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode!=="PDF_EXPORT");
  return <div className={styles.fileActionPanel}>
    <button type="button" className={styles.miniAction} onClick={()=>onPreview(job)}><Eye size={12}/> Preview</button>
    <button type="button" className={styles.miniAction} onClick={()=>onHistory(job)}><History size={12}/> History</button>
    {canEditPhoto&&<button type="button" className={styles.miniAction} onClick={()=>onOpenPhotoStudio(job)}><ImageIcon size={12}/> Edit photo</button>}
    {job.status==="AWAITING_SHOP_APPROVAL"&&!job.batchId&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onApprove(job)}><Check size={12}/> Accept</button>}
    {["AWAITING_SHOP_APPROVAL","AWAITING_PAYMENT"].includes(job.status)&&!job.batchId&&<button type="button" className={styles.miniAction+" "+styles.miniReject} onClick={()=>onReject(job)}><X size={12}/> Reject</button>}
    {assignable&&<label className={styles.printerAssignment}>Printer
      <select aria-label={"Assign printer for "+job.jobNumber} value={job.printerDeviceId??""} onChange={event=>onAssignPrinter(job,event.currentTarget.value)}>
        <option value="">Choose printer</option>
        {printers.filter(printer=>printer.isActive).map(printer=><option key={printer.id} value={printer.id}>{printer.outputMode==="PDF_EXPORT"?"Save as PDF · ":""}{printer.displayName}</option>)}
      </select>
    </label>}
    {job.status==="LOCAL_QUEUE_READY"&&job.printerDeviceId&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onDispatch(job)}><Printer size={12}/>{job.outputMode==="PDF_EXPORT"?"Save PDF":"Start printing"}</button>}
    {confirmable&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onConfirmOutput(job)}><Check size={12}/> Confirm output</button>}
    {job.status==="OUTPUT_UNCONFIRMED"&&job.outputMode==="PDF_EXPORT"&&<button type="button" className={styles.miniAction+" "+styles.miniApprove} onClick={()=>onRetryPdfExport(job)}><RotateCcw size={12}/> Save PDF again</button>}
    {canRefund&&<button type="button" className={styles.miniAction+" "+styles.miniReject} onClick={()=>onRefund(job)}><Wallet size={12}/> Refund</button>}
  </div>;
}
function OrderCard({journey,printers,agents,onReviewBatch,onVerifyPayment,...actions}:{
  journey:ReturnType<typeof buildShopOrderJourneys>[number];printers:PrinterDevice[];agents:AgentDevice[];
  onReviewBatch:(batchId:string)=>void;onVerifyPayment:(payment:ShopPayment)=>void;
  onPreview:(job:ShopPrintJob)=>void;onApprove:(job:ShopPrintJob)=>void;onReject:(job:ShopPrintJob)=>void;
  onRefund:(job:ShopPrintJob)=>void;onAssignPrinter:(job:ShopPrintJob,printerId:string)=>void;
  onDispatch:(job:ShopPrintJob)=>void;onConfirmOutput:(job:ShopPrintJob)=>void;
  onRetryPdfExport:(job:ShopPrintJob)=>void;onHistory:(job:ShopPrintJob)=>void;onOpenPhotoStudio:(job:ShopPrintJob)=>void;
}){
  const printer=printerSummary(journey.jobs,printers,agents);
  const canReview=journey.canReview&&!!journey.batchId;
  const allOutputsReady=journey.jobs.length>0&&journey.jobs.every(job=>readyStatuses.has(job.status));
  const headline=journey.isClosed?"No files moving forward":journey.attentionJobs.length?"Needs a quick check":journey.stage==="received"?"New order received":journey.stage==="review"?"Your review is next":journey.stage==="payment"?"Waiting for payment":journey.stage==="printing"?"Order is being prepared":allOutputsReady?"Ready for collection":"Print output saved";
  const stageIndex=journey.isClosed?1:journey.stageIndex;
  return <article className={styles.orderCard}>
    <div className={styles.orderCardTop}><div className={styles.orderIdentity}>
      <div className={styles.orderEyebrow}><span>ORDER</span><span className={styles.orderReference}>{journey.reference}</span>{journey.batchId&&<span className={styles.batchBadge}>Batch · {journey.batchId.slice(0,8).toUpperCase()}</span>}</div>
      <h3>{journey.name}</h3><p><Clock3 size={13}/> Received {formatIstDateTime(journey.createdUtc)} · {journey.jobs.length} file(s)</p>
    </div><div className={styles.currentStatus+" "+styles["tone"+journey.stage[0].toUpperCase()+journey.stage.slice(1)]}><span/><strong>{headline}</strong></div></div>
    <div className={styles.miniProgress} aria-label={"Order progress: "+orderJourneyStageLabel(journey.stage)}>
      {flowSteps.map((step,index)=><span key={step.id} className={styles.progressSegment+(index<=stageIndex?" "+styles.progressComplete:"")+(index===stageIndex?" "+styles.progressCurrent:"")}/>)}
    </div>
    <div className={styles.orderFacts}>
      <div><strong>{journey.acceptedCount}</strong><span>files included</span></div><div><strong>{journey.rejectedCount}</strong><span>excluded</span></div>
      <div><strong>{journey.pageCount}</strong><span>pages to produce</span></div>
      <div><strong>{money(journey.payment?.amountMinor??journey.quotedAmountMinor,journey.currency)}</strong><span>{journey.payment?"payment total":"quoted value"}</span></div>
    </div>
    <div className={styles.orderSignals}>
      <div className={styles.signalRow}><span className={styles.signalIcon+(journey.payment?.status==="VERIFIED"||journey.payment?.status==="PARTIALLY_REFUNDED"?" "+styles.signalGood:"")}><CreditCard size={15}/></span><div><strong>Payment</strong><small>{paymentLabel(journey.payment,journey.stage)}</small></div></div>
      <div className={styles.signalRow}><span className={styles.signalIcon+(printer.ready?" "+styles.signalGood:"")}><Printer size={15}/></span><div><strong>{printer.name}</strong><small>{printer.detail}</small></div></div>
      {journey.attentionJobs.length>0&&<div className={styles.attentionLine}><AlertTriangle size={14}/><span>{journey.attentionJobs.length} file(s) need attention. Check physical output before retrying.</span></div>}
    </div>
    <div className={styles.cardWorkflowActions}>
      {canReview&&<button type="button" className={styles.actionButton} onClick={()=>onReviewBatch(journey.batchId!)}><FileText size={14}/> Review all files</button>}
      {journey.canVerifyPayment&&journey.payment&&<button type="button" className={styles.actionButton} onClick={()=>onVerifyPayment(journey.payment!)}><ShieldCheck size={14}/> Verify payment</button>}
      {journey.isClosed&&<span className={styles.closedHint}>All files have a recorded final decision.</span>}
    </div>
    <div className={styles.orderFileList}>
      <div className={styles.sectionMicroHeading}>BATCH FILES · ACTIONS AVAILABLE HERE</div>
      {journey.jobs.map(job=><div className={styles.orderFileItem} key={job.id}>
        <div className={styles.orderFileMeta}><strong title={job.document.originalFileName}>{job.document.originalFileName}</strong><span>{job.pageCount} page(s) · {job.copies} {job.copies===1?"copy":"copies"} · {orderJourneyStatusLabel(job.status)}</span></div>
        <FileActions job={job} printers={printers} {...actions}/>
      </div>)}
    </div>
  </article>;
}
export default function OrderJourneyPage({jobs,payments,printers,agents,refreshing,onRefresh,onNavigate,onReviewBatch,onVerifyPayment,...actions}:Props){
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
  const agentsOnline=agents.filter(isAgentOnline).length;
  const reachable=printers.filter(p=>p.isActive&&!!p.agentDeviceId&&agents.some(a=>a.id===p.agentDeviceId&&isAgentOnline(a))).length;
  const inMotion=journeys.filter(j=>!j.isClosed).length;
  const visualJourney=journeys.find(j=>j.attentionJobs.length>0)??journeys.find(j=>j.stage==="printing")??journeys[0];
  return <div className={styles.journeyPage}>
    <header className={styles.pageHero}><div><div className={styles.eyebrow}><span className={styles.livePulse}/> LIVE ORDER CONTROL</div>
      <h2>From customer upload to print handover.</h2><p>Manage orders here. Open the queue only when a file preview is needed.</p></div>
      <div className={styles.heroActions}><span className={styles.liveLabel}><span/> Live shop view</span><button type="button" className={styles.refreshButton} onClick={onRefresh} disabled={refreshing}><RefreshCw size={15} className={refreshing?styles.spinning:""}/>{refreshing?"Refreshing":"Refresh"}</button></div>
    </header>
    <section className={styles.flowPanel} aria-label="The order process">
      <div className={styles.flowPanelHeader}><div><span className={styles.panelEyebrow}>THE PRINT JOURNEY</span><h3>Choose a stage to filter its orders</h3></div><span className={styles.flowPanelHint}>Select the same stage again to clear the filter</span></div>
      <div className={styles.flowSteps}>{flowSteps.map((step,index)=>{const Icon=step.icon,count=counts[step.id],selected=stageFilter===step.id;return <div className={styles.flowStepWrap} key={step.id}>
        <button type="button" aria-pressed={selected} className={styles.flowStepButton+(count>0?" "+styles.flowStepActive:"")+(selected?" "+styles.flowStepSelected:"")} onClick={()=>setStageFilter(current=>current===step.id?"all":step.id)}>
          <span className={styles.flowIcon}><Icon size={19}/></span>
          <span className={styles.flowStepCopy}><strong>{step.title}</strong><small>{count} {count===1?"order":"orders"}</small></span><span className={styles.flowStepDetail}>{step.detail}</span>
        </button>{index<flowSteps.length-1&&<span className={styles.flowConnector} aria-hidden="true"><ArrowRight size={15}/></span>}</div>;})}</div>
      <div className={styles.flowLegend}><div className={styles.legendCopy}><ShieldCheck size={16}/><span><strong>Safe by design</strong> — only accepted files move forward, and printing is never triggered just by receiving payment.</span></div>
        <PrinterVisual stage={visualJourney?.stage??"received"} attention={(visualJourney?.attentionJobs.length??0)>0}/></div>
    </section>
    <section className={styles.summaryStrip} aria-label="Shop order summary">
      <div className={styles.summaryItem}><span className={styles.summaryIcon}><Activity size={17}/></span><div><strong>{inMotion}</strong><small>Orders in motion</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryViolet}><FileText size={17}/></span><div><strong>{counts.review}</strong><small>Need file review</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryAmber}><CreditCard size={17}/></span><div><strong>{journeys.filter(j=>j.canVerifyPayment).length}</strong><small>Payments to verify</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryCyan}><Printer size={17}/></span><div><strong>{counts.printing}</strong><small>Orders printing</small></div></div>
      <div className={styles.summaryItem}><span className={styles.summaryIcon+" "+styles.summaryGreen}><PackageCheck size={17}/></span><div><strong>{counts.ready}</strong><small>Ready / completed</small></div></div>
    </section>
    <section className={styles.operationsStrip} aria-label="Printer and Agent readiness">
      <div className={styles.operationsLabel}><span className={styles.operationsGlyph}><Wifi size={16}/></span><div><strong>Shop floor readiness</strong><small>Live connection to the local Windows computer</small></div></div>
      <div className={styles.operationMetric}><strong>{agentsOnline}</strong><span>Agents online</span></div>
      <div className={styles.operationMetric}><strong>{reachable}</strong><span>Printers reachable</span></div>
      <button type="button" className={styles.subtleLink} onClick={()=>onNavigate("Print Agent")}>Shop computer settings <ArrowRight size={14}/></button>
    </section>
    <section className={styles.ordersSection}><div className={styles.ordersHeading}><div><span className={styles.panelEyebrow}>YOUR CUSTOMERS</span><h3>Order journeys</h3><p>Filter by batch ID, order ID, file name or folder name. These work together with stage filters.</p></div><span className={styles.ordersCount}>{filteredJourneys.length} of {journeys.length} orders</span></div>
      <div className={styles.orderFilters}>
        <label className={styles.orderSearch}><span>Find an order</span><input value={query} onChange={event=>setQuery(event.currentTarget.value)} placeholder="Batch ID, order ID, file or folder name" aria-label="Search order journey"/></label>
        <button type="button" className={styles.clearFilters} disabled={stageFilter==="all"&&!query} onClick={()=>{setStageFilter("all");setQuery("");}}>Clear filters</button>
      </div>
      {journeys.length===0?<div className={styles.emptyState}><div className={styles.emptyIllustration}><div className={styles.emptySheet}><span/><span/><span/></div><div className={styles.emptyPrinter}><Printer size={27}/></div><i><Check size={13}/></i></div>
        <h3>Your first customer order will appear here</h3><p>Share a QR station with your customers. Their uploaded files will appear here as one easy-to-follow journey.</p></div>
      :filteredJourneys.length===0?<div className={styles.emptyState}><h3>No matching orders</h3><p>Try another batch, order, file or folder name, or clear the current filters.</p><button type="button" className={styles.actionButton} onClick={()=>{setStageFilter("all");setQuery("");}}>Clear filters</button></div>
      :<div className={styles.orderList}>{filteredJourneys.map(j=><OrderCard key={j.key} journey={j} printers={printers} agents={agents} onReviewBatch={onReviewBatch} onVerifyPayment={onVerifyPayment} {...actions}/>)}</div>}
    </section>
    <footer className={styles.journeyFooter}><span>Output accuracy matters.</span> A printer accepting a job does not prove paper was produced. Physical orders become complete only after the shop confirms the actual pages.</footer>
  </div>;
}
