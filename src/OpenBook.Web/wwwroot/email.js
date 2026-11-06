// Email forms: the live preview beside them (form[data-email-preview] posts its fields, with the antiforgery token, to
// that address and shows what comes back), the formatting buttons and fill-ins of the message box, the Attachment tab,
// "Add Cc or Bcc" and "Send a test to me". Everything works by delegation, so pages loaded by enhanced navigation need nothing.
(() => {
    const timers = new WeakMap();
    const tokens = new WeakMap();

    const paneFor = form => form.closest('.email-editor')?.querySelector('[data-email-pane]');

    function status(pane, text, error) {
        const line = pane?.querySelector('[data-preview-status]');
        if (!line) return;
        line.textContent = text || '';
        line.classList.toggle('error', !!error);
    }

    // Some pages name their fields differently (the reminder settings); data-email-subject/body say which.
    function fields(form) {
        const data = new FormData(form);
        for (const [attr, name] of [['emailSubject', 'subject'], ['emailBody', 'body']]) {
            const field = form.dataset[attr];
            if (field) data.set(name, data.get(field) ?? '');
        }
        return data;
    }

    // Writes the message into the frame underneath and brings it to the front once it has drawn, so it never flashes blank.
    function showMessage(pane, html) {
        const box = pane.querySelector('[data-email-view="message"]');
        if (!box) return;
        const current = box.querySelector('iframe.current') || box.querySelector('iframe');
        let next = [...box.querySelectorAll('iframe')].find(f => f !== current);
        if (!next) {
            next = document.createElement('iframe');
            next.setAttribute('sandbox', '');
            next.title = current.title;
            box.appendChild(next);
        }

        const token = (tokens.get(pane) || 0) + 1;
        tokens.set(pane, token);
        const swap = () => {
            if (tokens.get(pane) !== token || next.classList.contains('current')) return;
            next.classList.add('current');
            current.classList.remove('current');
        };
        next.addEventListener('load', () => setTimeout(swap, 50), { once: true });
        setTimeout(swap, 2000);
        next.srcdoc = html;
    }

    function setHead(pane, name, value) {
        const cell = pane.querySelector(`[data-email-head="${name}"]`);
        if (!cell) return;
        if (name === 'subject') cell.innerHTML = '<strong></strong>', cell.firstChild.textContent = value || '';
        else cell.textContent = value || (name === 'to' ? '(no address yet)' : '');
        const label = pane.querySelector(`[data-email-head-label="${name}"]`);
        if (label) {
            label.hidden = !value;
            cell.hidden = !value;
        }
    }

    function render(pane, p) {
        setHead(pane, 'from', p.from);
        setHead(pane, 'to', p.to);
        setHead(pane, 'cc', p.cc);
        setHead(pane, 'bcc', p.bcc);
        setHead(pane, 'subject', p.subject);
        const row = pane.querySelector('[data-email-attachment-row]');
        if (row) {
            row.hidden = !p.attachment;
            pane.querySelector('[data-email-attachment]').textContent = p.attachment || '';
        }
        const tab = pane.querySelector('.segmented [data-email-tab="attachment"]');
        if (tab) {
            tab.disabled = !p.attachment;
            if (!p.attachment) select(pane, 'message');
        }
        const notes = pane.querySelector('[data-email-notes]');
        if (notes) {
            notes.replaceChildren(...(p.notes || []).map(n => {
                const li = document.createElement('li');
                li.textContent = n;
                return li;
            }));
        }
        showMessage(pane, p.html);
    }

    // Outside the API a signed-out post is sent to the sign-in page, which fetch follows to an ordinary 200.
    function signedOut(response) {
        return response.redirected || !(response.headers.get('content-type') || '').includes('json');
    }

    async function refresh(form) {
        const pane = paneFor(form);
        if (!pane) return;
        status(pane, 'Updating the preview…');
        try {
            const response = await fetch(form.dataset.emailPreview, { method: 'POST', body: fields(form), credentials: 'same-origin' });
            if (signedOut(response)) {
                status(pane, 'The preview could not be updated: you\'ve been signed out. Your message is still on the page.', true);
                return;
            }
            const body = await response.json().catch(() => ({}));
            if (!response.ok) {
                status(pane, `Not shown yet: ${body.error || 'the preview could not be made.'}`, true);
                return;
            }
            render(pane, body);
            status(pane, '');
        } catch {
            status(pane, 'The preview could not be updated (offline or signed out). Your message is still on the page.', true);
        }
    }

    function schedule(form) {
        clearTimeout(timers.get(form));
        timers.set(form, setTimeout(() => refresh(form), 450));
    }

    // ---- The attachment ----

    function pdfUrl(pane) {
        const view = pane.querySelector('[data-email-view="attachment"]');
        if (!view) return null;
        const url = new URL(view.dataset.pdf, location.href);
        const design = pane.closest('.email-editor')?.querySelector('[data-email-design]');
        if (design && design.value) url.searchParams.set('design', design.value);
        return url.toString();
    }

    async function loadPdf(pane) {
        const view = pane.querySelector('[data-email-view="attachment"]');
        const url = pdfUrl(pane);
        if (!view || !url || view.dataset.loaded === url) return;
        view.dataset.loaded = url;
        const frame = view.querySelector('iframe');
        try {
            const response = await fetch(url, { credentials: 'same-origin' });
            if (!response.ok) throw new Error();
            const blob = await response.blob();
            const size = pane.querySelector('[data-email-size]');
            if (size) size.textContent = blob.size >= 1048576 ? `${(blob.size / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(blob.size / 1024))} KB`;
            const old = frame.dataset.blob;
            frame.dataset.blob = URL.createObjectURL(blob);
            frame.src = `${frame.dataset.blob}#navpanes=0&view=FitH`;
            if (old) setTimeout(() => URL.revokeObjectURL(old), 10000);
        } catch {
            delete view.dataset.loaded;
            frame.removeAttribute('src');
            status(pane, 'The attachment could not be shown.', true);
        }
    }

    function select(pane, which) {
        pane.querySelectorAll('.segmented [data-email-tab]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.emailTab === which)));
        pane.querySelectorAll('[data-email-view]').forEach(v => { v.hidden = v.dataset.emailView !== which; });
        if (which === 'attachment') loadPdf(pane);
    }

    // ---- The message box ----

    function wrap(area, before, after, placeholder) {
        const { selectionStart: start, selectionEnd: end, value } = area;
        const chosen = value.slice(start, end) || placeholder;
        area.setRangeText(before + chosen + after, start, end, 'end');
        if (start === end) area.setSelectionRange(start + before.length, start + before.length + chosen.length);
        area.focus();
        area.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function prefixLines(area, numbered) {
        const { value } = area;
        const start = value.lastIndexOf('\n', area.selectionStart - 1) + 1;
        let end = value.indexOf('\n', area.selectionEnd);
        if (end < 0) end = value.length;
        const lines = value.slice(start, end).split('\n').map((line, i) => (numbered ? `${i + 1}. ` : '- ') + line.replace(/^(\d+\.|-)\s+/, ''));
        area.setRangeText(lines.join('\n'), start, end, 'end');
        area.focus();
        area.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function format(area, kind) {
        if (kind === 'bold') wrap(area, '**', '**', 'bold text');
        else if (kind === 'italic') wrap(area, '_', '_', 'text');
        else if (kind === 'bullets') prefixLines(area, false);
        else if (kind === 'numbers') prefixLines(area, true);
        else if (kind === 'link') {
            const url = prompt('Link to (https://…)', 'https://');
            if (url && /^(https?:\/\/|mailto:)/i.test(url.trim())) wrap(area, '[', `](${url.trim()})`, 'link text');
        }
    }

    // The message box a toolbar or fill-in belongs to, else the last box or subject the person typed in.
    let lastField = null;
    const boxOf = target => target.closest('[data-email-body]')?.querySelector('textarea');

    document.addEventListener('focusin', e => {
        if (e.target.matches?.('.email-form input[type=text], .email-form input:not([type]), .email-form textarea, form[data-email-preview] textarea')) lastField = e.target;
    });

    document.addEventListener('click', e => {
        const target = e.target instanceof Element ? e.target : null;
        if (!target) return;
        const md = target.closest('[data-md]');
        if (md) {
            const area = boxOf(md);
            if (area) format(area, md.dataset.md);
            return;
        }
        const insert = target.closest('[data-insert]');
        if (insert) {
            const box = boxOf(insert);
            const field = lastField && lastField.form === box?.form ? lastField : box;
            if (field) {
                field.setRangeText(insert.dataset.insert, field.selectionStart, field.selectionEnd, 'end');
                field.focus();
                field.dispatchEvent(new Event('input', { bubbles: true }));
            }
            return;
        }
        const tab = target.closest('[data-email-tab]');
        if (tab) {
            const pane = tab.closest('[data-email-pane]');
            if (pane && !tab.disabled) select(pane, tab.dataset.emailTab);
            return;
        }
        const copies = target.closest('[data-email-copies]');
        if (copies) {
            const fieldsRow = copies.closest('form')?.querySelector('[data-email-copy-fields]');
            if (fieldsRow) {
                fieldsRow.hidden = false;
                copies.hidden = true;
                fieldsRow.querySelector('input')?.focus();
            }
            return;
        }
        const test = target.closest('[data-email-test-button]');
        if (test) sendTest(test.closest('form'), test);
    });

    document.addEventListener('keydown', e => {
        if (!(e.ctrlKey || e.metaKey) || e.altKey || !(e.target instanceof HTMLTextAreaElement) || !e.target.closest('[data-email-body]')) return;
        const kind = { b: 'bold', i: 'italic', k: 'link' }[e.key.toLowerCase()];
        if (!kind) return;
        e.preventDefault();
        format(e.target, kind);
    });

    async function sendTest(form, button) {
        if (!form?.dataset.emailTest) return;
        const pane = paneFor(form);
        button.disabled = true;
        status(pane, 'Sending a test copy…');
        try {
            const response = await fetch(form.dataset.emailTest, { method: 'POST', body: fields(form), credentials: 'same-origin' });
            if (signedOut(response)) {
                status(pane, "The test wasn't sent: you've been signed out.", true);
                return;
            }
            const body = await response.json().catch(() => ({}));
            status(pane, response.ok ? body.message : `The test wasn't sent: ${body.error || 'something went wrong.'}`, !response.ok);
        } catch {
            status(pane, "The test wasn't sent (offline or signed out).", true);
        } finally {
            button.disabled = false;
        }
    }

    const editorOf = target => target instanceof Element ? target.closest('form[data-email-preview]') : null;

    document.addEventListener('input', e => {
        const form = editorOf(e.target);
        if (form) schedule(form);
    });

    document.addEventListener('change', e => {
        const form = editorOf(e.target);
        if (!form) return;
        schedule(form);
        if (e.target.matches('[data-email-design]')) {
            const pane = paneFor(form);
            if (pane && !pane.querySelector('[data-email-view="attachment"]')?.hidden) loadPdf(pane);
            else pane?.querySelector('[data-email-view="attachment"]')?.removeAttribute('data-loaded');
        }
    });

})();
