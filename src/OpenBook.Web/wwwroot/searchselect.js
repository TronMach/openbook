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
