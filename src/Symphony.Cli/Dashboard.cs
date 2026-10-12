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
  :root { --bg:#f7f7f8; --fg:#1b1b1f; --muted:#6b6b76; --card:#fff; --line:#e3e3e8; --accent:#3b5bdb; --warn:#c2410c; --ok:#2f9e44; }
  @media (prefers-color-scheme: dark) { :root { --bg:#141417; --fg:#ececf1; --muted:#9a9aa5; --card:#1d1d22; --line:#2c2c33; --accent:#748ffc; --warn:#fb923c; --ok:#69db7c; } }
  * { box-sizing:border-box }
  body { margin:0; font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif; background:var(--bg); color:var(--fg); }
  main { max-width:1200px; margin:0 auto; padding:20px 16px 40px; }
  header { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  h1 { font-size:20px; margin:0 } h2 { font-size:15px; margin:24px 0 8px }
  .muted { color:var(--muted) } .warn { color:var(--warn) }
  .tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px; margin-top:16px }
  .tile { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px }
  .tile b { display:block; font-size:22px; font-variant-numeric:tabular-nums }
  .wrap { overflow-x:auto; background:var(--card); border:1px solid var(--line); border-radius:10px }
  table { border-collapse:collapse; width:100%; min-width:720px }
  th,td { text-align:left; padding:8px 10px; border-bottom:1px solid var(--line); vertical-align:top }
  th { font-weight:600; color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.03em }
  tr:last-child td { border-bottom:none }
  td.num { font-variant-numeric:tabular-nums; text-align:right }
  .msg { max-width:420px; white-space:pre-wrap; overflow-wrap:anywhere; color:var(--muted) }
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
  <h2>Running sessions</h2>
  <div class="wrap"><table><thead><tr><th>Issue</th><th>State</th><th>Turns</th><th>Last event</th><th>Tokens</th><th>Started</th></tr></thead><tbody id="running"></tbody></table></div>
  <h2>Retry queue</h2>
  <div class="wrap"><table><thead><tr><th>Issue</th><th>Attempt</th><th>Due</th><th>Error</th></tr></thead><tbody id="retrying"></tbody></table></div>
  <h2>Rate limits</h2>
  <pre class="tile muted" id="rate" style="overflow:auto;margin:0">none reported</pre>
</main>
<script>
const $ = id => document.getElementById(id);
const fmt = n => new Intl.NumberFormat().format(n ?? 0);
const time = s => s ? new Date(s).toLocaleTimeString() : '–';
const dur = s => { s = Math.round(s || 0); const h = Math.floor(s/3600), m = Math.floor(s%3600/60); return h ? `${h}h ${m}m` : `${m}m ${s%60}s`; };
function cell(row, text, cls) { const td = row.insertCell(); if (cls) td.className = cls; td.textContent = text ?? ''; return td; }
function issueCell(row, r) {
  const td = row.insertCell();
  if (r.issue_url) { const a = document.createElement('a'); a.href = r.issue_url; a.target = '_blank'; a.rel = 'noopener'; a.textContent = r.issue_identifier; td.append(a); }
  else td.textContent = r.issue_identifier;
}
function empty(body, cols, text) { const row = body.insertRow(); const td = cell(row, text, 'muted'); td.colSpan = cols; }
async function load() {
  try {
    const res = await fetch('/api/v1/state', { cache: 'no-store' });
    const s = await res.json();
    if (!res.ok) throw new Error(s.error?.message || res.statusText);
    $('error').textContent = s.workflow_error ? `Workflow error (dispatch paused): ${s.workflow_error}` : '';
    $('generated').textContent = `Updated ${time(s.generated_at)}`;
    $('t-running').textContent = s.counts.running; $('t-retrying').textContent = s.counts.retrying;
    $('t-tokens').textContent = fmt(s.codex_totals.total_tokens); $('t-runtime').textContent = dur(s.codex_totals.seconds_running);
    const rb = $('running'); rb.replaceChildren();
    if (!s.running.length) empty(rb, 6, 'No active sessions');
    for (const r of s.running) {
      const row = rb.insertRow(); issueCell(row, r);
      cell(row, r.stopping ? `${r.state} (stopping)` : r.state);
      cell(row, r.turn_count, 'num');
      const td = cell(row, '', 'msg'); td.textContent = `${r.last_event ?? '–'}${r.last_message ? ' · ' + r.last_message : ''}`;
      cell(row, fmt(r.tokens.total_tokens), 'num'); cell(row, time(r.started_at));
    }
    const qb = $('retrying'); qb.replaceChildren();
    if (!s.retrying.length) empty(qb, 4, 'Nothing queued');
    for (const r of s.retrying) { const row = qb.insertRow(); issueCell(row, r); cell(row, r.attempt, 'num'); cell(row, time(r.due_at)); cell(row, r.error ?? '', 'msg'); }
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
