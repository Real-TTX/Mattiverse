// End-to-end checks of the pages of Mattiverse in a real browser (the installed Edge, or Chrome) against a running stack.
//   ./scripts/redeploy.ps1 && cd tests/e2e && npm install && node run.js [http://localhost:8030]
// It signs in with the account of the development stack (tester / Tester123!, set MATTIVERSE_USER and MATTIVERSE_PASSWORD for another one) and
// changes nothing that it does not remove again: the one schedule it creates is deleted at the end. Exit code 1 when a check fails.
const { chromium } = require('playwright-core');

const BASE = (process.argv[2] || process.env.BASE || 'http://localhost:8030').replace(/\/$/, '');
const USER = process.env.MATTIVERSE_USER || 'tester';
const PASSWORD = process.env.MATTIVERSE_PASSWORD || 'Tester123!';
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
  '/Users', '/Users/Edit', '/Settings', '/Settings/Engines', '/Settings/System', '/Wizard', '/Account/Profile', '/Account/Profile?tab=password',
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
      const [response] = await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/theme')), page.click('.mat-theme')]);
      expectEqual(response.status(), 204, 'the choice is saved');
      await page.waitForTimeout(150);
      seen.push(await page.evaluate(() => document.documentElement.getAttribute('data-theme')));
    }
    expectTrue(new Set(seen.slice(0, 3)).size === 3 && seen[3] === seen[0], 'system, light and dark, then the first again: ' + seen.join(' > '));
  });
  await check('an administrator has two menus, the switcher shows the one with the page', async () => {
    await page.goto(BASE + '/Users');
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
