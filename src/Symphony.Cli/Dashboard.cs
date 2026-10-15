namespace Symphony.Cli;

/// <summary>Client-side dashboard pages that only consume the JSON API (SPEC 13.7.1).</summary>
internal static class Dashboard
{
    /// <summary>The default view at <c>/</c>: a read-only, Trello-style board.</summary>
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Symphony</title>
<script>try { if (localStorage.getItem('symphony.view') === 'score') location.replace('/score'); } catch {}</script>
<style>
  /* Trello-style board: a coloured board background, grey lists side by side, white cards with labels and badges. */
  :root {
    --board: linear-gradient(135deg, #0c66e4 0%, #5e4db2 100%);
    --nav: rgba(0, 0, 0, .28); --nav-fg: #fff; --chip: rgba(255, 255, 255, .2); --chip-hover: rgba(255, 255, 255, .32);
    --list: #f1f2f4; --list-fg: #44546f; --card: #fff; --card-hover: #f7f8f9; --fg: #172b4d; --muted: #626f86;
    --shadow: 0 1px 1px #091e4240, 0 0 1px #091e424f; --line: #091e4224; --focus: #388bff;
    --l-green: #4bce97; --l-yellow: #f5cd47; --l-orange: #fea362; --l-red: #f87168; --l-purple: #9f8fef; --l-blue: #579dff; --l-sky: #6cc3e0; --l-lime: #94c748; --l-fg: #172b4d;
    --overlay: rgba(9, 30, 66, .6); --modal: #f1f2f4; --warn-bg: #ffecb3;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {
    --board: linear-gradient(135deg, #09326c 0%, #352c63 100%);
    --nav: rgba(0, 0, 0, .4); --list: #101204; --list-fg: #9fadbc; --card: #22272b; --card-hover: #2c333a; --fg: #b6c2cf; --muted: #8c9bab;
    --shadow: 0 1px 1px #03040480, 0 0 1px #03040480; --line: #a6c5e229;
    --l-green: #216e4e; --l-yellow: #7f5f01; --l-orange: #a54800; --l-red: #ae2e24; --l-purple: #5e4db2; --l-blue: #0055cc; --l-sky: #206a83; --l-lime: #4c6b1f; --l-fg: #dee4ea;
    --overlay: rgba(0, 0, 0, .6); --modal: #323940; --warn-bg: #533f04; color-scheme: dark;
  } }
  :root[data-theme="dark"] {
    --board: linear-gradient(135deg, #09326c 0%, #352c63 100%);
    --nav: rgba(0, 0, 0, .4); --list: #101204; --list-fg: #9fadbc; --card: #22272b; --card-hover: #2c333a; --fg: #b6c2cf; --muted: #8c9bab;
    --shadow: 0 1px 1px #03040480, 0 0 1px #03040480; --line: #a6c5e229;
    --l-green: #216e4e; --l-yellow: #7f5f01; --l-orange: #a54800; --l-red: #ae2e24; --l-purple: #5e4db2; --l-blue: #0055cc; --l-sky: #206a83; --l-lime: #4c6b1f; --l-fg: #dee4ea;
    --overlay: rgba(0, 0, 0, .6); --modal: #323940; --warn-bg: #533f04; color-scheme: dark;
  }
  * { box-sizing: border-box }
  html, body { height: 100% }
  body { margin: 0; font: 14px/20px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Ubuntu, "Helvetica Neue", sans-serif; color: var(--fg); background: #0c66e4; background: var(--board); display: flex; flex-direction: column; }
  button { font: inherit; cursor: pointer; border: 0 }
  button:focus-visible, a:focus-visible, .card:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px }
  svg { width: 16px; height: 16px; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round }

  .nav { display: flex; align-items: center; gap: 8px; padding: 8px max(12px, env(safe-area-inset-right, 0px)) 8px max(12px, env(safe-area-inset-left, 0px)); background: var(--nav); color: var(--nav-fg); }
  .logo { display: flex; align-items: center; gap: 6px; font-weight: 700; font-size: 17px; letter-spacing: -.2px; margin-right: 8px }
  .logo i { width: 22px; height: 22px; border-radius: 4px; background: #fff; display: grid; grid-template-columns: 1fr 1fr; gap: 2px; padding: 4px }
  .logo i::before, .logo i::after { content: ""; background: #0c66e4; border-radius: 1px }
  .logo i::after { height: 60% }
  .grow { flex: 1 }
  .chip { display: inline-flex; align-items: center; gap: 6px; height: 32px; padding: 0 10px; border-radius: 4px; background: var(--chip); color: var(--nav-fg); white-space: nowrap; font-size: 14px; text-decoration: none }
  button.chip:hover { background: var(--chip-hover) }
  .chip.primary { background: #fff; color: #172b4d; font-weight: 500 }
  .chip.primary:hover { background: #e9f2ff }

  .bar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 10px max(12px, env(safe-area-inset-right, 0px)) 10px max(12px, env(safe-area-inset-left, 0px)); color: #fff; background: rgba(0, 0, 0, .12) }
  .bar h1 { font-size: 18px; margin: 0 8px 0 0; font-weight: 700 }
  .stat { display: inline-flex; align-items: baseline; gap: 6px; height: 32px; padding: 6px 10px; border-radius: 4px; background: var(--chip); font-size: 13px; white-space: nowrap }
  .stat b { font-variant-numeric: tabular-nums; font-size: 14px }
  .meter { display: inline-block; width: 46px; height: 6px; border-radius: 3px; background: rgba(255, 255, 255, .3); overflow: hidden; align-self: center }
  .meter > span { display: block; height: 100%; background: #fff }
  .states { font-size: 12px; opacity: .85 }
  .toast { position: fixed; left: 50%; bottom: calc(24px + env(safe-area-inset-bottom, 0px)); transform: translate(-50%, 20px); opacity: 0; transition: .3s; background: #172b4d; color: #fff; padding: 10px 16px; border-radius: 8px; font-weight: 500; box-shadow: 0 8px 24px rgba(0, 0, 0, .3); pointer-events: none; z-index: 10 }
  .toast.show { transform: translate(-50%, 0); opacity: 1 }
  @media (prefers-reduced-motion: reduce) { .toast { transition: none } }
  .banner { margin: 0 12px; padding: 8px 12px; border-radius: 8px; background: var(--warn-bg); color: var(--fg); font-size: 13px }
  .banner:empty { display: none }

  .board { flex: 1; min-height: 0; display: flex; align-items: flex-start; gap: 12px; overflow-x: auto; padding: 12px max(12px, env(safe-area-inset-right, 0px)) 16px max(12px, env(safe-area-inset-left, 0px)) }
  .list { flex: none; width: 272px; max-height: 100%; display: flex; flex-direction: column; background: var(--list); color: var(--list-fg); border-radius: 12px; box-shadow: var(--shadow) }
  .list header { display: flex; align-items: center; gap: 8px; padding: 10px 8px 4px 12px }
  .list h2 { margin: 0; font-size: 14px; font-weight: 600; color: var(--fg); flex: 1 }
  .list .count { font-size: 12px; min-width: 20px; height: 20px; padding: 0 6px; border-radius: 10px; background: var(--line); text-align: center; line-height: 20px; font-weight: 600 }
  .list .hint { padding: 0 12px 6px; font-size: 12px; color: var(--muted) }
  .cards { overflow-y: auto; padding: 2px 8px 2px; display: flex; flex-direction: column; gap: 8px; min-height: 8px }
  .list footer { display: flex; align-items: center; gap: 8px; margin: 6px 8px 8px; padding: 6px 8px; border-radius: 8px; color: var(--muted); font-size: 13px }
  .list footer:hover { background: var(--line) }

  .card { background: var(--card); border-radius: 8px; box-shadow: var(--shadow); padding: 8px 12px 6px; color: var(--fg); cursor: pointer; position: relative; overflow-wrap: anywhere; outline: 2px solid transparent }
  .card:hover { background: var(--card-hover); outline-color: var(--focus) }
  .labels { display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 4px }
  .label { display: inline-block; min-width: 56px; max-width: 100%; height: 16px; padding: 0 8px; border-radius: 4px; font-size: 12px; font-weight: 600; line-height: 16px; color: var(--l-fg); white-space: nowrap; overflow: hidden; text-overflow: ellipsis }
  .title { margin: 0 0 4px; }
  .badges { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 10px; color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums }
  .badge { display: inline-flex; align-items: center; gap: 4px; min-height: 24px }
  .badge svg { width: 14px; height: 14px }
  .badge.live { color: #1f845a } .badge.due { color: #c9372c }
  .member { margin-left: auto; width: 24px; height: 24px; border-radius: 50%; display: grid; place-items: center; font-size: 10px; font-weight: 700; color: #fff; background: linear-gradient(135deg, #1f845a, #0c66e4) }
  .ident { font-size: 12px; color: var(--muted); font-weight: 500 }
  .ident a { color: inherit }
  .pulse { width: 8px; height: 8px; border-radius: 50%; background: #22a06b; box-shadow: 0 0 0 0 #22a06b88; animation: pulse 1.6s infinite }
  @keyframes pulse { 70% { box-shadow: 0 0 0 6px #22a06b00 } 100% { box-shadow: 0 0 0 0 #22a06b00 } }
  @media (prefers-reduced-motion: reduce) { .pulse { animation: none } }
  .empty { padding: 4px 4px 2px; font-size: 12px; color: var(--muted) }

  dialog { border: 0; padding: 0; border-radius: 12px; background: var(--modal); color: var(--fg); width: min(768px, calc(100% - 24px)); max-height: calc(100% - 48px); box-shadow: 0 8px 12px #091e4226, 0 0 1px #091e424f }
  dialog::backdrop { background: var(--overlay) }
  .cb { display: grid; grid-template-columns: minmax(0, 1fr) 168px; gap: 16px; padding: 20px }
  .cb-head { grid-column: 1 / -1; display: flex; gap: 12px; align-items: flex-start }
  .cb-head h3 { margin: 0; font-size: 20px; line-height: 24px; flex: 1 }
  .cb-head .sub { font-size: 14px; color: var(--muted); font-weight: 400; margin-top: 4px }
  .cb-head .sub u { text-decoration: underline }
  .x { width: 32px; height: 32px; border-radius: 50%; background: transparent; color: var(--fg); display: grid; place-items: center }
  .x:hover { background: var(--line) }
  .cb h4 { margin: 16px 0 8px; font-size: 14px; display: flex; gap: 8px; align-items: center }
  .cb h4:first-child { margin-top: 0 }
  .desc { background: var(--card); border-radius: 8px; padding: 8px 12px; white-space: pre-wrap; overflow-wrap: anywhere }
  .act { display: flex; gap: 8px; margin-bottom: 10px }
  .act .member { margin: 0; flex: none; width: 32px; height: 32px; font-size: 11px }
  .act div { min-width: 0; overflow-wrap: anywhere }
  .act small { color: var(--muted); display: block }
  .act code { font-size: 12px; background: var(--line); padding: 0 4px; border-radius: 3px }
  .side { display: flex; flex-direction: column; gap: 8px }
  .side h5 { margin: 0; font-size: 12px; color: var(--muted); font-weight: 600 }
  .btn { display: flex; align-items: center; gap: 8px; height: 32px; padding: 0 12px; border-radius: 4px; background: var(--line); color: var(--fg); text-decoration: none; font-weight: 500; font-size: 14px }
  .btn:hover { filter: brightness(1.08) }
  .kv { font-size: 12px; color: var(--muted); overflow-wrap: anywhere } .kv b { display: block; color: var(--fg); font-weight: 500; font-variant-numeric: tabular-nums }
  @media (max-width: 640px) { .cb { grid-template-columns: 1fr } .nav .hide-sm { display: none } }
</style>
</head>
<body>
<nav class="nav">
  <div class="logo" id="logo"><i></i>Symphony</div>
  <span class="chip hide-sm"><svg viewBox="0 0 24 24"><rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>Read-only</span>
  <div class="grow"></div>
  <span class="chip hide-sm" id="updated">Loading…</span>
  <button class="chip primary" id="refresh" type="button"><svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>Poll now</button>
</nav>
<div class="bar">
  <h1>Agent queue</h1>
  <span class="stat">Running <b id="t-running">–</b></span>
  <span class="stat">Retrying <b id="t-retrying">–</b></span>
  <span class="stat">Tokens <b id="t-tokens">–</b></span>
  <span class="stat">Runtime <b id="t-runtime">–</b></span>
  <span id="rate" style="display:contents"></span>
  <span class="states" id="states"></span>
</div>
<div class="banner" id="error"></div>
<main class="board" id="board" aria-label="Board"></main>

<div class="toast" id="toast" role="status"></div>
<dialog id="card-back" aria-labelledby="cb-title"><div class="cb" id="cb"></div></dialog>

<script>
async function fetchState() {
  const res = await fetch('/api/v1/state', { cache: 'no-store' });
  const s = await res.json();
  if (!res.ok) throw new Error(s.error?.message || res.statusText);
  return s;
}
async function fetchIssue(id) {
  try {
    const res = await fetch(`/api/v1/${encodeURIComponent(id)}`, { cache: 'no-store' });
    return res.ok ? await res.json() : null;
  } catch { return null; }
}
</script>
<script>
const $ = id => document.getElementById(id);
const fmt = n => new Intl.NumberFormat().format(n ?? 0);
const short = n => n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(n ?? 0);
const time = s => s ? new Date(s).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '–';
const dur = s => { s = Math.round(s || 0); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60); return h ? `${h}h ${m}m` : `${m}m ${s % 60}s`; };
const since = iso => dur((Date.now() - new Date(iso)) / 1000);
const until = iso => { const s = (new Date(iso) - Date.now()) / 1000; return s <= 0 ? 'now' : 'in ' + dur(s); };
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
const ICON = {
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>',
  turns: '<path d="M4 12a8 8 0 0 1 14-5.3M20 12a8 8 0 0 1-14 5.3"/><path d="M18 3v4h-4M6 21v-4h4"/>',
  flag: '<path d="M5 21V4h11l-2 4 2 4H5"/>',
  retry: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/>',
  list: '<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>',
  lock: '<rect x="3" y="11" width="18" height="10" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
  text: '<path d="M4 6h16M4 12h16M4 18h10"/>',
  ext: '<path d="M14 4h6v6M20 4l-9 9"/><path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
};
const icon = n => `<svg viewBox="0 0 24 24" aria-hidden="true">${ICON[n]}</svg>`;
// Trello label colour per Symphony status.
const LABEL = {
  running: ['l-green', 'Running'], stopping: ['l-orange', 'Stopping'], ready: ['l-lime', 'Ready'], waiting: ['l-yellow', 'Waiting for a slot'],
  claimed: ['l-blue', 'Claimed'], retrying: ['l-orange', 'Retrying'], blocked: ['l-red', 'Blocked'], held: ['l-purple', 'Held'],
  missing_labels: ['l-sky', 'Missing label'], invalid: ['l-red', 'Invalid'], handed_off: ['l-blue', 'Handed off'],
};
function label(status) {
  const [c, t] = LABEL[status] ?? ['l-blue', status];
  const s = el('span', 'label', t); s.style.background = `var(--${c})`; return s;
}
function badge(name, text, cls) { const b = el('span', 'badge' + (cls ? ' ' + cls : '')); b.innerHTML = icon(name); b.append(text); return b; }

let state = null;
function card(item) {
  const c = el('article', 'card'); c.tabIndex = 0;
  const labels = el('div', 'labels'); labels.append(label(item.status)); if (item.priority != null) labels.append(Object.assign(label('x'), { textContent: `P${item.priority}`, style: 'background:var(--line);min-width:0' }));
  c.append(labels);
  c.append(el('div', 'ident', item.issue_identifier), el('p', 'title', item.title ?? '(no title)'));
  const b = el('div', 'badges'); item.badges.forEach(x => b.append(x));
  if (item.agent) { const m = el('span', 'member', 'AI'); m.title = 'Claude Code is working on this'; b.append(m); }
  c.append(b);
  const open = () => openCard(item);
  c.onclick = e => { if (!e.target.closest('a')) open(); };
  c.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } };
  return c;
}
function list(name, hint, items, empty) {
  const l = el('section', 'list'), h = el('header');
  h.append(el('h2', null, name), el('span', 'count', String(items.length)));
  const cards = el('div', 'cards');
  if (items.length) items.forEach(i => cards.append(card({ ...i, list: name }))); else cards.append(el('div', 'empty', empty));
  const f = el('footer'); f.innerHTML = icon('lock'); f.append('Change cards in the tracker');
  l.append(h, el('div', 'hint', hint), cards, f);
  return l;
}

async function load() {
  try {
    const s = state = await fetchState();
    $('error').textContent = s.workflow_error ? `Workflow error, dispatch paused: ${s.workflow_error}` : s.last_poll?.error ? `The last poll failed: ${s.last_poll.error}` : '';
    $('updated').textContent = `Polled ${time(s.last_poll?.at)}`;
    $('states').textContent = `Active states: ${(s.last_poll?.active_states ?? []).join(', ') || '–'}`;
    $('t-running').textContent = s.counts.running; $('t-retrying').textContent = s.counts.retrying;
    $('t-tokens').textContent = short(s.codex_totals.total_tokens); $('t-runtime').textContent = dur(s.codex_totals.seconds_running);
    renderRate(s.rate_limits);

    const queue = s.queue ?? [], byId = new Map(queue.map(c => [c.issue_id, c]));
    const q = c => ({ ...c, badges: [badge('flag', c.state)], why: c.reason });
    const run = s.running.map(r => ({ ...r, ...{ title: byId.get(r.issue_id)?.title, priority: byId.get(r.issue_id)?.priority }, status: r.stopping ? 'stopping' : 'running', agent: true,
      why: `${r.last_event ?? 'starting'}${r.last_message ? ': ' + r.last_message : ''}${r.stopping ? '\n' + r.stopping : ''}`,
      badges: [Object.assign(el('span', 'badge live'), { innerHTML: '<span class="pulse"></span>' }), badge('clock', since(r.started_at)), badge('turns', `${r.turn_count}`), badge('bolt', short(r.tokens.total_tokens))] }));
    const retry = s.retrying.map(r => ({ ...r, title: byId.get(r.issue_id)?.title, priority: byId.get(r.issue_id)?.priority, status: 'retrying',
      why: r.error ?? 'Continuation after a clean exit', badges: [badge('retry', `Attempt ${r.attempt}`), badge('clock', until(r.due_at), 'due')] }));
    const done = (s.handoffs ?? []).map(h => ({ ...h, status: 'handed_off', why: h.reason, badges: [badge('flag', h.state ?? 'Out of scope'), badge('clock', time(h.at))] }));

    $('board').replaceChildren(
      list('Not eligible', 'Blocked, held or missing a label', queue.filter(c => ['blocked', 'held', 'missing_labels', 'invalid'].includes(c.status)).map(q), 'Nothing held back'),
      list('Up next', 'Starts in priority order as slots free up', queue.filter(c => ['ready', 'waiting', 'claimed'].includes(c.status)).map(q), 'Nothing waiting'),
      list('Running', 'An agent session is working', run, 'No active sessions'),
      list('Retrying', 'Scheduled to run again', retry, 'Nothing scheduled'),
      list('Handed off', 'Recently released by Symphony', done, 'Nothing yet since start'));
  } catch (e) { $('error').textContent = `Can't load the board: ${e.message}`; }
}
function renderRate(r) {
  const out = $('rate'); out.replaceChildren();
  if (!r) return;
  for (const [k, v] of Object.entries(r)) {
    if (v && typeof v.used_percent === 'number') {
      const s = el('span', 'stat'); s.title = v.resets_at ? `Resets ${until(v.resets_at)}` : '';
      const m = el('span', 'meter'), f = el('span'); f.style.width = Math.min(100, v.used_percent) + '%'; m.append(f);
      s.append(k.replace('_', ' '), m, Object.assign(el('b'), { textContent: v.used_percent + '%' })); out.append(s);
    }
  }
}

async function openCard(item) {
  const d = $('card-back'), cb = $('cb');
  cb.innerHTML = `
    <div class="cb-head"><div style="flex:1;min-width:0"><h3 id="cb-title"></h3><div class="sub"><span></span> · in list <u></u></div></div>
      <button class="x" type="button" aria-label="Close">${icon('x')}</button></div>
    <div style="min-width:0">
      <h4>Labels</h4><div class="labels" id="cb-labels"></div>
      <h4>${icon('text')}Why it is here</h4><div class="desc" id="cb-why"></div>
      <h4>${icon('list')}Activity</h4><div id="cb-act"><div class="empty">Loading…</div></div>
    </div>
    <aside class="side"><h5>Tracker</h5><div id="cb-kv"></div></aside>`;
  if (item.issue_url) { const a = el('a', 'btn'); a.href = item.issue_url; a.target = '_blank'; a.rel = 'noopener'; a.innerHTML = icon('ext'); a.append('Open in tracker'); cb.querySelector('#cb-kv').before(a); }
  cb.querySelector('.sub span').textContent = item.issue_identifier;
  cb.querySelector('h3').textContent = item.title ?? item.issue_identifier;
  cb.querySelector('u').textContent = item.list;
  cb.querySelector('#cb-labels').append(label(item.status));
  cb.querySelector('#cb-why').textContent = item.why ?? '';
  cb.querySelector('.x').onclick = () => d.close();
  d.showModal();

  const detail = await fetchIssue(item.issue_identifier);
  const act = cb.querySelector('#cb-act'), kv = cb.querySelector('#cb-kv');
  const evs = (detail?.recent_events ?? []).slice().reverse();
  act.replaceChildren(...(evs.length ? evs.map(e => {
    const row = el('div', 'act'), body = el('div');
    body.append(Object.assign(el('b'), { textContent: 'Claude ' }), Object.assign(el('code'), { textContent: e.event }));
    if (e.message) body.append(' ' + e.message);
    body.append(el('small', null, new Date(e.at).toLocaleString()));
    row.append(el('span', 'member', 'AI'), body); return row;
  }) : [el('div', 'empty', 'No agent activity for this card yet.')]));
  const add = (k, v) => { if (v == null || v === '') return; const p = el('div', 'kv', k); p.append(Object.assign(el('b'), { textContent: v })); kv.append(p); };
  add('State', item.state);
  if (detail) {
    add('Workspace', detail.workspace?.path); add('Restarts', detail.attempts?.restart_count);
    if (detail.running) add('Tokens (in / out)', `${fmt(detail.running.tokens.input_tokens)} / ${fmt(detail.running.tokens.output_tokens)}`);
    add('Last error', detail.last_error);
  }
}
$('card-back').addEventListener('click', e => { if (e.target === e.currentTarget) e.currentTarget.close(); });
$('refresh').onclick = async () => { try { await fetch('/api/v1/refresh', { method: 'POST' }); } catch {} setTimeout(load, 500); };
// Easter egg: the Konami code, or five quick clicks on the logo, hands the baton to the score view (/score).
const KONAMI = ['arrowup', 'arrowup', 'arrowdown', 'arrowdown', 'arrowleft', 'arrowright', 'arrowleft', 'arrowright', 'b', 'a'];
let konami = 0, clicks = [];
function toScore() {
  try { localStorage.setItem('symphony.view', 'score'); } catch {}
  $('toast').textContent = 'Maestro, the score is yours.'; $('toast').classList.add('show');
  setTimeout(() => { location.href = '/score'; }, 900);
}
document.addEventListener('keydown', e => {
  const key = e.key.toLowerCase();
  konami = key === KONAMI[konami] ? konami + 1 : key === KONAMI[0] ? 1 : 0;
  if (konami === KONAMI.length) { konami = 0; toScore(); }
});
$('logo').onclick = () => { const now = Date.now(); clicks = clicks.filter(t => now - t < 2000).concat(now); if (clicks.length >= 5) { clicks = []; toScore(); } };
load(); setInterval(load, 2000);
</script>
</body>
</html>
""";

    /// <summary>
    /// The easter-egg view at <c>/score</c>: each running agent drawn as a stave. The board switches to it on the
    /// Konami code or five quick clicks on its logo, and remembers the choice in localStorage until the Board button.
    /// </summary>
    public const string ScoreHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Symphony · Score</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Bodoni+Moda:ital,opsz,wght@0,6..96,400..800;1,6..96,400..800&family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500&display=swap">
<style>
  /* A conductor's score: each running agent is a stave, each turn a note, token throughput a dynamic marking.
     The queue reads as the concert programme; hand-offs close with a double barline. */
  :root {
    --paper: #eef1f5; --sheet: #fbfcfd; --ink: #1a2233; --ink-2: #4c5872; --staff: #a3aec0; --rule: #d5dbe5;
    --brass: #94650f; --brass-soft: #f3e6c8; --rest: #8a3b3b; --ok: #2d6a4f;
    --display: "Bodoni Moda", "Didot", "Bodoni 72", Georgia, serif;
    --body: "IBM Plex Sans", system-ui, -apple-system, "Segoe UI", sans-serif;
    --mono: "IBM Plex Mono", ui-monospace, Menlo, Consolas, monospace;
    color-scheme: light;
  }
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {
    --paper: #0c111d; --sheet: #121a2a; --ink: #e6eaf2; --ink-2: #9aa6bd; --staff: #46526b; --rule: #222c40;
    --brass: #e2b65a; --brass-soft: #3a2f17; --rest: #e08a8a; --ok: #7cc6a1; color-scheme: dark;
  } }
  :root[data-theme="dark"] {
    --paper: #0c111d; --sheet: #121a2a; --ink: #e6eaf2; --ink-2: #9aa6bd; --staff: #46526b; --rule: #222c40;
    --brass: #e2b65a; --brass-soft: #3a2f17; --rest: #e08a8a; --ok: #7cc6a1; color-scheme: dark;
  }
  * { box-sizing: border-box }
  body { margin: 0; background: var(--paper); color: var(--ink); font: 15px/1.5 var(--body) }
  .wrap { max-width: 1240px; margin: 0 auto; padding: 28px max(16px, env(safe-area-inset-right, 0px)) 56px max(16px, env(safe-area-inset-left, 0px)) }
  a { color: inherit; text-decoration-color: var(--staff); text-underline-offset: 3px }
  a:hover { text-decoration-color: var(--brass) }
  :focus-visible { outline: 2px solid var(--brass); outline-offset: 2px }
  .it { font-family: var(--display); font-style: italic }
  .mono { font-family: var(--mono); font-size: 12px; letter-spacing: .02em }
  .muted { color: var(--ink-2) }

  header { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px 24px; flex-wrap: wrap; padding-bottom: 18px; border-bottom: 1px solid var(--rule) }
  h1 { font: 700 clamp(40px, 7vw, 72px)/0.9 var(--display); letter-spacing: -.02em; margin: 0 }
  h1 em { font-weight: 400; color: var(--brass) }
  .sub { margin-top: 10px; color: var(--ink-2); font-size: 14px; max-width: 60ch }
  .podium { display: flex; gap: 10px; align-items: center; flex-wrap: wrap }
  button { font: 500 14px var(--body); color: var(--ink); background: var(--sheet); border: 1px solid var(--staff); border-radius: 999px; padding: 8px 16px; cursor: pointer; display: inline-flex; gap: 8px; align-items: center }
  button:hover { border-color: var(--brass); color: var(--brass) }
  .baton { width: 18px; height: 2px; background: currentColor; transform: rotate(-30deg); border-radius: 2px; position: relative }
  .baton::after { content: ""; position: absolute; right: -2px; top: -2px; width: 6px; height: 6px; border-radius: 50%; background: currentColor }
  .notice { margin-top: 14px; padding: 10px 14px; border-left: 3px solid var(--brass); background: var(--brass-soft); font-size: 14px }
  .notice:empty { display: none }

  .facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 0; margin: 22px 0 8px; border-top: 1px solid var(--rule); border-bottom: 1px solid var(--rule) }
  .fact { padding: 12px 16px 14px 0 }
  .fact + .fact { padding-left: 16px; border-left: 1px solid var(--rule) }
  .fact dt { font-size: 11px; text-transform: uppercase; letter-spacing: .12em; color: var(--ink-2) }
  .fact dd { margin: 2px 0 0; font: 600 26px/1.2 var(--display); font-variant-numeric: lining-nums tabular-nums }
  .fact small { display: block; font-size: 12px; color: var(--ink-2) }

  .layout { display: grid; grid-template-columns: minmax(0, 1fr) 360px; gap: 40px; margin-top: 28px }
  @media (max-width: 960px) { .layout { grid-template-columns: minmax(0, 1fr) } }
  h2 { font: 400 italic 28px/1.1 var(--display); margin: 0 0 4px; display: flex; align-items: baseline; gap: 12px }
  h2 .mono { font-style: normal; color: var(--ink-2) }
  .lede { color: var(--ink-2); font-size: 13px; margin: 0 0 16px }

  .score { background: var(--sheet); border: 1px solid var(--rule); border-radius: 4px; padding: 8px 0; box-shadow: 0 1px 0 var(--rule), 0 12px 32px -24px rgba(16, 24, 40, .35) }
  .part { display: grid; grid-template-columns: 190px minmax(0, 1fr); gap: 0 12px; padding: 14px 20px 14px 18px; cursor: pointer; border-bottom: 1px dashed var(--rule); }
  .part:last-child { border-bottom: 0 }
  .part:hover { background: color-mix(in srgb, var(--brass-soft) 45%, transparent); color: var(--ink) }
  .part .name { min-width: 0; padding-top: 6px }
  .part .name b { display: block; font: 600 15px/1.25 var(--body) }
  .part .name .it { color: var(--brass); font-size: 15px }
  .part .lyric { grid-column: 2; font: italic 400 15px/1.4 var(--display); color: var(--ink-2); overflow-wrap: anywhere; padding-left: 34px }
  .part .lyric .mono { font-style: normal }
  @media (max-width: 640px) { .part { grid-template-columns: minmax(0, 1fr) } .part .lyric { grid-column: 1; padding-left: 0 } .part .name { padding-top: 0 } }
  .stave { display: block; width: 100%; height: 96px; overflow: visible }
  .stave line, .stave path { stroke: var(--staff); stroke-width: 1 }
  .stave .bar { stroke: var(--ink-2) }
  .stave .head { fill: var(--ink) }
  .stave .stem { stroke: var(--ink); stroke-width: 1.4 }
  .stave .now { fill: var(--brass) }
  .stave .now-stem { stroke: var(--brass); stroke-width: 1.6 }
  .stave .halo { fill: none; stroke: var(--brass); stroke-width: 1.5; transform-box: fill-box; transform-origin: center; animation: halo 1.8s ease-out infinite }
  .stave .dyn { font: 700 italic 22px var(--display); fill: var(--ink) }
  .stave .mark { font: italic 14px var(--display); fill: var(--ink-2) }
  .stave .dash line { stroke-dasharray: 3 5 }
  .stave .rest { fill: var(--ink) }
  @keyframes halo { 0% { opacity: .9; transform: scale(1) } 100% { opacity: 0; transform: scale(2.4) } }
  @media (prefers-reduced-motion: reduce) { .stave .halo { animation: none; opacity: .5 } }

  .programme { display: flex; flex-direction: column; gap: 28px }
  .prog h3 { font: 600 11px var(--body); text-transform: uppercase; letter-spacing: .14em; color: var(--ink-2); margin: 0 0 8px; display: flex; gap: 8px; align-items: center }
  .prog h3::after { content: ""; flex: 1; border-bottom: 1px solid var(--rule) }
  .prog ol { list-style: none; margin: 0; padding: 0 }
  .prog li { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 2px 12px; padding: 10px 0; border-bottom: 1px dotted var(--rule) }
  .prog li:last-child { border-bottom: 0 }
  .prog .t { font-weight: 500; line-height: 1.35 }
  .prog .dir { font: italic 17px/1.1 var(--display); color: var(--brass); text-align: right; white-space: nowrap }
  .prog .dir.stop { color: var(--rest) }
  .prog .why { grid-column: 1 / -1; font-size: 13px; color: var(--ink-2) }
  .prog .empty { font: italic 16px var(--display); color: var(--ink-2); padding: 6px 0 }
  .fine { display: flex; align-items: center; gap: 10px; font: italic 600 20px var(--display); margin-top: 6px }
  .fine i { width: 8px; height: 24px; border-left: 1px solid var(--ink); border-right: 4px solid var(--ink) }

  dialog { border: 1px solid var(--rule); background: var(--sheet); color: var(--ink); border-radius: 6px; padding: 0; width: min(620px, calc(100% - 24px)); max-height: calc(100% - 48px) }
  dialog::backdrop { background: rgba(10, 14, 24, .55) }
  .dlg { padding: 24px }
  .dlg h3 { font: 600 26px/1.15 var(--display); margin: 4px 0 4px }
  .dlg .close { float: right; padding: 4px 12px }
  .marks { list-style: none; margin: 18px 0 0; padding: 0; display: flex; flex-direction: column; gap: 12px }
  .marks li { display: grid; grid-template-columns: 30px minmax(0, 1fr); gap: 12px }
  .marks .rm { width: 28px; height: 28px; border: 1.5px solid var(--ink); display: grid; place-items: center; font: 700 15px var(--display) }
  .marks p { margin: 0; overflow-wrap: anywhere }
  .kv { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin-top: 16px; font-size: 13px }
  .kv div { min-width: 0; overflow-wrap: anywhere } .kv span { display: block; color: var(--ink-2); font-size: 11px; text-transform: uppercase; letter-spacing: .1em }
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div>
      <h1>Symphony <em>in</em> <span id="key">C</span></h1>
      <div class="sub" id="sub">Tuning up…</div>
    </div>
    <div class="podium">
      <span class="mono muted" id="updated"></span>
      <button id="board" type="button" title="Back to the board">Board</button>
      <button id="refresh" type="button"><span class="baton" aria-hidden="true"></span>Poll now</button>
    </div>
  </header>
  <div class="notice" id="error"></div>

  <dl class="facts">
    <div class="fact"><dt>Playing</dt><dd id="t-running">–</dd><small>agent sessions</small></div>
    <div class="fact"><dt>Da capo</dt><dd id="t-retrying">–</dd><small>scheduled retries</small></div>
    <div class="fact"><dt>Tokens</dt><dd id="t-tokens">–</dd><small id="t-split">input · output</small></div>
    <div class="fact"><dt>Duration</dt><dd id="t-runtime">–</dd><small>agent time since start</small></div>
    <div class="fact"><dt>Rate limits</dt><dd id="t-rate">–</dd><small id="t-rate-sub">none reported</small></div>
  </dl>

  <div class="layout">
    <section>
      <h2>The score <span class="mono" id="parts"></span></h2>
      <p class="lede">One stave per running agent. Each note is a turn, the gold note is the turn in progress, and the dynamic marking shows token throughput, from <span class="it">pp</span> (under 2k tokens a minute) to <span class="it">ff</span> (over 40k). Select a stave for its rehearsal marks.</p>
      <div class="score" id="score"></div>
    </section>

    <aside class="programme">
      <div>
        <h2>Programme</h2>
        <p class="lede" id="poll"></p>
      </div>
      <div class="prog"><h3>In the wings</h3><ol id="next"></ol></div>
      <div class="prog"><h3>Not on tonight</h3><ol id="held"></ol></div>
      <div class="prog"><h3>Fine</h3><ol id="done"></ol><div class="fine"><i aria-hidden="true"></i>Handed off</div></div>
    </aside>
  </div>
</div>

<dialog id="dlg"><div class="dlg" id="dlg-body"></div></dialog>

<script>
async function fetchState() {
  const res = await fetch('/api/v1/state', { cache: 'no-store' });
  const s = await res.json();
  if (!res.ok) throw new Error(s.error?.message || res.statusText);
  return s;
}
async function fetchIssue(id) {
  try {
    const res = await fetch(`/api/v1/${encodeURIComponent(id)}`, { cache: 'no-store' });
    return res.ok ? await res.json() : null;
  } catch { return null; }
}
</script>
<script>
const $ = id => document.getElementById(id);
const NS = 'http://www.w3.org/2000/svg';
const fmt = n => new Intl.NumberFormat().format(n ?? 0);
const short = n => n >= 1e6 ? (n / 1e6).toFixed(2) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n ?? 0);
const time = s => s ? new Date(s).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '–';
const dur = s => { s = Math.round(s || 0); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60); return h ? `${h}h ${m}m` : `${m}m ${s % 60}s`; };
const until = iso => { const s = (new Date(iso) - Date.now()) / 1000; return s <= 0 ? 'now' : 'in ' + dur(s); };
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function svg(tag, attrs, parent) { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); parent?.append(e); return e; }
function link(r) { if (!r.issue_url) return el('span', 'mono', r.issue_identifier); const a = el('a', 'mono', r.issue_identifier); a.href = r.issue_url; a.target = '_blank'; a.rel = 'noopener'; a.onclick = e => e.stopPropagation(); return a; }

// Token throughput as a dynamic marking.
function dynamic(tokens, startedAt) {
  const min = Math.max((Date.now() - new Date(startedAt)) / 60000, .5), rate = tokens / min;
  const d = rate < 2e3 ? 'pp' : rate < 5e3 ? 'p' : rate < 1e4 ? 'mp' : rate < 2e4 ? 'mf' : rate < 4e4 ? 'f' : 'ff';
  return [d, rate];
}
// A stable "melody": the pitch of turn i of an issue never changes between refreshes.
function pitch(id, i) { let h = 2166136261; for (const c of id + ':' + i) h = Math.imul(h ^ c.charCodeAt(0), 16777619); return (h >>> 0) % 9; }

// Status as a performance direction plus plain words.
const DIR = {
  ready: ['attacca', 'starts on the next poll'], waiting: ['tacet', 'waiting for a free slot'], claimed: ['segue', 'a run is finishing'],
  blocked: ['fermata', 'blocked'], held: ['senza', 'held by the tracker'], missing_labels: ['senza', 'missing a label'], invalid: ['ad lib.', 'invalid record'],
};

function stave(width, opts) {
  const H = 96, top = 22, gap = 9, x0 = 34, x1 = width - 14;
  const s = svg('svg', { class: 'stave', viewBox: `0 0 ${width} ${H}`, role: 'img', 'aria-label': opts.label });
  const lines = svg('g', { class: opts.dashed ? 'dash' : '' }, s);
  for (let i = 0; i < 5; i++) svg('line', { x1: x0, x2: x1, y1: top + i * gap, y2: top + i * gap }, lines);
  svg('line', { class: 'bar', x1: x0, x2: x0, y1: top, y2: top + 4 * gap }, s);
  if (opts.dyn) { const t = svg('text', { class: 'dyn', x: 0, y: top + 4 * gap + 6 }, s); t.textContent = opts.dyn; }
  return { s, top, gap, x0, x1 };
}
function runningStave(r, width) {
  const [dyn, rate] = dynamic(r.tokens.total_tokens, r.started_at);
  const st = stave(width, { dyn, label: `${r.issue_identifier}: turn ${r.turn_count}, ${dyn}` });
  const { s, top, gap, x0, x1 } = st, space = 22, fit = Math.max(1, Math.floor((x1 - x0 - 40) / space));
  const n = Math.max(1, r.turn_count), first = Math.max(1, n - fit + 1);
  if (first > 1) { const m = svg('text', { class: 'mark', x: x0 + 6, y: top - 8 }, s); m.textContent = `from turn ${first}`; }
  for (let i = first; i <= n; i++) {
    const x = x0 + 22 + (i - first) * space, p = pitch(r.issue_id, i), y = top + 4 * gap - p * gap / 2, cur = i === n;
    if ((i - first) % 4 === 0 && i !== first) svg('line', { class: 'bar', x1: x - 11, x2: x - 11, y1: top, y2: top + 4 * gap }, s);
    for (let ly = top + 5 * gap; ly <= y; ly += gap) svg('line', { x1: x - 8, x2: x + 8, y1: ly, y2: ly }, s); // ledger lines
    for (let ly = top - gap; ly >= y; ly -= gap) svg('line', { x1: x - 8, x2: x + 8, y1: ly, y2: ly }, s);
    const up = p < 4, sx = up ? x + 5 : x - 5;
    svg('line', { class: cur ? 'now-stem' : 'stem', x1: sx, x2: sx, y1: y, y2: up ? y - 30 : y + 30 }, s);
    svg('ellipse', { class: cur ? 'now' : 'head', cx: x, cy: y, rx: 6, ry: 4.4, transform: `rotate(-20 ${x} ${y})` }, s);
    if (cur && !r.stopping) svg('ellipse', { class: 'halo', cx: x, cy: y, rx: 6, ry: 4.4 }, s);
    if (cur) {
      svg('line', { class: 'bar', x1: x + 16, x2: x + 16, y1: top - 6, y2: top + 4 * gap + 6, 'stroke-dasharray': '2 3' }, s);
      const m = svg('text', { class: 'mark', x: Math.min(x + 22, x1 - 120), y: top - 8 }, s);
      m.textContent = r.stopping ? 'rit. (stopping)' : `turn ${n} · ${short(Math.round(rate))}/min`;
    }
  }
  return s;
}
function restStave(width, label, mark, dashed) {
  const st = stave(width, { label, dashed }), { s, top, gap, x0, x1 } = st, cx = (x0 + x1) / 2;
  svg('rect', { class: 'rest', x: cx - 9, y: top + gap, width: 18, height: gap / 2 }, s); // whole rest hangs from the 4th line
  const m = svg('text', { class: 'mark', x: cx, y: top - 8, 'text-anchor': 'middle' }, s); m.textContent = mark;
  return s;
}

let last = null;
function part(nameNodes, staveEl, lyric, onOpen) {
  const p = el('div', 'part'); p.tabIndex = 0; p.setAttribute('role', 'button');
  p.onkeydown = e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } };
  const name = el('div', 'name'); name.append(...nameNodes);
  const lyr = el('div', 'lyric'); lyr.append(...lyric);
  p.append(name, staveEl, lyr); p.onclick = onOpen; return p;
}
function render() {
  const s = last; if (!s) return;
  const queue = s.queue ?? [], byId = new Map(queue.map(c => [c.issue_id, c]));
  const score = $('score'), w = Math.max(240, (score.clientWidth || 700) - (innerWidth <= 640 ? 36 : 240));
  const parts = [];
  for (const r of s.running) {
    const c = byId.get(r.issue_id);
    parts.push(part([link(r), el('b', null, c?.title ?? r.issue_identifier), el('span', 'it', `${r.state} · ${dur((Date.now() - new Date(r.started_at)) / 1000)}`)],
      runningStave(r, w), [el('span', 'mono', (r.last_event ?? 'starting') + ' '), (r.last_message || '…') + (r.stopping ? ` (${r.stopping})` : '')],
      () => openPart(r.issue_identifier, c?.title)));
  }
  for (const r of s.retrying) {
    const c = byId.get(r.issue_id);
    parts.push(part([link(r), el('b', null, c?.title ?? r.issue_identifier), el('span', 'it', `D.C. · attempt ${r.attempt}`)],
      restStave(w, `${r.issue_identifier} resting until retry`, `da capo ${until(r.due_at)}`, true), [r.error ?? 'Continuation after a clean exit'],
      () => openPart(r.issue_identifier, c?.title)));
  }
  if (!parts.length) {
    const p = el('div', 'part'); p.style.cursor = 'default';
    p.append(Object.assign(el('div', 'name'), { innerHTML: '<b>Full orchestra</b><span class="it">tacet</span>' }), restStave(w, 'No agents running', 'tacet'),
      el('div', 'lyric', 'No agent is playing. Tasks in the wings start as soon as a slot frees up.'));
    parts.push(p);
  }
  score.replaceChildren(...parts);
  $('parts').textContent = `${s.running.length + s.retrying.length} part${s.running.length + s.retrying.length === 1 ? '' : 's'}`;

  const item = c => {
    const li = el('li'), [d, plain] = DIR[c.status] ?? [c.status, ''], dir = el('span', 'dir' + (['blocked', 'held', 'missing_labels', 'invalid'].includes(c.status) ? ' stop' : ''), d);
    dir.title = plain;
    const t = el('div', 't'); t.append(link(c), ' ', c.title ?? '');
    li.append(t, dir, el('div', 'why', `${c.reason}${c.priority != null ? ` · priority ${c.priority}` : ''}`));
    return li;
  };
  const fill = (id, items, empty) => $(id).replaceChildren(...(items.length ? items : [el('li', 'empty', empty)]));
  fill('next', queue.filter(c => ['ready', 'waiting', 'claimed'].includes(c.status)).map(item), 'No one waiting');
  fill('held', queue.filter(c => ['blocked', 'held', 'missing_labels', 'invalid'].includes(c.status)).map(item), 'Everyone is eligible');
  fill('done', (s.handoffs ?? []).map(hh => {
    const li = el('li'), t = el('div', 't'); t.append(link(hh), ' ', hh.title ?? '(no longer visible)');
    li.append(t, el('span', 'dir', time(hh.at)), el('div', 'why', hh.reason)); return li;
  }), 'Nothing handed off yet');
}

async function load() {
  try {
    last = await fetchState();
    const lp = last.last_poll ?? {};
    $('error').textContent = last.workflow_error ? `Workflow error, dispatch paused: ${last.workflow_error}` : lp.error ? `The last poll failed: ${lp.error}` : '';
    $('sub').textContent = `${last.running.length ? 'Playing' : 'Waiting'} from the tracker states ${(lp.active_states ?? []).join(' and ') || '–'}. The score is read-only; change tasks in the tracker.`;
    $('key').textContent = last.running.length ? (last.retrying.length ? 'D minor' : 'C major') : 'rest';
    $('updated').textContent = `polled ${time(lp.at)}`;
    $('poll').textContent = `${(last.queue ?? []).length} tasks in active states at the last poll. Directions in gold show what happens next.`;
    $('t-running').textContent = last.counts.running; $('t-retrying').textContent = last.counts.retrying;
    const t = last.codex_totals; $('t-tokens').textContent = short(t.total_tokens); $('t-split').textContent = `${short(t.input_tokens)} in · ${short(t.output_tokens)} out`;
    $('t-runtime').textContent = dur(t.seconds_running);
    const rl = Object.entries(last.rate_limits ?? {}).filter(([, v]) => v && typeof v.used_percent === 'number').sort((a, b) => b[1].used_percent - a[1].used_percent);
    $('t-rate').textContent = rl.length ? `${rl[0][1].used_percent}%` : '–';
    $('t-rate-sub').textContent = rl.length ? rl.map(([k, v]) => `${k.replace('_', ' ')} ${v.used_percent}%`).join(' · ') : 'none reported';
    render();
  } catch (e) { $('error').textContent = `Can't reach Symphony: ${e.message}`; }
}

async function openPart(id, title) {
  const d = $('dlg'), b = $('dlg-body');
  b.replaceChildren(); const close = el('button', 'close', 'Close'); close.type = 'button'; close.onclick = () => d.close();
  b.append(close, el('span', 'mono muted', id), el('h3', null, title ?? id), el('p', 'it muted', 'Rehearsal marks, oldest first'));
  d.showModal();
  const x = await fetchIssue(id);
  if (!x) { b.append(el('p', null, 'This task is no longer in the runtime state.')); return; }
  const kv = el('div', 'kv'), add = (k, v) => { if (v == null) return; const c = el('div'); c.append(el('span', null, k), String(v)); kv.append(c); };
  add('Status', x.status); add('Restarts', x.attempts?.restart_count); add('Workspace', x.workspace?.path);
  if (x.running) add('Tokens in · out', `${fmt(x.running.tokens.input_tokens)} · ${fmt(x.running.tokens.output_tokens)}`);
  add('Last error', x.last_error);
  const marks = el('ol', 'marks');
  (x.recent_events ?? []).forEach((e, i) => {
    const li = el('li'), body = el('div');
    body.append(el('span', 'mono muted', `${new Date(e.at).toLocaleTimeString()} · ${e.event}`), el('p', null, e.message ?? ''));
    li.append(el('span', 'rm', String.fromCharCode(65 + (i % 26))), body); marks.append(li);
  });
  b.append(kv, marks.children.length ? marks : el('p', 'it muted', 'No marks yet.'));
}
$('dlg').addEventListener('click', e => { if (e.target === e.currentTarget) e.currentTarget.close(); });
$('refresh').onclick = async () => { try { await fetch('/api/v1/refresh', { method: 'POST' }); } catch {} setTimeout(load, 500); };
addEventListener('resize', () => render());
// Leave the score: the Board button, or the Konami code again.
function toBoard() { try { localStorage.removeItem('symphony.view'); } catch {} location.href = '/'; }
$('board').onclick = toBoard;
const KONAMI = ['arrowup', 'arrowup', 'arrowdown', 'arrowdown', 'arrowleft', 'arrowright', 'arrowleft', 'arrowright', 'b', 'a'];
let konami = 0;
document.addEventListener('keydown', e => {
  const key = e.key.toLowerCase();
  konami = key === KONAMI[konami] ? konami + 1 : key === KONAMI[0] ? 1 : 0;
  if (konami === KONAMI.length) toBoard();
});
load(); setInterval(load, 2000);
</script>
</body>
</html>
""";
}
