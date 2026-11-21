// The text compose page: form[data-text-preview] posts its message (with the antiforgery token) to that address
// 400 ms after a change and shows what comes back in the [data-text-pane] beside it: the text as it will arrive,
// its length in parts, and notes. Works by delegation, so pages loaded by enhanced navigation need nothing.
(() => {
    const timers = new WeakMap();
    const counts = new WeakMap();

    function show(form, answer) {
        const pane = form.closest('.email-editor')?.querySelector('[data-text-pane]');
        if (!pane) return;
        const message = pane.querySelector('[data-text-message]');
        const length = pane.querySelector('[data-text-length]');
        const notes = pane.querySelector('[data-text-notes]');
        if (answer.error) {
            if (notes) {
                notes.replaceChildren();
                const li = document.createElement('li');
                li.textContent = answer.error;
                notes.appendChild(li);
            }
            return;
        }
        if (message) message.textContent = answer.text;
        if (length) length.textContent = `${answer.characters} characters · ${answer.parts} ${answer.parts === 1 ? 'part' : 'parts'}`;
        if (notes) {
            notes.replaceChildren();
            for (const text of [...(answer.notes || []), ...(answer.problems || [])]) {
                const li = document.createElement('li');
                li.textContent = text;
                notes.appendChild(li);
            }
        }
    }

    async function refresh(form) {
        const count = (counts.get(form) || 0) + 1;
        counts.set(form, count);
        try {
            const response = await fetch(form.dataset.textPreview, { method: 'POST', body: new FormData(form), credentials: 'same-origin' });
            const answer = await response.json();
            if (counts.get(form) === count) show(form, answer);
        } catch {
            // A preview that can't be fetched leaves the last one showing.
        }
    }

    document.addEventListener('input', event => {
        const form = event.target.closest?.('form[data-text-preview]');
        if (!form) return;
        clearTimeout(timers.get(form));
        timers.set(form, setTimeout(() => refresh(form), 400));
    });
})();
