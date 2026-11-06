// Offline capture: drafts are made on this device (with or without a connection), kept encrypted in IndexedDB, and
// sent to /api/capture/sync when online. The server enters each through the same service as the online forms and
// answers with what became of it. Nothing here posts to the books directly, edits existing records or numbers
// documents. The checks below mirror the server's so most drafts go through, but the server's rules decide.
(() => {
    'use strict';

    const DB = 'openbook-capture';
    // Under the server's 8 MB per draft once base64 has added a third.
    const MAX_PHOTO = 5.5 * 1024 * 1024;
    // Under the server's 30 MB per request; a draft or two more waits for the next request.
    const MAX_SEND = 20 * 1024 * 1024;
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

    async function refreshLists(device) {
        const response = await fetch('/api/capture/lists?device=' + encodeURIComponent(device), { credentials: 'same-origin' });
        if (response.status === 401) throw Object.assign(new Error('signed-out'), { signedOut: true });
        if (response.status === 410) throw Object.assign(new Error('removed'), { removed: true });
        if (response.status === 409) throw Object.assign(new Error('someone-else'), { someoneElse: true });
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
            case 'Invoice':
            case 'Estimate':
                if (!p.customerId) return 'Choose the customer.';
                if (!p.lines.length) return 'Add at least one line with a product or service.';
                for (const l of p.lines) {
                    if (!decimals(l.quantity, 4) || +l.quantity <= 0) return 'Quantities are above zero, with at most 4 decimals.';
                    if (l.rate != null && !decimals(l.rate, 4)) return 'Rates have at most 4 decimals (leave one blank to price it automatically).';
                }
                return null;
            default:
                return p.photo ? null : 'Take or choose the receipt photo.';
        }
    }

    function readPhoto(input) {
        const file = input && input.files && input.files[0];
        if (!file) return Promise.resolve(null);
        if (file.size > MAX_PHOTO) return Promise.reject(new Error('The photo is larger than 5.5 MB.'));
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
        // Invoice and estimate lines: rows without a product or service are left out.
        if (form.querySelector('.sale-line')) {
            p.lines = [...form.querySelectorAll('.sale-line')].map(row => {
                const field = name => row.querySelector(`[data-field=${name}]`).value.trim();
                return { itemId: field('itemId'), quantity: field('quantity'), rate: field('rate') || null, description: field('description') || null };
            }).filter(l => l.itemId);
            delete p.kind;
        }
        // The server takes numbers and nulls, not empty text.
        ['hours', 'miles', 'amount', 'billRate'].forEach(n => { if (n in p) p[n] = p[n] === '' ? null : p[n]; });
        ['customerId', 'itemId', 'vendorId', 'workerId', 'payee', 'memo', 'notes'].forEach(n => { if (p[n] === '') p[n] = null; });
        return p;
    }

    function wire(p) {
        const out = { ...p };
        ['hours', 'miles', 'amount', 'billRate'].forEach(n => { if (out[n] != null) out[n] = Number(out[n]); });
        if (out.lines) out.lines = out.lines.map(l => ({ ...l, quantity: Number(l.quantity), rate: l.rate == null ? null : Number(l.rate) }));
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

    // Requests that fit: at most 200 drafts and MAX_SEND each, in the order captured.
    function batches(drafts) {
        const sends = [];
        let current = null;
        for (const draft of drafts) {
            // One that needed attention is only asked about: the server has it, photo and all.
            const payload = draft.status === 'attention' ? { ...draft.payload, photo: undefined } : draft.payload;
            const wired = JSON.stringify({ id: draft.id, kind: draft.kind, capturedAt: draft.capturedAt, payload: wire(payload) });
            if (!current || current.size + wired.length > MAX_SEND || current.items.length === 200) {
                current = { size: 0, items: [], drafts: [] };
                sends.push(current);
            }
            current.size += wired.length;
            current.items.push(wired);
            current.drafts.push(draft);
        }
        return sends;
    }

    // One tab of this browser sends at a time. The server would enter a draft once anyway; this saves the work.
    function sync() {
        return navigator.locks
            ? navigator.locks.request('openbook-capture-sync', { ifAvailable: true }, lock => (lock ? send() : render()))
            : send();
    }

    async function send() {
        if (syncing || !navigator.onLine) return render();
        syncing = true;
        try {
            let device = await deviceId();
            try {
                await refreshLists(device);
            } catch (e) {
                if (e.removed || e.signedOut && await revoked(device)) return wiped();
                if (!e.someoneElse) throw e;
                // Someone else signed in on this browser: their own device, or a new one the first time.
                const who = await fetch('/api/me', { credentials: 'same-origin' }).then(r => (r.ok ? r.json() : {})).catch(() => ({}));
                await setMeta('deviceId', (who.username && await getMeta('device:' + who.username)) || null);
                device = await deviceId();
                try {
                    await refreshLists(device);
                } catch (again) {
                    if (again.removed) return wiped();
                    throw again;
                }
            }
            await setMeta('device:' + lists.me, device);
            fillSelects();
            // Each person's drafts go in under their own name; anyone else's wait for them to sign in here.
            const mine = (await loadDrafts()).filter(d => !d.user || d.user === lists.me);
            const waiting = mine.filter(d => d.status === 'waiting');

            // Those needing attention are asked about too, so one tried again or dismissed in OpenBook leaves here.
            for (const batch of batches([...waiting, ...mine.filter(d => d.status === 'attention')])) {
                const response = await fetch('/api/capture/sync', {
                    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                    body: `{"deviceId":${JSON.stringify(device)},"drafts":[${batch.items.join(',')}]}`,
                });
                if (response.status === 410) return wiped();
                if (response.status === 401) {
                    // Signed out: perhaps on purpose, because this device was lost. Ask (without signing in) whether to wipe.
                    if (await revoked(device)) return wiped();
                    throw Object.assign(new Error('signed-out'), { signedOut: true });
                }
                if (!response.ok) {
                    const answer = await response.json().catch(() => ({}));
                    throw new Error(answer.message || answer.title || 'sync ' + response.status);
                }
                for (const outcome of await response.json()) {
                    const draft = batch.drafts.find(d => d.id === outcome.id);
                    if (!draft) continue;
                    if (outcome.status === 'Entered' || outcome.status === 'Dismissed') await removeDraft(draft.id);
                    else await saveDraft({ ...draft, status: 'attention', error: outcome.error });
                }
            }
            if (waiting.length) show(`Sent ${waiting.length} ${waiting.length === 1 ? 'draft' : 'drafts'}.`);
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
            case 'Invoice':
            case 'Estimate': {
                const customer = ((lists && lists.customers) || []).find(c => c.id === p.customerId);
                const count = p.lines.length;
                return `${d.kind} · ${p.date}${customer ? ' · ' + customer.name : ''} · ${count} ${count === 1 ? 'line' : 'lines'}`;
            }
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

    function addLine() {
        const rows = $('sale-lines').querySelectorAll('.sale-line');
        const row = rows[0].cloneNode(true);
        row.querySelectorAll('[data-field]').forEach(el => { el.value = el.dataset.field === 'quantity' ? '1' : ''; });
        rows[rows.length - 1].after(row);
    }

    function resetSale(form) {
        form.querySelectorAll('.sale-line').forEach((row, i) => {
            if (i > 0) row.remove();
            else row.querySelectorAll('[data-field]').forEach(el => { el.value = el.dataset.field === 'quantity' ? '1' : ''; });
        });
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
                // The invoice form makes invoices or estimates.
                const kind = form.elements.kind ? form.elements.kind.value : form.dataset.kind;
                const payload = await payloadOf(form);
                const problem = check(kind, payload);
                if (problem) return show(problem, true);
                await saveDraft({ id: crypto.randomUUID(), user: lists && lists.me, kind, capturedAt: new Date().toISOString(), status: 'waiting', payload });
                form.querySelectorAll('input[name]:not([type=date]):not([type=checkbox]):not([name=billRate])').forEach(i => { i.value = ''; });
                if (form.querySelector('.sale-line')) resetSale(form);
                show(navigator.onLine ? 'Saved. Sending…' : 'Saved on this device. It will be sent when you\'re back online.');
                await render();
                sync();
            } catch (err) {
                show(err.message, true);
            }
        }));

        $('add-line').addEventListener('click', addLine);
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
