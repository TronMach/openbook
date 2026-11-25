// Passkeys: fills in the sign-in and add forms with what the browser's passkey dialog gives, then posts them.
// A form with data-passkey="get" signs in, "create" adds one. Forms stay hidden where the browser has no passkeys.
(() => {
    const bytes = text => Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
    const text = buffer => btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    const set = (form, name, value) => { form.elements[name].value = value; };

    async function fill(form) {
        const challenge = bytes(form.dataset.challenge);
        if (form.dataset.passkey === 'get') {
            const made = await navigator.credentials.get({ publicKey: { challenge, rpId: form.dataset.site, userVerification: 'required', timeout: 120000 } });
            set(form, 'credentialId', text(made.rawId));
            set(form, 'clientData', text(made.response.clientDataJSON));
            set(form, 'authenticatorData', text(made.response.authenticatorData));
            set(form, 'signature', text(made.response.signature));
            return;
        }
        const made = await navigator.credentials.create({
            publicKey: {
                challenge,
                rp: { id: form.dataset.site, name: 'OpenBook' },
                user: { id: bytes(form.dataset.userId), name: form.dataset.userName, displayName: form.dataset.userName },
                pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
                authenticatorSelection: { residentKey: 'required', userVerification: 'required' },
                excludeCredentials: (form.dataset.known || '').split(' ').filter(k => k).map(k => ({ type: 'public-key', id: bytes(k) })),
                attestation: 'none',
                timeout: 120000,
            },
        });
        set(form, 'clientData', text(made.response.clientDataJSON));
        set(form, 'authenticatorData', text(made.response.getAuthenticatorData()));
        set(form, 'publicKey', text(made.response.getPublicKey()));
        set(form, 'algorithm', made.response.getPublicKeyAlgorithm());
    }

    function say(form, message) {
        const box = form.querySelector('[data-passkey-error]');
        if (box) {
            box.textContent = message;
            box.hidden = !message;
        }
    }

    document.addEventListener('submit', async e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.passkey || form.dataset.filled)
            return;
        e.preventDefault();
        e.stopImmediatePropagation();
        if (form.dataset.asking)
            return;
        form.dataset.asking = '1';
        say(form, '');
        try {
            await fill(form);
            form.dataset.filled = '1';
            form.submit();
        } catch (error) {
            // Cancelled, timed out, or there's no passkey for this site here.
            say(form, error && error.name === 'InvalidStateError' ? form.dataset.knownText : form.dataset.failedText);
        } finally {
            delete form.dataset.asking;
        }
    }, true);

    const show = () => {
        const able = window.PublicKeyCredential && window.isSecureContext;
        for (const part of document.querySelectorAll('[data-passkey-part]'))
            part.hidden = !able;
        for (const part of document.querySelectorAll('[data-passkey-missing]'))
            part.hidden = !!able;
    };
    show();
    document.addEventListener('DOMContentLoaded', show);
    // After Blazor's enhanced navigation swaps the page.
    document.addEventListener('enhancedload', show);
    if (window.Blazor && window.Blazor.addEventListener)
        window.Blazor.addEventListener('enhancedload', show);
})();
