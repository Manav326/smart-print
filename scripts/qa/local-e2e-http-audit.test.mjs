import test from "node:test";
import assert from "node:assert/strict";
import { getUnexpectedHttpResponses } from "./local-e2e-http-audit.mjs";

test("only explicitly asserted method/path/status responses are accepted", () => {
  const observed = [
    { method: "POST", path: "/api/v1/auth/login", status: 401 },
    { method: "POST", path: "/api/v1/shop/printers", status: 400 },
    { method: "GET", path: "/api/v1/shop/profile", status: 404 }
  ];
  const expected = [
    { method: "POST", path: "/api/v1/auth/login", status: 401 },
    { method: "POST", path: "/api/v1/shop/printers", status: 400 }
  ];
  assert.deepEqual(getUnexpectedHttpResponses(observed, expected), [
    { method: "GET", path: "/api/v1/shop/profile", status: 404 }
  ]);
});

test("expected responses are consumed one-for-one; duplicates remain visible", () => {
  const observed = [
    { method: "POST", path: "/api/v1/payments/intent", status: 409 },
    { method: "POST", path: "/api/v1/payments/intent", status: 409 }
  ];
  const expected = [{ method: "POST", path: "/api/v1/payments/intent", status: 409 }];
  assert.equal(getUnexpectedHttpResponses(observed, expected).length, 1);
});

test("path, method and status must all match", () => {
  const observed = [
    { method: "GET", path: "/api/v1/shop/profile", status: 404 },
    { method: "GET", path: "/api/v1/shop/profile?from=other", status: 404 }
  ];
  const expected = [{ method: "GET", path: "/api/v1/shop/profile", status: 404 }];
  assert.deepEqual(getUnexpectedHttpResponses(observed, expected), [
    { method: "GET", path: "/api/v1/shop/profile?from=other", status: 404 }
  ]);
});
