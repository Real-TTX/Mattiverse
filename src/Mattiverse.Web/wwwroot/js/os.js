/* Mattiverse OS: the behaviour of the home screen (Pages/Shared/_Launcher.cshtml, css/os.css). Plain JavaScript, no build step.

   ONE window at a time, like the apps of a phone. Anything with data-window opens that page of Mattiverse in it (an iframe of the same origin;
   the page drops its menu there, js/window-mode.js). Red closes the window; yellow (or the bar at the bottom of a phone) goes home and keeps
   the app as it is, so a tap on its icon brings it back; green fills the screen. Back goes back inside the window.
   The address keeps the page of the window (#/Store), so a reload opens it again and the browser's Back goes home.

   An installed app opens in a new tab (its own address, target=_blank): the default for an app. Its menu (a right click, a long press) opens it
   in the window instead, and can make that the default for this app (kept in this browser).

   The person arranges the home screen, and the account keeps it (PUT /api/home: pages, what is off the screen, the sizes of the widgets):
   - a right click (a long press on a touch screen) gives everything its menu: an app, a widget, the dock, the window's bar, the empty screen;
     Shift with the right click is the browser's own menu, and a field keeps the browser's menu (to paste);
   - a thing is dragged with the mouse to another place; at the edge of the screen, or over a dot, the page turns (past the last one a new page begins);
   - "Edit the home screen" makes the things wiggle: a finger drags them too, a minus takes one off the screen, "Show again" brings it back;
   - the pages turn with a swipe, the dots below them, or the arrow keys.
   The search narrows the apps of all pages (the key / puts the focus there, Enter opens the first one). The clocks tick here. */
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
  var PAGE = 'mattiverse.os.page';

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

  function goBack() { try { frame.contentWindow.history.back(); } catch (e) { /* nothing to go back to */ } }
  function reloadWindow() { try { frame.contentWindow.location.reload(); } catch (e) { frame.src = frame.src; } }
  function windowInTab() { window.open(state.tab || currentPath() || state.start, '_blank', 'noopener'); }
  function toggleMax() {
    var max = win.classList.toggle('is-max');
    try { localStorage.setItem(MAX, max ? '1' : '0'); } catch (e) { /* private mode */ }
  }

  $$('[data-win-close]', win).forEach(function (b) { b.addEventListener('click', function () { home(true); }); });
  $$('[data-win-home]', win).forEach(function (b) { b.addEventListener('click', function () { home(false); }); });
  backBtn.addEventListener('click', goBack);
  $('[data-win-reload]', win).addEventListener('click', reloadWindow);
  $('[data-win-tab]', win).addEventListener('click', windowInTab);
  $('[data-win-max]', win).addEventListener('click', toggleMax);
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
      // Closed, or open on another page (an address typed while a window is open): that page in the window.
      if (!state.open || path !== currentPath()) { open(path, { fromAddress: true, title: '' }); }
    } else if (state.open) {
      home(false, true);
    }
  }
  window.addEventListener('popstate', fromAddress);

  // The theme of the home screen is the theme of the window.
  function syncTheme() {
    var doc;
    try { doc = frame.contentDocument; } catch (e) { return; }
    if (!doc || !doc.documentElement) { return; }
    var t = html.getAttribute('data-theme');
    if (t) { doc.documentElement.setAttribute('data-theme', t); } else { doc.documentElement.removeAttribute('data-theme'); }
  }
  new MutationObserver(syncTheme).observe(html, { attributes: true, attributeFilter: ['data-theme'] });

  // ------------------------------------------------------------------ the pages of the home screen

  var pagesEl = $('[data-os-pages]');
  var dotsEl = $('[data-os-dots]');
  var hiddenBox = $('[data-os-hidden]');
  var editbar = $('[data-os-editbar]');
  var editing = false;
  var drag = null;
  var thing = '.os-page > [data-home-key]';

  function pages() { return pagesEl ? $$('.os-page', pagesEl) : []; }
  function things(root) { return $$(':scope > [data-home-key]', root); }
  function keyOf(el) { return el.getAttribute('data-home-key'); }
  function nameOf(el) { return el.getAttribute('data-home-name') || el.getAttribute('data-title') || ''; }

  // From the start of one page to the start of the next (the gap between them included).
  function stride() {
    var p = pages();
    if (p.length > 1) { return p[1].getBoundingClientRect().left - p[0].getBoundingClientRect().left; }
    return pagesEl.clientWidth;
  }
  function currentPage() {
    var s = stride();
    var n = pages().length;
    return s > 0 ? Math.max(0, Math.min(n - 1, Math.round(pagesEl.scrollLeft / s))) : 0;
  }
  function goToPage(i, smooth) {
    if (!pagesEl) { return; }
    pagesEl.scrollTo({ left: Math.max(0, i) * stride(), behavior: smooth && !reduced ? 'smooth' : 'auto' });
  }

  function labelPages() {
    var p = pages();
    p.forEach(function (page, i) { page.setAttribute('aria-label', 'Page ' + (i + 1) + ' of ' + p.length); });
  }
  function markEmpty() {
    pages().forEach(function (page) { page.classList.toggle('is-empty', things(page).length === 0); });
  }

  function renderDots() {
    if (!dotsEl) { return; }
    var n = pages().length;
    var now = currentPage();
    dotsEl.innerHTML = '';
    dotsEl.hidden = n < 2 || (pagesEl && pagesEl.classList.contains('is-searching'));
    for (var i = 0; i < n; i++) {
      var dot = document.createElement('button');
      dot.type = 'button';
      dot.className = 'os-dot';
      dot.setAttribute('role', 'tab');
      dot.setAttribute('aria-selected', i === now ? 'true' : 'false');
      dot.setAttribute('aria-label', 'Page ' + (i + 1) + ' of ' + n);
      dot.setAttribute('data-page-index', String(i));
      dotsEl.appendChild(dot);
    }
  }
  function updateDots() {
    var now = currentPage();
    $$('.os-dot', dotsEl).forEach(function (d, i) { d.setAttribute('aria-selected', i === now ? 'true' : 'false'); });
    try { sessionStorage.setItem(PAGE, String(now)); } catch (e) { /* private mode */ }
  }

  function createPage() {
    var page = document.createElement('div');
    page.className = 'os-grid os-page is-empty';
    page.setAttribute('data-page', '');
    page.setAttribute('role', 'group');
    pagesEl.appendChild(page);
    labelPages();
    renderDots();
    return page;
  }
  // Empty pages go (while editing they stay, so that something can be put there); one page always stays.
  function pruneEmptyPages() {
    var now = currentPage();
    pages().forEach(function (page) { if (things(page).length === 0 && pages().length > 1) { page.remove(); } });
    labelPages();
    renderDots();
    goToPage(Math.min(now, pages().length - 1), false);
  }

  if (pagesEl) {
    var ticking = false;
    pagesEl.addEventListener('scroll', function () {
      if (ticking) { return; }
      ticking = true;
      requestAnimationFrame(function () { ticking = false; updateDots(); });
    }, { passive: true });
    if (dotsEl) {
      dotsEl.addEventListener('click', function (e) {
        var dot = e.target.closest('.os-dot');
        if (dot) { goToPage(parseInt(dot.getAttribute('data-page-index'), 10), true); }
      });
    }
    // Native dragging of a link or a picture would take the place of moving a thing.
    pagesEl.addEventListener('dragstart', function (e) { e.preventDefault(); });
    markEmpty();
    renderDots();
    // A reload (the home screen refreshes itself) comes back to the page the person was on.
    try {
      var kept = parseInt(sessionStorage.getItem(PAGE) || '0', 10);
      if (kept > 0 && kept < pages().length) { goToPage(kept, false); }
    } catch (e) { /* private mode */ }
    updateDots();
  }

  // ------------------------------------------------------------------ keeping the arrangement (the account has it)

  var saveTimer = null;
  function layout() {
    var sizes = {};
    $$('[data-home-key][data-size]').forEach(function (el) {
      if (el.closest('.os-ghost')) { return; }
      var size = el.getAttribute('data-size');
      if (size && size !== el.getAttribute('data-size-default')) { sizes[keyOf(el)] = size; }
    });
    return {
      pages: pages().map(function (p) { return things(p).map(keyOf); }).filter(function (keys) { return keys.length > 0; }),
      hidden: hiddenBox ? things(hiddenBox).map(keyOf) : [],
      sizes: sizes,
    };
  }
  function token() {
    var t = $('input[name="__RequestVerificationToken"]');
    return t ? t.value : '';
  }
  function failed() {
    if (window.MatUi && window.MatUi.toast) { window.MatUi.toast('The home screen could not be saved. Try again in a moment.', { tone: 'danger' }); }
  }
  var inflight = Promise.resolve();
  function send(keepalive) {
    saveTimer = null;
    inflight = fetch('/api/home', {
      method: 'PUT',
      credentials: 'same-origin',
      keepalive: !!keepalive,
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
      body: JSON.stringify(layout()),
    }).then(function (r) { if (!r.ok) { failed(); } }, function () { if (!keepalive) { failed(); } });
  }
  function save() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(function () { send(false); }, 400);
  }
  window.addEventListener('pagehide', function () { if (saveTimer) { clearTimeout(saveTimer); send(true); } });

  function resetLayout() {
    var ask = window.MatUi && window.MatUi.confirm
      ? window.MatUi.confirm('The pages, the places of the apps and widgets, their sizes and what you took off the screen are forgotten, and the home screen is as it comes.', { title: 'Reset the home screen?', ok: 'Reset', danger: true })
      : Promise.resolve(window.confirm('Reset the home screen?'));
    ask.then(function (yes) {
      if (!yes) { return; }
      clearTimeout(saveTimer);
      saveTimer = null;
      // After a save that is still on its way, so that it cannot bring the old arrangement back.
      inflight.then(function () { return fetch('/api/home', { method: 'DELETE', credentials: 'same-origin', headers: { 'RequestVerificationToken': token() } }); })
        .then(function (r) {
          if (!r.ok) { failed(); return; }
          try { sessionStorage.removeItem(PAGE); } catch (e) { /* private mode */ }
          location.reload();
        }, failed);
    });
  }

  // ------------------------------------------------------------------ moving things: animated, to a place, to a page, off the screen

  // FLIP: where everything was, then let each glide from there to its new place.
  function snapshot() {
    var at = [];
    pages().forEach(function (page) { things(page).forEach(function (el) { at.push({ el: el, r: el.getBoundingClientRect() }); }); });
    return at;
  }
  function glide(at) {
    if (reduced) { return; }
    at.forEach(function (a) {
      if (drag && a.el === drag.el) { return; }
      var r = a.el.getBoundingClientRect();
      var dx = a.r.left - r.left;
      var dy = a.r.top - r.top;
      // Only what moved on the page that is seen; a thing that changed pages jumps.
      if ((dx || dy) && Math.abs(dx) < window.innerWidth && a.el.animate) {
        a.el.animate([{ transform: 'translate(' + dx + 'px, ' + dy + 'px)' }, { transform: 'none' }], { duration: 220, easing: 'cubic-bezier(.2, .9, .25, 1)' });
      }
    });
  }
  function moveTo(el, page, before) {
    if (before === el || (el.parentNode === page && el.nextSibling === before)) { return; }
    var at = snapshot();
    page.insertBefore(el, before || null);
    glide(at);
    markEmpty();
  }
  function moveToPage(el, index) {
    var page = index >= pages().length ? createPage() : pages()[index];
    moveTo(el, page, null);
    goToPage(index, true);
    if (!editing) { setTimeout(pruneEmptyPages, 450); }
    save();
  }
  function moveBy(el, step) {
    var list = things(el.parentNode);
    var i = list.indexOf(el) + step;
    if (i < 0 || i >= list.length) { return; }
    moveTo(el, el.parentNode, step < 0 ? list[i] : list[i].nextSibling);
    save();
    try { el.focus(); } catch (e) { /* not focusable */ }
  }

  var sizeClass = { small: 'os-w2', medium: 'os-w2x2', wide: 'os-w4x2' };
  function setSize(el, size) {
    var at = snapshot();
    Object.keys(sizeClass).forEach(function (s) { el.classList.remove(sizeClass[s]); });
    el.classList.add(sizeClass[size] || 'os-w2');
    el.setAttribute('data-size', size);
    glide(at);
    save();
  }

  function hide(el) {
    if (!hiddenBox) { return; }
    var at = snapshot();
    $$(':scope > .os-remove', el).forEach(function (b) { b.remove(); });
    hiddenBox.appendChild(el);
    glide(at);
    markEmpty();
    updateEditbar();
    save();
  }
  function restore(el) {
    var page = pages()[currentPage()] || pages()[0];
    if (!page) { return; }
    var at = snapshot();
    page.appendChild(el);
    if (editing) { addRemove(el); }
    glide(at);
    if (!reduced && el.animate) { el.animate([{ transform: 'scale(.6)', opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: 260, easing: 'cubic-bezier(.2, .9, .25, 1)' }); }
    markEmpty();
    updateEditbar();
    save();
  }

  // ------------------------------------------------------------------ editing the home screen (wiggle, minus, Show again, Done)

  function addRemove(el) {
    if ($(':scope > .os-remove', el)) { return; }
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'os-remove';
    b.setAttribute('aria-label', 'Remove ' + nameOf(el) + ' from the home screen');
    b.title = 'Remove from the home screen';
    b.innerHTML = icon('minus');
    el.appendChild(b);
  }
  function updateEditbar() {
    var show = editbar && $('[data-os-showhidden]', editbar);
    if (show) { show.hidden = !hiddenBox || things(hiddenBox).length === 0; }
  }
  function hold() { body.classList.toggle('os-hold', editing || !!(drag && drag.started)); }
  function setEditing(on) {
    if (!pagesEl || editing === on) { return; }
    editing = on;
    body.classList.toggle('os-editing', on);
    if (editbar) { editbar.hidden = !on; }
    closeMenus();
    if (on) {
      pages().forEach(function (page) { things(page).forEach(addRemove); });
      updateEditbar();
      var done = editbar && $('[data-os-done]', editbar);
      if (done) { done.focus(); }
    } else {
      $$('.os-remove').forEach(function (b) { b.remove(); });
      pruneEmptyPages();
      save();
    }
    hold();
  }
  if (editbar) {
    editbar.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) { return; }
      // The bar's own click: it must not reach the document, which closes a menu the click did not land in (the one "Show again" just opened).
      e.stopPropagation();
      if (b.hasAttribute('data-os-done')) { setEditing(false); }
      if (b.hasAttribute('data-os-addpage')) { createPage(); goToPage(pages().length - 1, true); }
      if (b.hasAttribute('data-os-showhidden')) { var r = b.getBoundingClientRect(); openHiddenMenu(r.left, r.bottom + 6); }
    });
  }

  // ------------------------------------------------------------------ dragging a thing (the mouse at once; a finger while editing)

  function startDrag(e) {
    var el = drag.el;
    var r = el.getBoundingClientRect();
    drag.started = true;
    drag.dx = e.clientX - r.left;
    drag.dy = e.clientY - r.top;
    drag.origin = { parent: el.parentNode, next: el.nextSibling };
    var ghost = el.cloneNode(true);
    ghost.classList.add('os-ghost');
    ghost.removeAttribute('data-home-key');
    ghost.setAttribute('aria-hidden', 'true');
    $$('.os-remove, [id]', ghost).forEach(function (n) { if (n.classList.contains('os-remove')) { n.remove(); } else { n.removeAttribute('id'); } });
    ghost.style.width = r.width + 'px';
    ghost.style.height = r.height + 'px';
    ghost.style.left = r.left + 'px';
    ghost.style.top = r.top + 'px';
    body.appendChild(ghost);
    drag.ghost = ghost;
    el.classList.add('is-placeholder');
    body.classList.add('os-dragging');
    cancelPress();
    closeMenus();
    hold();
  }

  function clearTurn() {
    if (drag && drag.turn) { clearTimeout(drag.turn.timer); drag.turn = null; }
    $$('.os-dot.is-target').forEach(function (d) { d.classList.remove('is-target'); });
  }
  // Waiting at the edge of the screen (or over a dot) turns the page; the thing comes along to its end. Past the last page a new one begins,
  // unless the last one holds nothing but the thing itself.
  function turnTo(index, why) {
    if (drag.turn && drag.turn.why === why) { return; }
    clearTurn();
    drag.turn = {
      why: why,
      timer: setTimeout(function () {
        if (!drag) { return; }
        var p = pages();
        if (index < 0) { drag.turn = null; return; }
        if (index >= p.length) {
          var last = p[p.length - 1];
          if (things(last).filter(function (t) { return t !== drag.el; }).length === 0) { drag.turn = null; return; }
          createPage();
        }
        moveTo(drag.el, pages()[index], null);
        goToPage(index, true);
        drag.turn = null;
      }, why.indexOf('dot') === 0 ? 420 : 650),
    };
  }

  function moveDrag(x, y) {
    drag.ghost.style.left = (x - drag.dx) + 'px';
    drag.ghost.style.top = (y - drag.dy) + 'px';
    var under = document.elementFromPoint(x, y);
    var dot = under && under.closest ? under.closest('.os-dot') : null;
    if (dot) {
      dot.classList.add('is-target');
      turnTo(parseInt(dot.getAttribute('data-page-index'), 10), 'dot' + dot.getAttribute('data-page-index'));
      return;
    }
    var box = pagesEl.getBoundingClientRect();
    var now = currentPage();
    if (x < box.left + 40) { turnTo(now - 1, 'left' + now); } else if (x > box.right - 40) { turnTo(now + 1, 'right' + now); } else { clearTurn(); }

    var page = under && under.closest ? under.closest('.os-page') : null;
    if (!page || !pagesEl.contains(page)) { return; }
    var target = under.closest(thing);
    if (target === drag.el) { return; }
    if (!target) {
      // The empty part of a page: the thing goes to its end, when the pointer is past the last one.
      var list = things(page);
      var last = list[list.length - 1];
      if (last !== drag.el) {
        var lr = last ? last.getBoundingClientRect() : null;
        if (!lr || y > lr.bottom || (y >= lr.top && x > lr.right)) { moveTo(drag.el, page, null); drag.last = null; }
      }
      return;
    }
    var tr = target.getBoundingClientRect();
    var after = x > tr.left + tr.width / 2;
    if (drag.last && drag.last.target === target && drag.last.after === after) { return; }
    drag.last = { target: target, after: after };
    moveTo(drag.el, page, after ? target.nextSibling : target);
  }

  function endDrag(cancel) {
    var d = drag;
    drag = null;
    if (!d) { return; }
    if (d.turn) { clearTimeout(d.turn.timer); }
    $$('.os-dot.is-target').forEach(function (n) { n.classList.remove('is-target'); });
    if (!d.started) { hold(); return; }
    if (cancel && d.origin.parent.isConnected) { d.origin.parent.insertBefore(d.el, d.origin.next && d.origin.next.parentNode === d.origin.parent ? d.origin.next : null); }
    var r = d.el.getBoundingClientRect();
    var ghost = d.ghost;
    var finish = function () {
      ghost.remove();
      d.el.classList.remove('is-placeholder');
    };
    if (!reduced && ghost.animate) {
      ghost.animate([{ left: ghost.style.left, top: ghost.style.top }, { left: r.left + 'px', top: r.top + 'px' }], { duration: 170, easing: 'cubic-bezier(.2, .9, .25, 1)' }).onfinish = finish;
    } else {
      finish();
    }
    body.classList.remove('os-dragging');
    // The click that ends a drag opens nothing.
    suppressClick = true;
    markEmpty();
    if (!editing) { pruneEmptyPages(); } else { renderDots(); }
    hold();
    if (!cancel) { save(); }
  }

  document.addEventListener('pointerdown', function (e) {
    if (state.open || drag || (e.pointerType === 'mouse' && e.button !== 0)) { return; }
    var el = e.target.closest ? e.target.closest(thing) : null;
    if (!el || !pagesEl || pagesEl.classList.contains('is-searching') || e.target.closest('.os-remove')) { return; }
    // A finger drags only while editing; else it swipes the pages and scrolls.
    if (e.pointerType !== 'mouse' && !editing) { return; }
    drag = { el: el, id: e.pointerId, x0: e.clientX, y0: e.clientY, started: false };
  });
  document.addEventListener('pointermove', function (e) {
    if (!drag || e.pointerId !== drag.id) { return; }
    if (!drag.started) {
      if (Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) < 6) { return; }
      startDrag(e);
    }
    e.preventDefault();
    moveDrag(e.clientX, e.clientY);
  }, { passive: false });
  document.addEventListener('pointerup', function (e) { if (drag && e.pointerId === drag.id) { endDrag(false); } });
  document.addEventListener('pointercancel', function (e) { if (drag && e.pointerId === drag.id) { endDrag(false); } });

  // ------------------------------------------------------------------ opening things

  // An app opens in the window of the home screen unless the person chose a new tab for it in its menu (an app that cannot be framed shows the way to a tab there).
  function prefersWindow(el) {
    var key = el.getAttribute('data-app-key');
    try { return !key || localStorage.getItem(PREFS + key) !== 'tab'; } catch (e) { return true; }
  }

  // First of all clicks: the one that ends a drag or a long press opens nothing; while editing a tap does not open a thing (its minus takes it off),
  // and a tap beside the things ends editing.
  var suppressClick = false;
  // A new touch or press starts afresh (a long press may end without a click).
  window.addEventListener('pointerdown', function () { suppressClick = false; }, true);
  window.addEventListener('click', function (e) {
    if (suppressClick && !(e.target.closest && e.target.closest('.os-menu'))) { suppressClick = false; e.preventDefault(); e.stopPropagation(); return; }
    if (!editing) { return; }
    var t = e.target;
    var minus = t.closest ? t.closest('.os-remove') : null;
    if (minus) { e.preventDefault(); e.stopPropagation(); hide(minus.closest('[data-home-key]')); return; }
    if (t.closest && t.closest(thing)) { e.preventDefault(); e.stopPropagation(); return; }
    if (t.closest && t.closest('.os-editbar, .os-menu, .os-dots, .os-status, .os-bottom, .os-window, .os-folder, .mat-dialog, dialog')) { return; }
    if (t.closest && t.closest('.os-desktop')) { setEditing(false); }
  }, true);

  document.addEventListener('click', function (e) {
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

  // ------------------------------------------------------------------ menus: the person's, and the one of everything else (a right click, a long press)

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

  function icon(name) {
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
  }
  function item(iconName, text, attrs) {
    var b = document.createElement(attrs.href ? 'a' : 'button');
    b.className = 'os-menu__item' + (attrs.danger ? ' os-menu__item--danger' : '');
    b.setAttribute('role', attrs.radio ? 'menuitemradio' : attrs.check != null ? 'menuitemcheckbox' : 'menuitem');
    if (!attrs.href) { b.type = 'button'; }
    Object.keys(attrs).forEach(function (k) { if (k !== 'check' && k !== 'run' && k !== 'radio' && k !== 'danger' && attrs[k] != null) { b.setAttribute(k, attrs[k]); } });
    if (attrs.check != null) { b.setAttribute('aria-checked', attrs.check ? 'true' : 'false'); }
    b.innerHTML = (iconName ? icon(iconName) : '<span class="os-menu__blank" aria-hidden="true"></span>') + '<span></span>';
    b.lastChild.textContent = text;
    if (attrs.run) { b.addEventListener('click', function (e) { e.preventDefault(); closeMenus(); attrs.run(); }); }
    return b;
  }
  function sep(menu) {
    var last = menu.lastElementChild;
    if (!last || last.classList.contains('os-menu__sep') || last.classList.contains('os-menu__head')) { return; }
    var s = document.createElement('div');
    s.className = 'os-menu__sep';
    s.setAttribute('role', 'separator');
    menu.appendChild(s);
  }
  function heading(menu, text) {
    var h = document.createElement('div');
    h.className = 'os-menu__label';
    h.setAttribute('aria-hidden', 'true');
    h.textContent = text;
    menu.appendChild(h);
  }
  function newMenu(name, pic, sub) {
    closeMenus();
    var menu = document.createElement('div');
    menu.className = 'os-menu';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-label', name || 'Menu');
    if (name) {
      var head = document.createElement('div');
      head.className = 'os-menu__head';
      if (pic) { head.appendChild(pic.cloneNode(true)); }
      var label = document.createElement('div');
      label.innerHTML = '<strong></strong><small></small>';
      label.firstChild.textContent = name;
      label.lastChild.textContent = sub || '';
      head.appendChild(label);
      menu.appendChild(head);
    }
    return menu;
  }
  function showMenu(menu, x, y) {
    var last = menu.lastElementChild;
    if (last && last.classList.contains('os-menu__sep')) { last.remove(); }
    body.appendChild(menu);
    appMenu = menu;
    var w = menu.offsetWidth, h = menu.offsetHeight;
    menu.style.left = Math.max(8, Math.min(x, window.innerWidth - w - 8)) + 'px';
    menu.style.top = Math.max(8, Math.min(y, window.innerHeight - h - 8)) + 'px';
    var first = $('.os-menu__item', menu);
    if (first) { first.focus(); }
  }

  // The entries of an app: open it here or in a tab, always in a tab, its quick actions, its details and settings in Mattiverse.
  function appEntries(menu, el) {
    var name = el.getAttribute('data-title') || '';
    var tab = el.getAttribute('data-tab') || '';
    var key = el.getAttribute('data-app-key');
    var target = el.getAttribute('data-window');
    menu.appendChild(item('monitor', 'Open in the window', { run: function () { open(target, { source: el, title: name }); } }));
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { window.open(tab || target, '_blank', 'noopener'); } }));
    if (key && tab) {
      var inTab = !prefersWindow(el);
      menu.appendChild(item('check', 'Always open in a new tab', {
        check: inTab,
        run: function () { try { if (inTab) { localStorage.removeItem(PREFS + key); } else { localStorage.setItem(PREFS + key, 'tab'); } } catch (e) { /* private mode */ } }
      }));
    }
    // The quick actions of an app (contract 2, contributes.quickActions): an address of the app, opened like the app itself (a tab, or the window when it prefers it).
    var quick = [];
    try { quick = JSON.parse(el.getAttribute('data-quick') || '[]'); } catch (err) { quick = []; }
    quick = Array.isArray(quick) ? quick.filter(function (q) { return q && typeof q.label === 'string' && typeof q.window === 'string' && q.window.charAt(0) === '/' && q.window.charAt(1) !== '/'; }) : [];
    if (quick.length) {
      sep(menu);
      quick.forEach(function (q) {
        var qicon = /^[a-z0-9-]+$/.test(q.icon || '') ? q.icon : 'arrow-right';
        var qtab = typeof q.tab === 'string' && /^https?:\/\//.test(q.tab) ? q.tab : null;
        menu.appendChild(item(qicon, q.label, {
          run: function () {
            if (qtab && !prefersWindow(el)) { window.open(qtab, '_blank', 'noopener'); } else { open(q.window, { source: el, title: name }); }
          }
        }));
      });
    }
    var details = el.getAttribute('data-details');
    var manage = el.getAttribute('data-manage');
    if (details || manage) {
      sep(menu);
      if (details) { menu.appendChild(item('info', 'Details', { run: function () { open(details, { source: el, title: name }); } })); }
      if (manage) { menu.appendChild(item('sliders', 'Manage', { run: function () { open(manage, { source: el, title: name }); } })); }
    }
  }

  // A link inside a widget (an engine, a row of a list) or the widget itself: open it here or in a tab.
  function linkEntries(menu, link, widget) {
    var target = link.getAttribute('data-window');
    var title = link.getAttribute('data-title') || '';
    var row = $('.os-wlist__title, .os-engine__name', link);
    var what = link === widget ? 'Open' : 'Open ' + (row ? row.textContent : title);
    menu.appendChild(item('monitor', what, { run: function () { open(target, { source: widget, title: title }); } }));
    menu.appendChild(item('external', 'Open in a new tab', { run: function () { window.open(link.getAttribute('data-tab') || target, '_blank', 'noopener'); } }));
  }

  function editEntry(menu) {
    sep(menu);
    menu.appendChild(item(editing ? 'check' : 'edit', editing ? 'Done' : 'Edit the home screen', { run: function () { setEditing(!editing); } }));
  }

  // What every thing on a page has: move it (one place, to another page), its size (a widget), take it off the screen, edit the home screen.
  function thingEntries(menu, el) {
    var p = pages();
    var here = p.indexOf(el.parentNode);
    var list = things(el.parentNode);
    var at = list.indexOf(el);
    var moves = [];
    if (at > 0) { moves.push(item('chevron-left', 'One place back', { run: function () { moveBy(el, -1); } })); }
    if (at < list.length - 1) { moves.push(item('chevron-right', 'One place on', { run: function () { moveBy(el, 1); } })); }
    p.forEach(function (page, i) {
      if (i !== here) { moves.push(item('arrow-right', 'To page ' + (i + 1), { run: function () { moveToPage(el, i); } })); }
    });
    if (!(list.length === 1 && here === p.length - 1)) {
      moves.push(item('plus', 'To a new page', { run: function () { moveToPage(el, pages().length); } }));
    }
    if (moves.length) {
      sep(menu);
      heading(menu, 'Move');
      moves.forEach(function (m) { menu.appendChild(m); });
    }
    var sizes = (el.getAttribute('data-sizes') || '').split(' ').filter(Boolean);
    if (sizes.length > 1) {
      sep(menu);
      heading(menu, 'Size');
      var labels = { small: 'Small', medium: 'Medium', wide: 'Wide' };
      sizes.forEach(function (s) {
        menu.appendChild(item(null, labels[s] || s, { radio: true, check: el.getAttribute('data-size') === s, run: function () { setSize(el, s); } }));
      });
    }
    sep(menu);
    menu.appendChild(item('eye-off', 'Remove from the home screen', { run: function () { hide(el); } }));
    editEntry(menu);
  }

  function hiddenEntries(menu) {
    var hidden = hiddenBox ? things(hiddenBox) : [];
    if (!hidden.length) { return; }
    sep(menu);
    heading(menu, 'Show again');
    hidden.forEach(function (el) { menu.appendChild(item('plus', nameOf(el), { run: function () { restore(el); } })); });
  }
  function openHiddenMenu(x, y) {
    var menu = newMenu();
    menu.setAttribute('aria-label', 'Show again');
    hiddenEntries(menu);
    showMenu(menu, x, y);
  }

  // The empty screen, the status bar: the home screen itself.
  function openScreenMenu(x, y) {
    var menu = newMenu();
    menu.setAttribute('aria-label', 'Home screen');
    menu.appendChild(item(editing ? 'check' : 'edit', editing ? 'Done' : 'Edit the home screen', { run: function () { setEditing(!editing); } }));
    if (pagesEl) { menu.appendChild(item('plus', 'Add a page', { run: function () { setEditing(true); createPage(); goToPage(pages().length - 1, true); } })); }
    hiddenEntries(menu);
    sep(menu);
    menu.appendChild(item('image', 'Change the wallpaper', { run: function () { open('/Account/Profile?tab=home', { title: 'Wallpaper' }); } }));
    if ($('.os-dock__app[data-window="/Settings"]')) {
      menu.appendChild(item('store', 'Install an app', { run: function () { open('/Store', { title: 'Store' }); } }));
      menu.appendChild(item('settings', 'Settings', { run: function () { open('/Settings', { title: 'Settings' }); } }));
    }
    sep(menu);
    menu.appendChild(item('refresh', 'Reload', { run: function () { location.reload(); } }));
    if (pagesEl) { menu.appendChild(item('restore', 'Reset the home screen', { danger: true, run: resetLayout })); }
    showMenu(menu, x, y);
  }

  // The bar of the window: what its buttons do.
  function openWindowMenu(x, y) {
    var menu = newMenu(titleEl.textContent || 'Window', iconEl.firstElementChild, 'Window');
    if (!backBtn.disabled) { menu.appendChild(item('chevron-left', 'Back', { run: goBack })); }
    menu.appendChild(item('refresh', 'Reload', { run: reloadWindow }));
    menu.appendChild(item('external', 'Open in a new tab', { run: windowInTab }));
    menu.appendChild(item('monitor', 'Full screen', { check: win.classList.contains('is-max'), run: toggleMax }));
    sep(menu);
    menu.appendChild(item('home', 'Home screen (the app stays open)', { run: function () { home(false); } }));
    menu.appendChild(item('x', 'Close', { run: function () { home(true); } }));
    showMenu(menu, x, y);
  }

  var appSelector = '.os-app[data-window], .os-dock__app[data-window]';
  function openMenuFor(target, x, y) {
    if (target.closest('.os-window__bar')) { openWindowMenu(x, y); return; }
    var el = target.closest(thing) || target.closest(appSelector) || target.closest('.os-folderbtn');
    if (!el) { openScreenMenu(x, y); return; }
    var isApp = el.matches(appSelector);
    var pic = el.querySelector('.os-app__icon > .mv-appicon, .os-app__icon > .os-ico, :scope > .os-ico');
    var name = isApp ? (el.getAttribute('data-title') || '') : nameOf(el);
    var sub = isApp ? (el.getAttribute('title') ? el.getAttribute('title').split(' · ').slice(1).join(' · ') : 'Mattiverse') : el.classList.contains('os-folderbtn') ? 'Folder' : 'Widget';
    var menu = newMenu(name, pic, sub);
    if (isApp) {
      appEntries(menu, el);
    } else if (el.classList.contains('os-folderbtn')) {
      menu.appendChild(item('folder-open', 'Open', { run: function () { el.click(); } }));
    } else {
      var link = target.closest('[data-window]');
      if (link && el.contains(link)) { linkEntries(menu, link, el); }
    }
    if (el.matches(thing)) { thingEntries(menu, el); } else if (!el.closest('.os-folder')) { editEntry(menu); }
    showMenu(menu, x, y);
  }

  document.addEventListener('contextmenu', function (e) {
    // Shift with the right click: the browser's own menu; a field keeps it (to paste); the window's page has its own.
    if (e.shiftKey) { return; }
    var t = e.target;
    if (!t.closest || t.closest('input, textarea, select, [contenteditable="true"], .os-window__frame, dialog')) { return; }
    e.preventDefault();
    if (t.closest('.os-menu') || drag) { return; }
    var x = e.clientX, y = e.clientY;
    // The key for the menu (or Shift+F10) has no pointer: the menu opens below what has the focus.
    if (!x && !y) { var r = t.getBoundingClientRect(); x = r.left; y = r.bottom + 6; }
    if (editing && t.closest(thing) && e.pointerType === 'touch') { return; }
    openMenuFor(t, x, y);
  });

  // A long press on a touch screen: the menu (while editing, a press on a thing is the start of dragging it), and the tap that ends it opens nothing.
  var press = null;
  document.addEventListener('pointerdown', function (e) {
    if (e.pointerType === 'mouse') { return; }
    var t = e.target;
    if (!t.closest || t.closest('input, textarea, select, .os-menu, .os-window__frame, .os-editbar, dialog')) { return; }
    if (editing && t.closest(thing)) { return; }
    if (!t.closest('.os-desktop, .os-bottom, .os-window__bar, .os-folder, .os-status')) { return; }
    var x = e.clientX, y = e.clientY;
    press = {
      x: x, y: y, timer: setTimeout(function () {
        press = null;
        suppressClick = true;
        var el = t.closest(thing) || t.closest(appSelector);
        var r = el ? el.getBoundingClientRect() : null;
        openMenuFor(t, r ? r.left : x, r ? r.bottom + 8 : y);
      }, 480)
    };
  });
  function cancelPress() { if (press) { clearTimeout(press.timer); press = null; } }
  document.addEventListener('pointerup', cancelPress);
  document.addEventListener('pointercancel', cancelPress);
  document.addEventListener('pointermove', function (e) { if (press && Math.abs(e.clientX - press.x) + Math.abs(e.clientY - press.y) > 12) { cancelPress(); } });

  // ------------------------------------------------------------------ the keyboard: menus, pages, editing

  document.addEventListener('keydown', function (e) {
    var t = e.target;
    var inMenu = t.closest && t.closest('.os-menu');
    if (inMenu && /^(ArrowDown|ArrowUp|Home|End)$/.test(e.key)) {
      var entries = $$('.os-menu__item', inMenu).filter(function (b) { return b.offsetParent !== null; });
      var i = entries.indexOf(t);
      var next = e.key === 'Home' ? 0 : e.key === 'End' ? entries.length - 1 : e.key === 'ArrowDown' ? (i + 1) % entries.length : (i - 1 + entries.length) % entries.length;
      if (entries[next]) { e.preventDefault(); entries[next].focus(); }
      return;
    }
    if (e.key === 'Escape') {
      if (drag) { endDrag(true); return; }
      var hadMenu = !!appMenu || $$('.os-menu').some(function (m) { return !m.hidden; });
      closeMenus();
      if (!hadMenu && editing && !state.open) { setEditing(false); }
      return;
    }
    if (state.open || !pagesEl || e.ctrlKey || e.metaKey || e.altKey || (t && (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName)))) { return; }
    // While editing, Delete takes the thing that has the focus off the screen.
    if (editing && (e.key === 'Delete' || e.key === 'Backspace') && t.closest && t.closest(thing) === t) { e.preventDefault(); var nextOne = t.nextElementSibling; hide(t); if (nextOne && nextOne.focus) { nextOne.focus(); } return; }
    if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && pages().length > 1 && !inMenu) {
      e.preventDefault();
      goToPage(currentPage() + (e.key === 'ArrowRight' ? 1 : -1), true);
    }
  });

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

  // ------------------------------------------------------------------ the search: the apps of every page in one grid

  var find = $('[data-os-search]');
  if (find) {
    var filter = function () {
      var q = find.value.trim().toLowerCase();
      if (q !== '' && editing) { setEditing(false); }
      if (pagesEl) { pagesEl.classList.toggle('is-searching', q !== ''); }
      if (dotsEl) { dotsEl.hidden = q !== '' || pages().length < 2; }
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

  // An address with a window in it opens that window (last: everything above is ready).
  fromAddress();
})();
