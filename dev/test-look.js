// Redesign checks: icon library, key lights, icon colour/size, solid style, app icons, colourways.
const { chromium, devices } = require('playwright');
const fs = require('fs');
const OUT = process.env.OUT || '/tmp/claude-0/shots';
const DATA = process.env.DATA || '/tmp/claude-0/pddata';
const ok = (c, m) => { if (!c) { console.error('FAIL', m); process.exitCode = 1; } else console.log('ok', m); };
const prof = () => JSON.parse(fs.readFileSync(DATA + '/profile.json', 'utf8'));
(async () => {
  const b = await chromium.launch();
  const errors = [];
  const ph = await (await b.newContext({ ...devices['iPhone 13'] })).newPage();
  ph.on('pageerror', e => errors.push(e.message));
  await ph.goto('http://localhost:8765/'); // local = already trusted
  await ph.waitForSelector('#grid .key');
  await ph.click('#editBtn');
  await ph.locator('.key[aria-label="Copy"]').click();
  await ph.waitForSelector('#sheet.show');
  // icon search across the full library
  await ph.fill('input[type="search"]', 'rocket');
  await ph.waitForTimeout(150);
  const n = await ph.locator('.icongrid button[aria-label]').count();
  ok(n >= 1, 'icon search finds rocket: ' + n);
  await ph.locator('.icongrid button[aria-label="rocket"]').click();
  // tag search ("music" should find icons tagged with it, not only named)
  await ph.fill('input[type="search"]', 'spotify');
  await ph.waitForTimeout(100);
  await ph.fill('input[type="search"]', 'rocket');
  // light + icon colour + size + solid
  await ph.locator('.lights').first().locator('button[aria-label="Ember"]').click();
  await ph.locator('.lights').nth(1).locator('button[aria-label="White heat"]').click();
  await ph.locator('input[aria-label="Icon size"]').fill('120');
  await ph.locator('.seg button:has-text("Solid")').last().click();
  await ph.locator('#shBody').evaluate(e => e.scrollTop = e.scrollHeight);
  await ph.waitForTimeout(150);
  await ph.screenshot({ path: OUT + '/phone-look.png' });
  await ph.locator('#shBody').evaluate(e => e.scrollTop = 0);
  await ph.screenshot({ path: OUT + '/phone-editor-top.png' });
  await ph.click('#shFoot .pill.primary');
  await ph.waitForTimeout(700);
  let k = prof().pages[0].keys.find(x => x && x.label === 'Copy');
  ok(k.icon === 'i:rocket' && k.color === 'k:ember' && k.iconColor === 'k:whiteheat' && k.iconScale === 1.2 && k.style === 'solid', 'key look saved: ' + JSON.stringify({ icon: k.icon, color: k.color, ic: k.iconColor, s: k.iconScale, st: k.style }));
  // app icon from the PC (dry-run returns a tiny png)
  await ph.locator('.key[aria-label="Paste"]').click();
  await ph.click('.seg button:has-text("From PC")');
  await ph.fill('input[placeholder^="C:\\\\Path"]', 'C:\\Windows\\notepad.exe');
  await ph.click('button:has-text("Use app icon")');
  await ph.waitForTimeout(600);
  await ph.click('#shFoot .pill.primary');
  await ph.waitForTimeout(700);
  k = prof().pages[0].keys.find(x => x && x.label === 'Paste');
  ok(k.icon.startsWith('data:image/png'), 'app icon fetched from PC');
  await ph.click('#doneBtn');
  await ph.waitForTimeout(200);
  await ph.screenshot({ path: OUT + '/phone-custom.png' });
  // colourways
  for (const [t, label] of [['bisque', 'Light'], ['reduction', 'Deep red'], ['carbon', 'Pure black']]) {
    await ph.click('#settingsBtn');
    await ph.locator('.seg button', { hasText: label }).click();
    await ph.waitForTimeout(150);
    await ph.click('#shClose');
    await ph.waitForTimeout(350);
    ok(await ph.evaluate(() => document.documentElement.dataset.theme) === t, 'colourway ' + t);
    await ph.screenshot({ path: `${OUT}/phone-theme-${t}.png` });
  }
  await ph.click('#settingsBtn');
  await ph.locator('.seg button', { hasText: 'Ember' }).click();
  await ph.locator('.seg button', { hasText: 'Circle' }).click();
  await ph.waitForTimeout(200);
  await ph.screenshot({ path: OUT + '/phone-settings.png' });
  await ph.locator('.seg button', { hasText: 'Rounded' }).click();
  await ph.click('#shClose');
  // pair screen look
  const p2 = await (await b.newContext({ ...devices['iPhone 13'] })).newPage();
  await p2.goto((process.env.LAN || 'http://192.0.2.2:8765') + '/');
  await p2.waitForSelector('#pair:not(.hidden)');
  await p2.screenshot({ path: OUT + '/phone-pair.png' });
  // PC
  const pc = await b.newPage({ viewport: { width: 1280, height: 800 } });
  pc.on('pageerror', e => errors.push(e.message));
  await pc.goto('http://localhost:8765/#connect');
  await pc.waitForSelector('.bigpin'); await pc.waitForTimeout(600);
  await pc.screenshot({ path: OUT + '/pc-connect.png' });
  await pc.click('#shClose'); await pc.waitForTimeout(400);
  await pc.screenshot({ path: OUT + '/pc-deck.png' });
  ok(!errors.length, 'no page errors ' + errors.join(' | '));
  await b.close();
})();
