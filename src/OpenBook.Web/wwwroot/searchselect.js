// Keyboard for SearchSelect pickers: arrows move through the matches, Enter chooses, Esc closes, and focusing a
// picker selects its text so typing replaces it. Blazor renders the list and does the filtering; this only moves the
// highlight and "clicks" (mousedown) the highlighted choice.
(() => {
    const options = list => [...list.querySelectorAll('[role="option"]')];

    function move(input, list, step) {
        const all = options(list);
        if (all.length === 0) return;
        const current = all.findIndex(o => o.classList.contains('active'));
        const next = current < 0 ? (step > 0 ? 0 : all.length - 1) : Math.min(all.length - 1, Math.max(0, current + step));
        all.forEach(o => o.classList.remove('active'));
        all[next].classList.add('active');
        all[next].scrollIntoView({ block: 'nearest' });
        input.setAttribute('aria-activedescendant', all[next].id);
    }

    document.addEventListener('keydown', e => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement) || !input.classList.contains('search-input')) return;
        const list = document.getElementById(input.getAttribute('aria-controls'));
        const open = input.getAttribute('aria-expanded') === 'true' && list;

        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            if (open) move(input, list, e.key === 'ArrowDown' ? 1 : -1);
            else input.click();
        } else if (e.key === 'Enter' && open) {
            const active = list.querySelector('[role="option"].active');
            if (active) {
                e.preventDefault();
                e.stopPropagation();
                active.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
            }
        } else if (e.key === 'Escape' && open) {
            e.preventDefault();
            e.stopPropagation();
            input.blur();
        }
    }, true);

    document.addEventListener('focusin', e => {
        if (e.target instanceof HTMLInputElement && e.target.classList.contains('search-input')) e.target.select();
    });
})();

// The same picker for static (server-rendered) forms: a <select data-search> gets a search box in front of it and is
// hidden; the select stays in the form and carries the value, so the page works unchanged without script. Matching
// follows PickerOptions.Filter. Blazor's enhanced navigation drops the box when it patches the page, so it's added
// again after every enhanced load.
(() => {
    const maxShown = 50;
    const fold = s => s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();
    let next = 0;

    function choices(select) {
        return [...select.options].map(o => ({
            value: o.value,
            label: o.text.replace(/\s+/g, ' ').trim(),
            group: o.parentElement instanceof HTMLOptGroupElement ? o.parentElement.label : null,
            haystack: fold([o.text, o.dataset.keywords].filter(Boolean).join(' '))
        }));
    }

    function filter(options, query) {
        const typed = fold(query.trim());
        if (typed.length === 0) return options.slice(0, maxShown);
        const words = typed.split(' ').filter(Boolean);
        const rank = o => {
            const label = fold(o.label);
            if (label.startsWith(typed)) return 0;
            return label.split(/[ :\-(/]+/).some(part => part.startsWith(words[0])) ? 1 : 2;
        };
        return options
            .map((o, index) => ({ o, index }))
            .filter(x => words.every(w => x.o.haystack.includes(w)))
            .map(x => ({ ...x, rank: rank(x.o) }))
            .sort((a, b) => a.rank - b.rank || a.index - b.index)
            .slice(0, maxShown)
            .map(x => x.o);
    }

    function enhance(select) {
        const prev = select.previousElementSibling;
        if (prev && prev.classList.contains('static')) {
            if (prev.dataset.for === select.dataset.searchKey) return;
            prev.remove();
        }

        const key = `static-${++next}`;
        select.dataset.searchKey = key;
        const all = choices(select);
        const none = all.find(o => o.value === '');
        const options = all.filter(o => o.value !== '');
        const label = select.id ? document.querySelector(`label[for="${CSS.escape(select.id)}"]`) : null;
        // An empty choice ("Choose…", "None") shows as the placeholder, not as typed text.
        const current = () => select.value === '' ? '' : all.find(o => o.value === select.value)?.label ?? '';

        const box = document.createElement('div');
        box.className = 'search-select static';
        box.dataset.for = key;
        const input = document.createElement('input');
        Object.assign(input, { type: 'text', className: 'search-input', autocomplete: 'off', spellcheck: false, disabled: select.disabled });
        input.setAttribute('role', 'combobox');
        input.setAttribute('aria-autocomplete', 'list');
        input.setAttribute('aria-expanded', 'false');
        input.setAttribute('aria-controls', `search-${key}`);
        input.setAttribute('aria-label', select.getAttribute('aria-label') ?? label?.textContent.trim() ?? '');
        input.placeholder = none?.label ?? 'Type to search…';
        input.value = current();
        // The hidden select can't show the browser's "fill this in": the box asks instead, or Save does nothing and says nothing.
        if (select.required) {
            input.required = true;
            select.required = false;
        }
        const list = document.createElement('ul');
        list.id = `search-${key}`;
        list.className = 'search-list';
        list.setAttribute('role', 'listbox');
        list.hidden = true;
        box.append(input, list);
        select.before(box);

        let query = '';
        const open = () => list.hidden === false;

        function pick(option) {
            close();
            input.value = option?.label ?? '';
            const value = option?.value ?? '';
            if (select.value === value) return;
            select.value = value;
            select.dispatchEvent(new Event('change', { bubbles: true }));
        }

        function close() {
            list.hidden = true;
            list.replaceChildren();
            query = '';
            input.setAttribute('aria-expanded', 'false');
            input.removeAttribute('aria-activedescendant');
        }

        function item(option, id, cls) {
            const li = document.createElement('li');
            li.id = id;
            li.className = cls;
            li.setAttribute('role', 'option');
            li.setAttribute('aria-selected', option ? String(option.value === select.value) : String(select.value === ''));
            li.textContent = option?.label ?? none.label;
            li.addEventListener('mousedown', e => { e.preventDefault(); pick(option); });
            return li;
        }

        function render() {
            const matches = filter(options, query);
            const rows = [];
            if (none && query.length === 0) rows.push(item(null, `${list.id}-none`, 'search-option muted'));
            let group = null;
            matches.forEach((o, i) => {
                if (o.group && o.group !== group) {
                    group = o.group;
                    const heading = document.createElement('li');
                    heading.setAttribute('role', 'presentation');
                    heading.className = 'search-group';
                    heading.textContent = group;
                    rows.push(heading);
                }
                rows.push(item(o, `${list.id}-${i}`, i === 0 && query.length > 0 ? 'search-option active' : 'search-option'));
            });
            if (matches.length === 0 || matches.length === maxShown) {
                const note = document.createElement('li');
                note.setAttribute('role', 'presentation');
                note.className = 'search-empty muted';
                note.textContent = matches.length === 0 ? `No matches for “${query}”` : 'Type more to see other matches';
                rows.push(note);
            }
            list.replaceChildren(...rows);
            list.hidden = false;
            input.setAttribute('aria-expanded', 'true');
        }

        const show = () => { if (!open() && !input.disabled) { query = ''; render(); } };
        input.addEventListener('focus', show);
        input.addEventListener('click', show);
        input.addEventListener('input', () => { query = input.value.trim(); render(); });
        input.addEventListener('blur', () => {
            if (!open()) return;
            const exact = query.length > 0 ? options.find(o => o.label.localeCompare(query, undefined, { sensitivity: 'accent' }) === 0) : null;
            if (exact) pick(exact);
            else if (input.value.trim().length === 0 && none) pick(null);
            else { close(); input.value = current(); }
        });
    }

    // The select is hidden, so its label focuses the search box instead.
    document.addEventListener('click', e => {
        const select = e.target instanceof HTMLLabelElement ? e.target.control : null;
        const box = select?.matches('select[data-search]') ? select.previousElementSibling : null;
        if (box?.classList.contains('static')) {
            e.preventDefault();
            box.querySelector('input').focus();
        }
    });

    const enhanceAll = () => document.querySelectorAll('select[data-search]:not([multiple])').forEach(enhance);
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', enhanceAll);
    else enhanceAll();
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function')
        window.Blazor.addEventListener('enhancedload', enhanceAll);
})();
