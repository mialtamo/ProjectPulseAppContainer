using System.Net;
using System.Text;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.Web;

public static class ConfigurationChecklistPage
{
    public static string Render(
        ConfigurationChecklistResult result,
        string? version,
        string? environment,
        string role)
    {
        var rows = new StringBuilder();
        foreach (var check in result.Checks)
        {
            var css = check.Status.ToLowerInvariant().Replace(' ', '-');
            rows.Append("<tr><td><strong>")
                .Append(WebUtility.HtmlEncode(check.Name))
                .Append("</strong><small>")
                .Append(WebUtility.HtmlEncode(check.Setting))
                .Append("</small></td><td><span class=\"status ")
                .Append(WebUtility.HtmlEncode(css))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(check.Status))
                .Append("</span></td><td>")
                .Append(WebUtility.HtmlEncode(check.Details))
                .Append("</td></tr>");
        }

        var workerStatus = result.CanStartWorker ? "Configuration checks passed" : "Worker paused until fixed";
        var workerClass = result.CanStartWorker ? "ready" : "blocked";
        var overall = result.CanStartWorker
            ? "Required settings are present and valid. Use the live checks below to verify service access."
            : $"The worker is not running. Fix the {result.BlockedCount} blocked item(s), then deploy a new revision.";
        var safeVersion = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(version) ? "unknown" : version);
        var safeEnvironment = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(environment) ? "UNKNOWN" : environment);
        var safeRole = WebUtility.HtmlEncode(role);

        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="dark">
  <title>Project Pulse configuration checklist</title>
  <style>
    :root { color-scheme: dark; --bg:#070b12; --panel:#0d1521; --line:#203044; --text:#d7e6ff; --muted:#8da2bd; --cyan:#19ddff; --green:#39d98a; --amber:#ffc857; --red:#ff5c72; }
    * { box-sizing:border-box; }
    body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 Inter,Segoe UI,system-ui,sans-serif; }
    main { width:min(1100px,94vw); margin:32px auto; }
    header,.card { background:var(--panel); border:1px solid var(--line); border-radius:14px; padding:22px; margin-bottom:16px; }
    h1 { margin:0 0 4px; font-size:24px; } h2 { margin:0 0 12px; font-size:17px; }
    p { color:var(--muted); margin:6px 0; }
    .meta { color:var(--muted); font-size:13px; margin-top:12px; }
    .summary { border-left:4px solid var(--green); } .summary.blocked { border-left-color:var(--red); }
    .status { display:inline-block; padding:3px 9px; border-radius:99px; border:1px solid var(--line); font-size:12px; white-space:nowrap; }
    .status.ready { color:var(--green); } .status.warning { color:var(--amber); } .status.blocked { color:var(--red); }
    table { width:100%; border-collapse:collapse; } th,td { padding:12px 10px; text-align:left; border-bottom:1px solid var(--line); vertical-align:top; }
    th { color:var(--muted); font-weight:600; font-size:12px; text-transform:uppercase; }
    td small { display:block; color:var(--muted); margin-top:2px; font-family:ui-monospace,Consolas,monospace; }
    .actions { display:flex; gap:10px; flex-wrap:wrap; margin-top:14px; }
    a,button { color:var(--cyan); } a { text-decoration:none; } a:hover { text-decoration:underline; }
    button { background:#0a2230; border:1px solid #176178; border-radius:8px; padding:9px 14px; cursor:pointer; font:inherit; }
    button:disabled { opacity:.55; cursor:wait; }
    #probeResults { margin-top:12px; white-space:pre-wrap; color:var(--muted); }
    .note { font-size:13px; }
    @media(max-width:700px) { main { margin:14px auto; } header,.card { padding:16px; } th:nth-child(1),td:nth-child(1) { width:38%; } }
  </style>
</head>
<body>
<main>
  <header>
    <h1>Project Pulse configuration checklist</h1>
    <p>Checks what this running replica actually received. Configuration values and secrets are never shown.</p>
    <div class="meta">Environment: <strong>{{safeEnvironment}}</strong> · Role: <strong>{{safeRole}}</strong> · Build: <strong>{{safeVersion}}</strong></div>
  </header>
  <section class="card summary {{workerClass}}">
    <h2>{{workerStatus}}</h2>
    <p>{{WebUtility.HtmlEncode(overall)}}</p>
    <div class="actions">
      <button id="runProbes" type="button">Run live RTA and APIM checks</button>
      <a href="/requests.html">Open Requests</a>
    </div>
    <div id="probeResults" aria-live="polite"></div>
  </section>
  <section class="card">
    <h2>Required settings</h2>
    <table>
      <thead><tr><th>Check / setting</th><th>Status</th><th>What to do</th></tr></thead>
      <tbody>{{rows}}</tbody>
    </table>
  </section>
  <section class="card">
    <h2>After changing Container App settings</h2>
    <p class="note">This page reports the environment loaded by the active replica. If a setting was added after the replica started, deploy or restart the Container App revision so the running process receives it. A green configuration check confirms presence and format; the live checks confirm RTA and APIM access from this replica.</p>
    <p class="note">Service Bus send/receive can be verified with the Service Bus test on the Requests page. A successful send confirms broker acceptance; check the SERVICEBUS app log stream for the worker receive confirmation.</p>
  </section>
</main>
<script>
document.getElementById('runProbes').addEventListener('click', async function () {
  const button = this;
  const output = document.getElementById('probeResults');
  button.disabled = true;
  output.textContent = 'Checking RTA and APIM using this replica’s configured authentication...';
  try {
    const response = await fetch('/checklist/probes', {
      method: 'POST',
      headers: { 'X-Requested-With': 'XMLHttpRequest' }
    });
    const result = await response.json();
    if (!response.ok) {
      output.textContent = result.error || 'Live checks could not run. Resolve blocked settings above first.';
    } else {
      const show = item => item.isHealthy
        ? 'PASS — ' + item.dependency + ' (HTTP ' + (item.statusCode ?? 'OK') + ')'
        : 'FAIL — ' + item.dependency + ' (HTTP ' + (item.statusCode ?? 'unavailable') + '): ' + (item.detail || 'No healthy response');
      output.textContent = show(result.appApi) + '\n' + show(result.apim);
    }
  } catch (error) {
    output.textContent = 'Live checks failed: ' + error.message;
  } finally {
    button.disabled = false;
  }
});
</script>
</body>
</html>
""";
    }
}
