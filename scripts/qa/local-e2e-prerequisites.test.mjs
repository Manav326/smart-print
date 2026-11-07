import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { getBlockingPrerequisites, validateRecordPrerequisites, HEALTH_CHECK_RECORD } from "./local-e2e-prerequisites.mjs";
import { executeIsolatedQaRecord, shouldFailQaRun } from "./local-e2e-record-runner.mjs";

test("a Shop One pricing failure blocks quote-dependent work, not independent shop work", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "FAIL"],
    ["Customer opens the shop-one station with authenticated customer identity", "PASS"],
    ["Shop two configures isolated real QA printer/station", "PASS"]
  ]);

  assert.deepEqual(getBlockingPrerequisites("Shop one navigates every real sidebar section and the account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(
    getBlockingPrerequisites("Customer uploads a synthetic two-page PDF through the real portal UI", statuses),
    ["Shop one configures print pricing through the real UI"]
  );
});

test("a Shop One payment-settings failure blocks only payment-dependent workflows", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "PASS"],
    ["Shop one configures payment methods through the real UI", "FAIL"],
    ["Customer opens the shop-one station with authenticated customer identity", "PASS"],
    ["Customer uploads a synthetic two-page PDF through the real portal UI", "PASS"],
    ["Shopkeeper rejects one file, undoes that rejection, then applies the individual file decision", "PASS"],
    ["Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", "PASS"]
  ]);

  assert.deepEqual(getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Customer accepts the calculated quote and sends the job to the real shop queue", statuses), []);
  assert.deepEqual(
    getBlockingPrerequisites("Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", statuses),
    ["Shop one configures payment methods through the real UI"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Shop payment and refund screens load without recording false financial events", statuses),
    ["Shop one configures payment methods through the real UI"]
  );
});

test("a station provisioning failure blocks its station journeys but not Shop Two or unrelated negative security checks", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "FAIL"],
    ["Shop two configures isolated real QA printer/station", "FAIL"]
  ]);

  assert.deepEqual(
    getBlockingPrerequisites("Customer opens the shop-one station link in a guest browser session", statuses),
    ["Shop one provisions real QA printer and QR station"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Customer opens the shop-one station with authenticated customer identity", statuses),
    ["Shop one provisions real QA printer and QR station"]
  );
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("QR: unknown public station codes cannot create a customer session", statuses), []);
});

test("a customer-session failure blocks its uploads without blocking other roles", () => {
  const statuses = new Map([
    ["Local portal and API health", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "PASS"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "PASS"],
    ["Shop two configures isolated real QA printer/station", "PASS"],
    ["Customer opens the shop-one station with authenticated customer identity", "FAIL"]
  ]);

  assert.deepEqual(
    getBlockingPrerequisites("Customer uploads a synthetic two-page PDF through the real portal UI", statuses),
    ["Customer opens the shop-one station with authenticated customer identity"]
  );
  assert.deepEqual(getBlockingPrerequisites("Shop one navigates every real sidebar section and the account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses), []);
  assert.deepEqual(getBlockingPrerequisites("JOB: a signed-in customer cannot read another customer's job history entry", statuses), []);
});

test("health failure blocks local browser checks", () => {
  const statuses = new Map([
    ["Local portal and API health", "FAIL"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"]
  ]);
  assert.deepEqual(
    getBlockingPrerequisites("Public pages and registration/login/admin route rendering", statuses),
    ["Local portal and API health"]
  );
  assert.deepEqual(
    getBlockingPrerequisites("Shop two navigates every real sidebar section and account-menu-only tools", statuses),
    ["Local portal and API health"]
  );
});

test("a missing prerequisite is fail-closed", () => {
  assert.deepEqual(
    getBlockingPrerequisites(
      "Create and publish a free QA-only plan through the platform UI",
      new Map([["Local portal and API health", "PASS"]])
    ),
    ["Bootstrap-created platform administrator signs in through the real admin UI"]
  );
});


const runnerSource = readFileSync(new URL("./smartprint-local-e2e.mjs", import.meta.url), "utf8");
const runnerRecordNames = [...runnerSource.matchAll(/\bawait\s+record\(\s*"([^"]+)"/g)].map(match => match[1]);

test("every E2E record has an explicit prerequisite or independence contract and dependencies run first", () => {
  const result = validateRecordPrerequisites(runnerRecordNames);
  assert.deepEqual(result.errors, []);
  assert.equal(result.valid, true);
});

test("a failed Shop One setup skips only its dependants while Shop Two and unrelated checks still execute", async () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Bootstrap-created platform administrator signs in through the real admin UI", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "PASS"]
  ]);
  let independentRan = false;
  let blockedActionRan = false;

  const failedSetup = await executeIsolatedQaRecord({
    name: "Shop one provisions real QA printer and QR station",
    checkId: "SP-TEST-001",
    caseIds: ["SHOP-009", "SHOP-010"],
    recordStatuses: statuses,
    action: async () => { throw new Error("Synthetic setup failure for isolation test."); }
  });
  assert.equal(failedSetup.status, "FAIL");
  assert.match(failedSetup.check.error, /Synthetic setup failure/);

  const blockedGuestJourney = await executeIsolatedQaRecord({
    name: "Customer opens the shop-one station link in a guest browser session",
    checkId: "SP-TEST-002",
    caseIds: ["QR-008", "QR-009"],
    recordStatuses: statuses,
    action: async () => { blockedActionRan = true; }
  });
  assert.equal(blockedGuestJourney.status, "SKIPPED");
  assert.deepEqual(blockedGuestJourney.check.details.blockedBy, [
    "Shop one provisions real QA printer and QR station"
  ]);
  assert.equal(blockedActionRan, false);

  const shopTwo = await executeIsolatedQaRecord({
    name: "Shop two configures isolated real QA printer/station",
    checkId: "SP-TEST-003",
    caseIds: ["SHOP-015", "SHOP-016", "SHOP-017", "SHOP-018"],
    recordStatuses: statuses,
    action: async () => ({ tenant: "Shop Two", isolated: true })
  });
  assert.equal(shopTwo.status, "PASS");
  assert.equal(independentRan, false);

  const shopOneNavigation = await executeIsolatedQaRecord({
    name: "Shop one navigates every real sidebar section and the account-menu-only tools",
    checkId: "SP-TEST-004",
    recordStatuses: statuses,
    action: async () => { independentRan = true; return { navigation: "passed" }; }
  });
  assert.equal(shopOneNavigation.status, "PASS");
  assert.equal(independentRan, true);
});

test("a failed pricing check blocks quote and upload descendants but not guest link, security, or Shop Two checks", async () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "PASS"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures payment methods through the real UI", "PASS"],
    ["Customer opens the shop-one station with authenticated customer identity", "PASS"]
  ]);

  const pricing = await executeIsolatedQaRecord({
    name: "Shop one configures print pricing through the real UI",
    checkId: "SP-TEST-005",
    recordStatuses: statuses,
    action: async () => { throw new Error("Synthetic pricing failure."); }
  });
  assert.equal(pricing.status, "FAIL");

  const upload = await executeIsolatedQaRecord({
    name: "Customer uploads a synthetic two-page PDF through the real portal UI",
    checkId: "SP-TEST-006",
    recordStatuses: statuses,
    action: async () => ({ uploaded: true })
  });
  assert.equal(upload.status, "SKIPPED");
  assert.ok(upload.check.details.blockedBy.includes("Shop one configures print pricing through the real UI"));

  const guestLink = await executeIsolatedQaRecord({
    name: "Customer opens the shop-one station link in a guest browser session",
    checkId: "SP-TEST-007",
    recordStatuses: statuses,
    action: async () => ({ guestSession: true })
  });
  assert.equal(guestLink.status, "PASS");

  const shopTwoNavigation = await executeIsolatedQaRecord({
    name: "Shop two navigates every real sidebar section and account-menu-only tools",
    checkId: "SP-TEST-008",
    recordStatuses: statuses,
    action: async () => ({ sectionsVisited: 10 })
  });
  assert.equal(shopTwoNavigation.status, "PASS");
});

test("WARN and SKIPPED prerequisites are not treated as successful setup", async () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "PASS"],
    ["Create or sign in to the real global customer account", "PASS"]
  ]);

  const warning = await executeIsolatedQaRecord({
    name: "Shop one provisions real QA printer and QR station",
    checkId: "SP-TEST-009",
    recordStatuses: statuses,
    action: async () => ({ warning: "Station metadata could not be read back." })
  });
  assert.equal(warning.status, "WARN");

  const dependent = await executeIsolatedQaRecord({
    name: "DEVICE/QR: printer and station controls restore safely; QR rotation invalidates the old public code",
    checkId: "SP-TEST-010",
    recordStatuses: statuses,
    action: async () => ({ rotated: true })
  });
  assert.equal(dependent.status, "SKIPPED");
  assert.deepEqual(dependent.check.details.blockedBy, [
    "Shop one provisions real QA printer and QR station"
  ]);
});

test("an undeclared new record fails closed without aborting the next independent record", async () => {
  const statuses = new Map([[HEALTH_CHECK_RECORD, "PASS"]]);
  const missingContract = await executeIsolatedQaRecord({
    name: "NEW RECORD WITHOUT DEPENDENCY CONTRACT",
    checkId: "SP-TEST-011",
    recordStatuses: statuses,
    action: async () => { throw new Error("Must not run without a contract."); }
  });
  assert.equal(missingContract.status, "FAIL");
  assert.match(missingContract.check.error, /explicit prerequisite contract/);

  const next = await executeIsolatedQaRecord({
    name: "QR: unknown public station codes cannot create a customer session",
    checkId: "SP-TEST-012",
    recordStatuses: statuses,
    action: async () => ({ status: 404 })
  });
  assert.equal(next.status, "PASS");
});

test("a health failure safely skips all browser/API records instead of producing misleading passes", async () => {
  const statuses = new Map([[HEALTH_CHECK_RECORD, "FAIL"]]);
  const result = await executeIsolatedQaRecord({
    name: "Create or sign in to the second real QA shop through the shop UI",
    checkId: "SP-TEST-013",
    recordStatuses: statuses,
    action: async () => { throw new Error("Must not touch a failed environment."); }
  });
  assert.equal(result.status, "SKIPPED");
  assert.deepEqual(result.check.details.blockedBy, [HEALTH_CHECK_RECORD]);
});


test("per-shop plan assignment isolates Shop One failure from Shop Two provisioning", async () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Bootstrap-created platform administrator signs in through the real admin UI", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "FAIL"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"]
  ]);

  const shopOneAssignment = await executeIsolatedQaRecord({
    name: "Assign the QA-only plan to Shop One through the platform UI",
    checkId: "SP-TEST-014",
    recordStatuses: statuses,
    action: async () => { throw new Error("Shop One assignment must not run without its tenant."); }
  });
  assert.equal(shopOneAssignment.status, "SKIPPED");
  assert.deepEqual(shopOneAssignment.check.details.blockedBy, [
    "Create or sign in to the first real QA shop through the shop UI"
  ]);

  const shopTwoAssignment = await executeIsolatedQaRecord({
    name: "Assign the QA-only plan to Shop Two through the platform UI",
    checkId: "SP-TEST-015",
    recordStatuses: statuses,
    action: async () => ({ tenantId: "shop-two", assigned: true })
  });
  assert.equal(shopTwoAssignment.status, "PASS");

  const shopOneProvisioning = await executeIsolatedQaRecord({
    name: "Shop one provisions real QA printer and QR station",
    checkId: "SP-TEST-016",
    recordStatuses: statuses,
    action: async () => { throw new Error("Shop One provisioning must not run."); }
  });
  assert.equal(shopOneProvisioning.status, "SKIPPED");

  const shopTwoProvisioning = await executeIsolatedQaRecord({
    name: "Shop two configures isolated real QA printer/station",
    checkId: "SP-TEST-017",
    recordStatuses: statuses,
    action: async () => ({ tenant: "Shop Two", resourcesPersisted: true })
  });
  assert.equal(shopTwoProvisioning.status, "PASS");
});

test("unauthenticated Agent endpoint probes remain runnable when Agent enrollment setup fails", async () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Dedicated QA Agent enrollment code can be generated without pairing or replacing a machine identity", "FAIL"]
  ]);
  let probeRan = false;

  const probe = await executeIsolatedQaRecord({
    name: "Agent API requires an authenticated paired identity for all protected endpoints",
    checkId: "SP-TEST-018",
    recordStatuses: statuses,
    action: async () => { probeRan = true; return { endpointsRejectedWithoutIdentity: 17 }; }
  });
  assert.equal(probe.status, "PASS");
  assert.equal(probeRan, true);
});

test("records declare cross-tenant and produced-document fixture dependencies", () => {
  const statuses = new Map([
    [HEALTH_CHECK_RECORD, "PASS"],
    ["Customer opens the shop-one station with authenticated customer identity", "PASS"],
    ["Create or sign in to the first real QA shop through the shop UI", "PASS"],
    ["Create or sign in to the second real QA shop through the shop UI", "PASS"],
    ["Bootstrap-created platform administrator signs in through the real admin UI", "PASS"],
    ["Create and publish a free QA-only plan through the platform UI", "PASS"],
    ["Assign the QA-only plan to Shop One through the platform UI", "FAIL"],
    ["Assign the QA-only plan to Shop Two through the platform UI", "FAIL"],
    ["Customer uploads a synthetic two-page PDF through the real portal UI", "FAIL"],
    ["Shop one provisions real QA printer and QR station", "PASS"],
    ["Shop one configures print pricing through the real UI", "PASS"],
    ["Shop two configures isolated real QA printer/station", "FAIL"],
    ["Shop one configures payment methods through the real UI", "PASS"],
    ["Real UPI payment intent, synthetic QA reference, and shop rejection without marking money received", "PASS"]
  ]);

  assert.deepEqual(getBlockingPrerequisites(
    "Platform admin navigates every real control-centre section",
    statuses
  ), [], "Read-only admin navigation must continue if either shop assignment fails.");

  assert.ok(getBlockingPrerequisites(
    "QR/UPLOAD: session renewal, scoped resume and invalid local-upload metadata are safely rejected",
    statuses
  ).includes("Shop two configures isolated real QA printer/station"));
  assert.ok(getBlockingPrerequisites(
    "Agent and printer prerequisites are visible; dispatch without a paired QA Agent is safely blocked",
    statuses
  ).includes("Customer uploads a synthetic two-page PDF through the real portal UI"));
  assert.ok(getBlockingPrerequisites(
    "Shop payment and refund screens load without recording false financial events",
    statuses
  ).includes("Customer uploads a synthetic two-page PDF through the real portal UI"));
  assert.ok(getBlockingPrerequisites(
    "DEVICE/QR: printer and station controls restore safely; QR rotation invalidates the old public code",
    statuses
  ).includes("Create or sign in to the real global customer account"));
  assert.ok(getBlockingPrerequisites(
    "SHOP: invalid device, printer, profile, account and pricing inputs fail without changing saved settings",
    statuses
  ).includes("Shop two configures isolated real QA printer/station"));
});

test("the release gate fails on any failed, warned, skipped, unmapped or incomplete run", () => {
  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 0,
    coverageGatePassed: true,
    caseMappingErrors: 0
  }), false);

  assert.equal(shouldFailQaRun({
    failed: 1,
    warnings: 0,
    coverageGatePassed: true,
    caseMappingErrors: 0
  }), true);

  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 1,
    coverageGatePassed: true,
    caseMappingErrors: 0
  }), true);

  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 0,
    coverageGatePassed: false,
    caseMappingErrors: 0
  }), true);

  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 0,
    coverageGatePassed: true,
    caseMappingErrors: 1
  }), true);

  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 0,
    skipped: 1,
    coverageGatePassed: true,
    caseMappingErrors: 0
  }), true);

  assert.equal(shouldFailQaRun({
    failed: 0,
    warnings: 0,
    coverageGatePassed: true,
    caseMappingErrors: 0
  }), false);
});
