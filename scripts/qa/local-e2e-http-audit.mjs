function responseKey(response) {
  if (!response || !response.method || !response.path || !Number.isInteger(Number(response.status))) {
    throw new TypeError("HTTP response audit entries require method, path and numeric status.");
  }
  return [
    String(response.method).toUpperCase(),
    String(response.path),
    Number(response.status)
  ].join(" ");
}

export function getUnexpectedHttpResponses(observed, expected) {
  const expectedCounts = new Map();
  for (const item of expected) {
    const key = responseKey(item);
    expectedCounts.set(key, (expectedCounts.get(key) ?? 0) + 1);
  }

  const unexpected = [];
  for (const item of observed) {
    const key = responseKey(item);
    const remaining = expectedCounts.get(key) ?? 0;
    if (remaining > 0) expectedCounts.set(key, remaining - 1);
    else unexpected.push(item);
  }
  return unexpected;
}
