import type { PhotoStudioEditorSettings } from "./PhotoStudio";

export type CustomerPendingFileMetadata = {
  webkitRelativePath?: string;
  batchGroup?: string;
  printKind?: "DOCUMENT" | "PHOTO" | "ID_DOCUMENT";
  photoSettings?: { sizeCode: string; photoCount: number; photosPerPage?: number };
  nativePreviewToken?: string;
};

export type CustomerPendingSelectionMetadata = {
  selectedFileIndex: number;
  batchName: string;
  fileGroupNames: Record<string, string>;
  groupSettings: Record<string, unknown>;
  fileSettings: Record<string, unknown>;
  photoSettingsByFileFingerprint: Record<string, { sizeCode: string; photoCount: number; photosPerPage?: number }>;
  privatePrinting: boolean;
  privacyChoiceManuallySelected: boolean;
  uploadMode: "LOCAL" | "CLOUD";
  uploadModeManuallySelected: boolean;
  paymentMethod: "UPI" | "CASH";
  paymentReference: string;
  photoStudioDraft?: { sourceFingerprint: string; settings: Partial<PhotoStudioEditorSettings> } | null;
};

export type CustomerPendingSelection = {
  files: File[];
  nativePreviewTokens: Array<string | null>;
  metadata: CustomerPendingSelectionMetadata;
};

type StoredFile = {
  storageKey: string;
  index: number;
  name: string;
  type: string;
  size: number;
  lastModified: number;
  blob: Blob;
  fileMetadata: CustomerPendingFileMetadata;
};

type StoredMetadata = CustomerPendingSelectionMetadata & {
  version: 1;
  updatedAt: number;
};

const DATABASE_NAME = "smartprint-customer-upload-drafts";
const DATABASE_VERSION = 1;
const FILE_STORE = "pending-files";
const METADATA_STORE = "pending-metadata";

export function customerPendingSelectionStorageKey(publicCode: string, sessionToken: string): string {
  // Drafts are private to one customer session at this station. Never restore
  // one customer’s files into a later guest session on a shared device.
  return `${publicCode}:${sessionToken.slice(-16)}`;
}

function openDatabase(): Promise<IDBDatabase> {
  if (typeof indexedDB === "undefined") {
    return Promise.reject(new Error("This browser does not support on-device draft recovery."));
  }

  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
    request.onupgradeneeded = () => {
      const database = request.result;
      if (!database.objectStoreNames.contains(FILE_STORE)) database.createObjectStore(FILE_STORE);
      if (!database.objectStoreNames.contains(METADATA_STORE)) database.createObjectStore(METADATA_STORE);
    };
    request.onsuccess = () => {
      const database = request.result;
      database.onversionchange = () => database.close();
      resolve(database);
    };
    request.onerror = () => reject(request.error ?? new Error("On-device draft storage could not be opened."));
    request.onblocked = () => reject(new Error("On-device draft storage is busy in another SmartPrint page."));
  });
}

function transactionDone(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onabort = () => reject(transaction.error ?? new Error("On-device draft storage was interrupted."));
    transaction.onerror = () => reject(transaction.error ?? new Error("On-device draft storage failed."));
  });
}

function fileRecordKey(storageKey: string, index: number): string {
  return `${storageKey}\u0000${String(index).padStart(2, "0")}`;
}

function metadataForFile(file: File, previewTokens?: WeakMap<File, string>): CustomerPendingFileMetadata {
  const tagged = file as File & {
    __smartPrintBatchGroup?: string;
    __smartPrintPrintKind?: string;
    __smartPrintPhotoSettings?: { sizeCode: string; photoCount: number; photosPerPage?: number };
    webkitRelativePath?: string;
  };
  const printKind = tagged.__smartPrintPrintKind;
  return {
    ...(tagged.webkitRelativePath ? { webkitRelativePath: tagged.webkitRelativePath } : {}),
    ...(tagged.__smartPrintBatchGroup ? { batchGroup: tagged.__smartPrintBatchGroup } : {}),
    ...(printKind === "PHOTO" || printKind === "ID_DOCUMENT" ? { printKind } : {}),
    ...(tagged.__smartPrintPhotoSettings ? { photoSettings: tagged.__smartPrintPhotoSettings } : {}),
    ...(previewTokens?.get(file) ? { nativePreviewToken: previewTokens.get(file) } : {})
  };
}

/**
 * Persist the selected files in IndexedDB, not localStorage. This is a
 * device-local recovery copy only; it is never sent to the SmartPrint API.
 * Files are committed together so an interrupted save leaves the previous
 * selection intact instead of a partially replaced one.
 */
export async function saveCustomerPendingFiles(storageKey: string, files: File[], previewTokens?: WeakMap<File, string>): Promise<void> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction(FILE_STORE, "readwrite");
    const store = transaction.objectStore(FILE_STORE);
    const done = transactionDone(transaction);
    const range = IDBKeyRange.bound(`${storageKey}\u0000`, `${storageKey}\u0000\uffff`);
    const request = store.getAllKeys(range);
    request.onsuccess = () => {
      request.result.forEach(key => store.delete(key));
      files.forEach((file, index) => {
        const record: StoredFile = {
          storageKey,
          index,
          name: file.name,
          type: file.type,
          size: file.size,
          lastModified: file.lastModified,
          blob: file,
          fileMetadata: metadataForFile(file, previewTokens)
        };
        store.put(record, fileRecordKey(storageKey, index));
      });
    };
    request.onerror = () => {
      try { transaction.abort(); } catch { /* The transaction may already be inactive. */ }
    };
    await done;
  } finally {
    database.close();
  }
}

export async function saveCustomerPendingSelectionMetadata(
  storageKey: string,
  metadata: CustomerPendingSelectionMetadata
): Promise<void> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction(METADATA_STORE, "readwrite");
    const done = transactionDone(transaction);
    transaction.objectStore(METADATA_STORE).put({
      ...metadata,
      version: 1,
      updatedAt: Date.now()
    } satisfies StoredMetadata, storageKey);
    await done;
  } finally {
    database.close();
  }
}

export async function loadCustomerPendingSelection(storageKey: string): Promise<CustomerPendingSelection | null> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction([FILE_STORE, METADATA_STORE], "readonly");
    const done = transactionDone(transaction);
    const metadataRequest = transaction.objectStore(METADATA_STORE).get(storageKey);
    const range = IDBKeyRange.bound(`${storageKey}\u0000`, `${storageKey}\u0000\uffff`);
    const filesRequest = transaction.objectStore(FILE_STORE).getAll(range);
    const [storedMetadata, records] = await Promise.all([
      new Promise<StoredMetadata | undefined>((resolve, reject) => {
        metadataRequest.onsuccess = () => resolve(metadataRequest.result as StoredMetadata | undefined);
        metadataRequest.onerror = () => reject(metadataRequest.error);
      }),
      new Promise<StoredFile[]>((resolve, reject) => {
        filesRequest.onsuccess = () => resolve(filesRequest.result as StoredFile[]);
        filesRequest.onerror = () => reject(filesRequest.error);
      })
    ]);
    await done;
    if (records.length === 0) return null;
    const metadata = storedMetadata?.version === 1 ? storedMetadata : {
      version: 1 as const,
      updatedAt: Date.now(),
      selectedFileIndex: 0,
      batchName: "",
      fileGroupNames: {},
      groupSettings: {},
      fileSettings: {},
      photoSettingsByFileFingerprint: {},
      privatePrinting: false,
      privacyChoiceManuallySelected: false,
      uploadMode: "LOCAL" as const,
      uploadModeManuallySelected: false,
      paymentMethod: "UPI" as const,
      paymentReference: "",
      photoStudioDraft: null
    };

    records.sort((left, right) => left.index - right.index);
    const nativePreviewTokens: Array<string | null> = [];
    const files = records.map(record => {
      const file = new File([record.blob], record.name, {
        type: record.type,
        lastModified: record.lastModified
      }) as File & {
        __smartPrintBatchGroup?: string;
        __smartPrintPrintKind?: string;
        __smartPrintPhotoSettings?: { sizeCode: string; photoCount: number; photosPerPage?: number };
        webkitRelativePath?: string;
      };
      if (record.fileMetadata.batchGroup) file.__smartPrintBatchGroup = record.fileMetadata.batchGroup;
      if (record.fileMetadata.printKind) file.__smartPrintPrintKind = record.fileMetadata.printKind;
      if (record.fileMetadata.photoSettings) file.__smartPrintPhotoSettings = record.fileMetadata.photoSettings;
      nativePreviewTokens.push(record.fileMetadata.nativePreviewToken ?? null);
      if (record.fileMetadata.webkitRelativePath) {
        Object.defineProperty(file, "webkitRelativePath", {
          configurable: true,
          enumerable: true,
          value: record.fileMetadata.webkitRelativePath
        });
      }
      return file;
    });
    return {
      files,
      nativePreviewTokens,
      metadata: {
        selectedFileIndex: Math.min(Math.max(0, metadata.selectedFileIndex || 0), Math.max(0, files.length - 1)),
        batchName: typeof metadata.batchName === "string" ? metadata.batchName : "",
        fileGroupNames: metadata.fileGroupNames ?? {},
        groupSettings: metadata.groupSettings ?? {},
        fileSettings: metadata.fileSettings ?? {},
        photoSettingsByFileFingerprint: metadata.photoSettingsByFileFingerprint ?? {},
        privatePrinting: Boolean(metadata.privatePrinting),
        privacyChoiceManuallySelected: Boolean(metadata.privacyChoiceManuallySelected),
        uploadMode: metadata.uploadMode === "CLOUD" ? "CLOUD" : "LOCAL",
        uploadModeManuallySelected: Boolean(metadata.uploadModeManuallySelected),
        paymentMethod: metadata.paymentMethod === "CASH" ? "CASH" : "UPI",
        paymentReference: typeof metadata.paymentReference === "string" ? metadata.paymentReference.slice(0, 100) : "",
        photoStudioDraft: metadata.photoStudioDraft && typeof metadata.photoStudioDraft.sourceFingerprint === "string"
          ? metadata.photoStudioDraft
          : null
      }
    };
  } finally {
    database.close();
  }
}

export async function clearCustomerPendingSelection(storageKey: string): Promise<void> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction([FILE_STORE, METADATA_STORE], "readwrite");
    const done = transactionDone(transaction);
    const fileStore = transaction.objectStore(FILE_STORE);
    const range = IDBKeyRange.bound(`${storageKey}\u0000`, `${storageKey}\u0000\uffff`);
    const request = fileStore.getAllKeys(range);
    request.onsuccess = () => {
      request.result.forEach(key => fileStore.delete(key));
      transaction.objectStore(METADATA_STORE).delete(storageKey);
    };
    request.onerror = () => {
      try { transaction.abort(); } catch { /* The transaction may already be inactive. */ }
    };
    await done;
  } finally {
    database.close();
  }
}

/** Clear every abandoned guest draft for a QR station after its saved session
 * is missing, revoked or expired. This prevents cross-customer draft reuse. */
export async function clearCustomerPendingSelectionsForStation(publicCode: string): Promise<void> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction([FILE_STORE, METADATA_STORE], "readwrite");
    const done = transactionDone(transaction);
    const prefix = `${publicCode}:`;
    const range = IDBKeyRange.bound(prefix, `${prefix}\\uffff`);
    const fileStore = transaction.objectStore(FILE_STORE);
    const fileKeysRequest = fileStore.getAllKeys(range);
    fileKeysRequest.onsuccess = () => fileKeysRequest.result.forEach(key => fileStore.delete(key));
    fileKeysRequest.onerror = () => { try { transaction.abort(); } catch { /* Already inactive. */ } };
    const metadataStore = transaction.objectStore(METADATA_STORE);
    const metadataKeysRequest = metadataStore.getAllKeys(range);
    metadataKeysRequest.onsuccess = () => metadataKeysRequest.result.forEach(key => metadataStore.delete(key));
    metadataKeysRequest.onerror = () => { try { transaction.abort(); } catch { /* Already inactive. */ } };
    await done;
  } finally {
    database.close();
  }
}
