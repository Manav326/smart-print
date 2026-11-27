/** Bounded polling policy for the durable Private Printing ticket status resource. */
export const CUSTOMER_PRIVATE_RELAY_MAX_CONSECUTIVE_STATUS_ERRORS=8;

export function hasReachedCustomerPrivateRelayStatusErrorLimit(consecutiveErrors:number):boolean {
  return consecutiveErrors>=CUSTOMER_PRIVATE_RELAY_MAX_CONSECUTIVE_STATUS_ERRORS;
}

function isTransientTransportError(exception:unknown):boolean {
  if(exception instanceof TypeError)return true;
  if(typeof exception!=="object"||exception===null||!("code" in exception))return false;
  return ["ECONNRESET","ETIMEDOUT","EAI_AGAIN","ERR_NETWORK","UND_ERR_CONNECT_TIMEOUT","UND_ERR_SOCKET"]
    .includes(String((exception as {code?:unknown}).code??""));
}
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

type StatusLikeError={status?:unknown;code?:unknown};

/** Status reads are safe to retry for transport, timeout, rate-limit and server failures. */
export function shouldRetryCustomerPrivateRelayStatusError(exception:unknown):boolean {
  if(typeof exception!=="object"||exception===null||!("status" in exception))
    return isTransientTransportError(exception);
  const error=exception as StatusLikeError;
  if(typeof error.status!=="number")return isTransientTransportError(exception);
  return error.status===0||error.status===408||error.status===429||error.status>=500||
    (error.status===409&&error.code==="private_relay_completion_missing");
}

/** Ambiguous relay writes are reconciled against the ticket before any retry. */
export function shouldReconcileCustomerPrivateRelayPostError(exception:unknown):boolean {
  if(typeof exception!=="object"||exception===null||!("status" in exception))
    return isTransientTransportError(exception);
  const error=exception as StatusLikeError;
  if(typeof error.status!=="number")return isTransientTransportError(exception);
  return error.status===0||error.status===408||error.status===429||error.status>=500||
    (error.status===409&&[
      "private_relay_direct_transfer_in_progress",
      "private_relay_state_changed",
      "private_relay_already_completed",
      "private_relay_upload_in_progress"
    ].includes(String(error.code??"")));
}
