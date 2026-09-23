/*
 * ClassSoftwareHub 桌面版 —— 注入脚本（v5：不碰配色，只用站点自己的主题）
 *
 * 站点是 WinUIonWeb 做的，自带 win-titlebar（汉堡 + 图标 + 站名 + 居中搜索框），
 * 本来就是给 WinUI 窗口用的。这里只做两件事：
 *   1. 顶栏右侧给系统按钮组（最小化/最大化/关闭，也就是「三大金刚」）留出安全边距
 *   2. 算好「顶栏哪些区域可拖动、哪些是交互元素」告诉原生
 *   另外顺手把「站点当前是浅色还是深色」告诉原生（只影响系统按钮的图标颜色）。
 *
 * ⚠️ 配色 / 图层一律不覆盖 —— 完全用站点自己的主题（Nick 2026-09-20 决定：
 *    「你配得乱七八糟的，就用网站原先的主题就行」，视觉效果后置）。
 *
 * ⚠️ 站点会直接重写 <html> 的 class（如 theme-light），所以关键样式一律用
 *    「行内样式 + !important」，不依赖我们自己的 class 是否还在。
 *
 * 站点代码零改动；普通浏览器里这段脚本不存在。
 */
(function () {
  'use strict';

  if (window.__cshShellInstalled) return;
  window.__cshShellInstalled = true;

  var hasHost = !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
  var listeners = [];

  // ---------------- 与原生通信 ----------------
  function post(type, payload) {
    if (!hasHost) return;
    try {
      var msg = { type: type };
      if (payload && typeof payload === 'object') {
        for (var k in payload) {
          if (Object.prototype.hasOwnProperty.call(payload, k)) msg[k] = payload[k];
        }
      }
      window.chrome.webview.postMessage(msg);
    } catch (e) { /* ignore */ }
  }

  var api = {
    isDesktop: true,
    platform: 'winui3',
    available: hasHost,
    post: function (kind, payload) { post('shell.' + kind, payload); },
    onMessage: function (handler) {
      if (typeof handler !== 'function') return function () {};
      listeners.push(handler);
      return function () {
        var i = listeners.indexOf(handler);
        if (i >= 0) listeners.splice(i, 1);
      };
    },
    getInfo: function () { post('shell.getInfo'); },
    requestState: function () { post('shell.getState'); },
    log: function (message) { post('shell.log', { message: String(message) }); }
  };
  window.cshShell = window.cshShell || api;

  if (hasHost) {
    window.chrome.webview.addEventListener('message', function (e) {
      var data = e.data;
      try { if (typeof data === 'string') data = JSON.parse(data); } catch (err) { /* 原样 */ }
      try { handleNativeMessage(data); } catch (err2) { /* ignore */ }
      for (var i = 0; i < listeners.length; i++) {
        try { listeners[i](data); } catch (err3) { /* ignore */ }
      }
    });
  }

  // ---------------- 配置 ----------------
  var TITLEBAR_SELECTORS = ['.win-titlebar', '[data-csh-titlebar]', 'header.app-header', 'header', '.app-header', '.topbar'];
  var INTERACTIVE_SELECTOR = 'button,input,select,textarea,a,[role="button"],[role="searchbox"],[role="link"],[data-no-drag],[contenteditable="true"]';
  var DRAG_SPACER_SELECTORS = ['.win-titlebar-min-drag-region', '[data-csh-drag-spacer]'];

  // 层次配色：**暂时不用**（2026-09-20 Nick 决定：注入脚本不碰配色，先用站点自己的主题；
  // 视觉效果后面再单独设计）。留着是为了以后要「亚克力融合」时有个起点。
  var PALETTE = {
    light: {
      base: 'rgba(243, 243, 243, 0.92)',
      surface: 'rgba(255, 255, 255, 0.62)',
      opaque: '#F3F3F3'
    },
    dark: {
      base: 'rgba(32, 32, 32, 0.92)',
      surface: 'rgba(255, 255, 255, 0.05)',
      opaque: '#202020'
    }
  };

  var captionInset = 144;      // 系统按钮组宽度（CSS px）——原生会告知真实值
  var captionHeight = 48;
  var transparentMode = true;  // false = 不透明兜底
  var currentTheme = null;     // 'light' | 'dark'

  // 只有主站页面才做「界面融合」；外站标签页（应用内新标签打开的第三方站点）完全不动
  var SITE_HOSTS = ['classsoftwarehub.us.ci', 'classsoftwarehub.xfane.com', '127.0.0.1', 'localhost'];
  function isSitePage() {
    try {
      if (document.querySelector('.win-titlebar')) return true;      // 站点特征
      var h = (location.hostname || '').toLowerCase();
      return SITE_HOSTS.indexOf(h) >= 0;
    } catch (e) { return false; }
  }

  // ---------------- 行内样式工具 ----------------
  function setImp(el, prop, value) {
    if (!el || !el.style) return;
    try { el.style.setProperty(prop, value, 'important'); } catch (e) { /* ignore */ }
  }

  function isDarkTheme() {
    var html = document.documentElement;
    var cls = ' ' + ((html.className || '') + ' ') + ' ';
    if (cls.indexOf('theme-dark') >= 0 || cls.indexOf('dark-theme') >= 0) return true;
    if (cls.indexOf('theme-light') >= 0 || cls.indexOf('light-theme') >= 0) return false;

    // 兜底：看文字色亮度
    var probe = document.querySelector('#app') || document.body || html;
    var raw = '';
    try { raw = getComputedStyle(probe).getPropertyValue('--text-primary').trim(); } catch (e) { /* ignore */ }
    var m = String(raw).match(/rgba?\(([^)]+)\)/);
    if (m) {
      var p = m[1].split(',').map(function (s) { return parseFloat(s); });
      if (p.length >= 3) {
        var lum = (0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]) / 255;
        return lum > 0.5;
      }
    }
    return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  }

  function findTitlebar() {
    for (var i = 0; i < TITLEBAR_SELECTORS.length; i++) {
      var el = document.querySelector(TITLEBAR_SELECTORS[i]);
      if (el) {
        var r = el.getBoundingClientRect();
        if (r.width > 100 && r.height > 20 && r.top < 80) return el;
      }
    }
    return null;
  }

  function findDragSpacerWidth() {
    for (var i = 0; i < DRAG_SPACER_SELECTORS.length; i++) {
      var el = document.querySelector(DRAG_SPACER_SELECTORS[i]);
      if (el) {
        var w = el.getBoundingClientRect().width;
        if (w > 0 && w < 400) return Math.round(w);
      }
    }
    return 0;
  }

  function captionSpace() {
    // 站点可能已经预留了一部分（例如 48px 的 min-drag-region），补差额即可
    var reserved = findDragSpacerWidth();
    return Math.max(0, Math.round(captionInset - reserved));
  }

  // ---------------- 外观：一次性把层次压上去 ----------------
  function applyLooks() {
    if (!isSitePage()) return;     // 外站标签页不改样式

    var dark = isDarkTheme();
    if (dark !== currentTheme) {
      currentTheme = dark;
      post('shell.webTheme', { value: dark ? 'dark' : 'light' });
    }

    // ============================================================
    // 配色 / 图层：一律不碰 —— 完全用站点自己的主题（2026-09-20 Nick 决定：
    // 注入脚本配的乱，先不要；视觉效果后面再单独设计）。
    // 这里只做一件事：顶栏右侧给系统按钮组（三大金刚）留出安全区，
    // 免得系统按钮盖住站点标题栏自己的内容。
    // ============================================================
    var tb = document.querySelector('.win-titlebar');
    if (tb) {
      setImp(tb, 'box-sizing', 'border-box');
      setImp(tb, 'padding-right', captionSpace() + 'px');
    }
  }

  // ---------------- 可拖动区域计算 ----------------
  var lastRegionsJson = '';
  function reportTitlebarRegions(force) {
    if (!isSitePage()) return;
    var tb = findTitlebar();
    if (!tb) return;

    var tbRect = tb.getBoundingClientRect();
    var bandTop = Math.round(tbRect.top);
    var bandHeight = Math.round(Math.min(Math.max(tbRect.height, 24), captionHeight));

    var blockers = [];
    var nodes = tb.querySelectorAll(INTERACTIVE_SELECTOR);
    for (var i = 0; i < nodes.length; i++) {
      var r = nodes[i].getBoundingClientRect();
      if (r.width < 4 || r.height < 4) continue;
      if (r.bottom <= bandTop || r.top >= bandTop + bandHeight) continue;
      blockers.push({ x: Math.round(r.left), w: Math.round(r.width) });
    }
    blockers.sort(function (a, b) { return a.x - b.x; });

    var left = Math.round(tbRect.left);
    var right = Math.round(tbRect.right) - Math.round(captionInset); // 系统按钮区不参与拖动
    var segments = [];
    var cursor = left;
    for (var j = 0; j < blockers.length; j++) {
      var b = blockers[j];
      if (b.x > cursor) segments.push({ x: cursor, y: bandTop, w: b.x - cursor, h: bandHeight });
      cursor = Math.max(cursor, b.x + b.w);
    }
    if (cursor < right) segments.push({ x: cursor, y: bandTop, w: right - cursor, h: bandHeight });

    var payload = {
      titleBar: { x: left, y: bandTop, w: Math.round(tbRect.width), h: bandHeight },
      caption: segments,
      captionInset: captionInset,
      height: bandHeight,
      theme: currentTheme || 'light'
    };
    var json = JSON.stringify(payload);
    if (!force && json === lastRegionsJson) return;
    lastRegionsJson = json;
    post('shell.titlebar', payload);
  }

  // ---------------- 拖动：网页判断 + 桥接消息（规格书路径） ----------------
  function markNoDrag(tb) {
    // 显式把交互元素标记为不可拖动，方便排查
    try {
      var nodes = tb.querySelectorAll(INTERACTIVE_SELECTOR);
      for (var i = 0; i < nodes.length; i++) {
        if (!nodes[i].hasAttribute('data-csh-no-drag')) {
          nodes[i].setAttribute('data-csh-no-drag', '1');
        }
      }
    } catch (e) { /* ignore */ }
  }

  function installDragFallback() {
    if (!isSitePage()) return;
    var tb = findTitlebar();
    if (!tb || tb.__cshDrag) return;
    tb.__cshDrag = true;

    markNoDrag(tb);
    // 拖动区不让选中文字（避免拖出选区）
    try {
      tb.style.setProperty('-webkit-user-select', 'none', 'important');
      tb.style.setProperty('user-select', 'none', 'important');
    } catch (e) { /* ignore */ }

    // 指针按下：交互元素（搜索框/汉堡/链接/[data-csh-no-drag]）放行，其余发拖动消息
    tb.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) return;
      var t = e.target;
      if (t && t.closest && t.closest(INTERACTIVE_SELECTOR)) return;
      try { e.preventDefault(); } catch (err) { /* ignore */ }
      post('start-window-drag');
    }, true);

    // 双击空白处：切换最大化/还原
    tb.addEventListener('dblclick', function (e) {
      var t = e.target;
      if (t && t.closest && t.closest(INTERACTIVE_SELECTOR)) return;
      post('toggle-window-maximize');
    }, true);
  }

  function handleNativeMessage(msg) {
    if (!msg || typeof msg !== 'object') return;
    switch (msg.type) {
      case 'shell.captionInset':
        if (typeof msg.value === 'number' && msg.value > 0) captionInset = Math.round(msg.value);
        if (typeof msg.height === 'number' && msg.height > 0) captionHeight = Math.round(msg.height);
        applyLooks();
        reportTitlebarRegions(true);
        break;

      case 'shell.titlebarUpdate':
        applyLooks();
        reportTitlebarRegions(true);
        break;

      case 'shell.webTransparent':
        transparentMode = msg.value !== false;
        applyLooks();
        break;

      case 'shell.applyWebTheme':
        try {
          if (msg.value === 'light' || msg.value === 'dark' || msg.value === 'system') {
            localStorage.setItem('winui-theme-setting', msg.value);
            location.reload();
          }
        } catch (e) { /* ignore */ }
        break;
    }
  }

  // ---------------- 主流程 ----------------
  function refresh(force) {
    applyLooks();
    installDragFallback();
    reportTitlebarRegions(!!force);
  }

  function start() {
    refresh(true);

    // 站点会重写 <html> class / 路由切换会重建 DOM → 持续跟进
    try {
      if (window.MutationObserver) {
        var mo = new MutationObserver(function () {
          clearTimeout(mo.__t);
          mo.__t = setTimeout(function () { refresh(false); }, 50);
        });
        mo.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] });
      }
    } catch (e) { /* ignore */ }

    window.addEventListener('resize', function () { refresh(true); });
    window.addEventListener('hashchange', function () { setTimeout(function () { refresh(true); }, 150); });
    window.addEventListener('load', function () { refresh(true); });

    // 前 20 秒每秒补一次（首屏/皮肤/懒加载兜底）
    var ticks = 0;
    var timer = setInterval(function () {
      ticks++;
      refresh(false);
      if (ticks > 20) clearInterval(timer);
    }, 1000);
  }

  if (document.readyState === 'loading') {
    start();
    document.addEventListener('DOMContentLoaded', function () {
      refresh(true);
      post('shell.domReady', { title: document.title });
    });
  } else {
    start();
    post('shell.domReady', { title: document.title });
  }

  post('shell.ready', {
    url: location.href,
    title: document.title,
    ua: navigator.userAgent,
    lang: navigator.language,
    theme: isDarkTheme() ? 'dark' : 'light',
    themeSetting: localStorage.getItem('winui-theme-setting'),
    materialSetting: localStorage.getItem('winui-material-setting')
  });
})();
