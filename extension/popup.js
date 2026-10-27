// IBDM Pro popup: app status, pause capturing, and the videos found on this tab.
const $ = id => document.getElementById(id);

function fmtSize(b) {
  if (!(b > 0)) return '';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return `${b.toFixed(i ? 1 : 0)} ${u[i]}`;
}

function shortName(url) {
  try {
    const u = new URL(url);
    const last = decodeURIComponent(u.pathname.split('/').pop() || '');
    return last || u.hostname;
  } catch (e) { return url; }
}

async function load() {
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });
  const st = await api.runtime.sendMessage({ cmd: 'status', tabId: tab ? tab.id : -1 });
  $('ver').textContent = 'v' + st.version;
  // Same style as the app (remembered for when the app is not running).
  let theme = (await api.storage.local.get({ theme: 'glass' })).theme;
  if (st.app && st.app.theme) { theme = st.app.theme; api.storage.local.set({ theme }); }
  document.body.classList.toggle('dark', theme === 'dark');
  document.body.classList.toggle('light', theme === 'light');
  // …and the same language.
  IBDM_LANG = (st.app && st.app.lang) || (await api.storage.local.get({ lang: 'ar' })).lang;
  if (st.app && st.app.lang) api.storage.local.set({ lang: IBDM_LANG });
  document.documentElement.lang = IBDM_LANG;
  document.documentElement.dir = IBDM_LANG === 'en' ? 'ltr' : 'rtl';
  for (const el of document.querySelectorAll('[data-t]')) el.textContent = t(el.dataset.t);
  const box = $('status');
  if (st.app) {
    box.className = 'status on';
    $('statusText').textContent = `${t('متصل ببرنامج IBDM Pro')} ${st.app.version}` + (st.app.version !== st.version ? t(' (حدّث الإضافة من صفحة الإضافات ⟳)') : '');
  } else {
    box.className = 'status off';
    $('statusText').textContent = t('برنامج IBDM Pro غير مشغّل — شغّله من قائمة ابدأ');
  }
  $('capture').checked = !st.paused && !!(st.app && st.app.capture);
  $('capture').disabled = !st.app;

  const list = $('media');
  list.innerHTML = '';
  if (!st.media.length) {
    const p = document.createElement('div');
    p.className = 'empty';
    p.textContent = t('لم يتم رصد ملفات فيديو بعد — شغّل الفيديو أو استخدم زر «تحميل فيديو هذه الصفحة».');
    list.appendChild(p);
  }
  for (const m of st.media.slice().reverse()) {
    const row = document.createElement('div');
    row.className = 'item';
    const n = document.createElement('div');
    n.className = 'n';
    const name = document.createElement('div');
    name.textContent = shortName(m.url);
    name.title = m.url;
    const meta = document.createElement('small');
    meta.textContent = (m.type === 'stream' ? t('بث (m3u8)') : m.type === 'audio' ? t('صوت') : t('فيديو')) + (m.size > 0 ? ' — ' + fmtSize(m.size) : '');
    n.append(name, meta);
    const b = document.createElement('button');
    b.textContent = t('تحميل');
    b.onclick = () => send({ src: m.url, kind: 'media' }, tab, b);
    row.append(n, b);
    list.appendChild(row);
  }
  $('pageVideo').onclick = () => send({ src: '', kind: 'media' }, tab, $('pageVideo'));
}

function send(opts, tab, btn) {
  api.runtime.sendMessage({ cmd: 'media', src: opts.src, kind: opts.kind, pageUrl: tab.url, tabUrl: tab.url, title: tab.title }).catch(() => null).then(res => {
    btn.textContent = res && res.ok ? t('✓ تم') : t('البرنامج غير مشغّل');
    if (res && res.ok) setTimeout(() => window.close(), 700);
  });
}

$('capture').addEventListener('change', async e => {
  await api.runtime.sendMessage({ cmd: 'pause', paused: !e.target.checked });
});

load();
