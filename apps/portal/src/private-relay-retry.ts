/** Bounded polling policy for the durable Private Printing ticket status resource. */
export function customerPrivateRelayPollDelay(
  consecutivePendingChecks:number,
  consecutiveErrors:number,
  stageChanged:boolean,
  random=Math.random
):number {
  if(consecutiveErrors>0){
    const backoff=Math.min(30_000,2_000*Math.pow(2,Math.min(consecutiveErrors-1,4)));
    return Math.min(30_000,Math.round(backoff*(0.8+random()*0.4)));
  }
  const base=stageChanged?1_500:Math.min(12_000,1_500*Math.pow(1.7,Math.min(consecutivePendingChecks,5)));
  return Math.min(12_000,Math.round(base*(0.85+random()*0.3)));
}

/** Avoid an endless spinner when the status service itself remains unreachable. */
export const MAX_CUSTOMER_PRIVATE_RELAY_STATUS_ERRORS=8;

export function shouldContinueCustomerPrivateRelayStatusRetries(consecutiveErrors:number):boolean {
  return consecutiveErrors<MAX_CUSTOMER_PRIVATE_RELAY_STATUS_ERRORS;
}

type StatusLikeError={status?:unknown;code?:unknown};

/** Status reads are safe to retry for transport, timeout, rate-limit and server failures. */
export function shouldRetryCustomerPrivateRelayStatusError(exception:unknown):boolean {
  if(typeof exception!=="object"||exception===null||!("status" in exception))return true;
  const error=exception as StatusLikeError;
  if(typeof error.status!=="number")return true;
  return error.status===0||error.status===408||error.status===429||error.status>=500||
    (error.status===409&&error.code==="private_relay_completion_missing");
}

/** Ambiguous relay writes are reconciled against the ticket before any retry. */
export function shouldReconcileCustomerPrivateRelayPostError(exception:unknown):boolean {
  if(typeof exception!=="object"||exception===null||!("status" in exception))return true;
  const error=exception as StatusLikeError;
  if(typeof error.status!=="number")return true;
  return error.status===0||error.status===408||error.status===429||error.status>=500||
    (error.status===409&&[
      "private_relay_direct_transfer_in_progress",
      "private_relay_state_changed",
      "private_relay_already_completed"
    ].includes(String(error.code??"")));
}
