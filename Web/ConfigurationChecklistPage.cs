using System.Net;
using System.Text;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.Web;

public static class ConfigurationChecklistPage
{
    public static string Render(ConfigurationChecklistResult result)
    {
        if (!result.CanStartWorker)
        {
            var blockedItems = new StringBuilder();
            foreach (var check in result.Checks.Where(check => check.Status == "Blocked"))
            {
                blockedItems.Append("<li><strong>")
                    .Append(WebUtility.HtmlEncode(check.Name))
                    .Append("</strong>: ")
                    .Append(WebUtility.HtmlEncode(check.Details))
                    .Append("</li>");
            }

            return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Project Pulse checklist</title>
</head>
<body>
  <main>
    <h1>Configuration needs attention</h1>
    <ul>{{blockedItems}}</ul>
  </main>
</body>
</html>
""";
        }

        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Project Pulse checklist</title>
</head>
<body>
  <main>
    <h1>Verify connections</h1>
    <button id="verify" type="button">Verify connections</button>
    <div id="results" aria-live="polite"></div>
  </main>
  <script>
    const button = document.getElementById('verify');
    const results = document.getElementById('results');

    button.addEventListener('click', async () => {
      button.disabled = true;
      results.textContent = 'Checking RTA API, RTA database, and APIM...';
      try {
        const response = await fetch('/checklist/probes', {
          method: 'POST',
          headers: { 'X-Requested-With': 'XMLHttpRequest' }
        });
        const report = await response.json();
        if (report.allHealthy) {
          document.body.innerHTML = '<main><h1>All is well</h1></main>';
          return;
        }

        const items = (report.checks || []).map(check => {
          const status = check.statusCode == null ? 'No HTTP response' : 'HTTP ' + check.statusCode;
          return (check.isHealthy ? 'PASS' : 'FAIL') + ' — ' + check.name + ' — ' + status +
            (check.detail ? ': ' + check.detail : '');
        });
        results.textContent = (report.error ? report.error + '\n' : '') + items.join('\n');
      } catch (error) {
        results.textContent = 'Verification failed before a health result was received. Check the replica logs.';
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
