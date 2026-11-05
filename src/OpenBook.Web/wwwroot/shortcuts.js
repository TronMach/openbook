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
    document.addEventListener('mouseover', e => {
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
    document.addEventListener('submit', e => {
        const form = e.target;
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
    window.addEventListener('pageshow', () => { posted = new WeakMap(); });
    window.Blazor?.addEventListener?.('enhancedload', () => { posted = new WeakMap(); });

    document.addEventListener('keydown', e => {
        // Ctrl/Cmd + Enter: submit the form you're in, from any field (quick entry everywhere).
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            const form = e.target instanceof Element ? e.target.closest('form') : null;
            if (form) { e.preventDefault(); if (!e.repeat) form.requestSubmit(); }
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

    applyTheme();
    showChosen();
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', applyTheme);
        window.Blazor.addEventListener('enhancedload', showChosen);
    }
})();
