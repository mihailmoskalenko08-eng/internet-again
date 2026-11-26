const cacheName = "route-shell-v2";
const shell = ["./index.html","./style.css?v=2","./app.mjs?v=2","./model.mjs","./graph.mjs","./display.woff","./manifest.webmanifest","./icon-180.png","./icon-192.png","./icon-512.png"];
const assetUrls = new Set(shell.map(path => new URL(path,self.registration.scope).href));
const startPage = new URL("./index.html",self.registration.scope).href;
self.addEventListener("install", event => {
  event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(shell)).then(() => self.skipWaiting()));
});
self.addEventListener("activate", event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(key => key.startsWith("route-shell-") && key !== cacheName).map(key => caches.delete(key)))).then(() => clients.claim()));
});
self.addEventListener("fetch",event => {
  const request=event.request;
  if (request.method !== "GET" || new URL(request.url).origin !== self.location.origin) return;
  if (request.mode === "navigate") {
    event.respondWith(fetch(request).catch(() => caches.open(cacheName).then(cache => cache.match(startPage))));
  } else if (assetUrls.has(request.url)) {
    event.respondWith(caches.open(cacheName).then(cache => cache.match(request)).then(response => response || fetch(request)));
  }
});
