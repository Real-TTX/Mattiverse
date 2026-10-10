/* Mattiverse UI behaviour. Plain JavaScript, no build step.
   Every behaviour is switched on by a data-* attribute, so several of the same control can live on one page. */
(function () {
  'use strict';

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  // ------------------------------------------------------------------ theme (system / light / dark)

  var THEME_KEY = 'mattiverse-theme';

  function applyTheme(pref) {
    var dark = pref === 'dark' || (pref === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    document.documentElement.setAttribute('data-theme-pref', pref);
    var meta = document.getElementById('meta-theme-color');
    if (meta) { meta.setAttribute('content', dark ? '#121830' : '#ffffff'); }
    $$('[data-theme-set]').forEach(function (b) { b.classList.toggle('is-active', b.getAttribute('data-theme-set') === pref); });
  }

  function currentPref() { try { return localStorage.getItem(THEME_KEY) || 'system'; } catch (e) { return 'system'; } }

  applyTheme(currentPref());
  if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
      if (currentPref() === 'system') { applyTheme('system'); }
    });
  }

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-theme-set]');
    if (!btn) { return; }
    var pref = btn.getAttribute('data-theme-set');
    try { localStorage.setItem(THEME_KEY, pref); } catch (err) { /* private mode */ }
    applyTheme(pref);
    // Remember it on the account, so a new device starts with the same theme.
    var token = $('meta[name="request-verification-token"]');
    fetch('/api/theme', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token ? token.content : '' },
      body: JSON.stringify({ theme: pref }),
      credentials: 'same-origin'
    }).catch(function () { /* not signed in or offline: the local choice still applies */ });
  });

  // ------------------------------------------------------------------ scrollbars only while scrolling

  var scrollTimers = new WeakMap();
  document.addEventListener('scroll', function (e) {
    var el = e.target === document ? document.documentElement : e.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(scrollTimers.get(el));
    scrollTimers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 700));
  }, true);

  // ------------------------------------------------------------------ mobile menu closes after a tap on a link

  document.addEventListener('click', function (e) {
    var link = e.target.closest('.sidebar a.nav-link');
    var toggle = document.getElementById('nav-toggle');
    if (link && toggle) { toggle.checked = false; }
  });

  // ------------------------------------------------------------------ confirm dialogs: <form data-confirm="Really?"> / <a data-confirm>

  function confirmDialog(message, okText, danger) {
    return new Promise(function (resolve) {
      var backdrop = document.createElement('div');
      backdrop.className = 'dialog-backdrop';
      backdrop.innerHTML =
        '<div class="dialog" role="alertdialog" aria-modal="true">' +
        '<h3></h3><p></p>' +
        '<div class="mat-buttons"><button type="button" class="mat-btn mat-btn--ghost" data-no>Cancel</button>' +
        '<button type="button" class="mat-btn ' + (danger ? 'mat-btn--solid-danger' : 'mat-btn--primary') + '" data-yes></button></div></div>';
      backdrop.querySelector('h3').textContent = danger ? 'Are you sure?' : 'Please confirm';
      backdrop.querySelector('p').textContent = message;
      backdrop.querySelector('[data-yes]').textContent = okText || 'Confirm';
      function close(result) { backdrop.remove(); document.removeEventListener('keydown', onKey); resolve(result); }
      function onKey(ev) { if (ev.key === 'Escape') { close(false); } }
      backdrop.addEventListener('click', function (ev) {
        if (ev.target === backdrop || ev.target.hasAttribute('data-no')) { close(false); }
        else if (ev.target.hasAttribute('data-yes')) { close(true); }
      });
      document.addEventListener('keydown', onKey);
      document.body.appendChild(backdrop);
      backdrop.querySelector('[data-yes]').focus();
    });
  }

  document.addEventListener('submit', function (e) {
    var form = e.target;
    var message = form.getAttribute && form.getAttribute('data-confirm');
    if (!message || form.__confirmed) { return; }
    e.preventDefault();
    var submitter = e.submitter;
    confirmDialog(message, form.getAttribute('data-confirm-ok') || 'Confirm', form.hasAttribute('data-confirm-danger') || !!(submitter && submitter.classList.contains('mat-btn--danger'))).then(function (ok) {
      if (!ok) { return; }
      form.__confirmed = true;
      if (form.requestSubmit && submitter) { form.requestSubmit(submitter); } else { form.submit(); }
    });
  });

  // ------------------------------------------------------------------ list toolbars: submit on change, search with a short delay

  $$('form.js-autosubmit').forEach(function (form) {
    var timer;
    function submit() { try { sessionStorage.setItem('mv-focus', form.querySelector('input[type=search]') ? 'search' : ''); } catch (e) { } form.submit(); }
    form.addEventListener('change', function (e) { if (e.target.matches('select, input[type=checkbox]')) { submit(); } });
    form.addEventListener('input', function (e) {
      if (e.target.matches('input[type=search]')) { clearTimeout(timer); timer = setTimeout(submit, 450); }
    });
    form.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); clearTimeout(timer); submit(); } });
  });
  try {
    if (sessionStorage.getItem('mv-focus') === 'search') {
      var search = $('form.js-autosubmit input[type=search]');
      if (search) { search.focus(); var len = search.value.length; search.setSelectionRange(len, len); }
    }
    sessionStorage.removeItem('mv-focus');
  } catch (e) { }

  // ------------------------------------------------------------------ dependent fields: data-show-if="#selector" data-show-value="x"

  function refreshDependents() {
    $$('[data-show-if]').forEach(function (el) {
      var ctl = $(el.getAttribute('data-show-if'));
      if (!ctl) { return; }
      var wanted = el.getAttribute('data-show-value');
      var value = ctl.type === 'checkbox' ? String(ctl.checked) : ctl.value;
      if (ctl.type === 'radio') { var on = $('input[name="' + ctl.name + '"]:checked'); value = on ? on.value : ''; }
      el.hidden = wanted === null ? !value : value !== wanted;
    });
  }
  document.addEventListener('change', refreshDependents);
  refreshDependents();

  // ------------------------------------------------------------------ home: clock + filter

  var clock = $('[data-clock]');
  if (clock) {
    var dateEl = $('[data-clock-date]');
    var greetEl = $('[data-greeting]');
    var tick = function () {
      var now = new Date();
      clock.textContent = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      if (dateEl) { dateEl.textContent = now.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' }); }
      if (greetEl) {
        var h = now.getHours();
        var part = h < 5 ? 'Good night' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
        greetEl.textContent = part + ', ' + greetEl.getAttribute('data-greeting');
      }
    };
    tick();
    setInterval(tick, 15000);
  }

  var filter = $('[data-tile-filter]');
  if (filter) {
    filter.addEventListener('input', function () {
      var q = filter.value.trim().toLowerCase();
      $$('.tile[data-name]').forEach(function (t) { t.hidden = q !== '' && t.getAttribute('data-name').indexOf(q) === -1; });
    });
  }

  // ------------------------------------------------------------------ copy and reveal (credentials)

  document.addEventListener('click', function (e) {
    var copy = e.target.closest('[data-copy]');
    if (copy) {
      var text = copy.getAttribute('data-copy');
      var done = function () { var old = copy.innerHTML; copy.classList.add('is-done'); setTimeout(function () { copy.classList.remove('is-done'); }, 1200); };
      if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text).then(done); }
      else { var ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select(); try { document.execCommand('copy'); done(); } catch (err) { } ta.remove(); }
      return;
    }
    var reveal = e.target.closest('[data-reveal]');
    if (reveal) {
      var target = document.getElementById(reveal.getAttribute('data-reveal'));
      if (!target) { return; }
      var shown = target.getAttribute('data-shown') === '1';
      target.textContent = shown ? '••••••••••' : target.getAttribute('data-secret');
      target.setAttribute('data-shown', shown ? '0' : '1');
    }
  });

  // ------------------------------------------------------------------ pages that wait for something refresh themselves

  var auto = $('[data-autorefresh]');
  if (auto) {
    setTimeout(function () { if (!document.hidden) { location.reload(); } else { document.addEventListener('visibilitychange', function () { location.reload(); }, { once: true }); } },
      parseInt(auto.getAttribute('data-autorefresh'), 10) || 5000);
  }

  // ------------------------------------------------------------------ job log viewer: <pre data-job-log data-job-id="12">

  function pillFor(status) {
    var map = { Queued: ['pill--busy', 'Queued'], Running: ['pill--busy', 'Running'], Succeeded: ['pill--online', 'Done'], Failed: ['pill--offline', 'Failed'] };
    return map[status] || ['pill--unknown', status];
  }

  $$('[data-job-log]').forEach(function (pre) {
    var id = pre.getAttribute('data-job-id');
    var offset = 0;
    var stopped = false;
    var root = pre.closest('[data-job]') || document;
    var statusEl = $('[data-job-status]', root);
    var doneEls = $$('[data-job-done]', root);
    var errorEl = $('[data-job-error]', root);

    function poll() {
      if (stopped) { return; }
      fetch('/api/jobs/' + id + '/log?offset=' + offset, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
        .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); })
        .then(function (d) {
          var atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 40;
          if (d.text) { pre.textContent += d.text; }
          offset = d.offset;
          if (atBottom) { pre.scrollTop = pre.scrollHeight; }
          if (statusEl) { var p = pillFor(d.status); statusEl.className = 'pill ' + p[0]; statusEl.textContent = p[1]; }
          if (d.finished) {
            stopped = true;
            doneEls.forEach(function (el) { el.hidden = false; });
            if (errorEl && d.error) { errorEl.textContent = d.error; errorEl.hidden = false; }
            pre.dispatchEvent(new CustomEvent('job-finished', { bubbles: true, detail: d }));
            return;
          }
          setTimeout(poll, 1000);
        })
        .catch(function () { setTimeout(poll, 2500); });
    }
    poll();
  });

  // ------------------------------------------------------------------ wizard: steps in one page

  $$('[data-wizard]').forEach(function (wiz) {
    var panels = $$('[data-step]', wiz);
    var dots = $$('.wizard__dot', wiz);
    var index = 0;

    function show(i) {
      index = Math.max(0, Math.min(panels.length - 1, i));
      panels.forEach(function (p, n) { p.classList.toggle('is-active', n === index); });
      dots.forEach(function (d, n) { d.classList.toggle('is-active', n === index); d.classList.toggle('is-done', n < index); });
      var summary = $('[data-wizard-summary]', wiz);
      if (summary && index === panels.length - 1) { buildSummary(summary); }
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }

    function label(name) {
      var on = $('input[name="' + name + '"]:checked', wiz);
      if (!on || !on.value) { return 'Nothing (skip)'; }
      var title = on.closest('.choice') ? on.closest('.choice').querySelector('.choice__title span') : null;
      return title ? title.textContent : on.value;
    }

    function buildSummary(el) {
      var host = $('[name="PublicHost"]', wiz), domain = $('[name="BaseDomain"]', wiz);
      var rows = [
        ['Address of this host', host && host.value ? host.value : 'from the browser'],
        ['Domain for your apps', domain && domain.value ? '*.' + domain.value : 'none (apps use ports)'],
        ['Reverse proxy', label('ProxyAppId')],
        ['DynDNS client', label('DdnsAppId')],
        ['Docker management', label('DockerAppId')]
      ];
      el.innerHTML = '';
      rows.forEach(function (r) {
        var dt = document.createElement('dt'); dt.textContent = r[0];
        var dd = document.createElement('dd'); dd.textContent = r[1];
        el.appendChild(dt); el.appendChild(dd);
      });
    }

    wiz.addEventListener('click', function (e) {
      if (e.target.closest('[data-next]')) { e.preventDefault(); show(index + 1); }
      else if (e.target.closest('[data-prev]')) { e.preventDefault(); show(index - 1); }
    });
    show(0);
  });

  // ------------------------------------------------------------------ app view: reload the frame

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-frame-reload]');
    if (!btn) { return; }
    var frame = document.getElementById(btn.getAttribute('data-frame-reload'));
    if (frame) { frame.src = frame.src; }
  });
})();
