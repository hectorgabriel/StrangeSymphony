namespace Symphony.Cli;

/// <summary>Client-side dashboard that only consumes <c>/api/v1/state</c> (SPEC 13.7.1).</summary>
internal static class Dashboard
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Symphony</title>
<style>
  :root { --bg:#f7f7f8; --fg:#1b1b1f; --muted:#6b6b76; --card:#fff; --col:#ebecf0; --line:#e3e3e8; --accent:#3b5bdb; --warn:#c2410c; --ok:#2f9e44; --shadow:0 1px 2px rgba(0,0,0,.12); }
  @media (prefers-color-scheme: dark) { :root { --bg:#141417; --fg:#ececf1; --muted:#9a9aa5; --card:#25252c; --col:#1d1d22; --line:#2c2c33; --accent:#748ffc; --warn:#fb923c; --ok:#69db7c; --shadow:0 1px 2px rgba(0,0,0,.5); } }
  * { box-sizing:border-box }
  body { margin:0; font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif; background:var(--bg); color:var(--fg); }
  main { max-width:1400px; margin:0 auto; padding:20px 16px 40px; }
  header { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  h1 { font-size:20px; margin:0 } h2 { font-size:15px; margin:24px 0 8px }
  .muted { color:var(--muted) } .warn { color:var(--warn) }
  .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px; margin-top:16px }
  .tile { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px }
  .tile b { display:block; font-size:22px; font-variant-numeric:tabular-nums }
  .board { display:grid; grid-template-columns:repeat(auto-fit,minmax(230px,1fr)); gap:12px; align-items:start; margin-top:8px }
  .col { background:var(--col); border-radius:12px; padding:10px; min-width:0 }
  .col h3 { margin:2px 4px 10px; font-size:13px; display:flex; justify-content:space-between; gap:8px }
  .col h3 span { color:var(--muted); font-weight:500 }
  .col .hint { margin:-6px 4px 10px; font-size:12px; color:var(--muted) }
  .card { background:var(--card); border-radius:8px; box-shadow:var(--shadow); padding:9px 10px; margin-bottom:8px; overflow-wrap:anywhere }
  .card:last-child { margin-bottom:0 }
  .card .top { display:flex; justify-content:space-between; align-items:center; gap:8px; font-size:12px }
  .card .title { margin:4px 0 6px; font-weight:500 }
  .card .why { font-size:12px; color:var(--muted); white-space:pre-wrap }
  .card .meta { margin-top:6px; font-size:12px; color:var(--muted); font-variant-numeric:tabular-nums }
  .empty { color:var(--muted); font-size:12px; padding:4px }
  .badge { display:inline-block; padding:0 8px; border-radius:999px; font-size:11px; border:1px solid var(--line); white-space:nowrap }
  .badge.running, .badge.ready { color:var(--ok); border-color:var(--ok) }
  .badge.blocked, .badge.held, .badge.missing_labels, .badge.invalid, .badge.stopping { color:var(--warn); border-color:var(--warn) }
  a { color:var(--accent) }
  button { font:inherit; padding:6px 12px; border-radius:8px; border:1px solid var(--line); background:var(--card); color:var(--fg); cursor:pointer }
  #error { margin-top:12px }
</style>
</head>
<body>
<main>
  <header>
    <div><h1>Symphony</h1><div class="muted" id="generated">loading…</div></div>
    <button id="refresh" type="button">Poll now</button>
  </header>
  <div id="error" class="warn"></div>
  <div class="tiles">
    <div class="tile"><span class="muted">Running</span><b id="t-running">–</b></div>
    <div class="tile"><span class="muted">Retrying</span><b id="t-retrying">–</b></div>
    <div class="tile"><span class="muted">Total tokens</span><b id="t-tokens">–</b></div>
    <div class="tile"><span class="muted">Agent runtime</span><b id="t-runtime">–</b></div>
  </div>
  <h2>Board</h2>
  <div class="muted" id="poll"></div>
  <div class="board" id="board"></div>
  <h2>Rate limits</h2>
  <pre class="tile muted" id="rate" style="overflow:auto;margin:0">none reported</pre>
</main>
<script>
const $ = id => document.getElementById(id);
const fmt = n => new Intl.NumberFormat().format(n ?? 0);
const time = s => s ? new Date(s).toLocaleTimeString() : '–';
const dur = s => { s = Math.round(s || 0); const h = Math.floor(s/3600), m = Math.floor(s%3600/60); return h ? `${h}h ${m}m` : `${m}m ${s%60}s`; };
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function link(r) {
  if (!r.issue_url) return el('b', null, r.issue_identifier);
  const a = el('a', null, r.issue_identifier); a.href = r.issue_url; a.target = '_blank'; a.rel = 'noopener'; return a;
}
// One card: identifier and badge, title, an explanation, and a meta line.
function card(r, badge, title, why, meta) {
  const c = el('div', 'card'), top = el('div', 'top');
  top.append(link(r));
  if (badge) top.append(el('span', `badge ${badge}`, badge.replace('_', ' ')));
  c.append(top);
  if (title) c.append(el('div', 'title', title));
  if (why) c.append(el('div', 'why', why));
  if (meta) c.append(el('div', 'meta', meta));
  return c;
}
function column(name, hint, cards, emptyText) {
  const col = el('section', 'col'), h = el('h3', null, name);
  h.append(el('span', null, String(cards.length)));
  col.append(h, el('div', 'hint', hint));
  if (cards.length) col.append(...cards); else col.append(el('div', 'empty', emptyText));
  return col;
}
async function load() {
  try {
    const res = await fetch('/api/v1/state', { cache: 'no-store' });
    const s = await res.json();
    if (!res.ok) throw new Error(s.error?.message || res.statusText);
    $('error').textContent = s.workflow_error ? `Workflow error (dispatch paused): ${s.workflow_error}` : '';
    $('generated').textContent = `Updated ${time(s.generated_at)}`;
    $('t-running').textContent = s.counts.running; $('t-retrying').textContent = s.counts.retrying;
    $('t-tokens').textContent = fmt(s.codex_totals.total_tokens); $('t-runtime').textContent = dur(s.codex_totals.seconds_running);

    const lp = s.last_poll ?? {}, queue = s.queue ?? [], handoffs = s.handoffs ?? [];
    $('poll').textContent = `Symphony considers tasks in ${(lp.active_states ?? []).join(', ') || 'the active states'}. Last poll ${time(lp.at)}.`
      + (lp.error ? ` The last poll failed: ${lp.error}` : '') + ' Change a task in the tracker; this board is read-only.';
    const titles = new Map(queue.map(c => [c.issue_id, c.title]));
    const queued = c => card(c, c.status, c.title, c.reason, c.state + (c.priority != null ? ` · priority ${c.priority}` : ''));

    const notEligible = queue.filter(c => ['blocked', 'held', 'missing_labels', 'invalid'].includes(c.status)).map(queued);
    const upNext = queue.filter(c => ['ready', 'waiting', 'claimed'].includes(c.status)).map(queued);
    const running = s.running.map(r => card(r, r.stopping ? 'stopping' : 'running', titles.get(r.issue_id),
      `${r.last_event ?? 'starting'}${r.last_message ? ' · ' + r.last_message : ''}${r.stopping ? '\n' + r.stopping : ''}`,
      `${r.state} · turn ${r.turn_count} · ${fmt(r.tokens.total_tokens)} tokens · since ${time(r.started_at)}`));
    const retrying = s.retrying.map(r => card(r, 'retrying', titles.get(r.issue_id), r.error ?? 'Continuation after a clean exit',
      `attempt ${r.attempt} · due ${time(r.due_at)}`));
    const handedOff = handoffs.map(h => card(h, null, h.title, h.reason, `${h.state ?? 'out of scope'} · ${time(h.at)}`));

    $('board').replaceChildren(
      column('Not eligible', 'Blocked, held or missing labels', notEligible, 'Nothing held back'),
      column('Up next', 'Starts in priority order as slots free up', upNext, 'Nothing waiting'),
      column('Running', 'An agent session is working', running, 'No active sessions'),
      column('Retrying', 'Scheduled to run again', retrying, 'Nothing scheduled'),
      column('Handed off', 'Recently released by Symphony', handedOff, 'Nothing yet since start'));
    $('rate').textContent = s.rate_limits ? JSON.stringify(s.rate_limits, null, 2) : 'none reported';
  } catch (e) { $('error').textContent = `Cannot load state: ${e.message}`; }
}
$('refresh').onclick = async () => { await fetch('/api/v1/refresh', { method: 'POST' }); setTimeout(load, 500); };
load(); setInterval(load, 2000);
</script>
</body>
</html>
""";
}
