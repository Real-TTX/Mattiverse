// End-to-end checks of the pages of Mattiverse in a real browser (the installed Edge, or Chrome) against a running stack.
//   ./scripts/redeploy.ps1 && cd tests/e2e && npm install && node run.js [http://localhost:8030]
// It signs in with the account of the development stack (admin@example.com / Start12345, set MATTIVERSE_USER and MATTIVERSE_PASSWORD for another one) and
// changes nothing that it does not remove again: the one schedule it creates is deleted at the end. Exit code 1 when a check fails.
const { chromium } = require('playwright-core');

const BASE = (process.argv[2] || process.env.BASE || 'http://localhost:8030').replace(/\/$/, '');
const USER = process.env.MATTIVERSE_USER || 'admin@example.com';
const PASSWORD = process.env.MATTIVERSE_PASSWORD || 'Start12345';
const results = [];

async function check(name, fn) {
  try {
    await fn();
    results.push({ name, ok: true });
    console.log('  ok    ' + name);
  } catch (e) {
    results.push({ name, ok: false, why: e.message });
    console.log('  FAIL  ' + name + '\n        ' + e.message.split('\n')[0]);
  }
}

function expectTrue(value, what) {
  if (!value) { throw new Error(what); }
}

function expectEqual(actual, expected, what) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${what}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
  }
}

const pages = [
  '/', '/Apps', '/Store', '/Store/Install?id=matfile', '/Backups', '/Backups?tab=schedules', '/Backups/Create', '/Backups/ScheduleEdit', '/Jobs',
  '/Users', '/Users/Edit', '/Settings', '/Settings/Engines', '/Settings/System', '/Wizard', '/Account/Profile', '/Account/Profile?tab=home', '/Account/Profile?tab=password',
];

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true }).catch(() => chromium.launch({ channel: 'chrome', headless: true }));
  const errors = [];

  async function signedIn(options) {
    const ctx = await browser.newContext(options);
    const page = await ctx.newPage();
    page.on('pageerror', e => errors.push(page.url() + ': ' + e.message));
    page.on('console', m => { if (m.type() === 'error' && !/status of 40[34]/.test(m.text())) { errors.push(page.url() + ': ' + m.text()); } });
    await page.goto(BASE + '/Account/Login');
    await page.fill('#Input_Username', USER);
    await page.fill('#Input_Password', PASSWORD);
    await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/Account/Login')), page.click('button[type=submit]')]);
    return { ctx, page };
  }

  const modes = [
    ['light', { viewport: { width: 1360, height: 900 }, colorScheme: 'light' }],
    ['dark', { viewport: { width: 1360, height: 900 }, colorScheme: 'dark' }],
    ['phone', { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, colorScheme: 'light' }],
  ];

  console.log('Every page, light, dark and on a phone');
  for (const [mode, options] of modes) {
    const { ctx, page } = await signedIn(options);
    await check(`${mode}: ${pages.length} pages load, have one h1 and do not scroll sideways`, async () => {
      for (const url of pages) {
        const response = await page.goto(BASE + url, { waitUntil: 'networkidle' });
        expectEqual(response.status(), 200, url + ' status');
        await page.evaluate(t => document.documentElement.setAttribute('data-theme', t), mode === 'dark' ? 'dark' : 'light');
        expectEqual(await page.locator('h1').count(), 1, url + ' h1');
        const wide = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expectTrue(wide <= 1, `${url} scrolls sideways by ${wide}px`);
        expectEqual(await page.locator('[style]').count(), 0, url + ' elements with an inline style');
      }
    });
    await ctx.close();
  }

  const { ctx, page } = await signedIn({ viewport: { width: 1360, height: 900 } });

  console.log('The shell');
  await check('the theme button cycles through three themes and saves each choice', async () => {
    await page.goto(BASE + '/');
    const seen = [await page.evaluate(() => document.documentElement.getAttribute('data-theme'))];
    for (let i = 0; i < 3; i++) {
      const [response] = await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/theme')), page.click('[data-theme-toggle]')]);
      expectEqual(response.status(), 204, 'the choice is saved');
      await page.waitForTimeout(150);
      seen.push(await page.evaluate(() => document.documentElement.getAttribute('data-theme')));
    }
    expectTrue(new Set(seen.slice(0, 3)).size === 3 && seen[3] === seen[0], 'system, light and dark, then the first again: ' + seen.join(' > '));
  });
  console.log('The home screen (Mattiverse OS)');
  const win = () => page.frameLocator('#os-window .os-window__frame');
  const winTitle = () => page.locator('#os-window [data-win-title]').innerText();
  await check('the home screen is its own system: a status bar, the dock, the engines in a widget and in the folder System', async () => {
    await page.goto(BASE + '/');
    expectEqual(await page.locator('.mat-nav').count(), 0, 'no menu of the toolkit');
    expectEqual(await page.locator('.os-status').count(), 1, 'the status bar');
    expectEqual(await page.locator('.os-desktop[data-wallpaper]').count(), 1, 'the wallpaper');
    for (const href of ['/Store', '/Apps', '/Backups', '/Settings']) {
      expectEqual(await page.locator(`.os-dock a[data-window="${href}"]`).count(), 1, href + ' in the dock');
    }
    for (const href of ['/Jobs', '/People']) {
      expectEqual(await page.locator(`.os-grid .os-app[data-window="${href}"]`).count(), 1, href + ' on the screen');
    }
    expectEqual(await page.locator('.os-engines .os-engine').count(), 5, 'the five engines in their widget');
    await page.click('[data-os-folder="os-folder-system"]');
    expectTrue(await page.locator('#os-folder-system').isVisible(), 'the folder opens');
    expectEqual(await page.locator('#os-folder-system .os-app[href^="/Engines?id="]').count(), 5, 'the engines are its apps');
    await page.keyboard.press('Escape');
    expectTrue(!(await page.locator('#os-folder-system').isVisible()), 'Escape closes it');
  });
  await check('a part of Mattiverse opens in the one window: a title, the page without its menu, Back', async () => {
    await page.goto(BASE + '/');
    await page.click('.os-dock a[data-window="/Store"]');
    await page.waitForFunction(() => document.querySelector('#os-window [data-win-title]').textContent === 'Store');
    expectTrue(await page.locator('#os-window').isVisible(), 'the window is open');
    expectEqual(new URL(page.url()).hash, '#/Store', 'the address names its page');
    expectEqual(await win().locator('html.mv-in-window').count(), 1, 'the page knows it is in the window');
    expectTrue(!(await win().locator('.mat-nav').isVisible()), 'without the menu of the toolkit');
    expectTrue(await page.locator('[data-win-back]').isDisabled(), 'nothing to go back to yet');
    await win().locator('a[href^="/Store/Install"]').first().click();
    await page.waitForFunction(() => !document.querySelector('[data-win-back]').disabled);
    await page.click('[data-win-back]');
    await page.waitForFunction(() => document.querySelector('#os-window [data-win-title]').textContent === 'Store');
  });
  await check('yellow goes home and keeps the app, red closes it', async () => {
    await page.click('#os-window [data-win-home]');
    await page.waitForFunction(() => document.getElementById('os-window').hidden);
    expectEqual(new URL(page.url()).hash, '', 'the address is the home screen again');
    expectEqual(await page.locator('.os-dock a[data-window="/Store"].is-running').count(), 1, 'the store still runs: a dot under it');
    await page.click('.os-dock a[data-window="/Store"]');
    await page.waitForFunction(() => !document.getElementById('os-window').hidden);
    expectEqual(await winTitle(), 'Store', 'it comes back as it was');
    await page.click('#os-window .os-lights [data-win-close]');
    await page.waitForFunction(() => document.getElementById('os-window').hidden);
    expectEqual(await page.locator('.is-running').count(), 0, 'closed: no dot');
  });
  await check('a reload keeps the window, and the settings have their tabs inside it', async () => {
    await page.goto(BASE + '/#/Settings/Engines');
    await page.waitForFunction(() => document.querySelector('#os-window [data-win-title]').textContent === 'Engines');
    expectTrue(await win().locator('.mv-window-only .mat-tabs').isVisible(), 'the tabs of the settings');
    await page.evaluate(() => { location.hash = '#/Jobs'; });
    await page.waitForFunction(() => document.querySelector('#os-window [data-win-title]').textContent === 'Activity');
    expectEqual(new URL(page.url()).hash, '#/Jobs', 'another address while the window is open: that page in it');
    await page.click('#os-window .os-lights [data-win-close]');
  });
  await check('an engine opens its own window in Mattiverse', async () => {
    await page.goto(BASE + '/');
    await page.click('[data-os-folder="os-folder-system"]');
    const first = page.locator('#os-folder-system .os-app[href^="/Engines?id="]').first();
    const name = await first.getAttribute('data-title');
    await first.click();
    await page.waitForFunction(n => document.querySelector('#os-window [data-win-title]').textContent === n, name);
    expectEqual(await win().locator('h1').innerText(), name, 'the page of the engine');
    expectTrue(!(await page.locator('#os-folder-system').isVisible()), 'the folder closed');
    await page.click('#os-window .os-lights [data-win-close]');
  });
  await check('the search: / focuses it, it narrows the apps and the widgets step aside', async () => {
    await page.goto(BASE + '/');
    const all = await page.locator('.os-grid .os-app:visible').count();
    await page.keyboard.press('/');
    expectEqual(await page.evaluate(() => document.activeElement && document.activeElement.id), 'home-find', 'the search has the focus');
    await page.keyboard.type('people');
    expectEqual(await page.locator('.os-grid .os-app:visible').count(), 1, 'only People is left');
    expectEqual(await page.locator('.os-grid [data-widget]:visible').count(), 0, 'the widgets step aside');
    await page.fill('#home-find', '');
    expectEqual(await page.locator('.os-grid .os-app:visible').count(), all, 'all apps again');
  });
  await check('the person in the status bar: account, wallpaper, sign-out and the version; an app has a menu', async () => {
    await page.goto(BASE + '/');
    await page.click('[data-os-menu="os-user-menu"]');
    expectTrue(await page.locator('#os-user-menu').isVisible(), 'the menu opens');
    expectEqual(await page.locator('#os-user-menu [data-window="/Account/Profile?tab=home"]').count(), 1, 'the wallpaper');
    expectTrue(/Mattiverse \S+/.test(await page.locator('#os-user-menu .os-menu__foot').innerText()), 'the version (SHELL-06)');
    await page.keyboard.press('Escape');
    await page.click('.os-grid .os-app[data-window="/People"]', { button: 'right' });
    expectTrue(await page.locator('.os-menu[role=menu]:not(#os-user-menu)').isVisible(), 'the menu of an app');
    expectTrue((await page.locator('.os-menu[role=menu]:not(#os-user-menu)').innerText()).includes('Open in a new tab'), 'a new tab');
    await page.keyboard.press('Escape');
  });
  await check('the person arranges the home screen (a right click everywhere, dragging, pages, sizes, off the screen) and the account keeps it', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    expectEqual((await page.request.delete(BASE + '/api/home', { headers: { RequestVerificationToken: token } })).status(), 204, 'the home screen as it comes');
    // Activity and People first, so that both are on the screen whatever fits on it; the rest follows as it comes.
    expectEqual((await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { pages: [['s:jobs', 's:people']] } })).status(), 204, 'a layout is taken');
    await page.reload({ waitUntil: 'networkidle' });
    const keys = i => page.locator('.os-page').nth(i).locator(':scope > [data-home-key]').evaluateAll(els => els.map(e => e.getAttribute('data-home-key')));
    const menu = () => page.locator('.os-menu[role=menu]:not(#os-user-menu)');
    const entry = text => menu().locator('.os-menu__item', { hasText: text }).first();
    const saved = () => page.waitForResponse(r => r.url().endsWith('/api/home') && r.request().method() === 'PUT');

    await page.mouse.click(1300, 800, { button: 'right' });
    const screen = await menu().innerText();
    expectTrue(/Edit the home screen/.test(screen) && /Add a page/.test(screen) && /Reset the home screen/.test(screen), 'the menu of the empty screen');
    await page.keyboard.press('Escape');

    await page.locator('[data-home-key="w:today"]').click({ button: 'right', position: { x: 30, y: 30 } });
    let put = saved();
    await entry('Wide').click();
    expectEqual((await put).status(), 204, 'a size is saved');

    // People dragged with the mouse onto the place of Activity.
    const from = await page.locator('[data-home-key="s:people"]').boundingBox();
    const to = await page.locator('[data-home-key="s:jobs"]').boundingBox();
    put = saved();
    await page.mouse.move(from.x + from.width / 2, from.y + 30);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2 - 20, from.y + 30, { steps: 4 });
    await page.mouse.move(to.x + 10, to.y + 30, { steps: 16 });
    await page.mouse.up();
    await put;
    const order = await keys(0);
    expectTrue(order.indexOf('s:people') < order.indexOf('s:jobs'), 'People is before Activity: ' + order.join(' '));
    expectTrue(!(await page.locator('#os-window').isVisible()), 'the drag opened nothing');

    await page.locator('[data-home-key="s:jobs"]').click({ button: 'right' });
    put = saved();
    await entry('To a new page').click();
    await put;
    await page.locator('[data-home-key="w:docker"]').click({ button: 'right', position: { x: 20, y: 20 } });
    put = saved();
    await entry('Remove from the home screen').click();
    await put;

    await page.reload({ waitUntil: 'networkidle' });
    const screens = await page.locator('.os-page').count();
    expectEqual(await page.locator('.os-page:not([data-continues])').count(), 2, 'two pages of the person after a reload');
    expectEqual(await keys(screens - 1), ['s:jobs'], 'Activity alone on the last page');
    expectEqual(await page.getAttribute('[data-home-key="w:today"]', 'data-size'), 'wide', 'Today is wide');
    expectEqual(await page.locator('.os-page [data-home-key="w:docker"]').count(), 0, 'Docker is off the screen');
    expectEqual(await page.locator('.os-dots .os-dot').count(), screens, 'a dot per page');

    await page.mouse.click(1300, 800, { button: 'right' });
    await entry('Edit the home screen').click();
    expectTrue(await page.locator('.os-editbar').isVisible() && await page.locator('.os-page .os-remove').count() > 0, 'editing: the bar, a minus on each thing');
    await page.locator('[data-os-done]').click();
    expectEqual(await page.locator('.os-remove').count(), 0, 'Done ends it');

    await page.mouse.click(1300, 800, { button: 'right' });
    await entry('Reset the home screen').click();
    await Promise.all([page.waitForEvent('load'), page.locator('dialog[open] button[value="ok"]').click()]);
    expectEqual(await page.locator('.os-page:not([data-continues])').count(), 1, 'reset: one page of the person');
    expectEqual(await page.locator('.os-page [data-home-key="w:docker"]').count(), 1, 'reset: Docker is back');
  });
  await check('the home screen never scrolls: what does not fit on the screen is on the next page (a large screen, a phone)', async () => {
    for (const options of [{ viewport: { width: 1360, height: 900 } }, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }]) {
      const { ctx: c, page: p } = await signedIn(options);
      await p.goto(BASE + '/', { waitUntil: 'networkidle' });
      const what = options.viewport.width + 'x' + options.viewport.height;
      expectTrue(await p.evaluate(() => document.scrollingElement.scrollHeight <= window.innerHeight + 1), what + ': the page does not scroll');
      const outside = await p.evaluate(() => Array.prototype.slice.call(document.querySelectorAll('.os-page')).reduce((n, page) => {
        const bottom = page.getBoundingClientRect().bottom + 1;
        return n + Array.prototype.slice.call(page.children).filter(t => t.hasAttribute('data-home-key') && t.getBoundingClientRect().bottom > bottom && page.children.length > 1).length;
      }, 0));
      expectEqual(outside, 0, what + ': things below the end of their page');
      const screens = await p.locator('.os-page').count();
      expectTrue(screens === 1 || await p.locator('.os-dots .os-dot').count() === screens, what + ': a dot per page');
      await c.close();
    }
  });
  await check('with Today off the screen the home screen still has its one h1', async () => {
    await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    const put = await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { hidden: ['w:today'] } });
    expectEqual(put.status(), 204, 'Today off the screen');
    await page.reload({ waitUntil: 'networkidle' });
    expectEqual(await page.locator('h1').count(), 1, 'one h1');
    expectEqual((await page.request.put(BASE + '/api/home', { headers: { RequestVerificationToken: token }, data: { pages: [['<x>']] } })).status(), 400, 'a key that is no key: 400');
    await page.request.delete(BASE + '/api/home', { headers: { RequestVerificationToken: token } });
  });
  await check('the wallpaper is chosen on the account and drawn on the home screen', async () => {
    const choose = async (id) => {
      await page.goto(BASE + '/Account/Profile?tab=home');
      await page.check(`input[name=wallpaper][value=${id}]`);
      await Promise.all([page.waitForURL(/tab=home/), page.click('form[action*="handler=Wallpaper"] button[type=submit]')]);
    };
    await choose('jungle');
    await page.goto(BASE + '/');
    expectEqual(await page.getAttribute('.os-desktop', 'data-wallpaper'), 'jungle', 'the picture of the account');
    const image = await page.evaluate(() => getComputedStyle(document.querySelector('.os-desktop'), '::before').backgroundImage);
    expectTrue(image.includes('wallpapers/jungle.jpg'), 'the picture is drawn: ' + image.slice(0, 120));
    await choose('orbit');
    await page.goto(BASE + '/');
    expectEqual(await page.getAttribute('.os-desktop', 'data-wallpaper'), 'orbit', 'back to the default');
  });

  await check('an administrator has two menus, the switcher shows the one with the page', async () => {
    await page.goto(BASE + '/People');
    expectEqual(await page.locator('.mat-switcher__name').innerText(), 'Administration', 'the menu of the page');
    await page.goto(BASE + '/Store');
    expectEqual(await page.locator('.mat-switcher__name').innerText(), 'Mattiverse', 'the menu of the launcher');
  });
  await check('the menu marks the current page and the submenu opens on its branch', async () => {
    await page.goto(BASE + '/Settings/Engines');
    expectEqual(await page.locator('.mat-nav__item--sub.is-active').innerText(), 'Engines', 'the current child');
    expectEqual(await page.locator('.mat-nav__parent').first().getAttribute('aria-expanded'), 'true', 'the branch is open');
  });

  console.log('Lists');
  await check('the store: the search field, a filter dialog with a chip and the pager', async () => {
    await page.goto(BASE + '/Store');
    expectEqual(await page.locator('.mat-gallery .mat-card').count(), 12, 'cards on the first page');
    expectTrue(await page.locator('.mat-pager').count() === 1, 'a pager under the gallery');
    await page.click('[data-dialog-open="store-filter"]');
    expectEqual(await page.locator('dialog#store-filter[open]').count(), 1, 'the dialog opens');
    await page.selectOption('#store-f-category', 'Infrastructure');
    await page.click('dialog#store-filter button[type=submit]');
    await page.waitForURL(/category=Infrastructure/);
    expectEqual(await page.locator('.mat-chip').count(), 1, 'a chip for the filter');
    expectTrue(await page.locator('.mat-gallery .mat-card').count() >= 1, 'apps of the category');
  });
  await check('the users: search by field, sort in a dialog, the key / focuses the search', async () => {
    await page.goto(BASE + '/Users?q=' + USER);
    expectEqual(await page.locator('table.mat-table tbody tr').count(), 1, 'the user is found');
    expectEqual(await page.inputValue('#users-q'), USER, 'the field shows the term');
    await page.goto(BASE + '/Users');
    await page.keyboard.press('/');
    expectEqual(await page.evaluate(() => document.activeElement && document.activeElement.id), 'users-q', 'the search has the focus');
    await page.click('[data-dialog-open="users-sort"]');
    await page.check('#users-sort-by-name');
    await page.click('dialog#users-sort button[type=submit]');
    await page.waitForURL(/sort=name/);
  });
  await check('an empty search says so and offers to reset the tools', async () => {
    await page.goto(BASE + '/Users?q=nobody-has-this-name');
    expectEqual(await page.locator('.mat-empty').count(), 1, 'the empty state');
    await page.click('.mat-empty a');
    await page.waitForURL(u => !u.search);
  });

  console.log('Forms');
  await check('a required field says what to do, in the field and in the summary', async () => {
    await page.goto(BASE + '/Backups/ScheduleEdit');
    await page.fill('#Input_Name', '');
    await page.click('form.mat-form button[type=submit]:not([formnovalidate])');
    await page.waitForTimeout(200);
    expectEqual(await page.locator('.mat-field.is-invalid').count(), 1, 'one invalid field');
    expectTrue((await page.locator('.mat-field.is-invalid .mat-error').innerText()).length > 0, 'a message');
    expectEqual(await page.locator('[data-validation-summary]:not([hidden])').count(), 1, 'the summary shows');
  });
  await check('what the server finds lands in the summary too', async () => {
    await page.goto(BASE + '/Account/Profile?tab=password');
    await page.fill('#Input_CurrentPassword', 'not-the-password');
    await page.fill('#Input_NewPassword', 'another-password-1');
    await page.fill('#Input_ConfirmPassword', 'another-password-1');
    await page.click('form.mat-form button[type=submit]');
    await page.waitForLoadState('networkidle');
    expectTrue(await page.locator('[data-validation-summary]:not([hidden]) li').count() >= 1, 'a message in the summary');
    expectEqual(await page.locator('.mat-field.is-invalid').count(), 1, 'the field is marked');
  });
  await check('a saved schedule is a toast once, and deleting it asks first', async () => {
    await page.goto(BASE + '/Backups/ScheduleEdit');
    await page.fill('#Input_Name', 'E2E schedule');
    await page.click('form.mat-form button[type=submit]:not([formnovalidate])');
    await page.waitForURL(/tab=schedules/);
    await page.waitForTimeout(400);
    expectTrue(await page.locator('.mat-toast').count() >= 1, 'a toast says it was saved');
    await page.goto(BASE + '/Backups?tab=schedules');
    await page.waitForTimeout(400);
    expectEqual(await page.locator('.mat-toast').count(), 0, 'not shown a second time');
    await page.locator('table.mat-table tbody tr', { hasText: 'E2E schedule' }).locator('a[href*="ScheduleEdit"]').first().click();
    await page.click('button[formaction*="Delete"], button[asp-page-handler=Delete], form.mat-form button.mat-btn--danger');
    expectEqual(await page.locator('dialog.mat-dialog[open]').count(), 1, 'a dialog asks');
    await page.locator('dialog.mat-dialog[open] .mat-btn--danger').click();
    await page.waitForURL(/tab=schedules/);
    expectEqual(await page.locator('table.mat-table tbody tr', { hasText: 'E2E schedule' }).count(), 0, 'the schedule is gone');
  });
  await check('the wizard walks through five steps and shows what it will do', async () => {
    await page.goto(BASE + '/Wizard');
    expectEqual(await page.locator('.mv-step').count(), 5, 'steps');
    for (let i = 0; i < 4; i++) {
      await page.locator('.mv-step.is-active [data-next]').click();
      await page.waitForTimeout(120);
    }
    expectEqual(await page.locator('[data-wizard-summary] dt').count(), 4, 'rows of the summary');
    await page.locator('.mv-step.is-active [data-prev]').click();
    expectEqual(await page.locator('[data-step-label]').innerText(), 'Step 4 of 5', 'back goes one step');
  });
  await ctx.close();

  console.log('A phone');
  const phone = await signedIn({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await check('the menu is a drawer behind the burger button and closes again', async () => {
    await phone.page.goto(BASE + '/Apps');
    await phone.page.click('[data-nav-toggle]');
    await phone.page.waitForTimeout(350);
    expectTrue(await phone.page.evaluate(() => document.querySelector('.mat-app').classList.contains('is-nav-open')), 'open');
    await phone.page.click('.mat-nav__close');
    await phone.page.waitForTimeout(350);
    expectTrue(!(await phone.page.evaluate(() => document.querySelector('.mat-app').classList.contains('is-nav-open'))), 'closed');
  });
  await check('a dialog fills the screen', async () => {
    await phone.page.goto(BASE + '/Users');
    await phone.page.click('[data-dialog-open="users-sort"]');
    await phone.page.waitForTimeout(350);
    const box = await phone.page.locator('dialog#users-sort').boundingBox();
    expectTrue(box && box.width >= 389 && box.height >= 843, 'full screen: ' + JSON.stringify(box));
  });
  await phone.ctx.close();

  await check('no script errors on any page', async () => { expectEqual(errors, [], 'errors'); });

  await browser.close();
  const failed = results.filter(r => !r.ok).length;
  console.log(`\n${results.length - failed} of ${results.length} checks passed`);
  process.exit(failed ? 1 : 0);
})();
