// Chunked file upload with progress reporting, resumable via IndexedDB, and cancel support
(() => {
    const DB_NAME = 'bigpdf-uploads';
    const STORE_NAME = 'uploads';
    const uploadControllers = {}; // uploadId -> { xhrs: [], canceled: bool }

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = function (ev) {
                const db = ev.target.result;
                if (!db.objectStoreNames.contains(STORE_NAME)) db.createObjectStore(STORE_NAME);
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror = function () { reject(req.error); };
        });
    }

    async function idbGet(key) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const store = tx.objectStore(STORE_NAME);
            const r = store.get(key);
            r.onsuccess = () => resolve(r.result);
            r.onerror = () => reject(r.error);
        });
    }

    async function idbSet(key, value) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            const store = tx.objectStore(STORE_NAME);
            const r = store.put(value, key);
            r.onsuccess = () => resolve();
            r.onerror = () => reject(r.error);
        });
    }

    async function idbDelete(key) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            const store = tx.objectStore(STORE_NAME);
            const r = store.delete(key);
            r.onsuccess = () => resolve();
            r.onerror = () => reject(r.error);
        });
    }

    async function getOrCreateUploadId(fingerprint, fileName) {
        try {
            const existing = await idbGet(fingerprint);
            if (existing) return existing;
        } catch (ex) {
            // ignore idb errors
        }

        // request server-issued uploadId
        const resp = await fetch('/api/uploads/create', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fingerprint, fileName })
        });
        if (!resp.ok) throw new Error('Failed to create upload id');
        const obj = await resp.json();
        const uploadId = obj.uploadId;
        try { await idbSet(fingerprint, uploadId); } catch (ex) { }
        return uploadId;
    }

    async function startUploadsFromElement(element, dotNetRef, options) {
        if (!element || !element.files) return;
        options = options || {};
        const chunkSize = options.chunkSize || 256 * 1024;
        const concurrency = options.concurrency || 3;
        const maxRetries = options.maxRetries || 3;

        const tasks = [];
        for (let i = 0; i < element.files.length; i++) {
            const file = element.files[i];
            tasks.push(startSingleUpload(file, dotNetRef, { chunkSize, concurrency, maxRetries }));
        }

        await Promise.allSettled(tasks);
    }

    async function startSingleUpload(file, dotNetRef, opts) {
        const fingerprint = `${file.name}_${file.size}_${file.lastModified}`;
        const uploadId = await getOrCreateUploadId(fingerprint, file.name);

        // inform Blazor so it can store mapping and show UI
        if (dotNetRef) await dotNetRef.invokeMethodAsync('UploadStarted', file.name, uploadId);

        // perform upload
        await uploadFileWithParallelChunks(file, uploadId, dotNetRef, opts);

        // cleanup stored mapping on success is done by UploadCompleted callback from server action
    }

    async function uploadFileWithParallelChunks(file, uploadId, dotNetRef, opts) {
        const chunkSize = opts.chunkSize;
        const concurrency = opts.concurrency;
        const maxRetries = opts.maxRetries;

        const totalChunks = Math.max(1, Math.ceil(file.size / chunkSize));

        // get already uploaded chunks
        let received = [];
        try {
            const statusResp = await fetch(`/api/uploads/chunk/status?uploadId=${encodeURIComponent(uploadId)}`);
            if (statusResp.ok) {
                const obj = await statusResp.json();
                if (Array.isArray(obj.uploadedChunks)) received = obj.uploadedChunks.map(x => parseInt(x, 10));
            }
        } catch (ex) { }

        const toUpload = [];
        for (let i = 0; i < totalChunks; i++) if (!received.includes(i)) toUpload.push(i);

        let uploadedBytes = received.reduce((acc, idx) => acc + Math.min(chunkSize, file.size - idx * chunkSize), 0);

        uploadControllers[uploadId] = { xhrs: [], canceled: false };

        async function uploadChunkWithRetry(chunkIndex) {
            const start = chunkIndex * chunkSize;
            const end = Math.min(start + chunkSize, file.size);
            const blob = file.slice(start, end);

            for (let attempt = 0; attempt <= maxRetries; attempt++) {
                if (uploadControllers[uploadId].canceled) throw new Error('Upload canceled');
                try {
                    await sendChunkXHRWithController(blob, file.name, uploadId, chunkIndex, totalChunks, (loaded) => {
                        const percent = ((uploadedBytes + loaded) / file.size) * 100;
                        if (dotNetRef) dotNetRef.invokeMethodAsync('ReportProgress', file.name, percent);
                    }, uploadId);

                    uploadedBytes += blob.size;
                    if (dotNetRef) dotNetRef.invokeMethodAsync('ReportProgress', file.name, (uploadedBytes / file.size) * 100);
                    return;
                } catch (err) {
                    if (attempt < maxRetries) await new Promise(r => setTimeout(r, Math.pow(2, attempt) * 500));
                    else throw err;
                }
            }
        }

        // worker pool
        const pool = [];
        for (let i = 0; i < concurrency; i++) {
            pool.push((async function worker() {
                while (toUpload.length) {
                    const idx = toUpload.shift();
                    if (idx === undefined) break;
                    await uploadChunkWithRetry(idx);
                }
            })());
        }

        try {
            await Promise.all(pool);

            // finalize
            const resp = await fetch(`/api/uploads/chunk/complete?uploadId=${encodeURIComponent(uploadId)}&fileName=${encodeURIComponent(file.name)}&totalChunks=${totalChunks}`, { method: 'POST' });
            if (!resp.ok) throw new Error('Finalize failed: ' + resp.status);
            const json = await resp.json();
            if (dotNetRef) await dotNetRef.invokeMethodAsync('UploadCompleted', file.name, json.path || '', true, null);

            // remove mapping from idb
            try { await idbDelete(`${file.name}_${file.size}_${file.lastModified}`); } catch (ex) { }
        } catch (err) {
            const msg = err && err.message ? err.message : String(err);
            if (dotNetRef) await dotNetRef.invokeMethodAsync('UploadCompleted', file.name, null, false, msg);
        } finally {
            // cleanup controllers
            if (uploadControllers[uploadId]) uploadControllers[uploadId].xhrs = [];
        }
    }

    function sendChunkXHRWithController(blob, fileName, uploadId, chunkIndex, totalChunks, onProgress, uploadIdKey) {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            xhr.open('POST', '/api/uploads/chunk', true);
            xhr.setRequestHeader('X-Upload-Id', uploadId);
            xhr.setRequestHeader('X-File-Name', fileName);
            xhr.setRequestHeader('X-Chunk-Index', String(chunkIndex));
            xhr.setRequestHeader('X-Total-Chunks', String(totalChunks));

            xhr.upload.onprogress = function (e) { if (e.lengthComputable && onProgress) onProgress(e.loaded); };

            xhr.onreadystatechange = function () {
                if (xhr.readyState === 4) {
                    if (xhr.status >= 200 && xhr.status < 300) {
                        resolve(xhr.responseText ? JSON.parse(xhr.responseText) : null);
                    } else {
                        reject(new Error('Chunk upload failed with status ' + xhr.status));
                    }
                }
            };

            xhr.onerror = function () { reject(new Error('Network error during upload')); };

            // track XHR for cancellation
            if (uploadControllers[uploadIdKey]) uploadControllers[uploadIdKey].xhrs.push(xhr);

            xhr.send(blob);
        });
    }

    // cancel upload by uploadId
    async function cancelUpload(uploadId) {
        const entry = uploadControllers[uploadId];
        if (entry) {
            entry.canceled = true;
            for (const x of entry.xhrs) try { x.abort(); } catch (ex) { }
            entry.xhrs = [];
        }
        // optionally delete server-side partials: skip for now
        return true;
    }

    window.startUploadsFromElement = startUploadsFromElement;
    window.cancelUpload = cancelUpload;
})();
