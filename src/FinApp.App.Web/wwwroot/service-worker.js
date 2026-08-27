// Development service worker — deliberately inert.
//
// The real one is service-worker.published.js; `dotnet publish` swaps it in over this file and generates the
// asset manifest it needs (service-worker-assets.js), which only exists in a published output. Registering a
// no-op here rather than nothing keeps the registration path itself exercised in dev — the same code runs,
// the same scope is claimed — while leaving every request to go straight to the dev server, so a stale cache
// can never be the reason a change "didn't apply" locally.
//
// ⚠️ The empty fetch listener is load-bearing, not decoration: a service worker with no fetch handler is not
// an installable PWA as far as Chrome is concerned, and the whole point of this file's published twin is the
// install prompt. Keeping the shapes identical means dev and prod differ in caching, never in capability.
self.addEventListener('fetch', () => { });
