export const HEALTH_CHECK_RECORD = "Local portal and API health";

export const INDEPENDENT_RECORDS = Object.freeze([
  "Public pages and registration/login/admin route rendering",
  "Public routes real-browser responsive matrix",
  "Create or sign in to the first real QA shop through the shop UI",
  "Create or sign in to the second real QA shop through the shop UI",
  "Create or sign in to the real global customer account",
  "Bootstrap-created platform administrator signs in through the real admin UI",
  "QR: unknown public station codes cannot create a customer session",
  "QR: an invalid opaque session token cannot read current session data",
  "Agent API requires an authenticated paired identity for all protected endpoints",
  "QR: an invalid opaque session token cannot renew a session",
  "PAYMENT: UPI references shorter than four characters are rejected",
  "PAYMENT: UPI references containing whitespace or punctuation are rejected",
  "PAYMENT: UPI references longer than one hundred characters are rejected",
  "Live local browser has no same-origin server errors or critical resource failures"
]);

export const RECORD_PREREQUISITES = Object.freeze({
  "Create and publish a free QA-only plan through the platform UI": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "Assign the QA-only plan to Shop One through the platform UI": ["Bootstrap-created platform administrator signs in through the real admin UI", "Create or sign in to the first real QA shop through the shop UI", "Create and publish a free QA-only plan through the platform UI"],
  "Assign the QA-only plan to Shop Two through the platform UI": ["Bootstrap-created platform administrator signs in through the real admin UI", "Create or sign in to the second real QA shop through the shop UI", "Create and publish a free QA-only plan through the platform UI"],
  "Platform admin navigates every real control-centre section": ["Bootstrap-created platform administrator signs in through the real admin UI", "Create and publish a free QA-only plan through the platform UI", "Create or sign in to the first real QA shop through the shop UI", "Create or sign in to the second real QA shop through the shop UI"],
  "Shop one provisions real QA printer and QR station": ["Create or sign in to the first real QA shop through the shop UI", "Assign the QA-only plan to Shop One through the platform UI"],
  "Shop one configures print pricing through the real UI": ["Create or sign in to the first real QA shop through the shop UI", "Assign the QA-only plan to Shop One through the platform UI"],
  "Shop one configures payment methods through the real UI": ["Create or sign in to the first real QA shop through the shop UI"],
  "Customer opens the shop-one station link in a guest browser session": ["Shop one provisions real QA printer and QR station"],
  "Guest customer completes a no-account upload from the shop station link": ["Customer opens the shop-one station link in a guest browser session", "Shop one configures print pricing through the real UI", "Create or sign in to the real global customer account"],
  "Shop two configures isolated real QA printer/station": ["Create or sign in to the second real QA shop through the shop UI", "Assign the QA-only plan to Shop Two through the platform UI"],
  "SHOP: invalid device, printer, profile, account and pricing inputs fail without changing saved settings": ["Shop one provisions real QA printer and QR station", "Shop one configures print pricing through the real UI", "Shop two configures isolated real QA printer/station"],
  "REPORTS: supported reporting windows return tenant-scoped summary data and invalid periods are rejected": ["Create or sign in to the first real QA shop through the shop UI", "Create or sign in to the second real QA shop through the shop UI"],
  "Shop one navigates every real sidebar section and the account-menu-only tools": ["Create or sign in to the first real QA shop through the shop UI"],
  "Shop two navigates every real sidebar section and account-menu-only tools": ["Create or sign in to the second real QA shop through the shop UI"],
  "Shop, platform-admin and cross-tenant authorization boundaries": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station", "Create or sign in to the real global customer account", "Bootstrap-created platform administrator signs in through the real admin UI"],
  "Customer opens the shop-one station with authenticated customer identity": ["Shop one provisions real QA printer and QR station", "Create or sign in to the real global customer account"],
  "QR/UPLOAD: session renewal, scoped resume and invalid local-upload metadata are safely rejected": ["Customer opens the shop-one station with authenticated customer identity", "Shop two configures isolated real QA printer/station"],
  "Customer QR session is linked to the global customer account": ["Customer opens the shop-one station with authenticated customer identity", "Create or sign in to the real global customer account"],
  "PHOTO: customer Photo Studio edits and prepares a local multi-photo A4 PDF without creating a print job": ["Customer opens the shop-one station with authenticated customer identity"],
  "Real document validator rejects a malformed PDF without creating a print job": ["Customer opens the shop-one station with authenticated customer identity"],
  "Customer uploads a synthetic two-page PDF through the real portal UI": ["Customer opens the shop-one station with authenticated customer identity", "Shop one configures print pricing through the real UI"],
  "Customer accepts the calculated quote and sends the job to the real shop queue": ["Customer uploads a synthetic two-page PDF through the real portal UI"],
  "Shop rejects the unpaid QA job and customer sees the same terminal result": ["Customer accepts the calculated quote and sends the job to the real shop queue"],
  "Customer cannot start payment for a shop-rejected job": ["Shop rejects the unpaid QA job and customer sees the same terminal result"],
  "Customer opens a fresh QR session and uploads two real documents as one batch": ["Shop one provisions real QA printer and QR station", "Shop one configures print pricing through the real UI", "Create or sign in to the real global customer account"],
  "Two-file customer batch is sent to the shop and appears as one tenant-scoped batch": ["Customer opens a fresh QR session and uploads two real documents as one batch"],
  "Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision": ["Two-file customer batch is sent to the shop and appears as one tenant-scoped batch"],
  "Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received": ["Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", "Shop one configures payment methods through the real UI"],
  "Shop payment and refund screens load without recording false financial events": ["Shop one configures payment methods through the real UI", "Customer uploads a synthetic two-page PDF through the real portal UI", "Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received"],
  "JOB/PAYMENT/REFUND: rejected and unpaid files cannot be approved, dispatched, verified or refunded": ["Shop rejects the unpaid QA job and customer sees the same terminal result", "Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received"],
  "Agent and printer prerequisites are visible; dispatch without a paired QA Agent is safely blocked": ["Shop one provisions real QA printer and QR station", "Customer uploads a synthetic two-page PDF through the real portal UI"],
  "Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity": ["Create or sign in to the first real QA shop through the shop UI"],
  "Disposable QA Agent enrolls, heartbeats, validates printer sync, rejects code replay and revokes safely": ["Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity"],
  "DEVICE/QR: printer and station controls restore safely; QR rotation invalidates the old public code": ["Shop one provisions real QA printer and QR station", "Create or sign in to the real global customer account"],
  "Platform and shop routes remain isolated after creating real QA data": ["Customer accepts the calculated quote and sends the job to the real shop queue", "Shop two configures isolated real QA printer/station", "Bootstrap-created platform administrator signs in through the real admin UI"],
  "PLATFORM: operational settings validate, audit no-op saves, and tenant suspension is reversible": ["Bootstrap-created platform administrator signs in through the real admin UI", "Assign the QA-only plan to Shop Two through the platform UI", "Shop two configures isolated real QA printer/station"],
  "AUTH: registration validation, role boundaries, refresh rotation and logout revocation": ["Create or sign in to the first real QA shop through the shop UI", "Create or sign in to the second real QA shop through the shop UI", "Create or sign in to the real global customer account"],
  "RBAC: a global customer token cannot enter shop-owner APIs": ["Create or sign in to the real global customer account", "Create or sign in to the first real QA shop through the shop UI"],
  "RBAC: a shop-owner token cannot enter customer identity APIs": ["Create or sign in to the first real QA shop through the shop UI"],
  "RBAC: a shop-owner token cannot access the platform control plane": ["Create or sign in to the first real QA shop through the shop UI"],
  "RBAC: a customer token cannot access the platform control plane": ["Create or sign in to the real global customer account"],
  "TENANT: Shop One cannot disable Shop Two's printer": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station"],
  "TENANT: Shop One cannot disable Shop Two's QR station": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station"],
  "TENANT: Shop One cannot rotate Shop Two's QR station code": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station"],
  "TENANT: printer listings expose only the authenticated shop's printers": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station"],
  "TENANT: QR station listings expose only the authenticated shop's stations": ["Shop one provisions real QA printer and QR station", "Shop two configures isolated real QA printer/station"],
  "TENANT: revoked Agent identity remains visible only to its source shop": ["Create or sign in to the second real QA shop through the shop UI", "Disposable QA Agent enrolls, heartbeats, validates printer sync, rejects code replay and revokes safely"],
  "UPLOAD: a print upload requires an Idempotency-Key": ["Customer opens the shop-one station with authenticated customer identity"],
  "UPLOAD: malformed batch identifiers are rejected before document ingestion": ["Customer opens the shop-one station with authenticated customer identity"],
  "DOC: cloud document upload rejects non-multipart requests": ["Customer opens the shop-one station with authenticated customer identity"],
  "JOB: a customer session cannot read a job outside its session": ["Customer opens the shop-one station with authenticated customer identity"],
  "JOB: a signed-in customer cannot read another customer's job history entry": ["Create or sign in to the real global customer account"],
  "PLATFORM: tenant directory rejects overlong search input": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "PLATFORM: tenant directory rejects overlong status filters": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "PLATFORM: tenant directory clamps oversized pagination safely": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "PLATFORM: tenant directory clamps negative and zero pagination safely": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "PLATFORM: unknown tenant status history does not disclose audit data": ["Bootstrap-created platform administrator signs in through the real admin UI"],
  "SHOP: overlong shop display names are rejected without changing saved branding": ["Create or sign in to the first real QA shop through the shop UI"],
  "SHOP: malformed accent colors are rejected without changing saved branding": ["Create or sign in to the first real QA shop through the shop UI"],
  "SHOP: printer creation rejects missing manufacturer and model fields": ["Create or sign in to the first real QA shop through the shop UI"],
  "SHOP: QR station creation rejects an empty station name without creating a row": ["Shop one provisions real QA printer and QR station"],
  "SHOP: an unknown printer ID cannot be changed": ["Create or sign in to the first real QA shop through the shop UI"],
  "SHOP: an unknown QR station ID cannot be changed": ["Shop one provisions real QA printer and QR station"]
});

export function validateRecordPrerequisites(recordNames) {
  const errors = [];
  if (!Array.isArray(recordNames) || recordNames.some(name => typeof name !== "string" || !name.trim())) {
    return { valid: false, errors: ["Record catalogue must be an array of non-empty names."] };
  }

  const nameSet = new Set(recordNames);
  const order = new Map(recordNames.map((name, index) => [name, index]));
  if (nameSet.size !== recordNames.length) errors.push("Record catalogue contains duplicate record names.");
  if (!nameSet.has(HEALTH_CHECK_RECORD)) errors.push("Record catalogue is missing its local health root.");

  const independent = new Set(INDEPENDENT_RECORDS);
  for (const name of INDEPENDENT_RECORDS) {
    if (!nameSet.has(name)) errors.push("Independent record is not present in the runner: " + name);
    if (Object.hasOwn(RECORD_PREREQUISITES, name)) {
      errors.push("Record cannot be both independent and prerequisite-mapped: " + name);
    }
  }

  for (const [name, prerequisites] of Object.entries(RECORD_PREREQUISITES)) {
    if (!nameSet.has(name)) errors.push("Prerequisite mapping has no matching runner record: " + name);
    if (!Array.isArray(prerequisites) || prerequisites.length === 0) {
      errors.push("Mapped record must declare at least one prerequisite: " + name);
      continue;
    }
    if (new Set(prerequisites).size !== prerequisites.length) {
      errors.push("Prerequisite list contains duplicates: " + name);
    }
    for (const prerequisite of prerequisites) {
      if (!nameSet.has(prerequisite)) {
        errors.push("Prerequisite is not present in the runner: " + prerequisite + " (required by " + name + ")");
      } else if (order.has(prerequisite) && order.has(name) && order.get(prerequisite) >= order.get(name)) {
        errors.push("Prerequisite must run before its dependent record: " + prerequisite + " -> " + name);
      }
    }
  }

  for (const name of recordNames) {
    if (name === HEALTH_CHECK_RECORD) continue;
    if (!independent.has(name) && !Object.hasOwn(RECORD_PREREQUISITES, name)) {
      errors.push("Runner record has no explicit prerequisite or independence declaration: " + name);
    }
  }

  return { valid: errors.length === 0, errors };
}

export function getBlockingPrerequisites(recordName, recordStatuses) {
  if (typeof recordName !== "string" || !recordName.trim()) {
    throw new TypeError("A non-empty QA record name is required.");
  }
  if (recordName !== HEALTH_CHECK_RECORD &&
      !Object.hasOwn(RECORD_PREREQUISITES, recordName) &&
      !INDEPENDENT_RECORDS.includes(recordName)) {
    throw new Error("QA record is missing an explicit prerequisite contract: " + recordName);
  }

  const prerequisites = recordName === HEALTH_CHECK_RECORD
    ? []
    : [HEALTH_CHECK_RECORD, ...(RECORD_PREREQUISITES[recordName] ?? [])];
  return [...new Set(prerequisites)]
    .filter(prerequisite => recordStatuses.get(prerequisite) !== "PASS");
}

export async function executeIsolatedQaRecord({
  name,
  action,
  caseIds = [],
  checkId,
  recordStatuses
}) {
  const started = Date.now();
  const baseCheck = {
    id: checkId,
    name,
    caseIds: [...caseIds]
  };

  let blockedBy;
  try {
    blockedBy = getBlockingPrerequisites(name, recordStatuses);
  } catch (error) {
    const message = String(error instanceof Error ? error.message : error).slice(0, 1600);
    recordStatuses.set(name, "FAIL");
    return {
      status: "FAIL",
      errorMessage: message,
      check: { ...baseCheck, status: "FAIL", durationMs: Date.now() - started, error: message }
    };
  }

  if (blockedBy.length > 0) {
    const details = {
      status: "SKIPPED",
      reason: "Required setup did not pass.",
      blockedBy
    };
    recordStatuses.set(name, "SKIPPED");
    return {
      status: "SKIPPED",
      check: { ...baseCheck, status: "SKIPPED", durationMs: Date.now() - started, details }
    };
  }

  try {
    const details = await action();
    const status = details?.status === "SKIPPED" ? "SKIPPED" : details?.warning ? "WARN" : "PASS";
    recordStatuses.set(name, status);
    return {
      status,
      warning: status === "WARN" ? String(details.warning) : null,
      check: { ...baseCheck, status, durationMs: Date.now() - started, details: details ?? null }
    };
  } catch (error) {
    const message = String(error instanceof Error ? error.message : error).slice(0, 1600);
    recordStatuses.set(name, "FAIL");
    return {
      status: "FAIL",
      errorMessage: message,
      check: { ...baseCheck, status: "FAIL", durationMs: Date.now() - started, error: message }
    };
  }
}

export function shouldFailQaRun({
  failed = 0,
  warnings = 0,
  skipped = 0,
  coverageGatePassed = false,
  caseMappingErrors = 0
} = {}) {
  return Number(failed) > 0 ||
    Number(warnings) > 0 ||
    Number(skipped) > 0 ||
    coverageGatePassed !== true ||
    Number(caseMappingErrors) > 0;
}
