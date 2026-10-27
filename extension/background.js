// IBDM Pro browser extension — برمجة و تطوير ابراهيم الخزرجي
// Hands browser downloads to the IBDM Pro app (running on this computer) instead of letting the browser download them,
// and finds videos on pages so they can be downloaded at a chosen quality.

// Chromium loads i18n.js here; Firefox lists it before this file in the manifest.
if (typeof IBDM_EN === 'undefined') importScripts('i18n.js');
const APP = 'http://127.0.0.1:39401';
const HEADERS = { 'Content-Type': 'application/json', 'X-IBDM-Pro': '1' };

// ---------- talking to the app ----------

let appConfig = null;
let appCheckedAt = 0;

async function ping(force = false) {
  if (!force && Date.now() - appCheckedAt < 4000) return appConfig;
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 1500);
    const r = await fetch(APP + '/ping', { signal: ctrl.signal, cache: 'no-store', headers: { 'X-IBDM-Pro': '1' } });
    clearTimeout(timer);
    appConfig = r.ok ? await r.json() : null;
  } catch (e) {
    appConfig = null;
  }
  appCheckedAt = Date.now();
  if (appConfig && appConfig.version) selfUpdate(appConfig.version);
  if (appConfig && appConfig.lang && appConfig.lang !== IBDM_LANG) {
    // The app's language changed: menus and titles follow it.
    IBDM_LANG = appConfig.lang;
    api.storage.local.set({ lang: IBDM_LANG });
    buildMenus();
  }
  return appConfig;
}

// The app ships the extension's files in its own folder; after the app updates, those files are newer than what the
// browser is running. api.runtime.reload() makes the browser read them again — so the extension updates itself.
function newer(a, b) {
  const x = String(a).split('.').map(Number), y = String(b).split('.').map(Number);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0);
  }
  return false;
}
async function selfUpdate(appVersion) {
  const mine = api.runtime.getManifest().version;
  if (!newer(appVersion, mine)) return;
  // At most 3 tries per app version (right away, after 1 minute, after 10 minutes) — never a reload loop,
  // e.g. when the extension was loaded from a folder the app does not update.
  const { selfUpdate: st } = await api.storage.local.get({ selfUpdate: null });
  const rec = st && st.for === appVersion ? st : { for: appVersion, tries: 0, at: 0 };
  const waits = [0, 60e3, 600e3];
  if (rec.tries >= waits.length || Date.now() - rec.at < waits[rec.tries]) return;
  await api.storage.local.set({ selfUpdate: { for: appVersion, tries: rec.tries + 1, at: Date.now() } });
  api.runtime.reload();
}

async function sendToApp(payload) {
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 8000);
    const r = await fetch(APP + '/add', { method: 'POST', headers: HEADERS, body: JSON.stringify(payload), signal: ctrl.signal });
    clearTimeout(timer);
    return r.ok;
  } catch (e) {
    return false;
  }
}

async function cookiesFor(url) {
  try {
    const list = await api.cookies.getAll({ url });
    return list.map(c => `${c.name}=${c.value}`).join('; ');
  } catch (e) {
    return '';
  }
}

function extOf(name) {
  const clean = (name || '').split(/[?#]/)[0];
  const base = clean.split(/[\\/]/).pop() || '';
  const i = base.lastIndexOf('.');
  return i > 0 ? base.slice(i + 1).toLowerCase() : '';
}

const MIME_EXT = {
  'application/zip': 'zip', 'application/x-zip-compressed': 'zip', 'application/x-rar-compressed': 'rar', 'application/vnd.rar': 'rar',
  'application/x-7z-compressed': '7z', 'application/pdf': 'pdf', 'application/x-msdownload': 'exe', 'application/x-msi': 'msi',
  'application/vnd.android.package-archive': 'apk', 'application/x-iso9660-image': 'iso', 'video/mp4': 'mp4', 'video/x-matroska': 'mkv',
  'video/webm': 'webm', 'audio/mpeg': 'mp3', 'audio/mp4': 'm4a', 'application/x-bittorrent': 'torrent',
};

function baseName(path) {
  return (path || '').split(/[\\/]/).pop() || '';
}

function nameFromUrl(url) {
  try { return decodeURIComponent(new URL(url).pathname.split('/').pop() || ''); } catch (e) { return ''; }
}

function hostOf(url) {
  try { return new URL(url).hostname; } catch (e) { return ''; }
}

function isExcluded(url, list) {
  const h = hostOf(url);
  return (list || []).some(s => s && (h === s || h.endsWith('.' + s)));
}

function notify(title, message) {
  // No notifications permission needed: the badge and the app's own window carry the news.
  api.action.setBadgeBackgroundColor({ color: '#E5533D' });
  api.action.setBadgeText({ text: '!' });
  api.action.setTitle({ title: `${title}\n${message}` });
  setTimeout(() => refreshBadge(), 6000);
}

// ---------- capturing browser downloads ----------

const letThrough = new Set(); // urls the browser should download itself (fallback when the app cannot be reached)

async function shouldCapture(item) {
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return null; // blob:, data:, file: stay in the browser
  if (letThrough.has(url)) { letThrough.delete(url); return null; }
  const { paused } = await api.storage.local.get({ paused: false });
  if (paused) return null;
  const cfg = await ping(true);
  if (!cfg || !cfg.capture) return null;
  // The user chose "download with the browser" for this link in the app.
  const only = cfg.browserOnly || [];
  if (only.includes(url) || only.includes(item.url) || (item.finalUrl && only.includes(item.finalUrl))) return null;
  const name = baseName(item.filename) || nameFromUrl(url);
  const ext = extOf(name) || extOf(url) || MIME_EXT[(item.mime || '').toLowerCase()] || '';
  if (!ext || !(cfg.extensions || []).includes(ext)) return null;
  const size = item.fileSize > 0 ? item.fileSize : item.totalBytes;
  if (cfg.minSizeKB > 0 && size > 0 && size < cfg.minSizeKB * 1024) return null;
  if (isExcluded(url, cfg.excluded) || (item.referrer && isExcluded(item.referrer, cfg.excluded))) return null;
  return { url, name, size };
}

async function capture(item, found) {
  try { await api.downloads.cancel(item.id); } catch (e) { }
  try { await api.downloads.erase({ id: item.id }); } catch (e) { }
  const ok = await sendToApp({
    kind: 'file',
    url: found.url,
    pageUrl: item.referrer || '',
    referrer: item.referrer || '',
    fileName: extOf(found.name) ? found.name : '',
    mime: item.mime || '',
    size: found.size > 0 ? found.size : -1,
    cookies: await cookiesFor(found.url),
    userAgent: navigator.userAgent,
  });
  if (!ok) {
    // The app went away between the check and now: give the download back to the browser.
    letThrough.add(found.url);
    api.downloads.download({ url: found.url });
  }
}

if (api.downloads.onDeterminingFilename) {
  // Chrome, Edge, Brave, Opera: decide before the browser saves anything.
  api.downloads.onDeterminingFilename.addListener((item, suggest) => {
    shouldCapture(item).then(found => {
      if (found) capture(item, found);
      else suggest();
    }).catch(() => suggest());
    return true; // we answer asynchronously
  });
} else {
  // Firefox has no such event: take the download over the moment it starts.
  api.downloads.onCreated.addListener(item => {
    shouldCapture(item).then(found => { if (found) capture(item, found); }).catch(() => {});
  });
}

// ---------- right-click menu ----------

// Built one after another: two builds at once (install + language change) would add the same item twice.
let menuChain = Promise.resolve();
function buildMenus() {
  const make = o => new Promise(res => api.contextMenus.create(o, () => { void api.runtime.lastError; res(); }));
  menuChain = menuChain.then(() => api.contextMenus.removeAll()).then(async () => {
    await make({ id: 'ibdm-link', title: t('تحميل بواسطة IBDM Pro'), contexts: ['link', 'image', 'audio'] });
    await make({ id: 'ibdm-video', title: t('تحميل هذا الفيديو بواسطة IBDM Pro'), contexts: ['video'] });
    await make({ id: 'ibdm-page', title: t('تحميل فيديو هذه الصفحة بواسطة IBDM Pro'), contexts: ['page', 'frame'] });
  }).catch(() => { });
}
api.runtime.onInstalled.addListener(details => { buildMenus(); ping(true); injectIntoOpenTabs(); });

// Pages that were already open get the video button right away (after installing, or after the extension updates
// itself), without having to reload them.
async function injectIntoOpenTabs() {
  if (!api.scripting) return;
  try {
    const tabs = await api.tabs.query({ url: ['http://*/*', 'https://*/*'] });
    for (const tab of tabs) {
      api.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, files: ['i18n.js', 'content.js'] }).catch(() => { });
    }
  } catch (e) { }
}
api.runtime.onStartup.addListener(buildMenus);

api.contextMenus.onClicked.addListener(async (info, tab) => {
  const page = info.frameUrl || info.pageUrl || (tab && tab.url) || '';
  let payload;
  if (info.menuItemId === 'ibdm-link') {
    const url = info.linkUrl || info.srcUrl;
    payload = { kind: 'link', url, pageUrl: page, referrer: page, fileName: nameFromUrl(url) };
  } else if (info.menuItemId === 'ibdm-video') {
    const src = info.srcUrl && /^https?:/i.test(info.srcUrl) ? info.srcUrl : page;
    payload = { kind: 'media', url: src, pageUrl: page, referrer: page, title: tab ? tab.title : '' };
  } else {
    payload = { kind: 'media', url: page, pageUrl: page, referrer: page, title: tab ? tab.title : '' };
  }
  if (!payload.url || !/^https?:/i.test(payload.url)) return;
  payload.cookies = await cookiesFor(payload.url);
  payload.userAgent = navigator.userAgent;
  if (!(await sendToApp(payload))) notify(t('IBDM Pro غير مشغّل'), t('شغّل برنامج IBDM Pro ثم حاول مرة ثانية'));
});

// ---------- videos found on each tab ----------

const tabMedia = new Map(); // tabId -> Map(url -> {url, size, type})

const MEDIA_EXT = /\.(mp4|webm|mkv|mov|m4v|flv|3gp|mp3|m4a|aac|ogg|opus|wav|flac|m3u8|mpd)(\?|$)/i;
const SEGMENT = /\.(ts|m4s|aac\.part)(\?|$)|[?&](range|bytestart)=|\/seg-?\d+|segment\d|frag\d|chunk-?\d/i;

api.webRequest.onHeadersReceived.addListener(details => {
  if (details.tabId < 0 || details.statusCode >= 400) return;
  const url = details.url;
  if (!/^https?:/i.test(url) || SEGMENT.test(url)) return;
  const h = {};
  for (const x of details.responseHeaders || []) h[x.name.toLowerCase()] = x.value;
  const type = (h['content-type'] || '').toLowerCase();
  const isPlaylist = /mpegurl|dash\+xml/.test(type) || /\.(m3u8|mpd)(\?|$)/i.test(url);
  const isMedia = type.startsWith('video/') || type.startsWith('audio/') || isPlaylist || (details.type === 'media' && MEDIA_EXT.test(url));
  if (!isMedia) return;
  let size = parseInt(h['content-length'] || '-1', 10);
  const range = /\/(\d+)$/.exec(h['content-range'] || '');
  if (range) size = parseInt(range[1], 10);
  if (!isPlaylist && size > 0 && size < 300 * 1024) return; // tiny pieces and ad pings
  if (!tabMedia.has(details.tabId)) tabMedia.set(details.tabId, new Map());
  const list = tabMedia.get(details.tabId);
  if (!list.has(url) && list.size < 50) {
    list.set(url, { url, size, type: isPlaylist ? 'stream' : (type.startsWith('audio/') ? 'audio' : 'video') });
    refreshBadge(details.tabId);
  }
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object'] }, ['responseHeaders']);

api.tabs.onUpdated.addListener((tabId, change) => {
  if (change.url) { tabMedia.delete(tabId); refreshBadge(tabId); }
});
api.tabs.onRemoved.addListener(tabId => tabMedia.delete(tabId));
api.tabs.onActivated.addListener(({ tabId }) => refreshBadge(tabId));

async function refreshBadge(tabId) {
  const cfg = await ping();
  const { paused } = await api.storage.local.get({ paused: false });
  if (tabId === undefined) {
    const [t] = await api.tabs.query({ active: true, currentWindow: true });
    tabId = t ? t.id : -1;
  }
  const count = tabMedia.has(tabId) ? tabMedia.get(tabId).size : 0;
  api.action.setTitle({ title: cfg ? (paused ? t('IBDM Pro — الالتقاط متوقف مؤقتاً') : t('IBDM Pro — متصل')) : t('IBDM Pro — البرنامج غير مشغّل') });
  if (!cfg) {
    api.action.setBadgeBackgroundColor({ color: '#6B6B73' });
    api.action.setBadgeText({ text: 'off' });
  } else if (count > 0) {
    api.action.setBadgeBackgroundColor({ color: cfg.theme === 'dark' ? '#D4AF37' : cfg.theme === 'light' ? '#A87F0A' : '#D7141F' });
    if (tabId >= 0) api.action.setBadgeText({ tabId, text: String(count) });
    api.action.setBadgeText({ text: '' });
  } else {
    api.action.setBadgeText({ text: paused ? '❚❚' : '' });
    if (tabId >= 0) api.action.setBadgeText({ tabId, text: paused ? '❚❚' : '' });
  }
}

// ---------- messages from the page button and the popup ----------

api.runtime.onMessage.addListener((msg, sender, reply) => {
  (async () => {
    try { await handle(msg, sender, reply); } catch (e) { try { reply({ ok: false, error: String(e) }); } catch (x) { } }
  })();
  return true;
});

async function handle(msg, sender, reply) {
  {
    if (msg.cmd === 'status') {
      const cfg = await ping(true);
      const { paused } = await api.storage.local.get({ paused: false });
      const list = tabMedia.has(msg.tabId) ? [...tabMedia.get(msg.tabId).values()] : [];
      reply({ app: cfg, paused, media: list, version: api.runtime.getManifest().version });
    } else if (msg.cmd === 'pause') {
      await api.storage.local.set({ paused: !!msg.paused });
      refreshBadge();
      reply({ ok: true });
    } else if (msg.cmd === 'media') {
      // From the button on a video, or the popup list.
      const tabUrl = sender.tab ? sender.tab.url : msg.tabUrl;
      const page = msg.pageUrl || tabUrl || '';
      const src = msg.src && /^https?:/i.test(msg.src) ? msg.src : '';
      const url = src || page;
      const ok = /^https?:/i.test(url) && await sendToApp({
        kind: msg.kind || 'media',
        url,
        pageUrl: page,
        referrer: tabUrl || page,
        title: msg.title || '',
        fileName: msg.fileName || '',
        cookies: await cookiesFor(url),
        userAgent: navigator.userAgent,
      });
      if (!ok) { appCheckedAt = 0; refreshBadge(); }
      reply({ ok });
    } else if (msg.cmd === 'ping') {
      reply({ app: await ping() });
    }
  }
}

setInterval(() => refreshBadge(), 15000);
refreshBadge();
api.storage.local.get({ lang: 'ar' }).then(v => { IBDM_LANG = v.lang; buildMenus(); refreshBadge(); });
