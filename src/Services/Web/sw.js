/* 极简 Service Worker：只缓存图标，页面/脚本/样式始终走网络，
   保证电脑上更新了程序后手机端刷新就能看到最新内容。
   缓存版本号改了会清掉旧缓存。 */
const VERSION = 'ta-shell-v3';
const SHELL = ['/icon-192.png', '/apple-touch-icon.png', '/manifest.webmanifest'];

self.addEventListener('install', (e) => {
  self.skipWaiting();
  e.waitUntil(
    caches.open(VERSION).then((c) => c.addAll(SHELL)).catch(() => {})
  );
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== VERSION).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== location.origin) return;

  // 页面、脚本、样式、接口：一律走网络，绝不缓存（否则更新后手机看不到新版）
  const path = url.pathname;
  if (path.startsWith('/api/') || path === '/' || path === '/index.html' ||
      path === '/app.js' || path === '/style.css' || path === '/sw.js') {
    return;
  }

  // 只有图标这类静态资源走缓存
  e.respondWith(
    caches.match(req).then((hit) => hit || fetch(req).then((res) => {
      const copy = res.clone();
      caches.open(VERSION).then((c) => c.put(req, copy)).catch(() => {});
      return res;
    }).catch(() => hit))
  );
});
