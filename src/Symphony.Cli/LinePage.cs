namespace Symphony.Cli;

/// <summary>The metrics view at <c>/line</c>: run history from <c>/api/v1/metrics</c>, as numbers and tables.</summary>
internal static class LinePage
{
    /// <summary>Shared with the audit page.</summary>
    public const string Style = """
  :root { --bg: #f7f8f9; --panel: #fff; --fg: #172b4d; --muted: #626f86; --line: #091e4224; --accent: #0c66e4; --bar: #cce0ff; --warn: #ae2e24; --warn-bg: #ffeceb; color-scheme: light; }
  @media (prefers-color-scheme: dark) { :root { --bg: #161a1d; --panel: #22272b; --fg: #b6c2cf; --muted: #8c9bab; --line: #a6c5e229; --accent: #579dff; --bar: #09326c; --warn: #fd9891; --warn-bg: #42221f; color-scheme: dark; } }
  * { box-sizing: border-box }
  body { margin: 0; font: 14px/20px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Ubuntu, "Helvetica Neue", sans-serif; color: var(--fg); background: var(--bg) }
  header { display: flex; align-items: center; gap: 12px; padding: 12px 20px; border-bottom: 1px solid var(--line); background: var(--panel) }
  header h1 { font-size: 18px; margin: 0 }
  header a { color: var(--accent); text-decoration: none }
  header .grow { flex: 1 }
  .muted { color: var(--muted) }
  main { max-width: 1100px; margin: 0 auto; padding: 20px; display: grid; gap: 20px }
  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px }
  .tile { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px 14px }
  .tile dt { font-size: 12px; color: var(--muted) }
  .tile dd { margin: 2px 0 0; font-size: 24px; line-height: 30px; font-weight: 600; font-variant-numeric: tabular-nums }
  .tile small { color: var(--muted) }
  section { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 14px 16px; overflow-x: auto }
  section h2 { font-size: 15px; margin: 0 0 2px }
  section p { margin: 0 0 10px; color: var(--muted) }
  section h3 { font-size: 13px; margin: 18px 0 6px }
  section .tiles { margin-bottom: 14px }
  table { width: 100%; border-collapse: collapse; font-variant-numeric: tabular-nums }
  th, td { text-align: right; padding: 6px 8px; border-top: 1px solid var(--line); white-space: nowrap }
  th { font-size: 12px; font-weight: 600; color: var(--muted); border-top: 0 }
  th:first-child, td:first-child { text-align: left; white-space: normal }
  th.wrap, td.wrap { text-align: left; white-space: normal; min-width: 160px }
  td.bar { position: relative; min-width: 120px }
  td.bar i { position: absolute; left: 8px; top: 7px; bottom: 7px; background: var(--bar); border-radius: 3px }
  td.bar span { position: relative }
  .banner { display: none; padding: 10px 14px; border-radius: 8px; background: var(--warn-bg); color: var(--warn) }
  .banner.show { display: block }
  .empty { color: var(--muted); padding: 8px 0 }
""";

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Symphony · Line</title>
<style>
""" + Style + """
</style>
</head>
<body>
<header>
  <h1>Symphony · Line</h1>
  <span class="muted" id="since"></span>
  <div class="grow"></div>
  <a href="/">Board</a>
  <a href="/audit">Audit</a>
  <a href="/api/v1/metrics">JSON</a>
</header>
<main>
  <div class="banner" id="error"></div>
  <dl class="tiles" id="tiles"></dl>
  <section><h2>By status</h2><p>Agent time and tokens for each status an agent worked in. Rework is a stage that ran again after it had been handed off.</p><div id="states"></div></section>
  <section><h2>Verification gates</h2><p>Symphony's checks before an agent may move a task to each status. A hand-off is one attempt to get a task past the gate; first try means its first check passed.</p><div id="gates"></div></section>
  <section><h2>Why work goes back</h2><p>A loop is an agent sending a task back, such as the tester moving it to Con Defectos, with the defects it found by category and severity. Triage is the wait until the next agent run. Rework is what the loop cost: the runs that repeated a stage until the task got past the tester again. Rework no report explains was a person sending the task back, for example from review.</p><dl class="tiles" id="rework-tiles"></dl><div id="categories"></div><h3>Whose work went back</h3><div id="sources"></div><h3>Recent loops</h3><div id="loops"></div></section>
  <section><h2>By model</h2><p>Where the tokens went.</p><div id="models"></div></section>
  <section><h2>Last 14 days</h2><p>Runs finished, tasks closed and tokens used per day.</p><div id="days"></div></section>
  <section><h2>Tasks</h2><p>The 50 most recently active tasks. Cycle time runs from the first agent run to the terminal status.</p><div id="issues"></div></section>
</main>
<script>
const $ = id => document.getElementById(id);
const fmt = n => new Intl.NumberFormat().format(n ?? 0);
const short = n => n >= 1e9 ? (n / 1e9).toFixed(1) + 'B' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(n ?? 0);
const dur = s => { if (s == null) return '–'; s = Math.round(s); const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60); return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m ${s % 60}s`; };
const pct = (a, b) => b ? Math.round(100 * a / b) + '%' : '–';
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function tile(name, value, note) { const d = el('div', 'tile'); d.append(el('dt', '', name), el('dd', '', value)); if (note) d.append(el('small', '', note)); return d; }
// cols: [header, row => text, row => number for a bar (optional), 'wrap' for long text (optional)]
function table(cols, rows, empty) {
  if (!rows.length) return el('div', 'empty', empty);
  const t = el('table'), head = el('tr');
  for (const [h, , , wrap] of cols) head.append(el('th', wrap ? 'wrap' : '', h));
  t.append(head);
  for (const [, , bar] of cols) if (bar) bar.max = Math.max(1, ...rows.map(bar));
  for (const r of rows) {
    const tr = el('tr');
    for (const [, text, bar, wrap] of cols) {
      const td = el('td', bar ? 'bar' : wrap ? 'wrap' : '');
      if (bar) { const i = el('i'); i.style.width = `calc(${100 * bar(r) / bar.max}% - 16px)`; td.append(i, el('span', '', text(r))); } else td.textContent = text(r);
      tr.append(td);
    }
    t.append(tr);
  }
  return t;
}
async function load() {
  try {
    const res = await fetch('/api/v1/metrics', { cache: 'no-store' });
    if (!res.ok) throw new Error(`metrics ${res.status}`);
    const m = await res.json(), t = m.totals, b = m.budget;
    $('error').className = 'banner' + (b?.day_exceeded ? ' show' : '');
    $('error').textContent = b?.day_exceeded ? `Today's token budget is used up (${fmt(b.day_tokens)} of ${fmt(b.max_tokens_per_day)}). Work is paused until tomorrow.` : '';
    $('since').textContent = m.since ? `since ${new Date(m.since).toLocaleDateString()}` : 'no finished runs yet';
    $('tiles').replaceChildren(
      tile('Tasks worked', fmt(t.issues), `${fmt(t.closed_issues)} closed`),
      tile('Runs', fmt(t.runs), `${fmt(t.resumed_runs)} resumed a session`),
      tile('Agent time', dur(t.agent_seconds)),
      tile('Tokens', short(t.total_tokens), `${short(t.input_tokens)} in · ${short(t.output_tokens)} out`),
      tile('Failed runs', pct(t.failed_runs, t.runs), `${fmt(t.failed_runs)} of ${fmt(t.runs)}`),
      tile('Rework', pct(t.rework_runs, t.runs), `${fmt(t.rework_runs)} runs`),
      tile('Avg cycle time', dur(t.avg_cycle_seconds), 'first run to closed'),
      tile('Gate rejections', pct(t.gate_rejections, t.gate_checks), `${fmt(t.gate_rejections)} of ${fmt(t.gate_checks)} checks`),
      tile('Today', short(b?.day_tokens ?? 0), b?.max_tokens_per_day ? `of ${short(b.max_tokens_per_day)} daily budget` : 'no daily budget'));
    $('states').replaceChildren(table([
      ['Status', r => r.state], ['Tasks', r => fmt(r.issues)], ['Runs', r => fmt(r.runs)], ['Agent time', r => dur(r.agent_seconds)],
      ['Per task', r => dur(r.avg_seconds_per_issue)], ['Tokens', r => short(r.total_tokens), r => r.total_tokens], ['Failed', r => fmt(r.failed_runs)], ['Rework', r => fmt(r.rework_runs)],
      ['Models', r => Object.keys(r.tokens_by_model).join(', ')]], m.by_state, 'No finished runs yet.'));
    $('gates').replaceChildren(table([
      ['Status', r => r.state], ['Tasks', r => fmt(r.issues)], ['Checks', r => fmt(r.checks), r => r.checks], ['Rejected', r => `${fmt(r.rejected)} (${pct(r.rejected, r.checks)})`],
      ['Hand-offs', r => fmt(r.handoffs)], ['First try', r => pct(r.first_try_handoffs, r.handoffs)], ['Avg check', r => dur(r.avg_seconds)],
      ['Rejected for', r => Object.entries(r.rejections_by_category).map(([k, v]) => `${k} ×${v}`).join(', ') || '–', null, 'wrap']], m.by_gate ?? [], 'No gate has run yet. Gates are set in the workflow\'s gates section.'));
    const w = m.rework, sev = o => ['critical', 'major', 'minor'].filter(k => o?.[k]).map(k => `${k} ${o[k]}`).join(' · ') || '–';
    const counts = o => Object.entries(o ?? {}).map(([k, v]) => `${k} ×${v}`).join(', ') || '–';
    if (!w) {
      $('rework-tiles').replaceChildren();
      for (const id of ['categories', 'sources', 'loops']) $(id).replaceChildren(el('div', 'empty', id === 'categories' ? 'No loops or rework yet. Defect reports need the workflow\'s defects section.' : ''));
    } else {
      $('rework-tiles').replaceChildren(
        tile('Loops', fmt(w.loops), `${fmt(w.defects)} defects · ${sev(w.by_severity)}`),
        tile('Tasks sent back', fmt(w.issues_with_loops), `of ${fmt(t.issues)} worked`),
        tile('Closed without rework', pct(w.closed_without_rework, w.closed_issues), `${fmt(w.closed_without_rework)} of ${fmt(w.closed_issues)} closed`),
        tile('Avg triage', dur(w.avg_triage_seconds), 'then ' + (Object.entries(w.next_states).map(([k, v]) => `${v} ${k}`).join(' · ') || '–')),
        tile('Loop rework', short(w.loop_rework_tokens), `${fmt(w.loop_rework_runs)} runs · ${dur(w.loop_rework_seconds)}`),
        tile('Unexplained rework', fmt(w.other_rework_runs) + ' runs', `${short(w.other_rework_tokens)} tokens, no defect report`));
      $('categories').replaceChildren(table([
        ['Category', r => r.category], ['Defects', r => fmt(r.defects), r => r.defects], ['Severity', r => sev(r.severities)], ['Loops', r => fmt(r.loops)], ['Tasks', r => fmt(r.issues)],
        ['Rework tokens', r => short(r.rework_tokens), r => r.rework_tokens]], w.by_category, 'No defects reported yet.'));
      $('sources').replaceChildren(table([
        ['Status', r => r.state || '–'], ['Model', r => r.model], ['Loops', r => fmt(r.loops), r => r.loops], ['Defects', r => fmt(r.defects)]], w.by_source, 'No loops yet.'));
      $('loops').replaceChildren(table([
        ['When', r => new Date(r.at).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })], ['Task', r => r.identifier],
        ['Sent back', r => `${r.from_state} → ${r.state}`, null, 'wrap'], ['Defects', r => counts(r.categories), null, 'wrap'], ['Severity', r => sev(r.severities), null, 'wrap'],
        ['Work by', r => r.work_model ? `${r.work_model} in ${r.work_state}` : '–', null, 'wrap'], ['Triage', r => r.next_state ? dur(r.triage_seconds) : 'waiting'], ['Next', r => r.next_state ?? '–'],
        ['Rework', r => r.rework_runs ? `${fmt(r.rework_runs)} runs · ${short(r.rework_tokens)}` : '–']], w.recent, 'No loops yet.'));
    }
    $('models').replaceChildren(table([
      ['Model', r => r.model], ['Runs', r => fmt(r.runs)], ['Agent time', r => dur(r.agent_seconds)], ['Input', r => short(r.input_tokens)], ['Output', r => short(r.output_tokens)],
      ['Tokens', r => short(r.total_tokens), r => r.total_tokens]], m.by_model, 'No finished runs yet.'));
    $('days').replaceChildren(table([
      ['Day', r => r.day], ['Runs', r => fmt(r.runs)], ['Closed', r => fmt(r.closed_issues)], ['Agent time', r => dur(r.agent_seconds)], ['Tokens', r => short(r.total_tokens), r => r.total_tokens]], m.by_day, ''));
    $('issues').replaceChildren(table([
      ['Task', r => r.identifier], ['Statuses', r => r.states.map(s => `${s.state} ×${s.runs}`).join(', '), null, 'wrap'], ['Runs', r => fmt(r.runs)], ['Failed', r => fmt(r.failed_runs)], ['Rework', r => fmt(r.rework_runs)], ['Loops', r => fmt(r.loops)],
      ['Gate rejections', r => fmt(r.gate_rejections)], ['Agent time', r => dur(r.agent_seconds)], ['Tokens', r => short(r.total_tokens), r => r.total_tokens], ['Cycle time', r => r.closed_at ? dur(r.cycle_seconds) : 'open']], m.issues, 'No finished runs yet.'));
  } catch (e) { $('error').className = 'banner show'; $('error').textContent = `Can't load the metrics: ${e.message}`; }
}
load(); setInterval(load, 10000);
</script>
</body>
</html>
""";
}
