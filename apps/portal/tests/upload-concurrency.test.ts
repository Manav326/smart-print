import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { mapConcurrentSettled } from "../src/upload-concurrency.ts";

describe("bounded customer file uploads", () => {
  it("caps concurrent transfers at three while completing a 20-file batch", async () => {
    let active = 0;
    let maximumActive = 0;
    const outcomes = await mapConcurrentSettled(
      Array.from({ length: 20 }, (_, index) => index),
      3,
      async value => {
        active += 1;
        maximumActive = Math.max(maximumActive, active);
        await new Promise(resolve => setTimeout(resolve, 2));
        active -= 1;
        return value * 2;
      }
    );

    assert.equal(maximumActive, 3);
    assert.equal(outcomes.length, 20);
    assert.ok(outcomes.every(result => result.status === "fulfilled"));
    assert.deepEqual(
      outcomes.map(result => result.status === "fulfilled" ? result.value : null),
      Array.from({ length: 20 }, (_, index) => index * 2)
    );
  });

  it("keeps completed uploads when one file fails", async () => {
    const outcomes = await mapConcurrentSettled(
      [0, 1, 2, 3, 4, 5],
      3,
      async value => {
        if (value === 2) throw new Error("file 2 failed");
        return "uploaded-" + value;
      }
    );

    assert.deepEqual(
      outcomes.map(result => result.status),
      ["fulfilled", "fulfilled", "rejected", "fulfilled", "fulfilled", "fulfilled"]
    );
    assert.equal(
      outcomes[2].status === "rejected" && outcomes[2].reason instanceof Error
        ? outcomes[2].reason.message : "",
      "file 2 failed"
    );
    assert.equal(outcomes[5].status, "fulfilled");
  });

  it("rejects an invalid concurrency setting", async () => {
    await assert.rejects(
      () => mapConcurrentSettled([1], 0, async value => value),
      /positive integer/
    );
  });
});
