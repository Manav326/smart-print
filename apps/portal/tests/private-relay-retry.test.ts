import assert from "node:assert/strict";
import test from "node:test";
import {
  customerPrivateRelayPollDelay,
  hasReachedCustomerPrivateRelayStatusErrorLimit,
  shouldReconcileCustomerPrivateRelayPostError,
  shouldRetryCustomerPrivateRelayStatusError
} from "../src/private-relay-retry.ts";

test("pending status polling backs off, resets on stage change, and stays within its cap",()=>{
  const randomLow=()=>0;
  const randomHigh=()=>1;
  assert.equal(customerPrivateRelayPollDelay(0,0,false,randomLow),1275);
  assert.equal(customerPrivateRelayPollDelay(0,0,true,randomLow),1275);
  assert.equal(customerPrivateRelayPollDelay(5,0,false,randomHigh),12000);
  assert.equal(customerPrivateRelayPollDelay(100,0,false,randomHigh),12000);
});

test("transient status failures use bounded exponential backoff",()=>{
  const randomLow=()=>0;
  const randomHigh=()=>1;
  assert.equal(customerPrivateRelayPollDelay(0,1,false,randomLow),1600);
  assert.equal(customerPrivateRelayPollDelay(0,2,false,randomLow),3200);
  assert.equal(customerPrivateRelayPollDelay(0,99,false,randomHigh),30000);
});

test("only transient and completion-reconciliation status errors are retried",()=>{
  for(const status of [0,408,429,500,502,503])
    assert.equal(shouldRetryCustomerPrivateRelayStatusError({status}),true);
  assert.equal(shouldRetryCustomerPrivateRelayStatusError({
    status:409,code:"private_relay_completion_missing"
  }),true);
  for(const status of [400,401,403,404,413,422])
    assert.equal(shouldRetryCustomerPrivateRelayStatusError({status}),false);
  assert.equal(shouldRetryCustomerPrivateRelayStatusError({status:409,code:"other_conflict"}),false);
  assert.equal(shouldRetryCustomerPrivateRelayStatusError(new TypeError("fetch failed")),true);
  assert.equal(shouldRetryCustomerPrivateRelayStatusError(new Error("programming error")),false);
});

test("status reconciliation stops at exactly eight consecutive failures",()=>{
  for(let count=0;count<8;count++)
    assert.equal(hasReachedCustomerPrivateRelayStatusErrorLimit(count),false);
  assert.equal(hasReachedCustomerPrivateRelayStatusErrorLimit(8),true);
  assert.equal(hasReachedCustomerPrivateRelayStatusErrorLimit(9),true);
});

test("ambiguous relay POST outcomes are reconciled but definitive rejections are not retried",()=>{
  for(const status of [0,408,429,500,502,503])
    assert.equal(shouldReconcileCustomerPrivateRelayPostError({status}),true);
  for(const code of [
    "private_relay_direct_transfer_in_progress",
    "private_relay_state_changed",
    "private_relay_already_completed",
    "private_relay_upload_in_progress"
  ]) assert.equal(shouldReconcileCustomerPrivateRelayPostError({status:409,code}),true);
  for(const status of [400,401,403,404,413,422])
    assert.equal(shouldReconcileCustomerPrivateRelayPostError({status}),false);
  assert.equal(shouldReconcileCustomerPrivateRelayPostError({status:409,code:"other_conflict"}),false);
});

test("persistent status-service failures keep retrying with a bounded delay",()=>{
  assert.equal(customerPrivateRelayPollDelay(0,8,false,()=>0.5),30000);
  assert.equal(customerPrivateRelayPollDelay(0,100,false,()=>0.5),30000);
});
