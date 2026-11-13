export type CustomerPendingFileMetadata = {
  webkitRelativePath?: string;
  batchGroup?: string;
  printKind?: "DOCUMENT" | "PHOTO" | "ID_DOCUMENT";
  photoSettings?: { sizeCode: string; photoCount: number; photosPerPage?: number };
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
};

export type CustomerPendingSelection = {
  files: File[];
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

function metadataForFile(file: File): CustomerPendingFileMetadata {
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
    ...(tagged.__smartPrintPhotoSettings ? { photoSettings: tagged.__smartPrintPhotoSettings } : {})
  };
}

/**
 * Persist the selected files in IndexedDB, not localStorage. This is a
 * device-local recovery copy only; it is never sent to the SmartPrint API.
 * Files are committed together so an interrupted save leaves the previous
 * selection intact instead of a partially replaced one.
 */
export async function saveCustomerPendingFiles(storageKey: string, files: File[]): Promise<void> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction(FILE_STORE, "readwrite");
    const store = transaction.objectStore(FILE_STORE);
    const range = IDBKeyRange.bound(`${storageKey}\u0000`, `${storageKey}\u0000\uffff`);
    const keys = await new Promise<IDBValidKey[]>((resolve, reject) => {
      const request = store.getAllKeys(range);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error("The previous file selection could not be read."));
    });
    keys.forEach(key => store.delete(key));
    files.forEach((file, index) => {
      const record: StoredFile = {
        storageKey,
        index,
        name: file.name,
        type: file.type,
        size: file.size,
        lastModified: file.lastModified,
        blob: file,
        fileMetadata: metadataForFile(file)
      };
      store.put(record, fileRecordKey(storageKey, index));
    });
    await transactionDone(transaction);
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
    transaction.objectStore(METADATA_STORE).put({
      ...metadata,
      version: 1,
      updatedAt: Date.now()
    } satisfies StoredMetadata, storageKey);
    await transactionDone(transaction);
  } finally {
    database.close();
  }
}

export async function loadCustomerPendingSelection(storageKey: string): Promise<CustomerPendingSelection | null> {
  const database = await openDatabase();
  try {
    const transaction = database.transaction([FILE_STORE, METADATA_STORE], "readonly");
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
    await transactionDone(transaction);
    if (!storedMetadata || storedMetadata.version !== 1 || records.length === 0) return null;

    records.sort((left, right) => left.index - right.index);
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
      metadata: {
        selectedFileIndex: Math.min(Math.max(0, storedMetadata.selectedFileIndex || 0), Math.max(0, files.length - 1)),
        batchName: typeof storedMetadata.batchName === "string" ? storedMetadata.batchName : "",
        fileGroupNames: storedMetadata.fileGroupNames ?? {},
        groupSettings: storedMetadata.groupSettings ?? {},
        fileSettings: storedMetadata.fileSettings ?? {},
        photoSettingsByFileFingerprint: storedMetadata.photoSettingsByFileFingerprint ?? {},
        privatePrinting: Boolean(storedMetadata.privatePrinting),
        privacyChoiceManuallySelected: Boolean(storedMetadata.privacyChoiceManuallySelected),
        uploadMode: storedMetadata.uploadMode === "CLOUD" ? "CLOUD" : "LOCAL",
        uploadModeManuallySelected: Boolean(storedMetadata.uploadModeManuallySelected)
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
    const fileStore = transaction.objectStore(FILE_STORE);
    const range = IDBKeyRange.bound(`${storageKey}\u0000`, `${storageKey}\u0000\uffff`);
    const keys = await new Promise<IDBValidKey[]>((resolve, reject) => {
      const request = fileStore.getAllKeys(range);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
    keys.forEach(key => fileStore.delete(key));
    transaction.objectStore(METADATA_STORE).delete(storageKey);
    await transactionDone(transaction);
  } finally {
    database.close();
  }
}
