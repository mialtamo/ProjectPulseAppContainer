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
        if (result.CanStartWorker)
        {
            return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Project Pulse checklist</title>
</head>
<body>
  <main><h1>All is well</h1></main>
</body>
</html>
""";
        }

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
    <h1>Project Pulse needs configuration</h1>
    <p>The processing worker is paused. Correct these settings, then restart or deploy the Container App revision:</p>
    <ul>{{blockedItems}}</ul>
  </main>
</body>
</html>
""";
    }
}
