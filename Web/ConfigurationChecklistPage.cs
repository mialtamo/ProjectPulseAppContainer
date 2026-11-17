using System.Net;
using System.Text;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.Web;

public static class ConfigurationChecklistPage
{
    public static string Render(IReadOnlyList<string> issues)
    {
        if (issues.Count == 0)
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

        var issueItems = new StringBuilder();
        foreach (var issue in issues)
        {
            issueItems.Append("<li>")
                .Append(WebUtility.HtmlEncode(issue))
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
    <h1>Checks need attention</h1>
    <ul>{{issueItems}}</ul>
  </main>
</body>
</html>
""";
    }
}
