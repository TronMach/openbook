// Printed forms: the design editor's live preview (form[data-preview] names the frame it fills), and printing
// ([data-print] buttons and form[data-print-form] open the print dialog for a PDF without leaving the page).
(() => {
    const timers = new WeakMap();
    const blobs = new WeakMap();

    // Posts the editor's fields (with its antiforgery token) and shows the PDF that comes back.
    async function refresh(form) {
        const frame = document.getElementById(form.dataset.preview);
        if (!frame) return;
        const data = new FormData(form);
        const choice = document.querySelector(`[data-preview-document="${form.dataset.preview}"]`);
        data.set('document', choice ? choice.value : 'Invoice');
        if (form.dataset.logo) data.set('logoFrom', form.dataset.logo);
        try {
            const response = await fetch('/settings/forms/preview', { method: 'POST', body: data, credentials: 'same-origin' });
            const url = URL.createObjectURL(await response.blob());
            const old = blobs.get(frame);
            blobs.set(frame, url);
            frame.src = url;
            if (old) setTimeout(() => URL.revokeObjectURL(old), 5000);
        } catch {
            // Offline or signed out: keep the last preview.
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
        const id = target.getAttribute('data-preview-document');
        return id ? document.querySelector(`form[data-preview="${id}"]`) : null;
    }

    document.addEventListener('input', e => { const form = editorFor(e.target); if (form) schedule(form); });
    document.addEventListener('change', e => { const form = editorFor(e.target); if (form) schedule(form); });
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
