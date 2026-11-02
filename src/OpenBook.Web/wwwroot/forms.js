// Printed forms: the design editor's live preview (form[data-preview] names the pane it fills), and printing
// ([data-print] buttons and form[data-print-form] open the print dialog for a PDF without leaving the page).
(() => {
    const timers = new WeakMap();
    const states = new WeakMap();

    function readPref(name, fallback) {
        try { return localStorage.getItem(name) ?? fallback; } catch { return fallback; }
    }

    function writePref(name, value) {
        try { localStorage.setItem(name, value); } catch { /* private window: not remembered */ }
    }

    // A pane's state: the PDF it shows (without the viewer settings) and how it fits.
    function stateOf(pane) {
        let state = states.get(pane);
        if (!state) {
            const frame = pane.querySelector('iframe');
            state = { url: frame ? frame.getAttribute('src').split('#')[0] : null, fit: readPref('ob-preview-fit', 'FitH'), token: 0, blob: null };
            states.set(pane, state);
        }
        return state;
    }

    function status(pane, text, error) {
        const line = pane.querySelector('[data-preview-status]');
        if (!line) return;
        line.textContent = text || '';
        line.classList.toggle('error', !!error);
    }

    // Shows a PDF in the pane: it loads in a second frame underneath the first and comes to the front shortly after it
    // has loaded (time for the viewer to draw it), so the page never goes blank in between. The viewer opens without its
    // thumbnails, fitted to the page's width or whole page.
    function show(pane, url) {
        const box = pane.querySelector('.preview-frames');
        if (!box || !url) return;
        const state = stateOf(pane);
        state.url = url;
        const current = box.querySelector('iframe.current') || box.querySelector('iframe');
        let next = [...box.querySelectorAll('iframe')].find(f => f !== current);
        if (!next) {
            next = document.createElement('iframe');
            next.title = current.title;
            box.appendChild(next);
        }

        const token = ++state.token;
        const swap = () => {
            if (state.token !== token || next.classList.contains('current')) return;
            next.classList.add('current');
            current.classList.remove('current');
        };
        next.addEventListener('load', () => setTimeout(swap, 400), { once: true });
        setTimeout(swap, 4000);
        next.src = `${url}#navpanes=0&view=${state.fit}`;
    }

    // Posts the editor's fields (with its antiforgery token) and shows the PDF that comes back; a refusal (a column
    // heading too long, say) is shown above the preview, which keeps the last good page.
    async function refresh(form) {
        const pane = document.querySelector(`[data-preview-pane="${form.dataset.preview}"]`);
        if (!pane) return;
        const data = new FormData(form);
        const choice = pane.querySelector('[data-preview-document]');
        data.set('document', choice ? choice.value : 'Invoice');
        if (form.dataset.logo) data.set('logoFrom', form.dataset.logo);
        status(pane, 'Updating the preview…');
        try {
            const response = await fetch('/settings/forms/preview', { method: 'POST', body: data, credentials: 'same-origin' });
            if (!response.ok) {
                const page = new DOMParser().parseFromString(await response.text(), 'text/html');
                status(pane, `Not shown yet: ${page.body.textContent.trim() || 'the preview could not be made.'}`, true);
                return;
            }

            const url = URL.createObjectURL(await response.blob());
            const state = stateOf(pane);
            const old = state.blob;
            state.blob = url;
            show(pane, url);
            status(pane, '');
            if (old) setTimeout(() => URL.revokeObjectURL(old), 10000);
        } catch {
            status(pane, 'The preview could not be updated (offline or signed out). Your changes are still on the page.', true);
        }
    }

    function schedule(form) {
        clearTimeout(timers.get(form));
        timers.set(form, setTimeout(() => refresh(form), 500));
    }

    function editorFor(target) {
        if (!(target instanceof Element)) return null;
        const form = target.closest('form[data-preview]');
        if (form) return form;
        const pane = target.closest('[data-preview-pane]');
        return pane && target.hasAttribute('data-preview-document')
            ? document.querySelector(`form[data-preview="${pane.dataset.previewPane}"]`)
            : null;
    }

    function setFull(pane, full) {
        pane.classList.toggle('full', full);
        document.documentElement.classList.toggle('preview-full', full);
        const button = pane.querySelector('[data-preview-full]');
        if (button) {
            button.setAttribute('aria-pressed', String(full));
            button.textContent = full ? 'Close full screen' : 'Full screen';
        }
    }

    // Puts the remembered fit and layout on the page's panes.
    function apply() {
        document.querySelectorAll('[data-preview-pane]').forEach(pane => {
            const state = stateOf(pane);
            pane.querySelectorAll('[data-preview-fit]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.previewFit === state.fit)));
            const larger = readPref('ob-preview-larger', 'false') === 'true';
            pane.closest('[data-design-editor]')?.classList.toggle('larger', larger);
            pane.querySelector('[data-preview-larger]')?.setAttribute('aria-pressed', String(larger));
            if (state.fit !== 'FitH') show(pane, state.url);
        });
    }

    document.addEventListener('click', e => {
        const target = e.target instanceof Element ? e.target : null;
        const pane = target?.closest('[data-preview-pane]');
        const fit = target?.closest('[data-preview-fit]');
        if (pane && fit) {
            const state = stateOf(pane);
            state.fit = fit.dataset.previewFit;
            writePref('ob-preview-fit', state.fit);
            pane.querySelectorAll('[data-preview-fit]').forEach(b => b.setAttribute('aria-pressed', String(b === fit)));
            show(pane, state.url);
            return;
        }

        const larger = target?.closest('[data-preview-larger]');
        if (pane && larger) {
            const editor = pane.closest('[data-design-editor]');
            const on = !editor.classList.contains('larger');
            editor.classList.toggle('larger', on);
            writePref('ob-preview-larger', String(on));
            larger.setAttribute('aria-pressed', String(on));
            return;
        }

        if (pane && target.closest('[data-preview-full]')) {
            setFull(pane, !pane.classList.contains('full'));
            return;
        }

        // A link that opens in a named frame (the ready-made designs' Preview) brings the frame into view when it's off
        // screen, as on phones, where the frame sits below the list.
        const link = target?.closest('a[target]');
        if (!link || !link.target || link.target.startsWith('_')) return;
        const frame = document.querySelector(`iframe[name="${CSS.escape(link.target)}"]`);
        if (!frame) return;
        const box = frame.getBoundingClientRect();
        if (box.top > window.innerHeight || box.bottom < 0) frame.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });

    document.addEventListener('keydown', e => {
        if (e.key !== 'Escape') return;
        const full = document.querySelector('[data-preview-pane].full');
        if (full) setFull(full, false);
    });

    document.addEventListener('input', e => { const form = editorFor(e.target); if (form) schedule(form); });
    document.addEventListener('change', e => { const form = editorFor(e.target); if (form) schedule(form); });

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', apply);
    else apply();
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function')
        window.Blazor.addEventListener('enhancedload', apply);
})();

(() => {
    // Loads a PDF into a hidden frame and opens the print dialog; where a browser won't print a frame, the PDF opens in a tab.
    function printPdf(url, after) {
        let frame = document.getElementById('ob-print-frame');
        if (frame) frame.remove();
        frame = document.createElement('iframe');
        frame.id = 'ob-print-frame';
        frame.title = 'Printing';
        frame.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0';
        frame.addEventListener('load', () => {
            setTimeout(() => {
                try {
                    frame.contentWindow.focus();
                    frame.contentWindow.print();
                } catch {
                    window.open(url, '_blank');
                }
                if (after) after();
            }, 300);
        }, { once: true });
        frame.src = url;
        document.body.appendChild(frame);
    }

    document.addEventListener('click', e => {
        const button = e.target instanceof Element ? e.target.closest('[data-print]') : null;
        if (!button) return;
        e.preventDefault();
        printPdf(button.getAttribute('data-print'));
    });

    // Ticking the header box ticks every document in its table.
    document.addEventListener('change', e => {
        if (!(e.target instanceof HTMLInputElement) || !e.target.hasAttribute('data-check-all')) return;
        e.target.closest('form')?.querySelectorAll('input[type=checkbox][name=doc]').forEach(box => { box.checked = e.target.checked; });
    });

    // A print form posts its choices and prints the PDF that comes back; buttons with their own formaction submit as usual.
    document.addEventListener('submit', async e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-print-form')) return;
        if (e.submitter && e.submitter.hasAttribute('formaction')) return;
        e.preventDefault();
        try {
            const response = await fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin' });
            const blob = await response.blob();
            const url = URL.createObjectURL(blob);
            if (response.ok && blob.type === 'application/pdf') {
                // The queue changes once documents are printed, so the page is reloaded after the dialog.
                printPdf(url, () => setTimeout(() => location.reload(), 1000));
            } else {
                window.open(url, '_blank');
            }
        } catch {
            form.submit();
        }
    });
})();
