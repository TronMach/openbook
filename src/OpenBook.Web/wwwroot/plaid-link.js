// Plaid Link on the bank connections page. A form with data-plaid-link (the address that hands out link tokens) opens
// Plaid's own window instead of posting: the person picks a bank and signs in to it there, and Link hands back a one-time
// public token, which the form then posts with the bank's name. Plaid's script is loaded only when it's needed.
(() => {
    const source = 'https://cdn.plaid.com/link/v2/stable/link-initialize.js';
    let loading = null;
    const load = () => loading ??= new Promise((resolve, reject) => {
        if (window.Plaid) { resolve(); return; }
        const script = document.createElement('script');
        script.src = source;
        script.onload = () => resolve();
        script.onerror = () => { loading = null; reject(new Error("Plaid's window couldn't be loaded. Check the connection to the internet and try again.")); };
        document.head.appendChild(script);
    });

    document.addEventListener('submit', async e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-plaid-link')) return;
        const field = name => form.elements.namedItem(name);
        if (field('publicToken').value) return;
        e.preventDefault();
        const button = form.querySelector('button[type=submit]');
        const error = form.querySelector('[data-plaid-error]');
        const show = text => { if (error) { error.textContent = text || ''; error.hidden = !text; } };
        if (button && button.disabled) return;
        if (button) button.disabled = true;
        show('');
        const done = text => { if (button) button.disabled = false; show(text); };
        try {
            const body = new FormData();
            body.append('__RequestVerificationToken', field('__RequestVerificationToken').value);
            const response = await fetch(form.getAttribute('data-plaid-link'), { method: 'POST', body, credentials: 'same-origin' });
            const data = await response.json().catch(() => ({}));
            if (!response.ok || !data.linkToken) throw new Error(data.error || "Plaid couldn't be started. Try again.");
            await load();
            window.Plaid.create({
                token: data.linkToken,
                onSuccess: (publicToken, metadata) => {
                    field('publicToken').value = publicToken;
                    field('name').value = (metadata && metadata.institution && metadata.institution.name) || '';
                    form.submit();
                },
                onExit: problem => done(problem ? (problem.display_message || problem.error_message || 'Plaid closed before the bank was connected.') : ''),
            }).open();
        } catch (problem) {
            done(problem instanceof Error ? problem.message : String(problem));
        }
    });
})();
