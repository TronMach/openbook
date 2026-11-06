// OpenBook's service worker: makes the app installable and keeps static files (styles, scripts, icons) cached so
// it opens quickly. Pages and API responses hold the company's data and are never cached: they always come from
// the server, and when it can't be reached the offline page is shown instead. The one exception is the capture
// page, a static shell with no data in it (its drafts and lists live encrypted in IndexedDB), so it opens offline.
const VERSION = 'openbook-v22';
const SHELL = ['/offline.html', '/capture.html', '/capture.js', '/app.css', '/icons/icon-192.png', '/icons/favicon-32.png', '/manifest.webmanifest'];
const STATIC = /\.(css|js|png|svg|ico|woff2?|webmanifest)$/i;

self.addEventListener('install', event => {
    event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
    event.waitUntil(caches.keys()
        .then(keys => Promise.all(keys.filter(k => k !== VERSION).map(k => caches.delete(k))))
        .then(() => self.clients.claim()));
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== 'GET' || url.origin !== self.location.origin || url.pathname.startsWith('/api/') || url.pathname.startsWith('/_blazor'))
        return;

    if (request.mode === 'navigate') {
        // Always the network for pages; when the server can't be reached, the capture page from the cache, else the offline page.
        const fallback = url.pathname === '/capture.html' ? '/capture.html' : '/offline.html';
        event.respondWith(fetch(request).catch(() => caches.match(fallback)));
        return;
    }

    if (STATIC.test(url.pathname)) {
        // Cached copy first, refreshed in the background.
        event.respondWith(caches.open(VERSION).then(async cache => {
            const cached = await cache.match(request);
            const fresh = fetch(request).then(response => {
                if (response.ok && response.type === 'basic')
                    cache.put(request, response.clone());
                return response;
            }).catch(() => cached);
            return cached || fresh;
        }));
    }
});
