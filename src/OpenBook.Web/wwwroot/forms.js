// Printed forms: the design editor's live preview (form[data-preview] names the frame it fills).
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
