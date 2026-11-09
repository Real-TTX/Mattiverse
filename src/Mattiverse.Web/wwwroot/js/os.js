/* Mattiverse OS: the behaviour of the home screen (Pages/Shared/_Launcher.cshtml, css/os.css). Plain JavaScript, no build step.

   ONE window at a time, like the apps of a phone. Anything with data-window opens that page of Mattiverse in it (an iframe of the same origin;
   the page drops its menu there, js/window-mode.js). Red closes the window; yellow (or the bar at the bottom of a phone) goes home and keeps
   the app as it is, so a tap on its icon brings it back; green fills the screen. Back goes back inside the window.
   The address keeps the page of the window (#/Store), so a reload opens it again and the browser's Back goes home.

   An installed app opens in a new tab (its own address, target=_blank): the default for an app. Its menu (a right click, a long press) opens it
   in the window instead, and can make that the default for this app (kept in this browser).
   The search narrows the apps (the key / puts the focus there, Enter opens the first one). The clocks tick here. */
(function () {
  'use strict';

  var win = document.getElementById('os-window');
  if (!win) { return; }

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  var html = document.documentElement;
  var body = document.body;
  var frame = $('.os-window__frame', win);
  var scrim = $('.os-scrim');
  var titleEl = $('[data-win-title]', win);
  var iconEl = $('[data-win-icon]', win);
  var backBtn = $('[data-win-back]', win);
  var instance = html.getAttribute('data-os') || 'Mattiverse';
  var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var PREFS = 'mattiverse.os.window.';
  var MAX = 'mattiverse.os.max';

  var state = { open: false, start: null, stack: [], source: null, tab: null };

  // ------------------------------------------------------------------ the window

  function centre(el) {
    var r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }

  // The window grows out of the icon that opened it and shrinks back into it.
  function animate(name, from, done) {
    if (reduced || !from) { if (done) { done(); } return; }
    var r = win.getBoundingClientRect();
    win.style.transformOrigin = (from.x - r.left) + 'px ' + (from.y - r.top) + 'px';
    win.classList.add(name);
    var end = function () {
      win.removeEventListener('animationend', end);
      win.classList.remove(name);
      win.removeAttribute('style');
      if (done) { done(); }
    };
    win.addEventListener('animationend', end);
  }

  function setIcon(source) {
    iconEl.innerHTML = '';
    var pic = source && source.querySelector('.os-app__icon > .mv-appicon, .os-app__icon > .os-ico, .os-dock__app > .os-ico');
    if (pic) { iconEl.appendChild(pic.cloneNode(true)); }
  }

  function setTitle(text) {
    titleEl.textContent = text || '';
    win.setAttribute('aria-label', text || 'Window');
    frame.setAttribute('title', text || 'Window');
  }

  function markRunning() {
    $$('.is-running').forEach(function (el) { el.classList.remove('is-running'); });
    if (!state.start) { return; }
    $$('[data-window]').forEach(function (el) {
      if (el.getAttribute('data-window') === state.start && (el.classList.contains('os-app') || el.classList.contains('os-dock__app'))) { el.classList.add('is-running'); }
    });
  }

  function open(url, opts) {
    opts = opts || {};
    closeMenus();
    closeFolders();
    var resume = state.start === url && frame.getAttribute('src');
    state.source = opts.source || state.source;
    if (!resume) {
      state.start = url;
      state.stack = [];
      state.tab = null;
      backBtn.disabled = true;
      setTitle(opts.title || '');
      setIcon(opts.source);
      frame.setAttribute('src', url);
    }
    markRunning();
    win.hidden = false;
    scrim.hidden = false;
    body.classList.add('os-has-window');
    state.open = true;
    if (!opts.fromAddress) { history.pushState({ os: url }, '', '#' + (resume ? (currentPath() || url) : url)); }
    animate('is-opening', opts.source && body.contains(opts.source) ? centre(opts.source) : null, function () {
      try { frame.focus(); } catch (e) { /* the frame may not take the focus yet */ }
    });
  }

  // Home: the window goes, the app stays (close = false) or is gone (close = true).
  function home(close, fromAddress) {
    if (!state.open && !close) { return; }
    var from = state.source && body.contains(state.source) ? centre(state.source) : null;
    var finish = function () {
      win.hidden = true;
      scrim.hidden = true;
      body.classList.remove('os-has-window');
      if (close) {
        frame.removeAttribute('src');
        frame.src = 'about:blank';
        state.start = null;
        state.stack = [];
        setIcon(null);
        setTitle('');
      }
      markRunning();
      if (state.source && body.contains(state.source)) { try { state.source.focus(); } catch (e) { /* no focus */ } }
    };
    var wasOpen = state.open;
    state.open = false;
    if (!fromAddress && location.hash) { history.replaceState(null, '', location.pathname + location.search); }
    if (wasOpen) { animate('is-closing', from, finish); } else { finish(); }
  }

  function currentPath() {
    try {
      var loc = frame.contentWindow && frame.contentWindow.location;
      if (loc && loc.href !== 'about:blank' && loc.origin === location.origin) { return loc.pathname + loc.search; }
    } catch (e) { /* another origin */ }
    return null;
  }

  // Every page that loads in the window: its title, whether Back has somewhere to go, the address, the theme.
  frame.addEventListener('load', function () {
    var path = currentPath();
    if (!path) { return; }
    var s = state.stack;
    if (s.length > 1 && s[s.length - 2] === path) { s.pop(); } else if (s[s.length - 1] !== path) { s.push(path); }
    backBtn.disabled = s.length < 2;
    var doc = frame.contentDocument;
    if (doc) {
      setTitle((doc.title || '').split(' · ')[0].replace(instance, '').trim() || titleEl.textContent);
      var ext = doc.querySelector('[data-window-tab]');
      state.tab = ext ? ext.getAttribute('data-window-tab') : null;
      syncTheme();
    }
    if (state.open) { history.replaceState({ os: state.start }, '', '#' + path); }
  });

  $$('[data-win-close]', win).forEach(function (b) { b.addEventListener('click', function () { home(true); }); });
  $$('[data-win-home]', win).forEach(function (b) { b.addEventListener('click', function () { home(false); }); });
  backBtn.addEventListener('click', function () { try { frame.contentWindow.history.back(); } catch (e) { /* nothing to go back to */ } });
  $('[data-win-reload]', win).addEventListener('click', function () { try { frame.contentWindow.location.reload(); } catch (e) { frame.src = frame.src; } });
  $('[data-win-tab]', win).addEventListener('click', function () { window.open(state.tab || currentPath() || state.start, '_blank', 'noopener'); });
  $('[data-win-max]', win).addEventListener('click', function () {
    var max = win.classList.toggle('is-max');
    try { localStorage.setItem(MAX, max ? '1' : '0'); } catch (e) { /* private mode */ }
  });
  try { if (localStorage.getItem(MAX) === '1') { win.classList.add('is-max'); } } catch (e) { /* private mode */ }
  scrim.addEventListener('click', function () { home(false); });

  // A page in the window asks to go home (a link to /, or it landed on the home screen).
  window.addEventListener('message', function (e) {
    if (e.origin === location.origin && e.data && e.data.type === 'mv-home') { home(false); }
  });

  // The browser's Back and Forward, and an address with a window in it (#/Store).
  function fromAddress() {
    var h = location.hash;
    if (h.length > 2 && h.charAt(1) === '/') {
      var path = decodeURIComponent(h.slice(1));
      if (!state.open) { open(path, { fromAddress: true, title: '' }); }
    } else if (state.open) {
      home(false, true);
    }
  }
  window.addEventListener('popstate', fromAddress);
  fromAddress();

  // The theme of the home screen is the theme of the window.
  function syncTheme() {
    var doc;
    try { doc = frame.contentDocument; } catch (e) { return; }
    if (!doc || !doc.documentElement) { return; }
    var t = html.getAttribute('data-theme');
    if (t) { doc.documentElement.setAttribute('data-theme', t); } else { doc.documentElement.removeAttribute('data-theme'); }
  }
  new MutationObserver(syncTheme).observe(html, { attributes: true, attributeFilter: ['data-theme'] });

  // ------------------------------------------------------------------ opening things

  function prefersWindow(el) {
    var key = el.getAttribute('data-app-key');
    try { return !!key && localStorage.getItem(PREFS + key) === '1'; } catch (e) { return false; }
  }

  var suppressClick = false;
  document.addEventListener('click', function (e) {
    if (suppressClick) { suppressClick = false; e.preventDefault(); return; }
    var el = e.target.closest ? e.target.closest('[data-window]') : null;
    if (!el || e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) { return; }
    if (el.getAttribute('target') === '_blank' && !prefersWindow(el)) { return; }
    e.preventDefault();
    // From the person's menu the window grows out of the person's button.
    open(el.getAttribute('data-window'), { source: el.closest('.os-menu') ? $('[data-os-menu]') : el, title: el.getAttribute('data-title') });
  });

  // The logo in the status bar is the way home.
  var brand = $('.os-status__brand');
  if (brand) { brand.addEventListener('click', function (e) { if (state.open) { e.preventDefault(); home(false); } }); }

  // ------------------------------------------------------------------ menus: the person's, and the one of an app

  var appMenu = null;
  function closeMenus() {
    $$('.os-menu').forEach(function (m) { if (m !== appMenu) { m.hidden = true; } });
    $$('[data-os-menu]').forEach(function (b) { b.setAttribute('aria-expanded', 'false'); });
    if (appMenu) { appMenu.remove(); appMenu = null; }
  }
  document.addEventListener('click', function (e) {
    var toggle = e.target.closest ? e.target.closest('[data-os-menu]') : null;
    if (toggle) {
      var menu = document.getElementById(toggle.getAttribute('data-os-menu'));
      var show = menu.hidden;
      closeMenus();
      menu.hidden = !show;
      toggle.setAttribute('aria-expanded', show ? 'true' : 'false');
      if (show) { var first = $('.os-menu__item', menu); if (first) { first.focus(); } }
      return;
    }
    if (!(e.target.closest && e.target.closest('.os-menu'))) { closeMenus(); }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { closeMenus(); } });

  function icon(name) {
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
  }
  function item(iconName, text, attrs) {
    var b = document.createElement(attrs.href ? 'a' : 'button');
    b.className = 'os-menu__item';
    b.setAttribute('role', attrs.check ? 'menuitemcheckbox' : 'menuitem');
    if (!attrs.href) { b.type = 'button'; }
    Object.keys(attrs).forEach(function (k) { if (k !== 'check' && k !== 'run' && attrs[k] != null) { b.setAttribute(k, attrs[k]); } });
    if (attrs.check != null) { b.setAttribute('aria-checked', attrs.check ? 'true' : 'false'); }
    b.innerHTML = icon(iconName) + '<span></span>';
    b.lastChild.textContent = text;
    if (attrs.run) { b.addEventListener('click', function (e) { e.preventDefault(); closeMenus(); attrs.run(); }); }
    return b;
  }

  // The menu of an app: open it here or in a tab, always in the window, its details and settings in Mattiverse.
  function openAppMenu(el, x, y) {
    closeMenus();
    state.menuFor = el;
    var name = el.getAttribute('data-title') || '';
    var tab = el.getAttribute('data-tab') || '';
    var key = el.getAttribute('data-app-key');
    var menu = document.createElement('div');
    menu.className = 'os-menu';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', name);
    var head = document.createElement('div');
    head.className = 'os-menu__head';
    var pic = el.querySelector('.os-app__icon > .mv-appicon, .os-app__icon > .os-ico, .os-dock__app > .os-ico, :scope > .os-ico');
    if (pic) { head.appendChild(pic.cloneNode(true)); }
    var label = document.createElement('div');
    label.innerHTML = '<strong></strong><small></small>';
    label.firstChild.textContent = name;
    label.lastChild.textContent = el.getAttribute('title') ? el.getAttribute('title').split(' · ').slice(1).join(' · ') : 'Mattiverse';
    head.appendChild(label);
    menu.appendChild(head);
    var target = el.getAttribute('data-window');
    menu.appendChild(item('monitor', 'Open in the window', { run: function () { open(target, { source: el, title: name }); } }));
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { window.open(tab || target, '_blank', 'noopener'); } }));
    if (key && tab) {
      var always = prefersWindow(el);
      menu.appendChild(item('check', 'Always open in the window', {
        check: always,
        run: function () { try { if (always) { localStorage.removeItem(PREFS + key); } else { localStorage.setItem(PREFS + key, '1'); } } catch (e) { /* private mode */ } }
      }));
    }
    var details = el.getAttribute('data-details');
    var manage = el.getAttribute('data-manage');
    if (details || manage) {
      var sep = document.createElement('div');
      sep.className = 'os-menu__sep';
      menu.appendChild(sep);
      if (details) { menu.appendChild(item('info', 'Details', { run: function () { open(details, { source: el, title: name }); } })); }
      if (manage) { menu.appendChild(item('sliders', 'Manage', { run: function () { open(manage, { source: el, title: name }); } })); }
    }
    body.appendChild(menu);
    appMenu = menu;
    var w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.max(8, Math.min(x, window.innerWidth - w - 8)) + 'px';
    menu.style.top = Math.max(8, Math.min(y, window.innerHeight - h - 8)) + 'px';
    var first = $('.os-menu__item', menu);
    if (first) { first.focus(); }
  }

  var appSelector = '.os-app[data-window], .os-dock__app[data-window]';
  document.addEventListener('contextmenu', function (e) {
    var el = e.target.closest ? e.target.closest(appSelector) : null;
    if (!el) { return; }
    e.preventDefault();
    openAppMenu(el, e.clientX, e.clientY);
  });
  // A long press on a touch screen: the menu, and the tap that ends it does not open the app.
  var press = null;
  document.addEventListener('pointerdown', function (e) {
    if (e.pointerType === 'mouse') { return; }
    var el = e.target.closest ? e.target.closest(appSelector) : null;
    if (!el) { return; }
    var x = e.clientX, y = e.clientY;
    press = { x: x, y: y, timer: setTimeout(function () { press = null; suppressClick = true; var r = el.getBoundingClientRect(); openAppMenu(el, r.left, r.bottom + 8); }, 480) };
  });
  var cancelPress = function () { if (press) { clearTimeout(press.timer); press = null; } };
  document.addEventListener('pointerup', cancelPress);
  document.addEventListener('pointercancel', cancelPress);
  document.addEventListener('pointermove', function (e) { if (press && Math.abs(e.clientX - press.x) + Math.abs(e.clientY - press.y) > 12) { cancelPress(); } });

  // ------------------------------------------------------------------ folders (System: the engines), like the folders of a phone

  function closeFolders() {
    $$('.os-folder').forEach(function (f) {
      if (!f.hidden) {
        f.hidden = true;
        var btn = $('[data-os-folder="' + f.id + '"]');
        if (btn) { btn.setAttribute('aria-expanded', 'false'); }
      }
    });
  }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest ? e.target.closest('[data-os-folder]') : null;
    if (btn) {
      var folder = document.getElementById(btn.getAttribute('data-os-folder'));
      if (!folder) { return; }
      closeMenus();
      folder.hidden = false;
      btn.setAttribute('aria-expanded', 'true');
      var first = $('.os-app', folder);
      if (first) { first.focus(); }
      return;
    }
    // a click beside the panel closes the folder
    var openFolder = e.target.closest ? e.target.closest('.os-folder') : null;
    if (openFolder && !e.target.closest('.os-folder__panel')) { closeFolders(); }
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { closeFolders(); } });

  // ------------------------------------------------------------------ the search

  var find = $('[data-os-search]');
  if (find) {
    var filter = function () {
      var q = find.value.trim().toLowerCase();
      $$('.os-grid .os-app[data-name]').forEach(function (a) { a.hidden = q !== '' && a.getAttribute('data-name').indexOf(q) === -1; });
      $$('.os-grid [data-widget]').forEach(function (w) { w.hidden = q !== ''; });
    };
    find.addEventListener('input', filter);
    find.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { find.value = ''; filter(); find.blur(); }
      if (e.key === 'Enter') {
        var first = $$('.os-grid .os-app[data-name]').filter(function (a) { return !a.hidden; })[0];
        if (first) { e.preventDefault(); first.click(); }
      }
    });
    document.addEventListener('keydown', function (e) {
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented || state.open) { return; }
      var t = e.target;
      if (t && (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName))) { return; }
      e.preventDefault();
      find.focus();
    });
  }

  // ------------------------------------------------------------------ the clocks and the greeting

  var formats = {
    time: { hour: '2-digit', minute: '2-digit' },
    date: { day: 'numeric', month: 'long', year: 'numeric' },
    'short-date': { weekday: 'short', day: 'numeric', month: 'short' },
    weekday: { weekday: 'long' },
  };
  var greet = $('[data-os-greeting]');
  function tick() {
    var now = new Date();
    $$('[data-os-clock]').forEach(function (el) {
      var f = formats[el.getAttribute('data-os-clock')];
      if (!f) { return; }
      el.textContent = f.hour ? now.toLocaleTimeString([], f) : now.toLocaleDateString([], f);
    });
    if (greet) {
      var h = now.getHours();
      var part = h < 5 ? 'Good night' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
      greet.textContent = part + ', ' + greet.getAttribute('data-os-greeting');
    }
  }
  tick();
  setInterval(tick, 10000);
})();
