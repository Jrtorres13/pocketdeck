// v1.2 checks: live info keys, dials, tap/double/hold, smart pages, running light, new action editors.
const { chromium, devices } = require('playwright');
const fs = require('fs');
const OUT = process.env.OUT || '/tmp/claude-0/shots';
const DATA = process.env.DATA || '/tmp/claude-0/pddata';
const LAN = process.env.LAN || 'http://192.0.2.2:8765';
const ok = (c, m) => { if (!c) { console.error('FAIL', m); process.exitCode = 1; } else console.log('ok', m); };
const log = () => { try { return fs.readFileSync(DATA + '/dryrun.log', 'utf8').trim().split('\n').filter(Boolean).map(JSON.parse); } catch { return []; } };
const H = { 'X-Deck': '1', 'Content-Type': 'application/json' };
const uid = () => Math.random().toString(36).slice(2, 10);
(async () => {
  const pin = JSON.parse(fs.readFileSync(DATA + '/config.json', 'utf8')).pin;
  const pr = await (await fetch(LAN + '/api/pair', { method: 'POST', headers: H, body: JSON.stringify({ pin, device: 'Test' }) })).json();
  const T = { ...H, 'X-Deck-Token': pr.token };
  const prof = (await (await fetch('http://localhost:8765/api/profile', { headers: H })).json()).profile;
  const K = (label, icon, color, action, x) => Object.assign({ label, icon: 'i:' + icon, color: 'k:' + color, action }, x || {});
  const live = { id: uid(), name: 'Live', keys: [
    K('', 'clock', 'cream', { type: 'widget', kind: 'clock' }), K('', 'cpu', 'amber', { type: 'widget', kind: 'cpu' }), K('', 'memory-stick', 'sky', { type: 'widget', kind: 'ram' }),
    K('', 'battery-medium', 'sage', { type: 'widget', kind: 'battery' }), K('', 'hourglass', 'ember', { type: 'widget', kind: 'timer', min: 1 }),
    K('', 'timer', 'teal', { type: 'widget', kind: 'stopwatch' }), K('Reps', 'hash', 'rose', { type: 'widget', kind: 'counter' }), K('', 'cloud-sun', 'sky', { type: 'widget', kind: 'weather', city: 'Chicago', unit: 'f' }),
    K('PC volume', 'volume-2', 'ember', { type: 'dial', preset: 'volume', step: 2 }), K('Spotify', 'music', 'sage', { type: 'dial', preset: 'appvol', app: 'spotify', step: 4 }),
    K('Discord', 'headphones', 'iris', { type: 'dial', preset: 'appvol', app: 'discord', step: 4 }), K('Spotify app', 'music', 'sage', { type: 'open', target: 'C:\\Users\\me\\AppData\\Roaming\\Spotify\\Spotify.exe', mode: 'focus' }),
    K('Notepad', 'notebook-pen', 'stone', { type: 'open', target: 'notepad.exe' }),
    K('Triple', 'layers', 'amber', hk('ctrl+1'), { dbl: hk('ctrl+2'), hold: hk('ctrl+3') }),
    K('Snap left', 'panel-left', 'stone', { type: 'window', op: 'left' })
  ] };
  function hk(keys) { return { type: 'hotkey', keys }; }
  const browser = { id: uid(), name: 'Browser', apps: 'chrome', keys: [K('New tab', 'plus', 'sky', hk('ctrl+t'))] };
  prof.pages = [live, browser, ...prof.pages];
  await fetch('http://localhost:8765/api/profile', { method: 'PUT', headers: H, body: JSON.stringify(prof) });

  const b = await chromium.launch();
  const errors = [];
  const ctx = await b.newContext({ ...devices['iPhone 13'] });
  await ctx.route('**/geocoding-api.open-meteo.com/**', r => r.fulfill({ json: { results: [{ name: 'Chicago', latitude: 41.8, longitude: -87.6 }] } }));
  await ctx.route('**/api.open-meteo.com/**', r => r.fulfill({ json: { current: { temperature_2m: 71.6, weather_code: 2 } } }));
  await ctx.addInitScript(t => { localStorage.setItem('pd.token', JSON.stringify(t)); localStorage.setItem('pd.page', JSON.stringify(null)); }, pr.token);
  const ph = await ctx.newPage();
  ph.on('pageerror', e => errors.push(e.message));
  await ph.goto(LAN + '/');
  await ph.waitForSelector('#grid .key');
  // smart pages: dry-run PC says chrome is in front -> Browser page
  await ph.waitForFunction(() => document.querySelector('#pageTitle').textContent === 'Browser', null, { timeout: 6000 }).catch(() => {});
  ok(await ph.textContent('#pageTitle') === 'Browser', 'smart page followed the app in front (chrome)');
  await ph.click('#pages button:has-text("Live")');
  await ph.waitForTimeout(400);
  const big = async i => (await ph.locator(`#grid .key[data-i="${i}"] .wbig`).textContent()).trim();
  ok(/^\d{1,2}:\d\d$/.test(await big(0)), 'clock shows time: ' + await big(0));
  ok(await big(1) === '23%' && await big(2) === '61%' && await big(3) === '80%', 'CPU / RAM / battery live from PC');
  await ph.waitForTimeout(1200);
  ok(await big(7) === '72°', 'weather key shows temperature: ' + await big(7));
  // timer: tap starts, hold resets
  await ph.locator('#grid .key[data-i="4"]').click(); await ph.waitForTimeout(1300);
  ok(/0:5\d/.test(await big(4)), 'timer counting down: ' + await big(4));
  // counter: 3 taps
  for (let i = 0; i < 3; i++) { await ph.locator('#grid .key[data-i="6"]').click(); await ph.waitForTimeout(80); }
  ok(await big(6) === '3', 'counter counted 3');
  // dials
  ok((await ph.locator('#grid .key[data-i="8"] .badge').textContent()) === '42', 'volume dial shows 42');
  ok((await ph.locator('#grid .key[data-i="9"] .badge').textContent()) === '70', 'Spotify dial shows app volume 70');
  ok(await ph.locator('#grid .key[data-i="10"]').evaluate(e => e.classList.contains('live')), 'Discord dial lit as muted');
  const n0 = log().length;
  const box = await ph.locator('#grid .key[data-i="8"]').boundingBox();
  await ph.mouse.move(box.x + 10, box.y + box.height / 2); await ph.mouse.down();
  for (let x = 10; x <= 90; x += 10) await ph.mouse.move(box.x + x, box.y + box.height / 2);
  await ph.mouse.up(); await ph.waitForTimeout(500);
  const dl = log().slice(n0);
  ok(dl.some(a => a.type === 'audio' && a.op === 'up'), 'dragging the dial turned volume up: ' + JSON.stringify(dl));
  ok(await ph.textContent('#pageTitle') === 'Live', 'dial drag did not swipe the page');
  // running light
  ok(await ph.locator('#grid .key[data-i="11"] .run').count() === 1, 'running light on Spotify (running on PC)');
  ok(await ph.locator('#grid .key[data-i="12"] .run').count() === 0, 'no running light on Notepad');
  await ph.screenshot({ path: OUT + '/phone-live.png' });
  // tap / double-tap / hold
  let n1 = log().length;
  await ph.locator('#grid .key[data-i="13"]').click(); await ph.waitForTimeout(500);
  let l = log().slice(n1).map(a => a.keys); ok(l.join() === 'ctrl+1', 'single tap ran tap action: ' + l);
  n1 = log().length;
  await ph.locator('#grid .key[data-i="13"]').dblclick(); await ph.waitForTimeout(500);
  l = log().slice(n1).map(a => a.keys); ok(l.join() === 'ctrl+2', 'double-tap ran double action: ' + l);
  n1 = log().length;
  const kb = await ph.locator('#grid .key[data-i="13"]').boundingBox();
  await ph.mouse.move(kb.x + 20, kb.y + 20); await ph.mouse.down(); await ph.waitForTimeout(700); await ph.mouse.up(); await ph.waitForTimeout(400);
  l = log().slice(n1).map(a => a.keys); ok(l.join() === 'ctrl+3', 'hold ran hold action: ' + l);
  // open in "switch to" mode reaches the PC with mode
  n1 = log().length;
  await ph.locator('#grid .key[data-i="11"]').click(); await ph.waitForTimeout(400);
  ok(log().slice(n1).some(a => a.type === 'open' && a.mode === 'focus'), 'switch-to-app sent');
  // editor: grouped types, soundboard list, live info, dial
  await ph.click('#editBtn');
  await ph.locator('#grid .key[data-i="14"]').click();
  await ph.waitForSelector('#sheet.show');
  ok(await ph.locator('.tgroup').count() === 6, 'action types grouped in 6 sections');
  await ph.screenshot({ path: OUT + '/phone-types.png' });
  await ph.click('.type:has-text("Soundboard")');
  await ph.waitForSelector('.chip:has-text("airhorn")');
  await ph.click('.chip:has-text("airhorn")');
  await ph.locator('#shBody').evaluate(e => e.scrollTop = 600);
  await ph.screenshot({ path: OUT + '/phone-sound.png' });
  n1 = log().length;
  await ph.click('#shFoot button:has-text("Test")'); await ph.waitForTimeout(400);
  ok(log().slice(n1).some(a => a.type === 'sound' && a.file === 'airhorn.mp3'), 'soundboard test plays airhorn on PC');
  await ph.locator('#shBody').evaluate(e => e.scrollTop = 0);
  await ph.click('.type:has-text("App volume")');
  await ph.waitForSelector('.chip:has-text("spotify")');
  await ph.click('.chip:has-text("spotify")');
  await ph.click('.type:has-text("Audio device")');
  await ph.click('.seg button:has-text("Switch to one device")');
  await ph.waitForSelector('.chip:has-text("Headphones (USB)")');
  await ph.click('.chip:has-text("Headphones (USB)")');
  await ph.click('#shFoot .pill.primary'); await ph.waitForTimeout(700);
  const saved = (await (await fetch('http://localhost:8765/api/profile', { headers: H })).json()).profile.pages[0].keys[14];
  ok(saved.action.type === 'output' && saved.action.id === 'hp', 'audio device key saved: ' + JSON.stringify(saved.action));
  // settings: smart page field + new templates
  await ph.click('#doneBtn');
  await ph.click('#settingsBtn');
  ok(await ph.locator('input[placeholder^="Open with app"]').count() >= 2, 'pages have an "open with app" field');
  await ph.click('.type:has-text("Mixer (dials)")'); await ph.waitForTimeout(500);
  await ph.screenshot({ path: OUT + '/phone-mixer.png' });
  await ph.click('#settingsBtn');
  await ph.click('.type:has-text("Windows and PC")'); await ph.waitForTimeout(1200);
  await ph.screenshot({ path: OUT + '/phone-pc.png' });
  ok(errors.length === 0, 'no page errors ' + errors.join(' | '));
  await b.close();
})();
