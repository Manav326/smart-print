export interface ConcurrentUploadProgress {
  completed: number;
  activeIndices: number[];
}

export type ConcurrentUploadOutcome<T> =
  | { status: "fulfilled"; value: T }
  | { status: "rejected"; reason: unknown };

export const CUSTOMER_UPLOAD_CONCURRENCY = 3;

/**
 * Runs independent per-file transfers with bounded concurrency.
 * Results remain in selection order; a failed file does not discard successes.
 */
export async function mapConcurrentSettled<TInput, TOutput>(
  items: readonly TInput[],
  concurrency: number,
  worker: (item: TInput, index: number) => Promise<TOutput>,
  onProgress?: (progress: ConcurrentUploadProgress) => void,
  onSettled?: (index: number, outcome: ConcurrentUploadOutcome<TOutput>, progress: ConcurrentUploadProgress) => void
): Promise<ConcurrentUploadOutcome<TOutput>[]> {
  if (!Number.isSafeInteger(concurrency) || concurrency < 1) {
    throw new RangeError("Upload concurrency must be a positive integer.");
  }

  const outcomes = new Array<ConcurrentUploadOutcome<TOutput>>(items.length);
  if (items.length === 0) return outcomes;

  let nextIndex = 0;
  let completed = 0;
  const active = new Set<number>();
  const snapshot = (): ConcurrentUploadProgress => ({
    completed,
    activeIndices: [...active].sort((left, right) => left - right)
  });

  const runWorker = async (): Promise<void> => {
    while (true) {
      const index = nextIndex++;
      if (index >= items.length) return;

      active.add(index);
      onProgress?.(snapshot());

      let outcome: ConcurrentUploadOutcome<TOutput>;
      try {
        outcome = { status: "fulfilled", value: await worker(items[index], index) };
      } catch (reason) {
        outcome = { status: "rejected", reason };
      }

      outcomes[index] = outcome;
      active.delete(index);
      completed += 1;
      const progress = snapshot();
      onProgress?.(progress);
      onSettled?.(index, outcome, progress);
    }
  };

  await Promise.all(Array.from({ length: Math.min(items.length, concurrency) }, () => runWorker()));
  return outcomes;
}
