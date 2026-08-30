// TandemTab's published service worker — the "being an app" half of the PWA work (OPEN-BETA R2.5).
//
// What it is for: with iOS on hold indefinitely the mobile web IS the iOS product, and until now it could not
// be installed and would not open at all without a connection. This caches the published app shell so the app
// boots from the home screen with no network, and so Chrome offers to install it.
//
// What it is NOT: offline *data*. Every API read falls through to the network by design — nothing here caches
// a response, so a figure on screen is always one the server just sent. Offline reads and a write outbox are
// R4.5 (Trip Mode) and are deliberately not smuggled in here.
//
// ⚠️ One generation at a time. Every entry is precached under a cache name carrying the build's asset-manifest
// version, so a running page can never mix a new index.html with an old .wasm. The price is bounded staleness:
// a new build installs in the background and waits, and takes over once every tab of the app is closed. That is
// the standard trade and it is the safe half of it — the alternative (skipWaiting) swaps the runtime under a
// page that is still using it. See the note in OPEN-BETA.md if this ever needs revisiting.

self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'tandemtab-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// Everything needed to boot the shell: the .NET runtime and assemblies, the pages, styles, scripts and icons.
// ⚠️ .webmanifest is its own extension and matches none of the obvious patterns — leaving it out means an
// installed app cannot re-read its own manifest offline. Measured against the published asset list, not assumed.
const offlineAssetsInclude = [/\.dll$/, /\.wasm/, /\.html$/, /\.js$/, /\.json$/, /\.webmanifest$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.svg$/, /\.ico$/, /\.dat$/, /\.blat$/];
// The worker must never cache itself. ⚠️ Nothing else is excluded, and appsettings.Development.json in
// particular is deliberately left IN: the host stamps the client's environment, so a Development server makes
// the client ask for that file at boot, and a missing one is not a 404 offline — it is a failed fetch, which
// takes the whole app down. Excluding it cost an hour and hid the same fault in the production shape.
const offlineAssetsExclude = [/^service-worker\.js$/];

// ⛔⛔ THE SHELL IS SERVED FOR THESE PATHS AND NOTHING ELSE. This list is the whole of the SPA's routing
// surface (`@page` in Dashboard.razor and the seven Thin pages) and it is an ALLOWLIST on purpose.
//
// ⚠️ It used to be a blocklist — "serve index.html for every navigation that isn't a .html page" — and that
// took Google sign-in down in production for two days (last callback to reach the server: 2026-08-26; this
// worker shipped 2026-08-28). The OAuth return leg is
// `GET /auth/external/google/callback?code=…`: same-origin, top-level, no `.html` on the end. The old rule
// answered it out of the cache, so Google's `code` was thrown away and the request never reached the server
// at all. The app then booted on the landing page with nothing to exchange — "it redirects back and nothing
// happens" — and no error anywhere, because from the server's side the sign-in simply never occurred.
//
// ★ The mechanism is worth keeping, because it is why this looked intermittent and why it survived review:
// navigating away to accounts.google.com releases the last client this worker controlled, so a NEW worker
// that was sitting in `waiting` activates *while the user is on Google's consent screen* — and then catches
// the return. The start of the flow reaches the network and the end of it does not, in one sign-in.
//
// The rule to hold on to: a navigation this worker does not positively recognise as a page of the SPA
// belongs to the SERVER. Adding a `@page` route means adding it here; forgetting only costs that route its
// offline boot. Getting it wrong the other way costs an endpoint its existence.
const spaRoutes = new Set([
    '/',
    '/thin-home', '/thin-goals', '/thin-dash', '/thin-wallets', '/thin-recurring', '/thin-spending', '/thin-budgets',
]);
// Trailing slashes only — no query, no hash: `path` is already `URL.pathname`.
const isSpaRoute = path => spaRoutes.has(path.length > 1 ? path.replace(/\/+$/, '') : path);

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        // `integrity` makes the browser verify each asset against the manifest's hash, so a truncated or
        // proxy-mangled download fails the install rather than poisoning the cache with a broken runtime.
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));

    // addAll is all-or-nothing: a half-populated cache would boot an app missing an assembly, which fails
    // later and further away than a failed install does.
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
    // Only ours, and only the older ones — the browser hands the whole origin's cache storage to every worker
    // on it, so an unfiltered delete would be deleting somebody else's data on a shared origin.
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

async function onFetch(event) {
    const request = event.request;

    // A write is never answerable from a cache, and neither is a cross-origin read (the Google Fonts
    // stylesheet, an OAuth redirect). Both go to the network untouched.
    if (request.method !== 'GET') return fetch(request);
    if (new URL(request.url).origin !== self.location.origin) return fetch(request);

    const path = new URL(request.url).pathname;
    const isNavigation = request.mode === 'navigate';
    // Only a route the SPA actually owns is answered with the shell. Everything else — the OAuth callback
    // above all — falls through to `request`, misses the cache, and reaches the server. The legal pages need
    // no special case any more: they are precached under their own URLs, so the plain `request` match finds
    // them and they still open offline.
    const key = isNavigation && isSpaRoute(path) ? 'index.html' : request;

    const cache = await caches.open(cacheName);
    // ⚠️ ignoreSearch matters for one specific reason: index.html asks for `css/app.css?v=45` (the manual
    // cache-bust) while the manifest lists the file without a query, so an exact match misses and the app
    // would come up unstyled offline. It is safe here because this cache holds published files only — an API
    // read like /accounts?period=2 has nothing to collide with, cached or not.
    const cachedResponse = await cache.match(key, { ignoreSearch: true });

    // A miss is the normal path for every API call, so it must be cheap and must not be cached on the way
    // back: a snapshot read answered from a cache is a wrong balance shown as a right one.
    return cachedResponse || fetch(request);
}
