// OpenBook's service worker: makes the app installable and keeps static files (styles, scripts, icons) cached so
// it opens quickly. Pages and API responses hold the company's data and are never cached: they always come from
// the server, and when it can't be reached the offline page is shown instead.
const VERSION = 'openbook-v1';
const SHELL = ['/offline.html', '/icons/icon-192.png', '/icons/favicon-32.png', '/manifest.webmanifest'];
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
        // Always the network for pages; the offline page only when the server can't be reached.
        event.respondWith(fetch(request).catch(() => caches.match('/offline.html')));
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
