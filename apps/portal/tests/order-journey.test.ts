import assert from "node:assert/strict";
import test from "node:test";
import type { ShopPayment, ShopPrintJob } from "../src/api.ts";
import { buildShopOrderJourneys, filterShopOrderJourneys } from "../src/order-journey.ts";
function job(v: Partial<ShopPrintJob> & Pick<ShopPrintJob,"id"|"jobNumber"|"status">): ShopPrintJob {
  return {
    id:v.id, jobNumber:v.jobNumber, status:v.status, batchId:v.batchId??null,
    batchName:v.batchName??null, batchGroupName:v.batchGroupName??null, rejectedPageNumbersJson:v.rejectedPageNumbersJson??"[]",
    pageCount:v.pageCount??2,copies:v.copies??1,colourMode:v.colourMode??"Mono",paperSize:"A4",isDuplex:false,
    printKind:v.printKind??"DOCUMENT",privatePrinting:v.privatePrinting??false,
    photoCount:v.photoCount??0,photosPerPage:v.photosPerPage??null,photoSizeCode:v.photoSizeCode??null,
    currency:v.currency??"INR",quotedAmountMinor:v.quotedAmountMinor??1000,
    createdUtc:v.createdUtc??"2026-11-04T10:00:00Z",
    document:v.document??{originalFileName:v.jobNumber+".pdf",mediaType:"application/pdf",sizeBytes:1024},
    payment:v.payment??null,failureCode:v.failureCode??null
  } as ShopPrintJob;
}
function payment(v: Partial<ShopPayment> & Pick<ShopPayment,"id"|"printJobId"|"status">): ShopPayment {
  return {
    id:v.id,printJobId:v.printJobId,batchPaymentId:v.batchPaymentId??null,batchFileCount:v.batchFileCount??null,
    method:v.method??"UPI",status:v.status,amountMinor:v.amountMinor??2000,currency:v.currency??"INR",
    paymentReference:v.paymentReference??null,createdUtc:v.createdUtc??"2026-11-04T10:05:00Z",
    updatedUtc:v.updatedUtc??"2026-11-04T10:05:00Z"
  } as ShopPayment;
}
test("groups files in one batch and associates its batch payment",()=>{
  const jobs=[job({id:"a",jobNumber:"SP-1",batchId:"batch-1",batchName:"Morning orders",status:"AWAITING_SHOP_APPROVAL",pageCount:3}),
    job({id:"b",jobNumber:"SP-2",batchId:"batch-1",batchName:"Morning orders",status:"AWAITING_SHOP_APPROVAL",pageCount:5})];
  const oldAttempt=payment({id:"older-payment",printJobId:"a",batchPaymentId:"older-payment",status:"REJECTED",createdUtc:"2026-11-04T09:00:00Z"});
  const pay=payment({id:"batch-payment-id",printJobId:"a",batchPaymentId:"batch-payment-id",status:"CUSTOMER_REPORTED",createdUtc:"2026-11-04T11:00:00Z"});
  const orders=buildShopOrderJourneys(jobs,[oldAttempt,pay]);
  assert.equal(orders.length,1);assert.equal(orders[0].name,"Morning orders");
  assert.equal(orders[0].jobs.length,2);assert.equal(orders[0].payment?.id,"batch-payment-id");
  assert.equal(orders[0].stage,"payment");assert.equal(orders[0].pageCount,8);
  assert.equal(orders[0].canVerifyPayment,true);
});
test("keeps unbatched uploads as separate orders",()=>{
  const orders=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"AWAITING_SHOP_APPROVAL"}),job({id:"b",jobNumber:"SP-2",status:"AWAITING_SHOP_APPROVAL"})],[]);
  assert.equal(orders.length,2);assert.notEqual(orders[0].key,orders[1].key);
});
test("maps the lifecycle to received, review, payment, printing and ready",()=>{
  const cases=[["UPLOADED","received"],["AWAITING_SHOP_APPROVAL","review"],["AWAITING_PAYMENT","payment"],
    ["PAYMENT_REJECTED","payment"],["PAID_AUTHORISED","printing"],["LOCAL_QUEUE_CLAIMED","printing"],
    ["LOCAL_QUEUE_READY","printing"],["PRINT_AUTHORISED","printing"],["SPOOLER_ACCEPTED","printing"],
    ["PRINTING","printing"],["COMPLETED","ready"],["PDF_EXPORTED","ready"]];
  for(const [status,expected] of cases) assert.equal(buildShopOrderJourneys([job({id:status,jobNumber:status,status})],[])[0].stage,expected,status);
});
test("UPI and cash checkout waiting states stay in the payment stage when the payment read is temporarily unavailable",()=>{
  for(const status of ["AWAITING_CUSTOMER_PAYMENT","AWAITING_COUNTER_PAYMENT"]) {
    const order=buildShopOrderJourneys([job({id:status,jobNumber:status,status})],[])[0];
    assert.equal(order.stage,"payment",status);
  }
});
test("one rejected file does not hide accepted files still printing",()=>{
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",batchId:"b",status:"REJECTED"}),
    job({id:"b",jobNumber:"SP-2",batchId:"b",status:"PRINTING"})],[])[0];
  assert.equal(order.stage,"printing");assert.equal(order.acceptedCount,1);assert.equal(order.rejectedCount,1);assert.equal(order.isClosed,false);
});
test("output uncertainty and failures are visible and never marked ready",()=>{
  for(const status of ["OUTPUT_UNCONFIRMED","FAILED"]) {
    const order=buildShopOrderJourneys([job({id:status,jobNumber:status,status,failureCode:status==="FAILED"?"PRINTER_OFFLINE":null})],[])[0];
    assert.equal(order.stage,"printing");assert.equal(order.attentionJobs.length,1);assert.notEqual(order.stage,"ready");
  }
});
test("reported payments are eligible for the existing manual verification action",()=>{
  const pay=payment({id:"pay",printJobId:"a",status:"CUSTOMER_REPORTED"});
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"AWAITING_PAYMENT"})],[pay])[0];
  assert.equal(order.canVerifyPayment,true);assert.equal(order.stage,"payment");
});
test("a verified payment advances the journey into printing preparation",()=>{
  const pay=payment({id:"pay",printJobId:"a",status:"VERIFIED"});
  const order=buildShopOrderJourneys([job({id:"a",jobNumber:"SP-1",status:"PAID_AUTHORISED"})],[pay])[0];
  assert.equal(order.stage,"printing");
});

test("stage filters and search cover batch, order, original file and folder names", () => {
  const orders = buildShopOrderJourneys([
    job({id:"a",jobNumber:"SP-100",batchId:"batch-abc",batchName:"Morning packet",batchGroupName:"School forms",status:"AWAITING_SHOP_APPROVAL",
      document:{originalFileName:"admission-form.pdf",mediaType:"application/pdf",sizeBytes:100}}),
    job({id:"b",jobNumber:"SP-200",status:"COMPLETED",document:{originalFileName:"portrait.jpg",mediaType:"image/jpeg",sizeBytes:100}})
  ],[]);
  assert.equal(filterShopOrderJourneys(orders,"review","batch-abc").length,1);
  assert.equal(filterShopOrderJourneys(orders,"review","SP-100").length,1);
  assert.equal(filterShopOrderJourneys(orders,"review","admission-form.pdf").length,1);
  assert.equal(filterShopOrderJourneys(orders,"review","school forms").length,1);
  assert.equal(filterShopOrderJourneys(orders,"review","portrait.jpg").length,0);
  assert.equal(filterShopOrderJourneys(orders,"all","portrait.jpg").length,1);
});

test("excluded totals include rejected, fully refunded and cancelled files", () => {
  const order = buildShopOrderJourneys([
    job({id:"completed",jobNumber:"SP-1",batchId:"mixed",status:"COMPLETED",pageCount:2}),
    job({id:"rejected",jobNumber:"SP-2",batchId:"mixed",status:"REJECTED",pageCount:3}),
    job({id:"refunded",jobNumber:"SP-3",batchId:"mixed",status:"REFUNDED",pageCount:4}),
    job({id:"cancelled",jobNumber:"SP-4",batchId:"mixed",status:"CANCELLED",pageCount:1})
  ], [])[0];
  assert.equal(order.acceptedCount, 1);
  assert.equal(order.rejectedCount, 3);
  assert.equal(order.refundedCount, 1);
});

test("pages to produce subtract unique rejected pages before multiplying by copies", () => {
  const order = buildShopOrderJourneys([
    job({id:"partial",jobNumber:"SP-1",batchId:"refund",status:"PAID_AUTHORISED",pageCount:5,copies:2,rejectedPageNumbersJson:"[2,4,4,99,0]"}),
    job({id:"ready",jobNumber:"SP-2",batchId:"refund",status:"COMPLETED",pageCount:2,copies:1})
  ], [])[0];
  assert.equal(order.pageCount, 8);
});

test("invalid rejected-page metadata does not break shop journey totals", () => {
  const order = buildShopOrderJourneys([
    job({id:"legacy",jobNumber:"SP-1",status:"PRINTING",pageCount:3,copies:2,rejectedPageNumbersJson:"not-json"})
  ], [])[0];
  assert.equal(order.pageCount, 6);
});


test("workload totals separate B&W, colour and photo layout pages while applying copies and page rejections", () => {
  const order = buildShopOrderJourneys([
    job({id:"mono",jobNumber:"SP-1",batchId:"workload",status:"PRINTING",pageCount:5,copies:2,rejectedPageNumbersJson:"[2]"}),
    job({id:"colour-id",jobNumber:"SP-2",batchId:"workload",status:"PRINTING",pageCount:3,copies:1,colourMode:"Colour",printKind:"ID_DOCUMENT"}),
    job({id:"photo",jobNumber:"SP-3",batchId:"workload",status:"PRINTING",pageCount:2,copies:1,printKind:"PHOTO",photoCount:7,photosPerPage:4})
  ], [])[0];

  assert.equal(order.blackWhitePageCount, 8);
  assert.equal(order.colourPageCount, 3, "ID documents remain ordinary colour document pages");
  assert.equal(order.photoCount, 7);
  assert.equal(order.photoPageCount, 2);
  assert.equal(order.pageCount, 13, "Total output pages include photo layout pages exactly once");
});

test("private orders use generic labels and never match original filenames in order search", () => {
  const order = buildShopOrderJourneys([
    job({id:"private-1",jobNumber:"SP-PRIVATE-1",batchId:"private-batch",batchName:"Sensitive folder name",
      batchGroupName:"Sensitive group",status:"PRINTING",privatePrinting:true,
      document:{originalFileName:"passport-scan.pdf",mediaType:"application/pdf",sizeBytes:100}}),
    job({id:"private-2",jobNumber:"SP-PRIVATE-2",batchId:"private-batch",status:"PRINTING",privatePrinting:true,
      document:{originalFileName:"identity-card.pdf",mediaType:"application/pdf",sizeBytes:100}})
  ], [])[0];

  assert.equal(order.privatePrinting,true);
  assert.equal(order.name,"Private print order");
  assert.equal(filterShopOrderJourneys([order],"all","passport-scan.pdf").length,0);
  assert.equal(filterShopOrderJourneys([order],"all","identity-card.pdf").length,0);
  assert.equal(filterShopOrderJourneys([order],"all","Sensitive folder name").length,0);
  assert.equal(filterShopOrderJourneys([order],"all","Sensitive group").length,0);
  assert.equal(filterShopOrderJourneys([order],"all","SP-PRIVATE-1").length,1,
    "The operational order reference remains searchable");
});
