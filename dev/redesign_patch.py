# One-off: turns the v1 UI (index.v1.html) into the Kiln redesign (index.src.html).
import re, os
here = os.path.dirname(os.path.abspath(__file__))
old = open(os.path.join(here, 'index.v1.html'), encoding='utf-8').read()
head = open(os.path.join(here, 'head.html'), encoding='utf-8').read()
js = old[old.index('<script>'):]

def between(src, start, end, new):
    a = src.index(start); b = src.index(end, a)
    return src[:a] + new + src[b:]

def rep(src, a, b, count=1):
    assert a in src, a[:80]
    return src.replace(a, b, count)

# ---------- helpers
js = rep(js, "const fg = bg => lum(bg) > .45 ? '#0B1120' : '#F8FAFC';", r"""
/* key lights: a preset name ('k:ember') or a custom '#rrggbb' */
const LIGHTS = [['cream', 'Cream', .92], ['ember', 'Ember', .70], ['amber', 'Amber', .82], ['whiteheat', 'White heat', .96], ['oxblood', 'Oxblood', .58], ['rose', 'Rose', .74],
  ['sage', 'Sage', .78], ['teal', 'Teal', .76], ['sky', 'Sky', .76], ['iris', 'Iris', .72], ['stone', 'Stone', .66], ['ash', 'Ash', .44]];
const LIGHT_L = Object.fromEntries(LIGHTS.map(([n, , l]) => [n, l]));
function cssColor(c) { if (!c) return ''; if (c.startsWith('k:')) return `var(--key-${c.slice(2)})`; return /^#[0-9a-f]{6}$/i.test(c) ? c : ''; }
function isLight(c) { if (!c) return true; if (c.startsWith('k:')) return (LIGHT_L[c.slice(2)] || .9) >= .7; return lum(c) > .4; }
const OLD_HEX = { '#1b2336': 'cream', '#334155': 'stone', '#0f766e': 'teal', '#16a34a': 'sage', '#22c55e': 'sage', '#0ea5e9': 'sky', '#2563eb': 'sky', '#6366f1': 'iris', '#4f46e5': 'iris', '#9333ea': 'iris', '#db2777': 'rose', '#dc2626': 'oxblood', '#ea580c': 'ember', '#ca8a04': 'amber', '#000000': 'ash', '#f8fafc': 'whiteheat' };
const MARK = '<svg viewBox="0 0 48 48" fill="none" aria-hidden="true"><rect x="3" y="3" width="42" height="42" rx="12" stroke="currentColor" stroke-width="2"/><rect x="11" y="11" width="11" height="11" rx="3.5" fill="currentColor"/><rect x="26" y="11" width="11" height="11" rx="3.5" fill="currentColor" opacity=".45"/><rect x="11" y="26" width="11" height="11" rx="3.5" fill="currentColor" opacity=".45"/><rect x="26" y="26" width="11" height="11" rx="3.5" fill="var(--color-accent)"/></svg>';
""")

# ---------- model
js = rep(js, "const COLORS = ['#1B2336', '#334155', '#0F766E', '#16A34A', '#22C55E', '#0EA5E9', '#2563EB', '#6366F1', '#9333EA', '#DB2777', '#DC2626', '#EA580C', '#CA8A04', '#000000', '#F8FAFC'];\n", "")
js = rep(js, "const K = (label, icon, color, action, extra) => Object.assign({ label, icon: 'i:' + icon, color, action }, extra || {});",
     "const K = (label, icon, color, action, extra) => Object.assign({ label, icon: 'i:' + icon, color: 'k:' + (OLD_HEX[String(color).toLowerCase()] || color), action }, extra || {});")
js = rep(js, "const DEFAULT_SETTINGS = { layout: 15, labels: true, haptics: true, awake: true, confirm: true, oled: false, gap: 10 };",
     "const DEFAULT_SETTINGS = { layout: 15, labels: true, haptics: true, awake: true, confirm: true, gap: 12, theme: 'ember', shape: 'rounded', keyStyle: 'backlit' };")
# warmer defaults on the main page
js = rep(js, "K('Play / Pause', 'play', '#16A34A',", "K('Play / Pause', 'play', 'ember',")
js = rep(js, "K('Mic', 'mic', '#0F766E', { type: 'audio', op: 'micmute' }),\n    K('Screenshot'", "K('Mic', 'mic', 'cream', { type: 'audio', op: 'micmute' }),\n    K('Screenshot'")
js = rep(js, "K('Lock PC', 'lock', '#DC2626',", "K('Lock PC', 'lock', 'oxblood',")
js = rep(js, "K('Apps', 'folder', '#CA8A04',", "K('Apps', 'folder', 'amber',")
js = rep(js, "K('Work page', 'briefcase', '#2563EB',", "K('Work page', 'briefcase', 'whiteheat',")

# migration of older profiles
js = rep(js, "  S.profile.settings = Object.assign({}, DEFAULT_SETTINGS, S.profile.settings || {});",
"""  S.profile.settings = Object.assign({}, DEFAULT_SETTINGS, S.profile.settings || {});
  migrate(S.profile);""")
js = rep(js, "function startDeck() {", """function migrate(p) {
  if (p.settings.oled) { p.settings.theme = 'carbon'; delete p.settings.oled; }
  const fix = o => { if (o && o.color && /^#/.test(o.color) && OLD_HEX[o.color.toLowerCase()]) o.color = 'k:' + OLD_HEX[o.color.toLowerCase()]; };
  p.pages.forEach(pg => (pg.keys || []).forEach(k => { if (k) { fix(k); fix(k.alt); } }));
}

function startDeck() {""")

# ---------- render
js = between(js, "function render() {", "function keyVisual(key) {", r"""function applyLook() {
  const st = settings();
  document.documentElement.dataset.theme = st.theme || 'ember';
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.content = getComputedStyle(document.documentElement).getPropertyValue('--color-paper').trim() || meta.content;
}
function render() {
  const pg = curPage(); if (!pg) return;
  S.pageId = pg.id; if (!pg.folder) LS.set('pd.page', pg.id);
  applyLook();
  document.body.classList.toggle('editing', S.editing);
  $('#pageTitle').textContent = pg.name;
  $('#backBtn').classList.toggle('hidden', !pg.folder);
  $('#editBtn').classList.toggle('hidden', S.editing);
  $('#editbar').classList.toggle('hidden', !S.editing);
  renderBanners();
  const { cols, k, gap } = dims();
  const g = $('#grid');
  g.dataset.shape = settings().shape || 'rounded';
  g.style.gridTemplateColumns = `repeat(${cols}, ${k}px)`;
  g.style.setProperty('--k', k + 'px'); g.style.setProperty('--gap', gap + 'px');
  g.innerHTML = '';
  const n = slots();
  for (let i = 0; i < n; i++) g.append(keyEl(pg.keys[i], i));
  const tabs = $('#pages'); tabs.innerHTML = '';
  if (pg.folder) {
    const parent = page(S.stack[S.stack.length - 1]);
    tabs.append(h('span', { class: 'crumb' }, `Folder in ${parent ? parent.name : 'your deck'}`));
  } else if (topPages().length > 1) {
    topPages().forEach(p => tabs.append(h('button', { class: p.id === pg.id ? 'cur' : '', 'aria-current': p.id === pg.id ? 'page' : null, onclick: () => goPage(p.id) }, p.name)));
  }
}

""")

js = between(js, "function keyVisual(key) {", "function keyId(key)", r"""function keyVisual(key) {
  let label = key.label, icon = key.icon, color = key.color || 'k:cream', iconColor = key.iconColor || '', badge = '', live = false;
  const a = key.action || {};
  if (a.type === 'toggle' && S.toggles[keyId(key)] && key.alt) {
    label = key.alt.label ? key.alt.label : label;
    icon = key.alt.icon || icon; color = key.alt.color || color;
  }
  if (a.type === 'audio') {
    const L = S.live;
    if (a.op === 'mute' && L.muted) { icon = 'i:volume-x'; live = true; }
    if (a.op === 'micmute' && L.micMuted) { icon = 'i:mic-off'; live = true; label = (label || 'Mic') + ' off'; }
    if ((a.op === 'up' || a.op === 'down' || a.op === 'set' || a.op === 'mute') && L.volume != null) badge = String(L.volume);
  }
  return { label, icon, color, iconColor, badge, live };
}
""")

js = between(js, "function keyEl(key, i) {", "/* ---------- press / hold / drag ---------- */", r"""function keyEl(key, i) {
  if (!key) {
    const el = h('button', { class: 'key empty', 'data-i': i, 'aria-label': 'Empty key' });
    if (S.editing) el.append(h('div', { class: 'ki', html: svg('plus') }));
    bindKey(el, null, i);
    return el;
  }
  const v = keyVisual(key);
  const showLabel = settings().labels && !key.hideLabel && v.label;
  const full = v.icon && v.icon.startsWith('data:') && key.imgFull;
  const solid = (key.style || settings().keyStyle) === 'solid';
  const vars = [`--kc:${cssColor(v.color) || 'var(--key-cream)'}`];
  if (v.iconColor) vars.push(`--ic:${cssColor(v.iconColor)}`);
  if (key.iconScale && key.iconScale !== 1) vars.push(`--is:${key.iconScale}`);
  if (solid) vars.push(`--on-kc:${isLight(v.color) ? 'var(--color-accent-ink)' : 'var(--color-ink)'}`);
  const el = h('button', {
    class: 'key' + (solid ? ' solid' : '') + (showLabel ? '' : ' nolabel') + (full ? ' imgfull' : '') + (v.live ? ' live' : ''),
    'data-i': i, 'aria-label': v.label || 'Key', style: vars.join(';')
  },
    iconNode(v.icon), showLabel ? h('div', { class: 'kl' }, v.label) : null,
    v.badge ? h('div', { class: 'badge figure' }, v.badge) : null,
    key.hold && key.hold.type && key.hold.type !== 'none' ? h('div', { class: 'hold', title: 'Has a long-press action' }) : null);
  bindKey(el, key, i);
  return el;
}

""")
js = rep(js, "ghost = el.cloneNode(true); ghost.classList.add('ghost'); ghost.classList.remove('dragging');",
     "ghost = el.cloneNode(true); ghost.classList.add('ghost-key'); ghost.classList.remove('dragging');")
js = rep(js, "!x.classList.contains('ghost') &&", "!x.classList.contains('ghost-key') &&")

js = between(js, "function renderBanners() {", "/* ============================================================ sheet framework */", r"""function renderBanners() {
  const b = $('#banners'); b.innerHTML = '';
  const st = $('#status');
  st.className = 'status' + (!S.online ? ' off' : S.paused ? ' paused' : '');
  st.textContent = !S.online ? 'Not connected, retrying' : S.paused ? `Paused on ${S.pc}` : S.editing ? 'Editing' : `Connected to ${S.pc || 'your PC'}`;
  if (!S.online) b.append(h('div', { class: 'notice bad' }, I('wifi-off'), h('span', { class: 'grow' }, 'Can’t reach your PC. Check the Wi-Fi and that PocketDeck is running there.')));
  else if (S.paused) b.append(h('div', { class: 'notice' }, I('pause'), h('span', { class: 'grow' }, 'Keys are paused from the PC tray icon.')));
}

""")

# ---------- key editor: look section
js = rep(js, "const key = isNew ? { label: '', icon: 'i:keyboard', color: '#1B2336', action: { type: 'hotkey', keys: '' } } : clone(pg.keys[i]);",
     "const key = isNew ? { label: '', icon: 'i:keyboard', color: 'k:cream', action: { type: 'hotkey', keys: '' } } : clone(pg.keys[i]);")
js = rep(js, """    prev.innerHTML = ''; const k = Math.min(110, window.innerWidth / 3.5);""", """    prev.innerHTML = ''; const k = Math.min(120, window.innerWidth / 3.2);""")
js = between(js, "  // ---- appearance", "  // ---- action", r"""  // ---- appearance
  const labelIn = h('input', { class: 'inp', value: key.label || '', placeholder: 'Label (optional)', maxlength: 40, oninput: e => { key.label = e.target.value; refreshPrev(); } });
  const appearance = h('div', { class: 'sec' }, h('h3', {}, 'Look'),
    field('Label', h('div', {}, labelIn, switchRow('Show the label', null, !key.hideLabel, v => { key.hideLabel = !v; refreshPrev(); }))),
    iconPicker(() => key.icon, v => { key.icon = v; refreshPrev(); }, key, refreshPrev),
    lookControls(key, refreshPrev));

""")
js = rep(js, "      if (t === 'folder') key.color = key.color === '#1B2336' ? '#CA8A04' : key.color;",
     "      if (t === 'folder' && (!key.color || key.color === 'k:cream')) key.color = 'k:amber';")
js = rep(js, "  key.alt = key.alt || { label: '', icon: '', color: '#DC2626' };", "  key.alt = key.alt || { label: '', icon: '', color: 'k:ember' };")
js = rep(js, "      h('div', { class: 'field' }, h('label', {}, 'Color when on'), swatches(() => key.alt.color, v => { key.alt.color = v; }))));",
     "      field('Light when on', lightPicker(() => key.alt.color, v => { key.alt.color = v; }))));")
js = rep(js, "!isNew ? h('button', { class: 'ibtn', 'aria-label': 'Delete key', title: 'Delete key', html: svg('trash-2'),", "!isNew ? h('button', { class: 'round', 'aria-label': 'Delete key', title: 'Delete key', html: svg('trash-2'),")
js = rep(js, "!isNew ? h('button', { class: 'ibtn', 'aria-label': 'Copy key', title: 'Copy key', html: svg('copy'),", "!isNew ? h('button', { class: 'round', 'aria-label': 'Copy key', title: 'Copy key', html: svg('copy'),")

# ---------- icon picker, lights
js = between(js, "function iconPicker(get, set, key, refresh, allowBlank) {", "function shrinkImage(file) {", r"""const ICON_NAMES = Object.keys(ICONS);
function iconSearch(q) {
  q = q.toLowerCase().trim();
  if (!q) return ICON_NAMES;
  const words = q.split(/\s+/);
  const hit = n => words.every(w => n.includes(w) || (ICON_TAGS[n] || '').includes(w));
  const exact = ICON_NAMES.filter(n => n.startsWith(q)), rest = ICON_NAMES.filter(n => !n.startsWith(q) && hit(n));
  return exact.concat(rest);
}
function iconPicker(get, set, key, refresh, allowBlank) {
  const box = h('div', { class: 'field' });
  const cur = () => get() || '';
  let mode = cur().startsWith('e:') ? 'emoji' : cur().startsWith('data:') ? 'image' : 'icon';
  let limit = 120;
  const draw = () => {
    box.innerHTML = '';
    box.append(h('span', { class: 'lbl' }, 'Icon'));
    box.append(h('div', { class: 'seg', role: 'tablist' },
      [['icon', 'Icons'], ['app', 'From PC'], ['emoji', 'Emoji'], ['image', 'Image'], ['none', allowBlank ? 'Same' : 'None']].map(([m, l]) =>
        h('button', { class: mode === m ? 'on' : '', role: 'tab', 'aria-selected': mode === m ? 'true' : 'false', onclick: () => { if (m === 'none') { set(''); mode = 'icon'; } else mode = m; limit = 120; draw(); } }, l))));
    const pane = h('div', { style: 'margin-top:12px' });
    box.append(pane);
    if (mode === 'icon') {
      const grid = h('div', { class: 'icongrid' });
      const search = h('input', { class: 'inp', type: 'search', placeholder: `Search ${ICON_NAMES.length.toLocaleString()} icons: mic, play, folder`, oninput: () => { limit = 120; fill(); } });
      const fill = () => {
        grid.innerHTML = '';
        const list = iconSearch(search.value);
        list.slice(0, limit).forEach(n => grid.append(h('button', { class: cur() === 'i:' + n ? 'on' : '', title: n, 'aria-label': n, html: svg(n), onclick: () => { set('i:' + n); fill(); } })));
        if (list.length > limit) grid.append(h('button', { class: 'more', onclick: () => { limit += 240; fill(); } }, `Show more (${list.length - limit} left)`));
        if (!list.length) grid.append(h('p', { class: 'hint', style: 'grid-column:1/-1' }, 'No icon by that name. Try another word.'));
      };
      pane.append(search, grid); fill();
    } else if (mode === 'app') {
      const a = key && key.action || {};
      const path = h('input', { class: 'inp mono', placeholder: 'C:\\Path\\to\\app.exe, a file or a folder', value: a.type === 'open' ? (a.target || '') : '' });
      const web = h('input', { class: 'inp mono', type: 'url', placeholder: 'youtube.com', value: a.type === 'url' ? (a.target || '') : '' });
      const go = async (body, btn) => {
        btn.disabled = true; const t = btn.textContent; btn.textContent = 'Getting icon';
        try { const r = await api('/api/icon', { method: 'POST', body, timeout: 15000 }); if (!r.ok) throw new Error(r.error); set(r.data); if (key) key.imgFull = false; toast('Icon added'); draw(); }
        catch (e) { toast(e.message, true); }
        finally { btn.disabled = false; btn.textContent = t; }
      };
      const b1 = h('button', { class: 'pill ghost', onclick: e => go({ path: path.value }, e.currentTarget) }, 'Use app icon');
      const b2 = h('button', { class: 'pill ghost', onclick: e => go({ url: web.value }, e.currentTarget) }, 'Use site icon');
      pane.append(
        field('An app, file or folder on your PC', h('div', {}, h('div', { class: 'row' }, h('div', { class: 'grow' }, path), h('button', { class: 'round', 'aria-label': 'Pick an app', html: svg('search'), onclick: () => pickApp(app => { path.value = app.path; go({ path: app.path }, b1); }) })), h('div', { class: 'row', style: 'margin-top:8px' }, b1))),
        field('A website', h('div', {}, web, h('div', { class: 'row', style: 'margin-top:8px' }, b2))),
        h('p', { class: 'hint' }, 'Takes the real icon from your PC, like Spotify’s or Chrome’s.'));
    } else if (mode === 'emoji') {
      const val = cur().startsWith('e:') ? cur().slice(2) : '';
      const picks = ['🎮', '🎧', '🎤', '📷', '🎬', '🔴', '💬', '📧', '📅', '💡', '🔥', '🚀', '⭐', '✅', '🔒', '📁', '🌐', '☕', '💰', '🧠', '🎵', '👋', '😂', '👍'];
      pane.append(h('input', { class: 'inp', value: val, placeholder: 'Type or paste any emoji', maxlength: 8, oninput: e => set(e.target.value ? 'e:' + e.target.value : '') }),
        h('div', { class: 'chips', style: 'margin-top:8px' }, picks.map(p => h('button', { class: 'chip', style: 'font-size:20px', 'aria-label': p, onclick: () => { set('e:' + p); draw(); } }, p))));
    } else if (mode === 'image') {
      const file = h('input', { type: 'file', accept: 'image/*', class: 'hidden', onchange: async e => { const f = e.target.files[0]; if (!f) return; try { set(await shrinkImage(f)); draw(); } catch (err) { toast(err.message, true); } } });
      pane.append(h('div', { class: 'row' }, h('button', { class: 'pill ghost', onclick: () => file.click() }, I('upload'), 'Choose an image'), file));
      if (key) pane.append(switchRow('Fill the whole key', 'The picture covers the key', key.imgFull, v => { key.imgFull = v; refresh(); }));
      pane.append(h('p', { class: 'hint' }, 'Logos and photos work well. They are shrunk to keep the deck quick.'));
    }
    refresh();
  };
  draw();
  return box;
}
function lookControls(key, refresh) {
  const box = h('div');
  const scale = h('span', { class: 'kbd' }, Math.round((key.iconScale || 1) * 100) + '%');
  box.append(
    field('Key light', lightPicker(() => key.color, v => { key.color = v; refresh(); })),
    field('Icon color', lightPicker(() => key.iconColor || '', v => { key.iconColor = v; refresh(); }, true)),
    field('Icon size', h('div', { class: 'row' }, h('input', { type: 'range', class: 'grow', min: 60, max: 140, step: 5, value: Math.round((key.iconScale || 1) * 100), 'aria-label': 'Icon size',
      oninput: e => { key.iconScale = +e.target.value / 100; scale.textContent = e.target.value + '%'; refresh(); } }), scale)),
    field('Key style', segmented([['', 'Deck default'], ['backlit', 'Backlit'], ['solid', 'Solid']], key.style || '', v => { key.style = v || undefined; refresh(); })));
  return box;
}
function segmented(opts, cur, onPick) {
  const box = h('div', { class: 'seg' });
  const draw = c => { box.innerHTML = ''; opts.forEach(([v, l]) => box.append(h('button', { class: v === c ? 'on' : '', 'aria-pressed': v === c ? 'true' : 'false', onclick: () => { onPick(v); draw(v); } }, l))); };
  draw(cur); return box;
}
function lightPicker(get, set, withAuto) {
  const box = h('div', { class: 'lights' });
  const draw = () => {
    box.innerHTML = '';
    if (withAuto) box.append(h('button', { class: 'light auto' + (!get() ? ' on' : ''), title: 'Match the key light', 'aria-label': 'Match the key light', onclick: () => { set(''); draw(); } }));
    LIGHTS.forEach(([n, l]) => box.append(h('button', { class: 'light' + (get() === 'k:' + n ? ' on' : ''), style: `--sw:var(--key-${n})`, title: l, 'aria-label': l, onclick: () => { set('k:' + n); draw(); } })));
    const custom = get() && get().startsWith('#');
    box.append(h('label', { class: 'light custom' + (custom ? ' on' : ''), title: 'Any color' },
      h('input', { type: 'color', 'aria-label': 'Any color', value: custom ? get() : '#ff8a3d', oninput: e => set(e.target.value), onchange: draw })));
  };
  draw();
  return box;
}
""")
js = between(js, "function swatches(get, set) {", "/* ---------- app picker ---------- */", "")

# app picker: real icon on pick + website icon
js = rep(js, "  const panel = h('div', { style: 'position:sticky;top:-14px;background:var(--surface);z-index:2;padding:10px 0;border-bottom:1px solid var(--muted)' });",
     "  const panel = h('div', { class: 'picker' });")
js = rep(js, "h('button', { class: 'btn', onclick: close }, 'Cancel')", "h('button', { class: 'pill ghost', onclick: close }, 'Cancel')")
js = rep(js, "list.append(h('button', { onclick: () => { onPick(a); close(); } }, a.name))", "list.append(h('button', { onclick: () => { onPick(a); close(); } }, I('app-window'), a.name))")
js = rep(js, "h('button', { class: 'btn', onclick: () => pickApp(app => { a.target = app.path; inp.value = app.path; setLabel(app.name, 'app-window'); }) }, I('search'), 'Pick app')),",
     "h('button', { class: 'pill ghost', onclick: () => pickApp(app => { a.target = app.path; inp.value = app.path; setLabel(app.name, 'app-window'); autoIcon({ path: app.path }); }) }, I('search'), 'Pick app')),")
js = rep(js, "  const setLabel = (txt, icon) => {", r"""  const autoIcon = async body => {
    if (!key || (key.icon && !key.icon.startsWith('i:'))) return;
    try { const r = await api('/api/icon', { method: 'POST', body, timeout: 15000 }); if (r.ok) { key.icon = r.data; changed(); } } catch {}
  };
  const setLabel = (txt, icon) => {""")
js = rep(js, "box.append(field('Website address', h('input', { class: 'inp mono', type: 'url', value: a.target || '', placeholder: 'https://example.com', oninput: e => { a.target = e.target.value; changed(); } }), 'Opens in your default browser on the PC.'));",
     "box.append(field('Website address', h('input', { class: 'inp mono', type: 'url', value: a.target || '', placeholder: 'https://example.com', oninput: e => { a.target = e.target.value; changed(); }, onchange: () => autoIcon({ url: a.target }) }), 'Opens in your default browser on the PC. The site’s own icon is added for you.'));")

# ---------- settings
js = rep(js, """  body.append(h('div', { class: 'sec' }, h('h3', {}, 'Layout'),""", """  body.append(h('div', { class: 'sec' }, h('h3', {}, 'Look'),
    field('Colorway', segmented([['ember', 'Ember'], ['reduction', 'Deep red'], ['carbon', 'Pure black'], ['bisque', 'Light']], st.theme || 'ember', v => { st.theme = v; up(); })),
    field('Key shape', segmented([['rounded', 'Rounded'], ['square', 'Square'], ['round', 'Circle']], st.shape || 'rounded', v => { st.shape = v; up(); })),
    field('Key style', segmented([['backlit', 'Backlit'], ['solid', 'Solid']], st.keyStyle || 'backlit', v => { st.keyStyle = v; up(); }), 'Each key can override this in its own Look settings.')));
  body.append(h('div', { class: 'sec' }, h('h3', {}, 'Layout'),""")
js = rep(js, "return h('button', { class: 'chip' + (+st.layout === +n ? ' on' : ''), onclick: () => { st.layout = +n; up(); openSettings(); } }, `${n}  (${a}×${b})`);",
     "return h('button', { class: 'chip' + (+st.layout === +n ? ' on' : ''), onclick: () => { st.layout = +n; up(); openSettings(); } }, `${n} keys, ${a} by ${b}`);")
js = rep(js, "    switchRow('Pure black background', 'Saves battery on OLED phones', st.oled, v => { st.oled = v; up(); })));", "));")
js = rep(js, "h('input', { class: 'inp grow', value: p.name, style: 'min-height:38px;border:0;background:transparent;padding:0', 'aria-label': 'Page name',",
     "h('input', { class: 'name grow', value: p.name, 'aria-label': 'Page name',")
js = rep(js, "pagesBox.append(h('div', { class: 'hint', style: 'margin-top:6px' }, 'Folders'));", "pagesBox.append(h('div', { class: 'lbl', style: 'margin-top:16px' }, 'Folders'));")
js = rep(js, "h('div', { class: 'hint' }, 'Streaming keys use Ctrl+Shift+F-keys: set the same hotkeys in OBS > Settings > Hotkeys.')));",
     "h('p', { class: 'hint' }, 'The streaming page uses Ctrl+Shift+F keys. Set the same hotkeys in OBS, under Settings, Hotkeys.')));")
js = rep(js, "body.append(h('div', { class: 'hint', style: 'text-align:center' }, 'PocketDeck 1.0'));", "body.append(h('p', { class: 'hint', style: 'text-align:center' }, 'PocketDeck 1.1'));")

# ---------- connect
js = between(js, "  body.innerHTML = '';\n  const url = info.urls[0];", "  const watch = setInterval(", r"""  body.innerHTML = '';
  const url = info.urls[0];
  if (!url) { body.append(h('div', { class: 'notice bad' }, I('wifi-off'), h('span', { class: 'grow' }, 'This PC is not on a network yet. Join your Wi-Fi first.'))); return; }
  const qr = qrcode(0, 'M'); qr.addData(url + '?pin=' + info.pin); qr.make();
  body.className = 'connect';
  body.append(
    h('div', { class: 'top' },
      h('div', { class: 'qrbox', html: qr.createSvgTag({ cellSize: 6, margin: 0, scalable: true }), role: 'img', 'aria-label': 'QR code that opens PocketDeck on your phone' }),
      h('div', {},
        h('ol', { class: 'steps-list' }, h('li', {}, 'Join the same Wi-Fi as this PC.'), h('li', {}, 'Point the phone camera at the code.'), h('li', {}, 'Or open the address below and type the PIN.')))),
    h('div', {}, h('span', { class: 'lbl' }, 'PIN'), h('div', { class: 'bigpin figure' }, info.pin)),
    h('div', {}, h('span', { class: 'lbl' }, info.urls.length > 1 ? 'Addresses' : 'Address'), ...info.urls.map(u => h('div', { class: 'url' }, u))),
    info.devices.length ? h('div', {}, h('span', { class: 'lbl' }, 'Paired phones'),
      h('div', { class: 'list' }, info.devices.map(d => h('div', { class: 'li' }, I('smartphone'), h('span', { class: 'grow' }, d.name), h('span', { class: 'hint', style: 'margin:0' }, ago(d.seen))))),
      h('button', { class: 'pill danger small', style: 'margin-top:8px', onclick: async () => { if (await confirmDlg('Disconnect every phone and make a new PIN?', 'Unpair all')) { await api('/api/unpair-all', { method: 'POST', body: {} }); openConnect(); } } }, 'Unpair all and make a new PIN')) : null,
    h('details', {}, h('summary', {}, 'Phone can’t connect?'),
      h('ul', { class: 'steps-list' },
        h('li', {}, 'Both must be on the same Wi-Fi, not a guest network.'),
        h('li', {}, 'In Windows, set this Wi-Fi to Private: Settings, Network, Wi-Fi, your network.'),
        h('li', {}, 'If Windows asked about the firewall, choose Allow, or run Install.bat again.'),
        h('li', {}, 'Try the other address if two are listed.'))));
""")
js = rep(js, "  const body = h('div', { class: 'connect-grid' }, h('p', { class: 'hint' }, 'Loading…'));", "  const body = h('div', {}, h('p', { class: 'hint' }, 'Loading'));")
js = rep(js, "if (n.devices.length > info.devices.length) { clearInterval(watch); toast('Phone connected!'); openConnect(); }", "if (n.devices.length > info.devices.length) { clearInterval(watch); toast('Phone connected'); openConnect(); }")

# phone tips
js = rep(js, "openSheet('Make it feel like an app', h('div', {}, h('ol', { class: 'tips', style: 'font-size:15px;color:var(--text)' }, steps.map(s => h('li', { style: 'margin-bottom:8px' }, s))),\n    h('div', { class: 'hint' }, 'Tip: a cheap phone stand + charger turns it into a permanent desk deck.')), { center: true, foot: [h('button', { class: 'btn primary block', onclick: closeSheet }, 'Got it')] });",
     "openSheet('Make it feel like an app', h('div', {}, h('ol', { class: 'steps-list' }, steps.map(s => h('li', {}, s))),\n    h('p', { class: 'hint', style: 'margin-top:16px' }, 'A phone stand and a charger turn it into a desk deck that stays put.')), { center: true, foot: [h('button', { class: 'pill primary block', onclick: closeSheet }, 'Got it')] });")

# pairing screen mark + loading
js = rep(js, "async function boot() {\n  wireStatic();", "async function boot() {\n  wireStatic();\n  $('#pairMark').innerHTML = MARK; $('#loadMark').innerHTML = MARK;")
js = rep(js, "catch (e) { $('#loading p').textContent = 'Cannot reach your PC. Is PocketDeck running and are you on the same Wi-Fi?'; setTimeout(boot, 4000); return; }",
     "catch (e) { $('#loadText').textContent = 'Can’t reach your PC. Check that PocketDeck is running there and you are on the same Wi-Fi.'; setTimeout(boot, 4000); return; }")

# wiring
js = rep(js, "  $('#connectBtn').innerHTML = svg('smartphone') + '<span>Connect phone</span>';\n  $('#editBtn').onclick = () => { S.editing = !S.editing; render(); };",
     "  $('#connectBtn').innerHTML = svg('smartphone') + '<span>Connect a phone</span>';\n  $('#editBtn').onclick = () => { S.editing = true; render(); };")
js = rep(js, "  $('#editBtn').innerHTML = svg('pencil');", "  $('#editBtn').innerHTML = svg('pencil');")

# generic class renames
js = js.replace("class: 'ibtn'", "class: 'round'")
def fix_btn(m):
    cls = m.group(1).split()
    out = []
    for c in cls:
        out.append('pill' if c == 'btn' else c)
    if 'pill' in out and not any(x in out for x in ('primary', 'danger', 'ghost')): out.append('ghost')
    return "class: '" + ' '.join(out) + "'"
js = re.sub(r"class: '((?:[a-z-]+ )*btn(?: [a-z-]+)*)'", fix_btn, js)
js = js.replace("class: 'btn ' + (danger ? 'danger' : 'primary')", "class: 'pill ' + (danger ? 'danger' : 'primary')")
js = js.replace("h('label', { class: 'sw-toggle' }, h('input', { type: 'checkbox', checked: !!val, onchange: e => onChange(e.target.checked) }), h('span'))",
                "h('label', { class: 'switch' }, h('input', { type: 'checkbox', role: 'switch', 'aria-label': title, checked: !!val, onchange: e => onChange(e.target.checked) }), h('span'))")
js = js.replace("h('p', { style: 'margin:0;line-height:1.5' }, msg)", "h('p', {}, msg)")
js = js.replace("{ class: 'hint', style: 'margin:0 0 10px' }", "{ class: 'hint', style: 'margin:0 0 12px' }")
js = js.replace("h('div', { style: 'height:12px' })", "h('div', { style: 'height:16px' })")
js = js.replace("{ class: 'row', style: 'flex-wrap:wrap' }", "{ class: 'row wrap' }")
js = js.replace("{ class: 'row', style: 'margin-top:8px;flex-wrap:wrap' }", "{ class: 'row wrap', style: 'margin-top:12px' }")
js = js.replace("{ class: 'row', style: 'margin-top:10px;flex-wrap:wrap' }", "{ class: 'row wrap', style: 'margin-top:12px' }")
js = js.replace("{ class: 'hint', style: 'margin:0 0 10px' }", "{ class: 'hint', style: 'margin:0 0 12px' }")
js = js.replace("const ICONS = /*@ICONS@*/{};", "const ICONS = /*@ICONS@*/{};\nconst ICON_TAGS = /*@TAGS@*/{};")
js = js.replace("I('wifi'), 'Can’t reach", "I('wifi-off'), 'Can’t reach")

out = head + js
open(os.path.join(here, 'index.src.html'), 'w', encoding='utf-8').write(out)
print('ok', len(out))
for leftover in ['ibtn', "'btn", 'var(--sub)', 'var(--text)', 'var(--card)', 'var(--border)', 'var(--surface)', 'var(--bg)', 'var(--muted)', 'var(--info)', 'oled', 'COLORS', 'swatches(', 'connect-grid', "'tips'"]:
    if leftover in js: print('LEFTOVER', leftover, [m.start() for m in re.finditer(re.escape(leftover), js)][:5])
