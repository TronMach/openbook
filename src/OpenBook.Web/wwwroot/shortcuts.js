// What's typed on an interactive form, kept in this tab (FormDraft.razor) in case the connection goes. Browser storage
// can be refused, and then there's simply no draft.
window.obDrafts = {
    read(key) { try { return sessionStorage.getItem(`ob-draft:${key}`); } catch { return null; } },
    save(key, json) { try { sessionStorage.setItem(`ob-draft:${key}`, json); } catch { } },
    clear(key) { try { sessionStorage.removeItem(`ob-draft:${key}`); } catch { } },
};

// OpenBook: theme choice (auto / light / dark) and keyboard shortcuts. No dependencies.
(() => {
    const themeKey = 'ob-theme';
    const themes = ['auto', 'light', 'dark'];

    const storedTheme = () => {
        try { return localStorage.getItem(themeKey) || 'auto'; } catch { return 'auto'; }
    };

    // The <head> script sets data-theme before first paint; this keeps it (and the button) right after
    // enhanced navigation, which can reset attributes on <html>.
    const applyTheme = () => {
        const theme = storedTheme();
        if (theme === 'auto') document.documentElement.removeAttribute('data-theme');
        else document.documentElement.setAttribute('data-theme', theme);
        const button = document.getElementById('theme-toggle');
        if (button) {
            button.textContent = { auto: 'Theme: auto', light: 'Theme: light', dark: 'Theme: dark' }[theme];
            button.title = 'Switch between following the system, light and dark';
        }
    };

    document.addEventListener('click', e => {
        if (!(e.target instanceof Element) || !e.target.closest('#theme-toggle')) return;
        const next = themes[(themes.indexOf(storedTheme()) + 1) % themes.length];
        try { localStorage.setItem(themeKey, next); } catch { /* private mode: applies to this page only */ }
        if (next === 'auto') document.documentElement.removeAttribute('data-theme');
        else document.documentElement.setAttribute('data-theme', next);
        applyTheme();
    });

    // A button that asks first. The question is data, so a name in it (a file's, say) can never run as script: an
    // inline onclick builds JavaScript from it, and HTML encoding doesn't stop that.
    document.addEventListener('click', e => {
        const asking = e.target instanceof Element ? e.target.closest('[data-confirm]') : null;
        if (asking && !confirm(asking.dataset.confirm)) e.preventDefault();
    });

    // Drop-down menus (<details class="menu">: the simple view's menu bar, the full view's drop-downs, the account menu,
    // a center's "New transaction"): one open at a time, closed by a click elsewhere or Esc; in a menu bar, while one is
    // open, pointing at another opens it.
    const openMenus = () => [...document.querySelectorAll('details.menu[open]')];
    document.addEventListener('click', e => {
        const inside = e.target instanceof Element ? e.target.closest('details.menu') : null;
        for (const menu of openMenus()) if (menu !== inside) menu.open = false;
    });
    document.addEventListener('toggle', e => {
        const menu = e.target;
        if (!(menu instanceof HTMLDetailsElement) || !menu.open || !menu.matches('details.menu')) return;
        for (const other of openMenus()) if (other !== menu) other.open = false;
    }, true);
    document.addEventListener('pointerover', e => {
        // A tap isn't pointing: it would open the menu, then its click would close it again.
        if (e.pointerType !== 'mouse') return;
        const summary = e.target instanceof Element ? e.target.closest('.menubar details.menu > summary, .full-menu details.menu > summary') : null;
        if (!summary || openMenus().length === 0) return;
        summary.parentElement.open = true;
    });
    document.addEventListener('keydown', e => {
        if (e.key !== 'Escape') return;
        const open = openMenus();
        if (open.length === 0) return;
        for (const menu of open) menu.open = false;
        open[0].querySelector('summary')?.focus();
    });

    // g + key goes somewhere; n + key starts something new.
    const go = { h: '/', s: '/sales', p: '/purchases', b: '/banking', i: '/inventory', j: '/journal', r: '/reports', a: '/accounts', c: '/customers', v: '/vendors' };
    const create = {
        i: '/sales/invoices/new', r: '/sales/receipts/new', p: '/sales/payments/new', e: '/sales/estimates/new',
        b: '/purchases/bills/new', x: '/banking', j: '/journal/new', c: '/customers/new', v: '/vendors/new', t: '/items/new',
    };
    const help = [
        ['/', 'Search'],
        ['?', 'Show these shortcuts'],
        ['g then ?', 'Open the user guide at the page you are on'],
        ['Ctrl + Enter', 'Save the form you are typing in'],
        ['Alt + Q', 'Jump to quick entry (account registers)'],
        ['g then h / s / p / b / i / j / r / a / c / v', 'Go to home, sales, purchases, banking, inventory, journal, reports, accounts, customers, vendors'],
        ['n then i / r / p / e / b / j / c / v / t', 'New invoice, sales receipt, payment, estimate, bill, journal entry, customer, vendor, product'],
    ];

    let pending = null;
    let pendingTimer = 0;
    const isTyping = el => el instanceof HTMLElement && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));

    const toggleHelp = () => {
        let dialog = document.getElementById('shortcut-help');
        if (!dialog) {
            dialog = document.createElement('dialog');
            dialog.id = 'shortcut-help';
            dialog.setAttribute('aria-label', 'Keyboard shortcuts');
            const rows = help.map(([keys, what]) => `<tr><td><kbd>${keys}</kbd></td><td>${what}</td></tr>`).join('');
            dialog.innerHTML = `<h2>Keyboard shortcuts</h2><table class="data">${rows}</table>`
                + `<p><a href="/help">Open the user guide</a></p><form method="dialog"><button>Close</button></form>`;
            document.body.appendChild(dialog);
        }
        if (dialog.open) dialog.close(); else dialog.showModal();
    };

    // A second submit of a form already on its way (a double click on a slow connection) would enter the document twice.
    // Only full-page posts: interactive forms have no method="post" and guard themselves, and print forms stay on the page.
    let posted = new WeakMap();
    const resubmitAfter = 5000;
    // A form with data-unsaved-watch has changes of its own; one with data-needs-saved works from what's saved.
    let unsaved = false;
    const watchChanges = e => {
        if (!(e.target instanceof Element) || !e.target.closest('form[data-unsaved-watch]')) return;
        unsaved = true;
        document.querySelectorAll('[data-unsaved-note]').forEach(note => { note.hidden = false; });
    };
    document.addEventListener('input', watchChanges);
    document.addEventListener('change', watchChanges);
    // A form that comes back with an error loses what was typed; a long sheet (a stock count) shouldn't.
    const typedKey = form => 'ob-typed:' + location.pathname;
    // The closing date password box on a refused page goes with whichever form is sent next.
    document.addEventListener('submit', e => {
        const box = document.getElementById('ob-closing-password');
        const form = e.target;
        if (!box || !box.value || !(form instanceof HTMLFormElement) || form.method !== 'post') return;
        if (form.querySelector('input[name="ClosingPassword"]')?.value) return;
        const field = form.querySelector('input[name="ClosingPassword"]') || form.appendChild(Object.assign(document.createElement('input'), { type: 'hidden', name: 'ClosingPassword' }));
        field.value = box.value;
    }, true);
    document.addEventListener('submit', e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-keep-typed')) return;
        // Everything but what mustn't be kept: a password stays out of storage, whichever form it's on.
        const typed = {};
        for (const field of form.querySelectorAll('input:not([type="password"], [type="hidden"], [type="file"], [type="radio"]), select:not([multiple]), textarea'))
            if (field.name) typed[field.name] = field.type === 'checkbox' ? field.checked : field.value;
        try { sessionStorage.setItem(typedKey(form), JSON.stringify(typed)); } catch { /* the sheet just comes back empty */ }
    });
    function restoreTyped() {
        const form = document.querySelector('form[data-keep-typed]');
        if (!form) return;
        try {
            const saved = sessionStorage.getItem(typedKey(form));
            sessionStorage.removeItem(typedKey(form));
            if (!saved || !new URLSearchParams(location.search).has('error')) return;
            for (const [name, value] of Object.entries(JSON.parse(saved))) {
                const field = form.querySelector(`[name="${CSS.escape(name)}"]`);
                if (field?.type === 'checkbox') field.checked = value === true;
                else if (field) field.value = value;
            }
        } catch { /* nothing to bring back */ }
    }
    restoreTyped();
    window.Blazor?.addEventListener?.('enhancedload', restoreTyped);

    document.addEventListener('submit', e => {
        const form = e.target;
        if (form instanceof HTMLFormElement && form.hasAttribute('data-needs-saved') && unsaved
            && !confirm('You have changes above that aren\'t saved. Go on without them?')) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        if (!(form instanceof HTMLFormElement) || form.method !== 'post' || form.target === '_blank'
            || e.submitter?.formTarget === '_blank' || form.hasAttribute('data-print-form')) return;
        const at = posted.get(form);
        if (at && Date.now() - at < resubmitAfter) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        posted.set(form, Date.now());
    }, true);
    // The second click of a double click on a save button, for interactive pages: a quick server has finished the first
    // by then, so the page's own busy flag is off again. "Add line" buttons aren't saves, so two quick clicks there still count.
    window.addEventListener('click', e => {
        if (e.detail < 2 || !(e.target instanceof Element)) return;
        const button = e.target.closest('button');
        if (button && (button.type === 'submit' || button.closest('.actions'))) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);
    // A page that came back (Back button, enhanced navigation) starts again.
    const startAgain = () => { posted = new WeakMap(); unsaved = false; };
    window.addEventListener('pageshow', startAgain);
    window.Blazor?.addEventListener?.('enhancedload', startAgain);

    document.addEventListener('keydown', e => {
        // Shift on its own is the start of typing ?, not a key of a g or n pair.
        if (['Shift', 'Control', 'Alt', 'Meta', 'CapsLock'].includes(e.key)) return;
        // Ctrl/Cmd + Enter: submit the form you're in, from any field (quick entry everywhere).
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            const form = e.target instanceof Element ? e.target.closest('form') : null;
            if (form) { e.preventDefault(); if (!e.repeat) form.requestSubmit(); return; }
            // An interactive form has no form element, just its save button, which may be waiting for the field
            // being typed in: leaving the field sends its value, and the button comes alive once that's back.
            const save = () => document.querySelector('.actions button:not(.secondary):not(.link)');
            if (save()) {
                e.preventDefault();
                if (e.repeat) return;
                if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
                let tries = 0;
                const press = () => {
                    const button = save();
                    if (button && !button.disabled) button.click();
                    else if (button && ++tries < 30) setTimeout(press, 50);
                };
                setTimeout(press, 50);
            }
            return;
        }
        if (e.altKey && (e.key === 'q' || e.key === 'Q')) {
            const first = document.querySelector('[data-quick-first], .quick-entry input:not([type=hidden])');
            if (first) { e.preventDefault(); first.focus(); }
            return;
        }
        if (e.ctrlKey || e.metaKey || e.altKey || isTyping(document.activeElement)) return;

        if (pending) {
            // g + ?: the guide's chapter for this page (the top bar's Help link knows which).
            const helpLink = pending === 'g' && e.key === '?' ? document.querySelector('a.help-topic') : null;
            const url = helpLink ? helpLink.getAttribute('href') : (pending === 'g' ? go : create)[e.key.toLowerCase()];
            pending = null;
            if (url) { e.preventDefault(); location.href = url; }
            return;
        }
        if (e.key === '/') {
            const search = document.querySelector('form.search input');
            if (search) { e.preventDefault(); search.focus(); search.select(); }
        } else if (e.key === '?') {
            e.preventDefault();
            toggleHelp();
        } else if (e.key === 'g' || e.key === 'n') {
            pending = e.key;
            clearTimeout(pendingTimer);
            pendingTimer = setTimeout(() => { pending = null; }, 1500);
        }
    });

    // Filter boxes (<input data-filter="#list">): hide the list's [data-filter-item]s that don't contain every word typed
    // (their text plus data-keywords), groups ([data-filter-group]) left empty, and show [data-filter-empty] when nothing
    // matches. Without script the whole list simply shows.
    document.addEventListener('input', e => {
        const box = e.target;
        if (!(box instanceof HTMLInputElement) || !box.dataset.filter) return;
        const list = document.querySelector(box.dataset.filter);
        if (!list) return;
        const words = box.value.toLowerCase().split(/\s+/).filter(w => w);
        let shown = 0;
        for (const item of list.querySelectorAll('[data-filter-item]')) {
            const text = (item.textContent + ' ' + (item.dataset.keywords || '')).toLowerCase();
            const match = words.every(w => text.includes(w));
            item.hidden = !match;
            if (match) shown++;
        }
        for (const group of list.querySelectorAll('[data-filter-group]'))
            group.hidden = !group.querySelector('[data-filter-item]:not([hidden])');
        const empty = list.parentElement?.querySelector('[data-filter-empty]');
        if (empty) empty.hidden = shown > 0;
    });

    // Parts of a form that only matter for some choices (<x data-shown-by="#select" data-shown-for="A B">): shown while
    // the select has one of those values. Without script everything shows, which still works.
    const showChosen = () => {
        for (const part of document.querySelectorAll('[data-shown-by]')) {
            const select = document.querySelector(part.dataset.shownBy);
            if (select) part.hidden = !(part.dataset.shownFor || '').split(' ').includes(select.value);
        }
    };
    document.addEventListener('change', e => {
        if (e.target instanceof HTMLSelectElement && document.querySelector('[data-shown-by]')) showChosen();
    });

    // Installable app: the service worker caches static files and shows an offline page (never your data).
    if ('serviceWorker' in navigator && window.isSecureContext)
        navigator.serviceWorker.register('/sw.js').catch(() => { });

    // A wide table scrolls inside its box (.table-wrap): while it does, the box takes keyboard focus, so the arrow keys
    // scroll it, and is named after the heading above it. A box that fits the screen stays out of the tab order.
    const headingBefore = el => {
        for (let node = el; node && node !== document.body; node = node.parentElement) {
            for (let prev = node.previousElementSibling; prev; prev = prev.previousElementSibling) {
                const heading = /^H[1-6]$/.test(prev.tagName) ? prev : prev.querySelector?.('h1, h2, h3, h4, h5, h6');
                if (heading && heading.textContent.trim()) return heading.textContent.trim();
            }
        }
        return document.querySelector('h1')?.textContent.trim() || document.title;
    };
    const reachScrolling = () => {
        for (const wrap of document.querySelectorAll('.table-wrap')) {
            const scrolls = wrap.scrollWidth > wrap.clientWidth + 1;
            if (scrolls && !wrap.hasAttribute('tabindex')) {
                wrap.setAttribute('tabindex', '0');
                wrap.setAttribute('role', 'region');
                wrap.setAttribute('aria-label', wrap.querySelector('caption')?.textContent.trim() || headingBefore(wrap));
                wrap.dataset.scrollFocus = '';
            } else if (!scrolls && 'scrollFocus' in wrap.dataset) {
                wrap.removeAttribute('tabindex');
                wrap.removeAttribute('role');
                wrap.removeAttribute('aria-label');
                delete wrap.dataset.scrollFocus;
            }
        }
    };
    let reachPending = false;
    const reachSoon = () => {
        if (reachPending) return;
        reachPending = true;
        requestAnimationFrame(() => { reachPending = false; reachScrolling(); });
    };
    window.addEventListener('resize', reachSoon);
    // Interactive pages draw their tables after loading.
    new MutationObserver(reachSoon).observe(document.body, { childList: true, subtree: true });

    applyTheme();
    showChosen();
    reachScrolling();
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', applyTheme);
        window.Blazor.addEventListener('enhancedload', showChosen);
        window.Blazor.addEventListener('enhancedload', reachScrolling);
    }
})();
