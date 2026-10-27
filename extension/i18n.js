// IBDM Pro — the extension's English texts. The extension follows the app's language (Arabic by default).
// Used by the background worker (importScripts), the popup (<script>) and the page button (content script).
// Firefox has promise-based browser.*; Chromium browsers have chrome.* (also promise-based in Manifest V3).
var api = (typeof browser !== 'undefined' && browser.runtime) ? browser : chrome;

var IBDM_EN = {
  'تحميل بواسطة IBDM Pro': 'Download with IBDM Pro',
  'تحميل هذا الفيديو بواسطة IBDM Pro': 'Download this video with IBDM Pro',
  'تحميل فيديو هذه الصفحة بواسطة IBDM Pro': "Download this page's video with IBDM Pro",
  'IBDM Pro غير مشغّل': 'IBDM Pro is not running',
  'شغّل برنامج IBDM Pro ثم حاول مرة ثانية': 'Start IBDM Pro, then try again',
  'IBDM Pro — الالتقاط متوقف مؤقتاً': 'IBDM Pro — capturing paused',
  'IBDM Pro — متصل': 'IBDM Pro — connected',
  'IBDM Pro — البرنامج غير مشغّل': 'IBDM Pro — the app is not running',
  'IBDM Pro — اختر الجودة وحمّل': 'IBDM Pro — choose the quality and download',
  'تحميل هذا الفيديو': 'Download this video',
  'لم يصل رد — حدّث الصفحة وحاول مرة ثانية': 'No answer — reload the page and try again',
  'اسحب لتغيير المكان — نقرتين للرجوع للمكان الأصلي': 'Drag to move — double-click to put it back',
  'اسحب لتكبير أو تصغير الزر': 'Drag to make the button bigger or smaller',
  'إخفاء': 'Hide',
  'جاري الإرسال…': 'Sending…',
  'أعد تحميل الصفحة': 'Reload the page',
  '✓ تم — اختر الجودة بالبرنامج': '✓ Done — choose the quality in the app',
  'شغّل برنامج IBDM Pro أولاً': 'Start IBDM Pro first',
  'متصل ببرنامج IBDM Pro': 'Connected to IBDM Pro',
  ' (حدّث الإضافة من صفحة الإضافات ⟳)': ' (reload the extension from the extensions page ⟳)',
  'برنامج IBDM Pro غير مشغّل — شغّله من قائمة ابدأ': 'IBDM Pro is not running — start it from the Start menu',
  'لم يتم رصد ملفات فيديو بعد — شغّل الفيديو أو استخدم زر «تحميل فيديو هذه الصفحة».': 'No video files detected yet — play the video or use "Download this page\'s video".',
  'بث (m3u8)': 'Stream (m3u8)',
  'صوت': 'Audio',
  'فيديو': 'Video',
  'تحميل': 'Download',
  '✓ تم': '✓ Done',
  'البرنامج غير مشغّل': 'The app is not running',
  'مدير التحميل': 'Download manager',
  'جاري الاتصال بالبرنامج…': 'Connecting to the app…',
  'التقاط التحميلات من المتصفح': 'Catch downloads from the browser',
  'الفيديوهات في هذه الصفحة': 'Videos on this page',
  'تحميل فيديو هذه الصفحة': "Download this page's video",
  'برمجة و تطوير': 'Programmed and developed by',
  'ابراهيم الخزرجي': 'Ibrahim Al-Khazraji',
};
var IBDM_LANG = 'ar';
function t(ar) { return IBDM_LANG === 'en' && IBDM_EN[ar] !== undefined ? IBDM_EN[ar] : ar; }
