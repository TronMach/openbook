// Offline capture: drafts are made on this device (with or without a connection), kept encrypted in IndexedDB, and
// sent to /api/capture/sync when online. The server enters each through the same service as the online forms and
// answers with what became of it. Nothing here posts to the books directly, edits existing records or numbers
// documents. The checks below mirror the server's so most drafts go through, but the server's rules decide.
(() => {
    'use strict';

    const DB = 'openbook-capture';
    const MAX_PHOTO = 7 * 1024 * 1024;
    const $ = id => document.getElementById(id);

    // ---- Storage: IndexedDB with an AES-GCM key that can't be read out of the browser ----

    function open() {
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(DB, 1);
            request.onupgradeneeded = () => {
                request.result.createObjectStore('meta');
                request.result.createObjectStore('drafts', { keyPath: 'id' });
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    async function tx(store, mode, work) {
        const db = await open();
        return new Promise((resolve, reject) => {
            const t = db.transaction(store, mode);
            let result;
            Promise.resolve(work(t.objectStore(store), r => { result = r; })).catch(reject);
            t.oncomplete = () => { db.close(); resolve(result); };
            t.onerror = () => { db.close(); reject(t.error); };
        });
    }

    const req = r => new Promise((resolve, reject) => { r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error); });
    const getMeta = key => tx('meta', 'readonly', async (s, done) => done(await req(s.get(key))));
    const setMeta = (key, value) => tx('meta', 'readwrite', s => s.put(value, key));

    async function key() {
        let k = await getMeta('key');
        if (!k) {
            k = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
            await setMeta('key', k);
        }
        return k;
    }

    async function seal(value) {
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, await key(), new TextEncoder().encode(JSON.stringify(value)));
        return { iv, data };
    }

    async function unseal(box) {
        const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: box.iv }, await key(), box.data);
        return JSON.parse(new TextDecoder().decode(plain));
    }

    async function saveDraft(draft) {
        const { payload, ...rest } = draft;
        // Encrypt first: an IndexedDB transaction closes if it waits on anything else.
        const box = await seal(payload);
        await tx('drafts', 'readwrite', s => s.put({ ...rest, box }));
    }

    async function loadDrafts() {
        const rows = await tx('drafts', 'readonly', async (s, done) => done(await req(s.getAll())));
        return Promise.all(rows.map(async r => ({ ...r, payload: await unseal(r.box) })));
    }

    const removeDraft = id => tx('drafts', 'readwrite', s => s.delete(id));

    // Empties both stores first (that works even while another tab holds the database open, which holds up deleting
    // it), then deletes the database, key included.
    async function wipe() {
        await tx('drafts', 'readwrite', s => s.clear());
        await tx('meta', 'readwrite', s => s.clear());
        await new Promise(resolve => { const r = indexedDB.deleteDatabase(DB); r.onsuccess = r.onerror = r.onblocked = () => resolve(); });
    }

    // ---- Lists the forms choose from ----

    let lists = null;

    async function refreshLists() {
        const response = await fetch('/api/capture/lists', { credentials: 'same-origin' });
        if (response.status === 401) throw Object.assign(new Error('signed-out'), { signedOut: true });
        if (!response.ok) throw new Error('lists ' + response.status);
        lists = await response.json();
        await setMeta('lists', await seal(lists));
    }

    async function loadLists() {
        const box = await getMeta('lists');
        lists = box ? await unseal(box) : null;
    }

    function fillSelects() {
        document.querySelectorAll('select[data-list]').forEach(select => {
            const items = (lists && lists[select.dataset.list]) || [];
            const current = select.value;
            select.replaceChildren();
            // No lists yet (never online) is different from an empty list (nothing set up in OpenBook).
            const empty = !lists ? 'Nothing yet: open this page online once'
                : select.dataset.list === 'workers' ? 'No people yet: add them under Time › People and rates' : 'None set up in OpenBook';
            if (select.hasAttribute('data-optional') || items.length === 0) select.add(new Option(items.length || select.hasAttribute('data-optional') ? 'None' : empty, ''));
            items.forEach(i => select.add(new Option(i.name, i.id)));
            if (current) select.value = current;
        });
        const worker = document.querySelector('#form-time [name=workerId]');
        if (lists && lists.myWorkerId && !worker.value) worker.value = lists.myWorkerId;
        const rate = document.querySelector('#form-mileage [name=billRate]');
        if (lists && lists.mileageRate && !rate.value) rate.value = lists.mileageRate;
    }

    // ---- Checks (the same rules the server applies) ----

    const decimals = (text, places) => new RegExp(`^\\d+(\\.\\d{1,${places}})?$`).test(String(text ?? '').trim());

    function check(kind, p) {
        switch (kind) {
            case 'Time':
                if (!p.workerId) return 'Choose who did the work.';
                if (!decimals(p.hours, 2) || +p.hours <= 0 || +p.hours > 24) return 'Hours are above 0 and at most 24, with at most 2 decimals.';
                if (p.billable && (!p.customerId || !p.itemId)) return 'Billable time needs a customer and a service.';
                return null;
            case 'Mileage':
                if (!decimals(p.miles, 1) || +p.miles <= 0) return 'Miles are above 0, with at most one decimal.';
                if (!p.purpose.trim()) return 'Enter the business purpose of the trip.';
                if (p.billable && !p.customerId) return 'Billable mileage needs a customer.';
                if (p.billRate && !decimals(p.billRate, 4)) return 'The rate per mile has at most 4 decimals.';
                return null;
            case 'Expense':
                if (!decimals(p.amount, 2) || +p.amount <= 0) return 'Enter the amount, above zero, to the cent.';
                if (!p.accountId || !p.categoryId) return 'Choose the account it was paid from and its category.';
                return null;
            default:
                return p.photo ? null : 'Take or choose the receipt photo.';
        }
    }

    function readPhoto(input) {
        const file = input && input.files && input.files[0];
        if (!file) return Promise.resolve(null);
        if (file.size > MAX_PHOTO) return Promise.reject(new Error('The photo is larger than 7 MB.'));
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve({ fileName: file.name || 'receipt.jpg', base64: String(reader.result).split(',')[1] });
            reader.onerror = () => reject(reader.error);
            reader.readAsDataURL(file);
        });
    }

    async function payloadOf(form) {
        const data = new FormData(form);
        const p = {};
        for (const el of form.elements) {
            if (!el.name || el.type === 'file') continue;
            p[el.name] = el.type === 'checkbox' ? el.checked : (data.get(el.name) || '').toString();
        }
        p.photo = await readPhoto(form.elements.photo);
        // The server takes numbers and nulls, not empty text.
        ['hours', 'miles', 'amount', 'billRate'].forEach(n => { if (n in p) p[n] = p[n] === '' ? null : p[n]; });
        ['customerId', 'itemId', 'vendorId', 'workerId', 'payee', 'memo', 'notes'].forEach(n => { if (p[n] === '') p[n] = null; });
        return p;
    }

    function wire(p) {
        const out = { ...p };
        ['hours', 'miles', 'amount', 'billRate'].forEach(n => { if (out[n] != null) out[n] = Number(out[n]); });
        if (!out.photo) delete out.photo;
        return out;
    }

    // ---- Sync ----

    let syncing = false;

    async function deviceId() {
        let id = await getMeta('deviceId');
        if (!id) {
            const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || 'browser';
            const response = await fetch('/api/capture/devices', {
                method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: `Capture on ${platform}` }),
            });
            if (response.status === 401) throw Object.assign(new Error('signed-out'), { signedOut: true });
            if (!response.ok) throw new Error('device ' + response.status);
            id = (await response.json()).id;
            await setMeta('deviceId', id);
        }
        return id;
    }

    async function revoked(device) {
        try {
            const response = await fetch('/capture/device-status/' + encodeURIComponent(device));
            return response.ok && (await response.json()).revoked === true;
        } catch (e) {
            return false;
        }
    }

    async function wiped() {
        await wipe();
        lists = null;
        show('This device\'s access was removed in OpenBook, so everything it held has been cleared. Sign in again to keep capturing.', true);
    }

    async function sync() {
        if (syncing || !navigator.onLine) return render();
        syncing = true;
        try {
            const device = await deviceId();
            try {
                await refreshLists();
            } catch (e) {
                if (e.signedOut && await revoked(device)) return wiped();
                throw e;
            }
            fillSelects();
            const waiting = (await loadDrafts()).filter(d => d.status === 'waiting');
            if (waiting.length) {
                const response = await fetch('/api/capture/sync', {
                    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ deviceId: device, drafts: waiting.map(d => ({ id: d.id, kind: d.kind, capturedAt: d.capturedAt, payload: wire(d.payload) })) }),
                });
                if (response.status === 410) return wiped();
                if (response.status === 401) {
                    // Signed out: perhaps on purpose, because this device was lost. Ask (without signing in) whether to wipe.
                    if (await revoked(device)) return wiped();
                    throw Object.assign(new Error('signed-out'), { signedOut: true });
                }
                if (!response.ok) throw new Error((await response.json().catch(() => ({}))).message || 'sync ' + response.status);
                for (const outcome of await response.json()) {
                    const draft = waiting.find(d => d.id === outcome.id);
                    if (!draft) continue;
                    if (outcome.status === 'Entered' || outcome.status === 'Dismissed') await removeDraft(draft.id);
                    else await saveDraft({ ...draft, status: 'attention', error: outcome.error });
                }
                show(`Sent ${waiting.length} ${waiting.length === 1 ? 'draft' : 'drafts'}.`);
            }
        } catch (e) {
            if (e.signedOut) show('Sign in to send your drafts. They\'re safe on this device meanwhile.', true, '/login?returnUrl=%2Fcapture.html');
            else if (navigator.onLine) show(`Couldn't reach OpenBook (${e.message}). Your drafts are safe and will be sent later.`, true);
        } finally {
            syncing = false;
            render();
        }
    }

    // ---- Screen ----

    function show(text, problem, link) {
        const box = $('message');
        box.hidden = false;
        box.className = 'alert ' + (problem ? 'error' : 'notice');
        box.textContent = text + ' ';
        if (link) {
            const a = document.createElement('a');
            a.href = link;
            a.textContent = 'Sign in';
            box.append(a);
        }
    }

    function describe(d) {
        const p = d.payload;
        switch (d.kind) {
            case 'Time': return `Time · ${p.date} · ${p.hours} h${p.notes ? ' · ' + p.notes : ''}`;
            case 'Mileage': return `Mileage · ${p.date} · ${p.miles} mi · ${p.purpose}`;
            case 'Expense': return `Expense · ${p.date} · ${p.amount}${p.payee ? ' · ' + p.payee : ''}${p.photo ? ' · photo' : ''}`;
            default: return `Receipt photo · ${new Date(d.capturedAt).toLocaleDateString()}`;
        }
    }

    async function render() {
        $('online').textContent = navigator.onLine ? 'Online' : 'Offline: drafts wait here';
        const drafts = await loadDrafts().catch(() => []);
        const waiting = drafts.filter(d => d.status === 'waiting').length;
        $('waiting').textContent = waiting ? `${waiting} waiting to send` : 'Nothing waiting';
        $('sync').disabled = !navigator.onLine || waiting === 0 || syncing;
        const list = $('drafts');
        list.replaceChildren(...drafts.sort((a, b) => a.capturedAt.localeCompare(b.capturedAt)).map(d => {
            const li = document.createElement('li');
            li.textContent = describe(d) + (d.status === 'attention' ? ` · needs attention in OpenBook: ${d.error}` : ' · waiting');
            if (d.status === 'attention') li.className = 'problem';
            return li;
        }));
        if (!drafts.length) list.replaceChildren(Object.assign(document.createElement('li'), { textContent: 'No drafts on this device.', className: 'muted' }));
    }

    function pick(tab) {
        document.querySelectorAll('[data-tab]').forEach(b => {
            b.setAttribute('aria-selected', String(b.dataset.tab === tab));
            b.classList.toggle('secondary', b.dataset.tab !== tab);
        });
        document.querySelectorAll('form[data-kind]').forEach(f => { f.hidden = f.id !== 'form-' + tab; });
        try { localStorage.setItem('ob-capture-tab', tab); } catch (e) { /* private mode */ }
    }

    async function start() {
        document.querySelectorAll('[data-tab]').forEach(b => b.addEventListener('click', () => pick(b.dataset.tab)));
        let tab = 'time';
        try { tab = localStorage.getItem('ob-capture-tab') || tab; } catch (e) { /* private mode */ }
        pick(tab);
        const today = new Date();
        const iso = new Date(today.getTime() - today.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
        document.querySelectorAll('input[type=date]').forEach(i => { i.value = iso; });

        document.querySelectorAll('form[data-kind]').forEach(form => form.addEventListener('submit', async e => {
            e.preventDefault();
            try {
                const payload = await payloadOf(form);
                const problem = check(form.dataset.kind, payload);
                if (problem) return show(problem, true);
                await saveDraft({ id: crypto.randomUUID(), kind: form.dataset.kind, capturedAt: new Date().toISOString(), status: 'waiting', payload });
                form.querySelectorAll('input:not([type=date]):not([type=checkbox]):not([name=billRate])').forEach(i => { i.value = ''; });
                show(navigator.onLine ? 'Saved. Sending…' : 'Saved on this device. It will be sent when you\'re back online.');
                await render();
                sync();
            } catch (err) {
                show(err.message, true);
            }
        }));

        $('sync').addEventListener('click', sync);
        window.addEventListener('online', sync);
        window.addEventListener('offline', render);
        setInterval(() => { if (navigator.onLine) sync(); }, 60000);

        if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => { });

        await loadLists().catch(() => { lists = null; });
        fillSelects();
        await render();
        if (!lists && !navigator.onLine) show('Open this page once while online so it can download your customers, accounts and the rest.', true);
        sync();
    }

    start();
})();
