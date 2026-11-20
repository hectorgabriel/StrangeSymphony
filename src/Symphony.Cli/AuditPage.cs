namespace Symphony.Cli;

/// <summary>The audit trail at <c>/audit</c>, from <c>/api/v1/audit</c>: who moved each task, and what the agents did.</summary>
internal static class AuditPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Symphony · Audit</title>
<style>
""" + LinePage.Style + """

  th, td, th:first-child, td:first-child { text-align: left }
  td:first-child { white-space: nowrap }
  .ok { color: var(--muted) }
  .banner.good { display: block; background: var(--panel); color: var(--muted); border: 1px solid var(--line) }
  form { display: flex; gap: 8px; align-items: center }
  input { font: inherit; color: var(--fg); background: var(--bg); border: 1px solid var(--line); border-radius: 6px; padding: 4px 8px; width: 140px }
  td.actor-outside { color: var(--warn); font-weight: 600 }
  tr.broken td { background: var(--warn-bg) }
</style>
</head>
<body>
<header>
  <h1>Symphony · Audit</h1>
  <span class="muted" id="count"></span>
  <div class="grow"></div>
  <form id="filter"><input id="issue" placeholder="Task, e.g. T12" aria-label="Filter by task"></form>
  <a href="/">Board</a>
  <a href="/line">Metrics</a>
  <a id="json" href="/api/v1/audit">JSON</a>
</header>
<main>
  <div class="banner" id="chain"></div>
  <section><h2>Trail</h2><p>Newest first. <b>Symphony</b> is the orchestrator, <b>agent</b> a tool call from an agent session, <b>outside</b> a change in the tracker that Symphony didn't make: a person or another integration. The tracker's own history says who.</p><div id="rows"></div></section>
</main>
<script>
const $ = id => document.getElementById(id);
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
const params = new URLSearchParams(location.search);
$('issue').value = params.get('issue') ?? '';
$('filter').addEventListener('submit', e => { e.preventDefault(); const v = $('issue').value.trim(); location.search = v ? '?issue=' + encodeURIComponent(v) : ''; });
const q = (s => s ? '?issue=' + encodeURIComponent(s) : '')(params.get('issue'));
$('json').href = '/api/v1/audit' + q;
const sha = c => c ? c.slice(0, 7) : '';
function what(r) {
  switch (r.kind) {
    case 'dispatched': return `Started an agent run in "${r.state}"` + (r.model ? ` with ${r.model}` : '') + (r.attempt ? `, attempt ${r.attempt}` : '') + (r.resumed ? ', resuming its session' : '');
    case 'run_ended': return `Run ended: ${r.outcome}` + (r.error ? ` (${r.error})` : '') + ` after ${r.turns ?? 0} turns`;
    case 'state_changed': return `Moved from "${r.from_state}" to "${r.state}"`;
    case 'released': return `Released: ${r.reason ?? ''}`;
    case 'closed': return `Closed: ${r.reason ?? ''}`;
    case 'tool_call':
      if (r.tool === 'update_state') return (r.ok ? `Moved from "${r.from_state}" to "${r.state}"` : `Tried to move to "${r.state}": refused (${r.category})`) + (r.commit ? ` at ${sha(r.commit)} on ${r.branch}` : '');
      return `${r.tool}` + (r.ok ? '' : `: failed (${r.category})`) + (r.url ? ` → ${r.url}` : '') + (r.args && Object.keys(r.args).length ? ` · ${Object.entries(r.args).map(([k, v]) => typeof v === 'object' ? `${k}: ${v.chars} chars` : `${k}: ${v}`).join(', ')}` : '');
    default: return r.kind;
  }
}
async function load() {
  try {
    const res = await fetch('/api/v1/audit' + q, { cache: 'no-store' });
    if (!res.ok) throw new Error(`audit ${res.status}`);
    const a = await res.json();
    const chain = $('chain');
    chain.className = 'banner show' + (a.chain_intact && !a.error ? ' good' : '');
    chain.textContent = a.error ? `The trail could not be read: ${a.error}`
      : a.chain_intact ? `Chain intact: all ${a.lines} lines check out against their hashes.`
      : `The trail was changed outside Symphony: line ${a.first_broken_line} no longer matches its hash or the line before it. Highlighted rows don't check out.`;
    $('count').textContent = `${a.entries.length} of ${a.lines} lines`;
    const rows = a.entries.slice().reverse();
    if (!rows.length) { $('rows').replaceChildren(el('div', 'empty', 'Nothing recorded yet.')); return; }
    const t = el('table'), head = el('tr');
    for (const h of ['Time', 'Task', 'Actor', 'What', 'Session']) head.append(el('th', h === 'What' ? 'wrap' : '', h));
    t.append(head);
    for (const r of rows) {
      const tr = el('tr', r.intact ? '' : 'broken');
      const task = el('td'), link = el('a', '', r.identifier); link.href = '?issue=' + encodeURIComponent(r.identifier); task.append(link);
      tr.append(el('td', '', new Date(r.at).toLocaleString()), task, el('td', 'actor-' + r.actor, r.actor), el('td', 'wrap', what(r)), el('td', 'ok', (r.session_id ?? '').slice(0, 8)));
      t.append(tr);
    }
    $('rows').replaceChildren(t);
  } catch (e) {
    $('chain').className = 'banner show';
    $('chain').textContent = `Could not load the audit trail: ${e.message}`;
  }
}
load();
setInterval(load, 15000);
</script>
</body>
</html>
""";
}
