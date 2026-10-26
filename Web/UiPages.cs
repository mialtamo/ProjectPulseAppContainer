using System.Net;

namespace ProjectPulse.Processor.Web;

public static class UiPages
{
    public static string LandingPage(string? versionValue, string? environmentValue, string? roleValue)
    {
        var version = WebUtility.HtmlEncode(versionValue ?? "0.0.0");
        var environment = WebUtility.HtmlEncode(environmentValue ?? "UNKNOWN");
        var role = WebUtility.HtmlEncode(roleValue ?? "RTA");

        return $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="dark" />
    <title>Project Pulse</title>
    <style>
        :root {
            --cyan: #19ddff;
            --blue: #3478ff;
            --violet: #8a4dff;
            --text: #d7e6ff;
            --muted: #6f86a8;
            --green: #39ff9a;
            --red: #ff4d6d;
            --amber: #ffc857;
        }
        * { box-sizing: border-box; }
        html, body {
            width: 100%;
            height: 100%;
            margin: 0;
            overflow: hidden;
            background: #000;
            color: var(--text);
            font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }
        body::before {
            content: "";
            position: fixed;
            inset: -35%;
            pointer-events: none;
            background:
                radial-gradient(circle at 50% 44%, rgba(25, 221, 255, .08), transparent 28%),
                radial-gradient(circle at 56% 48%, rgba(138, 77, 255, .07), transparent 34%);
            animation: atmosphere 8s ease-in-out infinite alternate;
        }
        .grid {
            position: fixed;
            inset: 0;
            opacity: .10;
            background-image:
                linear-gradient(rgba(74, 121, 255, .18) 1px, transparent 1px),
                linear-gradient(90deg, rgba(74, 121, 255, .18) 1px, transparent 1px);
            background-size: 72px 72px;
            mask-image: radial-gradient(circle at center, #000 0%, transparent 72%);
            pointer-events: none;
        }
        .shell {
            position: relative;
            width: 100%;
            height: 100%;
            display: grid;
            place-items: center;
            padding: 4vh 4vw;
        }
        .hero {
            width: min(980px, 72vw);
            max-height: 68vh;
            display: flex;
            align-items: center;
            justify-content: center;
            transform: translateY(3vh);
        }
        .logo-wrap {
            position: relative;
            width: 100%;
            animation: float 6s ease-in-out infinite;
        }
        .logo-wrap::before {
            content: "";
            position: absolute;
            inset: 18% 16% 12%;
            z-index: -1;
            background: radial-gradient(
                ellipse at center,
                rgba(20, 213, 255, .18) 0%,
                rgba(64, 99, 255, .09) 40%,
                rgba(137, 65, 255, .06) 58%,
                transparent 76%
            );
            filter: blur(28px);
            animation: glow 3.2s ease-in-out infinite alternate;
        }
        .logo {
            display: block;
            width: 100%;
            max-height: 68vh;
            height: auto;
            object-fit: contain;
            filter: drop-shadow(0 0 18px rgba(41, 197, 255, .13));
            user-select: none;
            -webkit-user-drag: none;
        }
        .status-panel {
            position: fixed;
            top: 24px;
            right: 28px;
            z-index: 20;
            min-width: 390px;
            padding: 16px 18px;
            border: 1px solid rgba(25, 221, 255, .16);
            border-radius: 8px;
            background: rgba(0, 0, 0, .52);
            backdrop-filter: blur(10px);
            box-shadow: 0 0 30px rgba(25, 221, 255, .05), inset 0 0 25px rgba(25, 221, 255, .025);
            font-family: "Courier New", Consolas, monospace;
            font-size: 12px;
            line-height: 1.85;
            color: #8098ba;
        }
        .status-title {
            margin-bottom: 8px;
            color: #d7e6ff;
            font-weight: 700;
            letter-spacing: .18em;
            text-transform: uppercase;
        }
        .status-row { white-space: nowrap; }
        .status-label { color: #8098ba; }
        .connected { color: var(--green); text-shadow: 0 0 9px rgba(57, 255, 154, .68); }
        .disconnected { color: var(--red); text-shadow: 0 0 9px rgba(255, 77, 109, .62); }
        .checking { color: var(--amber); text-shadow: 0 0 8px rgba(255, 200, 87, .35); }
        .last-check {
            margin-top: 8px;
            padding-top: 7px;
            border-top: 1px solid rgba(111, 134, 168, .14);
            color: #526783;
            font-size: 10px;
            letter-spacing: .08em;
        }
        .requests-button {
            display: flex;
            justify-content: center;
            margin-top: 14px;
            padding: 9px 12px;
            border: 1px solid rgba(25, 221, 255, .28);
            border-radius: 6px;
            background: rgba(25, 221, 255, .08);
            color: #d7e6ff;
            font: 700 11px/1.4 "Courier New", Consolas, monospace;
            letter-spacing: .12em;
            text-decoration: none;
            text-transform: uppercase;
            transition: background .15s ease, border-color .15s ease;
        }
        .requests-button:hover, .requests-button:focus-visible {
            border-color: rgba(25, 221, 255, .65);
            background: rgba(25, 221, 255, .16);
            outline: none;
        }
        .version {
            position: fixed;
            left: 24px;
            bottom: 19px;
            z-index: 5;
            display: flex;
            align-items: center;
            gap: 10px;
            color: var(--muted);
            font-size: 11px;
            font-weight: 600;
            letter-spacing: .16em;
            text-transform: uppercase;
            user-select: none;
        }
        .version .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--cyan);
            box-shadow: 0 0 12px rgba(25, 221, 255, .95);
            animation: blink 2.1s ease-in-out infinite;
        }
        .scanline {
            position: fixed;
            left: 0;
            right: 0;
            height: 1px;
            top: -2px;
            background: linear-gradient(90deg, transparent 10%, rgba(25, 221, 255, .22), rgba(138, 77, 255, .18), transparent 90%);
            box-shadow: 0 0 8px rgba(25, 221, 255, .14);
            animation: scan 8s linear infinite;
            pointer-events: none;
        }
        @keyframes float {
            0%, 100% { transform: translateY(0) scale(1); }
            50% { transform: translateY(-8px) scale(1.003); }
        }
        @keyframes glow {
            from { opacity: .72; transform: scale(.98); }
            to { opacity: 1; transform: scale(1.035); }
        }
        @keyframes blink {
            0%, 100% { opacity: .62; }
            50% { opacity: 1; }
        }
        @keyframes scan {
            from { transform: translateY(0); opacity: 0; }
            8% { opacity: 1; }
            92% { opacity: 1; }
            to { transform: translateY(100vh); opacity: 0; }
        }
        @keyframes atmosphere {
            from { transform: scale(1) rotate(0deg); opacity: .82; }
            to { transform: scale(1.06) rotate(.8deg); opacity: 1; }
        }
        @media (max-width: 700px) {
            .hero { width: min(1100px, 96vw); }
            .version { left: 16px; bottom: 14px; font-size: 9px; }
            .status-panel { top: 12px; right: 12px; left: 12px; min-width: 0; font-size: 10px; }
        }
        @media (prefers-reduced-motion: reduce) {
            *, *::before, *::after { animation: none !important; }
        }
    </style>
</head>
<body>
    <div class="grid" aria-hidden="true"></div>
    <div class="scanline" aria-hidden="true"></div>

    <div class="status-panel">
        <div class="status-title">SYSTEM STATUS</div>
        <div class="status-row">
            <span class="status-label">Checking Claims API / SQL..... </span>
            <span id="appApi" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="status-row">
            <span class="status-label">Checking APIM / Backend....... </span>
            <span id="apim" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="status-row">
            <span class="status-label">Checking Service Bus.......... </span>
            <span id="serviceBus" class="checking">&lt;CHECKING...&gt;</span>
        </div>
        <div class="last-check" id="lastCheck">LAST CHECK: awaiting first health probe</div>
        <a class="requests-button" href="/requests.html">View request history</a>
    </div>

    <main class="shell">
        <section class="hero" aria-label="Project Pulse">
            <div class="logo-wrap">
                <img class="logo" src="https://raw.githubusercontent.com/mialtamo/Project-Pulse/main/Assets/project-pulse-logo.png" alt="Project Pulse" />
            </div>
        </section>
    </main>

    <div class="version" aria-label="Project Pulse version {{version}}, environment {{environment}}, role {{role}}">
        <span class="dot" aria-hidden="true"></span>
        <span>Project Pulse&nbsp;&nbsp;v{{version}}&nbsp;&nbsp;•&nbsp;&nbsp;{{environment}}&nbsp;&nbsp;•&nbsp;&nbsp;{{role}}</span>
    </div>

    <script>
        function setStatus(id, connected) {
            const element = document.getElementById(id);
            if (connected) {
                element.className = 'connected';
                element.textContent = '<CONNECTED>';
            } else {
                element.className = 'disconnected';
                element.textContent = '<DISCONNECTED>';
            }
        }

        function setChecking() {
            for (const id of ['appApi', 'apim', 'serviceBus']) {
                const element = document.getElementById(id);
                element.className = 'checking';
                element.textContent = '<CHECKING...>';
            }
        }

        async function checkPulseHealth() {
            setChecking();
            try {
                const response = await fetch('/pulse-health', { method: 'GET', cache: 'no-store' });
                if (!response.ok) throw new Error('Health endpoint returned ' + response.status);
                const data = await response.json();
                setStatus('appApi', data.appApi);
                setStatus('apim', data.apim);
                setStatus('serviceBus', data.serviceBus);
                document.getElementById('lastCheck').textContent = 'LAST CHECK: ' + new Date().toLocaleTimeString();
            } catch {
                setStatus('appApi', false);
                setStatus('apim', false);
                setStatus('serviceBus', false);
                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString() + ' | HEALTH ENDPOINT UNAVAILABLE';
            }
        }

        checkPulseHealth();
        setInterval(checkPulseHealth, 5000);
    </script>
</body>
</html>
""";
    }

    public static string RequestHistoryPageFor(string? signedInUser)
    {
        var displayName = string.IsNullOrWhiteSpace(signedInUser) ? "Signed-in user" : signedInUser;
        return RequestHistoryPage.Replace("{{SIGNED_IN_USER}}", WebUtility.HtmlEncode(displayName), StringComparison.Ordinal);
    }

    private const string RequestHistoryPage = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse Requests</title>
<style>
:root{
  color-scheme:dark;
  --bg:#05070b;--panel:#0b111a;--panel2:#0f1722;--border:#1c2a3a;
  --text:#e5eefc;--muted:#8295ad;--cyan:#19ddff;--green:#54e39b;
  --amber:#ffc857;--red:#ff627d;--blue:#68a4ff;--purple:#b58cff
}
*{box-sizing:border-box}
body{margin:0;background:linear-gradient(180deg,#05070b,#07101a 55%,#05070b);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{position:sticky;top:0;z-index:10;padding:18px 24px;border-bottom:1px solid var(--border);background:rgba(5,7,11,.92);backdrop-filter:blur(10px);display:flex;justify-content:space-between;align-items:center;gap:16px}
h1{margin:0;font-size:19px;letter-spacing:.08em}.sub{color:var(--muted);font-size:12px;margin-top:4px}
.header-actions{display:flex;align-items:center;justify-content:flex-end;gap:16px;flex-wrap:wrap}
.signed-in-user{color:var(--muted);font-size:12px}
.signed-in-user strong{color:var(--text);font-weight:600}
.sign-out{padding:7px 10px;border:1px solid var(--border);border-radius:7px;background:var(--panel2)}
.sign-out:hover,.sign-out:focus-visible{border-color:var(--cyan);outline:none}
a{color:var(--cyan);text-decoration:none}
main{padding:20px 24px 40px;max-width:1800px;margin:0 auto}
.toolbar{display:grid;grid-template-columns:minmax(240px,1fr) auto auto auto auto;gap:10px;align-items:center;margin-bottom:14px}
.refresh-control{display:flex;align-items:center;gap:8px;color:var(--muted);font-size:12px;white-space:nowrap}
input,select,button{background:#0b1520;border:1px solid #24405a;color:var(--text);padding:9px 11px;border-radius:7px}
.load-more-wrap{display:flex;justify-content:center;padding:14px 0}
.load-more-wrap button[hidden]{display:none}
button{cursor:pointer}.muted{color:var(--muted)}
 .summary{display:flex;gap:10px;flex-wrap:wrap;margin:0 0 14px}
.timing-summary{margin:0 0 16px;padding:14px;border:1px solid var(--border);border-radius:10px;background:var(--panel)}
.timing-range-controls{display:flex;align-items:center;gap:9px;flex-wrap:wrap;margin:0 0 12px;color:var(--muted);font-size:12px}
.timing-range-controls select,.timing-range-controls input,.timing-range-controls button{border:1px solid var(--border);border-radius:6px;background:#0a121c;color:var(--text);padding:7px 9px;font:inherit}
.custom-timing-range{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.custom-timing-range[hidden]{display:none}
.timing-range-error{color:#ff7b91}

.timing-summary-heading{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:10px}
.timing-summary-title{display:flex;align-items:center;gap:8px;min-width:0;position:relative}
.timing-summary-heading h2{font-size:14px;margin:0;color:#c7d9f2}
.timing-help{position:relative;flex:none}
.timing-help summary{list-style:none;display:inline-flex;align-items:center;justify-content:center;width:22px;height:22px;border:1px solid #314154;border-radius:50%;color:#9db1c7;cursor:pointer}
.timing-help summary::-webkit-details-marker{display:none}
.timing-help summary:hover,.timing-help summary:focus-visible,.timing-help[open] summary{border-color:var(--cyan);color:var(--cyan);outline:none}
.timing-help summary svg{width:14px;height:14px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.timing-help-popover{position:absolute;left:0;top:calc(100% + 9px);z-index:30;width:min(480px,calc(100vw - 56px));max-height:min(72vh,560px);overflow:auto;padding:14px 16px;border:1px solid #314154;border-radius:10px;background:#0b111a;box-shadow:0 18px 44px rgba(0,0,0,.5);color:var(--text);font-size:12px;line-height:1.5}
.timing-help-popover h3{font-size:13px;margin:0 0 7px;color:#d9e7fa}
.timing-help-popover p{margin:7px 0}
.timing-help-popover dl{display:grid;grid-template-columns:110px 1fr;gap:7px 10px;margin:10px 0}
.timing-help-popover dt{font-weight:700;color:#c7d9f2}
.timing-help-popover dd{margin:0;color:#b4c3d5}
.timing-averages{display:grid;grid-template-columns:repeat(5,minmax(130px,1fr));gap:10px}
.timing-average-card{border:1px solid var(--border);border-radius:8px;padding:10px;background:#08111a}
.timing-average-card.green{border-color:#17613b}.timing-average-card.yellow{border-color:#806325}.timing-average-card.red{border-color:#743535}
.timing-average-label{font-size:11px;color:var(--muted);margin-bottom:6px}
.timing-average-value{font-size:18px;font-weight:700;font-variant-numeric:tabular-nums}
.timing-average-card.green .timing-average-value{color:var(--green)}
.timing-diagnostic-note{margin-top:10px;padding:9px 11px;border:1px solid #344456;border-radius:8px;color:#b4c3d5;font-size:12px;line-height:1.45}
.timing-average-card.yellow .timing-average-value{color:var(--amber)}
.timing-average-card.red .timing-average-value{color:var(--red)}
.timing-average-count,.timing-legend{font-size:10px;color:var(--muted);margin-top:5px}
.timing-legend{margin-top:10px}
.pill{padding:5px 9px;border:1px solid var(--border);border-radius:999px;background:#09111a;font-size:12px;color:var(--muted)}
.table-wrap{border:1px solid var(--border);border-radius:10px;overflow:auto;background:var(--panel);box-shadow:0 18px 60px rgba(0,0,0,.25)}
table{width:100%;border-collapse:collapse;min-width:1150px}
th,td{padding:10px 12px;border-bottom:1px solid var(--border);font-size:12px;vertical-align:top}
th{position:sticky;top:0;background:#0c1621;color:#9fb4cf;text-align:left;white-space:nowrap;cursor:pointer;user-select:none}
th:hover{color:#dceaff}
tr.data-row{cursor:pointer}
tr.data-row:hover{background:#0f1a27}
td.id{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;max-width:260px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.tag{display:inline-block;padding:2px 7px;border-radius:999px;font-size:11px;border:1px solid #29445f;white-space:nowrap}
.tag.APIM{color:var(--green)}.tag.ServiceBus{color:var(--amber)}
.status-Completed{color:var(--green)}.status-Buffered{color:var(--amber)}.status-Alerted,.status-WritebackFailed,.status-Rejected{color:var(--red)}
.expand-row{display:none;background:#07101a}.expand-row.open{display:table-row}
.expand-cell{padding:16px}
.detail-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin-bottom:14px}
.timing-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin:0 0 14px}
.exchange-outcomes{margin:0 0 14px}.exchange-title{font-size:12px;font-weight:700;margin:0 0 8px;color:#c7d9f2}.exchange-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px}.exchange-step{border:1px solid var(--border);border-radius:8px;background:#08111a;padding:10px}.exchange-step.success{border-color:#145a3d}.exchange-step.warning{border-color:#6d5728}.exchange-step.error{border-color:#6b3038}.exchange-step.neutral{border-color:var(--border)}.exchange-step-heading{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:5px}.exchange-step-name{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:.08em}.exchange-step-state{font-size:11px;font-weight:700}.exchange-step.success .exchange-step-state{color:var(--green)}.exchange-step.warning .exchange-step-state{color:var(--amber)}.exchange-step.error .exchange-step-state{color:var(--red)}.exchange-step-body{font-size:12px;line-height:1.45;color:#c7d9f2;overflow-wrap:anywhere}.exchange-note{font-size:11px;color:var(--muted);margin:8px 0 0}
.card{border:1px solid var(--border);padding:10px;background:#08111a;border-radius:7px}
.label{font-size:10px;color:var(--muted);text-transform:uppercase;letter-spacing:.12em;margin-bottom:4px}
.payload-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}
.payload-panel{min-width:0}
.payload-title{font-size:12px;font-weight:700;margin:0 0 10px;color:#c7d9f2}
.payload-code{position:relative;overflow:hidden;border:1px solid var(--border);border-radius:12px;background:#020407}
.payload-code.has-copy pre{padding-top:40px}
.copy-button{position:absolute;top:8px;right:12px;z-index:1;display:inline-flex;align-items:center;justify-content:center;width:28px;height:28px;padding:5px;border:1px solid transparent;border-radius:6px;background:transparent;color:#aab8c8;cursor:pointer;transition:right .15s ease,background .15s ease,color .15s ease,border-color .15s ease}
.payload-code.has-scroll .copy-button{right:40px}
.copy-button:hover,.copy-button:focus-visible{border-color:#314154;background:#17202b;color:#f2f5f8;outline:none}.copy-button.copied{color:var(--green)}.copy-button svg{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:1.75;stroke-linecap:round;stroke-linejoin:round;vector-effect:non-scaling-stroke}
pre{margin:0;white-space:pre-wrap;word-break:break-word;background:transparent;border:0;padding:12px;border-radius:11px;max-height:420px;overflow:auto;scrollbar-color:#738195 #0b111a;scrollbar-width:thin;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace}
pre::-webkit-scrollbar{width:12px;height:12px}
pre::-webkit-scrollbar-track{background:#0b111a}
pre::-webkit-scrollbar-thumb{background:#738195;border:3px solid #0b111a;border-radius:9px}
pre::-webkit-scrollbar-button{display:none;width:0;height:0}
pre::-webkit-scrollbar-corner{background:#0b111a}
.json-key{color:var(--blue)}.json-string{color:var(--green)}.json-number{color:var(--amber)}.json-boolean{color:var(--purple)}.json-null{color:var(--red)}
.payload-note{color:var(--amber);font-size:12px;padding:10px;margin:0 0 12px;border:1px dashed #6d5728;border-radius:7px}
.empty{padding:28px;text-align:center;color:var(--muted)}
@media(max-width:1100px){.timing-averages{grid-template-columns:repeat(3,minmax(130px,1fr))}}
@media(max-width:900px){.toolbar{grid-template-columns:1fr 1fr}.payload-grid{grid-template-columns:1fr}.timing-averages{grid-template-columns:repeat(2,minmax(130px,1fr))}}
@media(max-width:480px){.timing-averages{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
  <div><h1>PROJECT PULSE REQUESTS</h1><div class="sub">Recent APIM and Service Bus activity</div></div>
  <div class="header-actions">
    <span class="signed-in-user">Signed in as <strong>{{SIGNED_IN_USER}}</strong></span>
    <a class="sign-out" href="/.auth/logout?post_logout_redirect_uri=%2F">Sign out</a>
    <a href="/">Home</a>
  </div>
</header>
<main>
  <div class="toolbar">
    <input id="search" type="search" placeholder="Search ID, status, reason, code..." oninput="render()">
    <select id="destination" onchange="render()">
      <option value="">All destinations</option>
      <option value="APIM">APIM</option>
      <option value="ServiceBus">Service Bus</option>
    </select>
    <button onclick="load()">Refresh</button>
    <label class="refresh-control">Auto refresh
      <select id="refreshInterval" aria-label="Auto refresh interval" onchange="setRefreshRate()">
        <option value="5">5 seconds</option>
        <option value="10">10 seconds</option>
        <option value="30" selected>30 seconds</option>
        <option value="60">60 seconds</option>
        <option value="300">5 minutes</option>
      </select>
    </label>
    <span id="status" class="muted"></span>
  </div>
  <div id="summary" class="summary"></div>
  <section class="timing-summary" aria-label="Average request timings">
    <div class="timing-summary-heading">
      <div class="timing-summary-title">
        <h2 id="timingTitle">Average timings — All time</h2>
        <details class="timing-help">
          <summary aria-label="Explain the average timing metrics" title="What do these averages mean?">
            <svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="9"></circle><path d="M12 11v5"></path><circle cx="12" cy="8" r=".7" fill="currentColor" stroke="none"></circle></svg>
          </summary>
          <div class="timing-help-popover" role="region" aria-label="Average timing explanations">
            <h3>What the timing cards mean</h3>
            <p>Each card averages batches created in the selected time window, not only the 100 requests currently displayed. Its count is the number of batches with a recorded value for that timing.</p>
            <dl>
              <dt>RTA pull</dt><dd>Time for Project Pulse to fetch pending requests from RTA and read/parse the response. RTA processing, network or TLS latency, and larger pages can add time.</dd>
              <dt>Translation</dt><dd>Time to map the RTA item into the outbound DOCMP JSON. App CPU pressure or larger, more complex payloads can increase it.</dd>
              <dt>APIM round trip</dt><dd>Time to send the request through APIM and read the response body. It includes APIM, DOCMP/backend, and network time, so this metric does not separate those parts.</dd>
              <dt>RTA writeback</dt><dd>Time to translate the response and send the status/result PUT to RTA. RTA processing, network latency, and retries can add time.</dd>
              <dt>End-to-end</dt><dd>Elapsed time from the start of the RTA poll to the recorded outcome. It spans multiple steps and waiting between them, so it is not simply the sum of the other averages.</dd>
            </dl>
            <p>When RTA writeback is enabled, its timer includes the PUT calls, retries, and configured retry delays. The summary below counts batches recorded status PUT failures; that count does not prove retries occurred in every case.</p>
            <p>Use the batch's correlation ID to find the writeback warning/error logs, which record the observed HTTP status or exception category and attempt count. Averages alone cannot identify the cause, and APIM round-trip time still combines APIM, DOCMP/backend, and network time.</p>
          </div>
        </details>
      </div>
      <span id="timingUpdated" class="muted">Calculating…</span>
    </div>
    <div class="timing-range-controls">
      <label for="timingRange">Time range</label>
      <select id="timingRange" onchange="onTimingRangeChange()">
        <option value="7d">Last 7 days</option>
        <option value="30d">Last 30 days</option>
        <option value="365d">Last year</option>
        <option value="all" selected>All time</option>
        <option value="custom">Custom</option>
      </select>
      <div id="customTimingRange" class="custom-timing-range" hidden>
        <label for="timingFrom">From</label><input id="timingFrom" type="datetime-local" />
        <label for="timingTo">To</label><input id="timingTo" type="datetime-local" />
        <button type="button" onclick="applyCustomTimingRange()">Apply</button>
      </div>
      <span id="timingRangeError" class="timing-range-error" role="status"></span>
    </div>
    <div id="timingAverages" class="timing-averages"><span class="muted">Calculating averages…</span></div>
    <div id="timingDiagnostics" class="timing-diagnostic-note">Loading diagnostic summary…</div>
    <div class="timing-legend">Starting thresholds: RTA/APIM/writeback green ≤1s, yellow ≤5s, red &gt;5s; translation green ≤100ms, yellow ≤500ms, red &gt;500ms; total green ≤5s, yellow ≤15s, red &gt;15s.</div>
  </section>
  <div class="table-wrap">
    <table>
      <thead><tr>
        <th data-key="UpdatedAtUtc">Updated ↕</th>
        <th data-key="ClaimCount">Claims ↕</th>
        <th data-key="UniqueId">Unique ID ↕</th>
        <th data-key="Status">Status ↕</th>
        <th data-key="Destination">Destination ↕</th>
        <th data-key="HttpStatusCode">HTTP ↕</th>
        <th data-key="BusinessResponseCode">Business Code ↕</th>
        <th data-key="Reason">Reason ↕</th>
      </tr></thead>
      <tbody id="rows"></tbody>
    </table>
  </div>
  <div class="load-more-wrap"><button id="loadMoreButton" onclick="loadMore()" hidden>Load more</button></div>
</main>
<script>
let items=[]; let sortKey='UpdatedAtUtc'; let sortDir='desc'; let expandedRecordId=null; let refreshTimer=null; let nextPageToken=null; let loading=false; let averagesLoading=false; let averagesRequestId=0; const detailCache=new Map();

function durationLabel(value){
  if(value===null||value===undefined)return 'N/A';
  const ms=Math.max(0,Number(value));
  return ms>=1000?(ms/1000).toFixed(2)+' s':Math.round(ms)+' ms';
}
function groupByBatch(source){
  const groups=new Map();
  for(const item of source){
    const key=item.BatchId||item.RecordId;
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(item);
  }
  return Array.from(groups.values()).map(group=>{
    const representative=group.slice().sort((a,b)=>Date.parse(b.UpdatedAtUtc||0)-Date.parse(a.UpdatedAtUtc||0))[0];
    const uniqueIds=[...new Set(group.map(x=>x.UniqueId).filter(Boolean))];
    return {...representative,ClaimCount:group.length,UniqueId:uniqueIds.length===1?uniqueIds[0]:uniqueIds.join(', '),RecordId:representative.RecordId};
  });
}
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function syntaxHighlight(value){
  if(value===null||value===undefined||value==='') return '<span class="muted">No payload recorded</span>';
  let text=String(value);
  try{text=JSON.stringify(JSON.parse(text),null,2)}catch{}
  return esc(text).replace(/(&quot;.*?&quot;)(\s*:)?|\b(true|false)\b|\b(null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b/g,(m,str,colon,bool,nul)=>{
    if(str) return '<span class="'+(colon?'json-key':'json-string')+'">'+str+'</span>'+(colon||'');
    if(bool) return '<span class="json-boolean">'+bool+'</span>';
    if(nul) return '<span class="json-null">'+nul+'</span>';
    return '<span class="json-number">'+m+'</span>';
  });
}
function syncCopyButtonOffsets(root=document){
  root.querySelectorAll('.payload-code.has-copy').forEach(code=>{
    const pre=code.querySelector('pre');
    code.classList.toggle('has-scroll',!!pre&&pre.scrollHeight>pre.clientHeight+1);
  });
}
function payloadPanel(title,payload){
  const hasPayload=payload!==null&&payload!==undefined&&payload!=='';
  const copyButton=hasPayload
    ? '<button type="button" class="copy-button" aria-label="Copy JSON" title="Copy JSON" onclick="event.stopPropagation();copyPayload(this)"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="8" y="8" width="12" height="12" rx="2"></rect><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"></path></svg></button>'
    : '';
  return '<div class="payload-panel"><div class="payload-title">'+esc(title)+'</div><div class="payload-code'+(hasPayload?' has-copy':'')+'">'+copyButton+'<pre>'+syntaxHighlight(payload)+'</pre></div></div>';
}
async function copyPayload(button){
  const pre=button.closest('.payload-panel')?.querySelector('pre');
  const text=pre?.textContent||'';
  if(!text.trim()||text.trim()==='No payload recorded')return;
  try{
    if(navigator.clipboard&&window.isSecureContext){
      await navigator.clipboard.writeText(text);
    }else{
      const temporary=document.createElement('textarea');
      temporary.value=text;
      temporary.style.position='fixed';
      temporary.style.opacity='0';
      document.body.appendChild(temporary);
      temporary.select();
      const copied=document.execCommand('copy');
      temporary.remove();
      if(!copied)throw new Error('Clipboard copy failed');
    }
    button.classList.add('copied');
    button.title='Copied';
    button.setAttribute('aria-label','Copied');
    setTimeout(()=>{button.classList.remove('copied');button.title='Copy JSON';button.setAttribute('aria-label','Copy JSON')},1200);
  }catch{
    button.title='Copy failed';
    button.setAttribute('aria-label','Copy failed');
  }
}
function compare(a,b){
  let av=a?.[sortKey], bv=b?.[sortKey];
  if(sortKey==='UpdatedAtUtc'){av=Date.parse(av||0);bv=Date.parse(bv||0)}
  else if(sortKey==='HttpStatusCode'||sortKey==='ClaimCount'){av=Number(av??-1);bv=Number(bv??-1)}
  else{av=String(av??'').toLowerCase();bv=String(bv??'').toLowerCase()}
  if(av<bv)return sortDir==='asc'?-1:1;if(av>bv)return sortDir==='asc'?1:-1;return 0;
}
function filtered(){
  const q=document.getElementById('search').value.trim().toLowerCase();
  const dest=document.getElementById('destination').value;
  return groupByBatch(items).filter(x=>(!dest||x.Destination===dest)&&(!q||[x.UniqueId,x.Status,x.Destination,x.HttpStatusCode,x.BusinessResponseCode,x.Reason,x.BatchId,x.ClaimCount].some(v=>String(v??'').toLowerCase().includes(q)))).sort(compare);
}
function renderSummary(list){
  const apim=list.filter(x=>x.Destination==='APIM').length, sb=list.filter(x=>x.Destination==='ServiceBus').length;
  const completed=list.filter(x=>x.Status==='Completed').length, buffered=list.filter(x=>x.Status==='Buffered').length;
  const rejected=list.filter(x=>x.Status==='Rejected').length;
  document.getElementById('summary').innerHTML=
    '<span class="pill">'+list.length+' batch(es) visible</span>'+
    '<span class="pill">'+apim+' APIM</span>'+
    '<span class="pill">'+sb+' Service Bus</span>'+
    '<span class="pill">'+completed+' completed</span>'+
    '<span class="pill">'+buffered+' buffered</span>'+
    '<span class="pill">'+rejected+' rejected</span>';
}
function render(){
  const list=filtered(); renderSummary(list);
  const tbody=document.getElementById('rows');
  if(!list.length){expandedRecordId=null;tbody.innerHTML='<tr><td colspan="8" class="empty">No matching requests</td></tr>';return}
  tbody.innerHTML=list.map((x,i)=>{
    const key='row-'+i+'-'+esc(x.RecordId);
    return '<tr class="data-row" onclick="toggleDetail(\''+key+'\',\''+esc(x.RecordId)+'\')">'+
      '<td>'+new Date(x.UpdatedAtUtc).toLocaleString()+'</td>'+
      '<td>'+esc(x.ClaimCount)+'</td>'+
      '<td class="id" title="'+esc(x.UniqueId)+'">'+esc(x.UniqueId)+'</td>'+
      '<td class="status-'+esc(x.Status)+'">'+esc(x.Status)+'</td>'+
      '<td><span class="tag '+esc(x.Destination)+'">'+esc(x.Destination)+'</span></td>'+
      '<td>'+esc(x.HttpStatusCode??'')+'</td>'+
      '<td>'+esc(x.BusinessResponseCode??'')+'</td>'+
      '<td>'+esc(x.Reason??'')+'</td></tr>'+
      '<tr id="'+key+'" class="expand-row" data-record-id="'+esc(x.RecordId)+'"><td colspan="8" class="expand-cell"><div class="muted">Loading details...</div></td></tr>';
  }).join('');
  if(expandedRecordId){
    const expandedRow=Array.from(tbody.querySelectorAll('.expand-row')).find(row=>row.dataset.recordId===expandedRecordId);
    if(expandedRow){
      expandedRow.classList.add('open');
      void renderDetail(expandedRow,expandedRecordId);
    }else{
      expandedRecordId=null;
    }
  }
}
function writebackReasonText(reason){
  const messages={
    EmptyApimResponse:'APIM returned an empty response body.',
    InvalidApimResponseShape:'APIM response was not the expected JSON array.',
    ApimResponseCountMismatch:'APIM returned a different number of results than the submitted claim count.',
    MissingCorrelationId:'At least one APIM response item did not contain a correlation ID.',
    UnexpectedCorrelationId:'APIM returned a correlation ID that was not part of the submitted batch.',
    DuplicateCorrelationId:'APIM returned the same correlation ID more than once in the batch.',
    CorrelationIdSetMismatch:'The returned correlation-ID set did not exactly match the submitted batch.',
    RtaStatusWritebackFailed:'The RTA status PUT failed after APIM response validation succeeded. Project Pulse logs record each attempt and its observed HTTP status or exception, keyed by correlation ID.',
    ResultWritebackFailed:'RTA result writeback failed. This older record did not capture a more specific cause.'
  };
  return messages[reason]||('Writeback failed: '+(reason||'Unknown reason'));
}
function exchangeStep(name,state,body,tone){
  return '<div class="exchange-step '+tone+'"><div class="exchange-step-heading"><span class="exchange-step-name">'+esc(name)+'</span><span class="exchange-step-state">'+esc(state)+'</span></div><div class="exchange-step-body">'+body+'</div></div>';
}
function renderExchangeOutcomes(x){
  const source=x.InputSource==='MOCK'?'Mock API':'RTA';
  const pullTime=x.RtaPullDurationMs==null?'':' · '+durationLabel(x.RtaPullDurationMs);
  const pull=exchangeStep(source+' GET','Received','Request record reached Project Pulse polling. Per-poll HTTP status is not stored in request history.'+esc(pullTime),'success');

  const apimSent=!!x.SentToApimAtUtc;
  const tokenRejected=x.Status==='Rejected'&&x.Reason==='InvalidApimResponseToken';
  let apimState='Not sent',apimTone='neutral',apimBody='No APIM request is recorded.';
  if(apimSent){
    const code=x.HttpStatusCode;
    const hasResponse=!!x.ApimResponseAtUtc||code!=null;
    if(tokenRejected){apimState='Rejected';apimTone='error'}
    else if(code!=null&&code>=200&&code<300){apimState='Success';apimTone='success'}
    else if(code!=null){apimState='HTTP failure';apimTone='error'}
    else if(hasResponse){apimState='Response received';apimTone='warning'}
    else {apimState='Waiting / no response';apimTone='warning'}
    apimBody='POST'+(code==null?'':' · HTTP '+esc(code))+
      (x.BusinessResponseCode?' · business code '+esc(x.BusinessResponseCode):'')+
      (tokenRejected?' · response-token validation failed.':x.Reason&&x.Status!=='Completed'&&x.Status!=='WritebackFailed'?' · '+esc(x.Reason):'');
  }
  const apim=exchangeStep('APIM / DOCMP POST',apimState,apimBody,apimTone);

  let writeState='No outcome recorded',writeTone='neutral',writeBody='RTA writeback details were not captured for this record.';
  if(x.RtaWritebackEnabled===false){
    writeState='Disabled';writeTone='neutral';writeBody='RTA status PUT was disabled by configuration.';
  }else if(x.RtaWritebackSucceeded===true){
    writeState='Success';writeTone='success';
    writeBody='PUT succeeded'+(x.RtaWritebackHttpStatusCode==null?'':' · HTTP '+esc(x.RtaWritebackHttpStatusCode))+
      (x.RtaWritebackAttempts==null?'':' · '+esc(x.RtaWritebackAttempts)+' attempt(s)')+'.';
  }else if(x.RtaWritebackSucceeded===false){
    writeState='Failed';writeTone='error';
    writeBody='PUT failed'+(x.RtaWritebackHttpStatusCode==null?'':' · HTTP '+esc(x.RtaWritebackHttpStatusCode))+
      (x.RtaWritebackAttempts==null?'':' · '+esc(x.RtaWritebackAttempts)+' attempt(s)')+
      (x.RtaWritebackFailureKind?' · '+esc(x.RtaWritebackFailureKind):'')+
      (x.RtaWritebackFailureMessage?'<br>'+esc(x.RtaWritebackFailureMessage):'');
  }else if(tokenRejected||(x.HttpStatusCode!=null&&(x.HttpStatusCode<200||x.HttpStatusCode>=300))){
    writeState='Not attempted';writeTone='neutral';writeBody='The APIM response did not proceed to RTA writeback.';
  }else if(x.RtaWritebackEnabled===true){
    writeState='Pending / not recorded';writeTone='warning';writeBody='No RTA PUT outcome is stored yet. Older records do not include attempt-level details.';
  }
  const writeback=exchangeStep('RTA status PUT',writeState,writeBody,writeTone);
  return '<section class="exchange-outcomes"><h3 class="exchange-title">Exchange outcomes</h3><div class="exchange-grid">'+pull+apim+writeback+'</div><div class="exchange-note">HTTP codes and retry details are shown when Project Pulse stored them. Exception text is the final failure message; full stack traces remain in the log stream.</div></section>';
}
async function toggleDetail(rowId,recordId){
  const row=document.getElementById(rowId); const opening=!row.classList.contains('open');
  document.querySelectorAll('.expand-row.open').forEach(r=>r.classList.remove('open'));
  if(!opening){if(expandedRecordId===recordId)expandedRecordId=null;return}
  expandedRecordId=recordId;
  row.classList.add('open');
  await renderDetail(row,recordId);
}
async function renderDetail(row,recordId){
  let x=detailCache.get(recordId);
  if(!x){
    row.querySelector('td').innerHTML='<div class="muted">Loading details...</div>';
    const r=await fetch('/requests/'+encodeURIComponent(recordId),{cache:'no-store'});
    if(!r.ok){row.querySelector('td').innerHTML='<div class="payload-note">Could not load request details.</div>';return}
    x=await r.json();detailCache.set(recordId,x);
  }
  const validationFailure=x.Status==='Rejected'&&x.Reason==='InvalidApimResponseToken'
    ? '<div class="payload-note"><strong>APIM response rejected:</strong> response-token validation failed. The response was not processed and no RTA writeback was attempted.</div>'
    : '';
  const writebackFailure=x.Status==='WritebackFailed'
    ? '<div class="payload-note"><strong>Writeback failed:</strong> '+esc(writebackReasonText(x.Reason))+'</div>'
    : '';
  const rtaPayloads=x.RtaResponsePayloads||[];
  const rtaPayloadPanels=rtaPayloads.length
    ? rtaPayloads.map((payload,index)=>payloadPanel('Raw JSON received from RTA'+(rtaPayloads.length>1?' — page '+(index+1):''),payload)).join('')
    : payloadPanel('Raw JSON received from RTA',null);
  const rtaStatusPayloadPanel=payloadPanel('JSON sent back to RTA',x.RtaStatusPayload);
  row.querySelector('td').innerHTML=
    validationFailure+
    writebackFailure+
    '<div class="detail-grid">'+
      '<div class="card"><div class="label">Batch ID</div><div class="id">'+esc(x.BatchId)+'</div></div>'+
      '<div class="card"><div class="label">Claims in Batch</div>'+esc(groupByBatch(items).find(b=>b.BatchId===x.BatchId)?.ClaimCount??1)+'</div>'+
      '<div class="card"><div class="label">Sent to APIM</div>'+(x.SentToApimAtUtc?new Date(x.SentToApimAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">APIM Response</div>'+(x.ApimResponseAtUtc?new Date(x.ApimResponseAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">Service Bus</div>'+(x.BufferedToServiceBusAtUtc?new Date(x.BufferedToServiceBusAtUtc).toLocaleString():'N/A')+'</div>'+
    '</div>'+
    '<div class="timing-grid">'+
      '<div class="card"><div class="label">RTA Pull</div>'+durationLabel(x.RtaPullDurationMs)+'</div>'+
      '<div class="card"><div class="label">Translation</div>'+durationLabel(x.TranslationDurationMs)+'</div>'+
      '<div class="card"><div class="label">DOCMP Round Trip</div>'+durationLabel(x.ApimRoundTripDurationMs)+'</div>'+
      '<div class="card"><div class="label">RTA Writeback</div>'+durationLabel(x.RtaWritebackDurationMs)+'</div>'+
      '<div class="card"><div class="label">Total</div>'+durationLabel(x.EndToEndDurationMs)+'</div>'+
    '</div>'+
    renderExchangeOutcomes(x)+
    (x.PayloadLoggingEnabled
      ? (x.PayloadMode==='PARTIAL' ? '<div class="payload-note">Partial payload mode: common identifiers and personal-name fields are masked.</div>' : '')+
        '<div class="payload-grid">'+rtaPayloadPanels+rtaStatusPayloadPanel+payloadPanel('JSON sent to APIM',x.ApimRequestPayload)+payloadPanel('JSON received from APIM',x.ApimResponsePayload)+'</div>'
      : '<div class="payload-note">Payload capture and display are disabled because REQUEST_HISTORY_PAYLOAD_MODE=OFF.</div>');
  requestAnimationFrame(()=>syncCopyButtonOffsets(row));
}
window.addEventListener('resize',()=>syncCopyButtonOffsets());
const timingThresholds={RtaPull:[1000,5000],Translation:[100,500],ApimRoundTrip:[1000,5000],RtaWriteback:[1000,5000],EndToEnd:[5000,15000]};
function timingTone(key,averageMs){
  if(averageMs===null||averageMs===undefined)return '';
  const [green,yellow]=timingThresholds[key];
  return averageMs<=green?'green':averageMs<=yellow?'yellow':'red';
}
function timingLabel(key){
  return {RtaPull:'RTA pull',Translation:'Translation',ApimRoundTrip:'APIM round trip',RtaWriteback:'RTA writeback',EndToEnd:'End-to-end'}[key];
}
function formatAverage(ms){
  if(ms===null||ms===undefined)return 'N/A';
  return ms>=1000?(ms/1000).toFixed(2)+' s':Math.round(ms)+' ms';
}
function setTimingTitle(label){
  document.getElementById('timingTitle').textContent='Average timings — '+label;
}
function onTimingRangeChange(){
  const range=document.getElementById('timingRange').value;
  const custom=document.getElementById('customTimingRange');
  const error=document.getElementById('timingRangeError');
  error.textContent='';
  custom.hidden=range!=='custom';
  if(range==='custom'){
    setTimingTitle('Custom range');
    return;
  }
  const label={all:'All time','7d':'Last 7 days','30d':'Last 30 days','365d':'Last year'}[range];
  setTimingTitle(label);
  void loadAverages();
}
function applyCustomTimingRange(){
  const fromValue=document.getElementById('timingFrom').value;
  const toValue=document.getElementById('timingTo').value;
  const error=document.getElementById('timingRangeError');
  if(!fromValue||!toValue){
    error.textContent='Choose both a start and end date/time.';
    return;
  }
  const from=new Date(fromValue),to=new Date(toValue);
  if(Number.isNaN(from.getTime())||Number.isNaN(to.getTime())||from>to){
    error.textContent='The start date/time must be before the end date/time.';
    return;
  }
  error.textContent='';
  setTimingTitle(from.toLocaleString()+' – '+to.toLocaleString());
  void loadAverages();
}
function timingAveragesUrl(){
  const range=document.getElementById('timingRange').value;
  if(range!=='custom')return '/requests/averages?range='+encodeURIComponent(range);
  const from=document.getElementById('timingFrom').value;
  const to=document.getElementById('timingTo').value;
  if(!from||!to)return null;
  const fromDate=new Date(from),toDate=new Date(to);
  if(Number.isNaN(fromDate.getTime())||Number.isNaN(toDate.getTime())||fromDate>toDate)return null;
  const query=new URLSearchParams({range:'custom',fromUtc:fromDate.toISOString(),toUtc:toDate.toISOString()});
  return '/requests/averages?'+query.toString();
}
async function loadAverages(){
  const url=timingAveragesUrl();
  if(!url){
    document.getElementById('timingAverages').innerHTML='<span class="muted">Choose a valid custom date/time range to calculate averages.</span>';
    document.getElementById('timingDiagnostics').textContent='Select both dates and apply the custom range.';
    document.getElementById('timingUpdated').textContent='Waiting for range';
    return;
  }
  const requestId=++averagesRequestId;
  averagesLoading=true;
  const container=document.getElementById('timingAverages');
  const updated=document.getElementById('timingUpdated');
  try{
    const response=await fetch(url,{cache:'no-store'});
    if(!response.ok)throw new Error('HTTP '+response.status);
    const averages=await response.json();
    if(requestId!==averagesRequestId)return;
    const keys=['RtaPull','Translation','ApimRoundTrip','RtaWriteback','EndToEnd'];
    container.innerHTML=keys.map(key=>{
      const metric=averages[key]||{};
      const count=Number(metric.SampleCount||0);
      return '<div class="timing-average-card '+timingTone(key,metric.AverageMs)+'">'+
        '<div class="timing-average-label">'+timingLabel(key)+'</div>'+
        '<div class="timing-average-value">'+formatAverage(metric.AverageMs)+'</div>'+
        '<div class="timing-average-count">'+count.toLocaleString()+' batch'+(count===1?'':'es')+'</div>'+
      '</div>';
    }).join('');
    const failedCount=Number(averages.RtaStatusPutFailureBatchCount||0);
    document.getElementById('timingDiagnostics').textContent=failedCount
      ? failedCount.toLocaleString()+' batch'+(failedCount===1?'':'es')+' in this time window ended with an RTA status PUT failure. The writeback average can include retries and backoff; correlate the batch ID in Project Pulse logs to see observed statuses or exceptions.'
      : 'No batches in this time window are currently marked WritebackFailed. A high writeback average can still include slow successful PUTs or retries that eventually succeeded.';
    updated.textContent='Calculated '+new Date(averages.CalculatedAtUtc).toLocaleTimeString();
  }catch(error){
    if(requestId!==averagesRequestId)return;
    container.innerHTML='<span class="muted">Could not load averages: '+esc(error.message)+'</span>';
    document.getElementById('timingDiagnostics').textContent='Diagnostic summary unavailable.';
    updated.textContent='Unavailable';
  }finally{
    if(requestId===averagesRequestId)averagesLoading=false;
  }
}
async function load(){
  if(loading)return; loading=true;
  void loadAverages();
  const status=document.getElementById('status');
  status.textContent='Loading recent activity...'; detailCache.clear(); nextPageToken=null;
  try{
    const r=await fetch('/requests?limit=100',{cache:'no-store'});
    if(!r.ok)throw new Error('Request history returned HTTP '+r.status);
    items=await r.json(); nextPageToken=r.headers.get('X-Next-Page-Token');
    status.textContent=groupByBatch(items).length+' recent batch(es), '+items.length+' claim record(s) loaded';
    render();
  }catch(error){status.textContent='Could not load request history: '+error.message}
  finally{loading=false;document.getElementById('loadMoreButton').hidden=!nextPageToken}
}
async function loadMore(){
  if(loading||!nextPageToken)return; loading=true;
  const button=document.getElementById('loadMoreButton'); button.disabled=true; button.textContent='Loading...';
  try{
    const r=await fetch('/requests?limit=100&pageToken='+encodeURIComponent(nextPageToken),{cache:'no-store'});
    if(!r.ok)throw new Error('Request history returned HTTP '+r.status);
    const more=await r.json(); items=items.concat(more); nextPageToken=r.headers.get('X-Next-Page-Token');
    document.getElementById('status').textContent=groupByBatch(items).length+' batch(es), '+items.length+' claim record(s) loaded';
    render();
  }catch(error){document.getElementById('status').textContent='Could not load more history: '+error.message}
  finally{loading=false;button.disabled=false;button.textContent='Load more';button.hidden=!nextPageToken}
}
document.querySelectorAll('th[data-key]').forEach(th=>th.addEventListener('click',()=>{const k=th.dataset.key;if(sortKey===k)sortDir=sortDir==='asc'?'desc':'asc';else{sortKey=k;sortDir='asc'}render()}));
function setRefreshRate(){
  const seconds=Number(document.getElementById('refreshInterval').value);
  if(refreshTimer)clearInterval(refreshTimer);
  refreshTimer=setInterval(load,seconds*1000);
}
load(); setRefreshRate();
</script>
</body>
</html>
""";
}

