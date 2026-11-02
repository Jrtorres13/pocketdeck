// End-to-end test: run the server headless with --dry-run, then drive phone + PC pages.
const { chromium, devices } = require('playwright');
const fs = require('fs');
const LAN = process.env.LAN || 'http://192.0.2.2:8765';
const DATA = process.env.DATA || '/tmp/claude-0/pddata';
const OUT = process.env.OUT || '/tmp/claude-0/shots';
const log = () => fs.existsSync(DATA + '/dryrun.log') ? fs.readFileSync(DATA + '/dryrun.log', 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse) : [];
const ok = (c, m) => { if (!c) { console.error('FAIL', m); process.exitCode = 1; } else console.log('ok', m); };
(async () => {
  const browser = await chromium.launch();
  const errors = [];
  // ---------- PC editor (localhost)
  const pc = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  pc.on('pageerror', e => errors.push('pc: ' + e.message));
  pc.on('console', m => { if (m.type() === 'error' && !/fonts|ERR_/.test(m.text())) errors.push('pc console: ' + m.text()); });
  await pc.goto('http://localhost:8765/#connect');
  await pc.waitForSelector('.bigpin');
  const pin = (await pc.textContent('.bigpin')).trim();
  ok(/^\d{6}$/.test(pin), 'connect panel shows PIN ' + pin);
  ok(await pc.$('.qrbox svg'), 'QR code rendered');
  await pc.screenshot({ path: OUT + '/pc-connect.png' });
  // ---------- phone
  const ctx = await browser.newContext({ ...devices['iPhone 13'] });
  const ph = await ctx.newPage();
  ph.on('pageerror', e => errors.push('phone: ' + e.message));
  await ph.goto(LAN + '/');
  await ph.waitForSelector('#pair:not(.hidden)');
  await ph.screenshot({ path: OUT + '/phone-pair.png' });
  for (const d of '111111') await ph.click(`#numpad button[aria-label="${d}"]`);
  await ph.waitForTimeout(500);
  ok((await ph.textContent('#pinErr')).includes('Wrong'), 'wrong PIN rejected');
  for (const d of pin) await ph.click(`#numpad button[aria-label="${d}"]`);
  await ph.waitForSelector('#app:not(.hidden) .key');
  ok(true, 'paired with PIN');
  // phone tips sheet appears; close it
  await ph.waitForTimeout(700);
  if (await ph.isVisible('#sheet.show')) { await ph.click('#shFoot .pill'); await ph.waitForTimeout(300); }
  await ph.screenshot({ path: OUT + '/phone-deck.png' });
  const keys = await ph.$$('#grid .key');
  ok(keys.length === 15, '15 keys on main page: ' + keys.length);
  // press Play/Pause
  const before = log().length;
  await ph.locator('.key[aria-label="Play / Pause"]').dispatchEvent('pointerdown', { button: 0, pointerId: 1, isPrimary: true });
  await ph.locator('.key[aria-label="Play / Pause"]').dispatchEvent('pointerup', { button: 0, pointerId: 1, isPrimary: true });
  await ph.waitForTimeout(600);
  const l = log();
  ok(l.length > before && l[l.length - 1].type === 'media' && l[l.length - 1].key === 'playpause', 'play/pause sent to PC');
  // badge with volume
  ok((await ph.textContent('.key[aria-label="Vol +"]')).includes('42'), 'live volume badge shows');
  // swipe to next page
  await ph.mouse.move(300, 400); await ph.mouse.down(); await ph.mouse.move(80, 410, { steps: 5 }); await ph.mouse.up();
  await ph.waitForTimeout(300);
  ok((await ph.textContent('#pageTitle')) === 'Work', 'swipe goes to Work page: ' + await ph.textContent('#pageTitle'));
  await ph.screenshot({ path: OUT + '/phone-work.png' });
  // folder
  await ph.click('#pages button:nth-child(1)'); await ph.waitForTimeout(300); await ph.waitForTimeout(100);
  await ph.locator('.key[aria-label="Apps"]').dispatchEvent('pointerdown', { button: 0, pointerId: 1 });
  await ph.waitForTimeout(300);
  ok((await ph.textContent('#pageTitle')) === 'Apps' && await ph.isVisible('#backBtn'), 'folder opens with back button');
  await ph.click('#backBtn');
  // edit mode: new key in empty slot? main page full; edit an existing key instead
  await ph.click('#editBtn');
  await ph.locator('.key[aria-label="Undo"]').click();
  await ph.waitForSelector('#sheet.show');
  await ph.click('.type:has-text("Multi-action")');
  await ph.click('button:has-text("Add step")');
  await ph.fill('.step input.mono[placeholder="ctrl+shift+m"]', 'win+d');
  await ph.click('button:has-text("Add wait")');
  await ph.fill('input[placeholder^="Label (optional)"]', 'Combo');
  await ph.screenshot({ path: OUT + '/phone-editor.png' });
  await ph.click('#shFoot .pill.primary');
  await ph.waitForTimeout(800);
  const saved = JSON.parse(fs.readFileSync(DATA + '/profile.json', 'utf8'));
  const combo = saved.pages[0].keys.find(k => k && k.label === 'Combo');
  ok(combo && combo.action.type === 'multi' && combo.action.steps.length === 2 && combo.action.steps[0].keys === 'win+d', 'multi-action saved on PC');
  await ph.click('#doneBtn');
  // PC sees the change live (rev polling)
  await pc.click('#shClose');
  await pc.waitForTimeout(2600);
  ok(await pc.$('.key[aria-label="Combo"]'), 'PC editor picked up phone edit live');
  await pc.screenshot({ path: OUT + '/pc-deck.png' });
  // settings + template
  await ph.click('#settingsBtn');
  await ph.click('.type:has-text("Video calls")');
  await ph.waitForTimeout(500);
  ok((await ph.textContent('#pageTitle')) === 'Calls', 'template page added');
  await ph.screenshot({ path: OUT + '/phone-calls.png' });
  // landscape
  await ph.setViewportSize({ width: 844, height: 390 });
  await ph.waitForTimeout(300);
  await ph.screenshot({ path: OUT + '/phone-landscape.png' });
  const g = await ph.$eval('#grid', el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
  ok(g === 5, 'landscape uses 5 columns: ' + g);
  // mic toggle shows live state after press (dry-run state is fixed so just check request)
  // PC: open editor for a key, check hotkey recorder
  await pc.click('#editBtn');
  await pc.locator('.key[aria-label="Copy"]').click();
  await pc.waitForSelector('#sheet.show');
  await pc.click('button:has-text("Record")');
  await pc.keyboard.press('Control+Shift+KeyK');
  const v = await pc.inputValue('input.mono[placeholder="ctrl+shift+m"]');
  ok(v === 'ctrl+shift+k', 'hotkey recorder captured ' + v);
  await pc.screenshot({ path: OUT + '/pc-editor.png' });
  ok(errors.length === 0, 'no page errors ' + errors.join(' | '));
  await browser.close();
})();
