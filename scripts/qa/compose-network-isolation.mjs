import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

function readCompose(path, label) {
  let configuration;
  try {
    configuration = JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    throw new Error(`${label}: Compose did not produce valid JSON at ${path}: ${error.message}`);
  }

  assert.ok(configuration.services, `${label}: resolved Compose services are missing`);
  assert.ok(configuration.networks, `${label}: resolved Compose networks are missing`);
  return configuration;
}

function networkNames(service) {
  if (service.network_mode === "none") return [];
  if (Array.isArray(service.networks)) return service.networks;
  if (service.networks && typeof service.networks === "object") {
    return Object.keys(service.networks);
  }
  return ["default"];
}

function assertAttached(configuration, serviceName, expected, label) {
  const service = configuration.services[serviceName];
  assert.ok(service, `${label}: service ${serviceName} is missing`);
  const actual = networkNames(service);
  for (const network of expected) {
    assert.ok(actual.includes(network),
      `${label}: ${serviceName} must be attached to ${network}; got ${actual.join(", ")}`);
  }
  return actual;
}

function assertNotAttached(configuration, serviceName, forbidden, label) {
  const actual = networkNames(configuration.services[serviceName]);
  for (const network of forbidden) {
    assert.ok(!actual.includes(network),
      `${label}: ${serviceName} must not be attached to ${network}; got ${actual.join(", ")}`);
  }
}

function servicesOnNetwork(configuration, networkName) {
  return Object.entries(configuration.services)
    .filter(([, service]) => networkNames(service).includes(networkName))
    .map(([name]) => name)
    .sort();
}

function validate(path, label) {
  const configuration = readCompose(path, label);

  assertAttached(configuration, "api",
    ["default", "database", "document-processing", "malware-scanning"], label);
  assertAttached(configuration, "db", ["database"], label);
  assertAttached(configuration, "clamav",
    ["malware-scanning", "malware-egress"], label);
  assertAttached(configuration, "document-worker",
    ["document-processing"], label);

  // The token initializer has no network at all; the portal only reaches the API
  // through Nginx and cannot directly reach PostgreSQL or either document engine.
  assert.equal(configuration.services["document-worker-token"].network_mode, "none",
    `${label}: document-worker-token must have network_mode: none`);
  assertAttached(configuration, "portal", ["default"], label);
  assertNotAttached(configuration, "portal",
    ["database", "document-processing", "malware-scanning", "malware-egress"], label);

  assertNotAttached(configuration, "db",
    ["default", "document-processing", "malware-scanning", "malware-egress"], label);
  assertNotAttached(configuration, "clamav", ["default", "database", "document-processing"], label);
  assertNotAttached(configuration, "document-worker",
    ["default", "database", "malware-scanning", "malware-egress"], label);

  assert.deepEqual(servicesOnNetwork(configuration, "database"), ["api", "db"],
    `${label}: the database network must be shared only by API and PostgreSQL`);
  assert.deepEqual(servicesOnNetwork(configuration, "document-processing"),
    ["api", "document-worker"],
    `${label}: the document-processing network has unexpected members`);
  assert.deepEqual(servicesOnNetwork(configuration, "malware-scanning"), ["api", "clamav"],
    `${label}: only API and ClamAV may use the scanning network`);
  assert.deepEqual(servicesOnNetwork(configuration, "malware-egress"), ["clamav"],
    `${label}: only ClamAV may use the signature-update egress network`);

  assert.equal(configuration.networks.database?.internal, true,
    `${label}: database network must be internal`);
  assert.equal(configuration.networks["document-processing"]?.internal, true,
    `${label}: document-processing network must be internal`);
  assert.equal(configuration.networks["malware-scanning"]?.internal, true,
    `${label}: malware-scanning network must be internal`);
  assert.notEqual(configuration.networks["malware-egress"]?.internal, true,
    `${label}: malware-egress must permit ClamAV signature updates`);

  console.log(`PASS: ${label} keeps PostgreSQL, ClamAV and the parser off the portal network.`);
}

if (process.argv.length !== 4) {
  throw new Error(
    "Usage: node scripts/qa/compose-network-isolation.mjs <production-compose.json> <local-compose.json>");
}

validate(process.argv[2], "production Compose");
validate(process.argv[3], "local Compose");
