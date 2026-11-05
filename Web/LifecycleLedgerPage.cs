using System.Net;

namespace ProjectPulse.Processor.Web;

public static class LifecycleLedgerPage
{
    public static string HtmlFor(string? signedInUser, string? buildVersion, string? roleValue)
    {
        var displayName = string.IsNullOrWhiteSpace(signedInUser) ? "Signed-in user" : signedInUser;
        var version = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(buildVersion) ? "unknown" : buildVersion);
        var role = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(roleValue) ? "UNKNOWN" : roleValue);

        return Html
            .Replace("{{SIGNED_IN_USER}}", WebUtility.HtmlEncode(displayName), StringComparison.Ordinal)
            .Replace("{{BUILD_VERSION}}", version, StringComparison.Ordinal)
            .Replace("{{ROLE}}", role, StringComparison.Ordinal);
    }

    public static string Html => """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse Lifecycle Ledger</title>
<style>
:root{color-scheme:dark;--bg:#05070b;--panel:#0b111a;--panel2:#0f1722;--border:#1c2a3a;--text:#e5eefc;--muted:#8295ad;--cyan:#19ddff;--green:#54e39b;--amber:#ffc857;--red:#ff627d;--blue:#68a4ff}
*{box-sizing:border-box}
body{margin:0;background:linear-gradient(180deg,#05070b,#07101a 55%,#05070b);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{position:sticky;top:0;z-index:5;padding:16px 22px;border-bottom:1px solid var(--border);background:rgba(5,7,11,.94);display:flex;justify-content:space-between;align-items:center;gap:16px}
h1{font-size:18px;letter-spacing:.08em;margin:0}.sub{font-size:12px;color:var(--muted);margin-top:5px}
a{color:var(--cyan);text-decoration:none}
.actions{display:flex;gap:16px;align-items:center;flex-wrap:wrap}
.signed-in-user{color:var(--muted);font-size:12px}
.signed-in-user strong{color:var(--text);font-weight:600}
.build-version{display:inline-flex;align-items:center;padding:6px 9px;border:1px solid rgba(25,221,255,.35);border-radius:999px;background:rgba(25,221,255,.08);color:var(--cyan);font-size:11px;font-weight:700;letter-spacing:.04em;white-space:nowrap}
main{max-width:1800px;padding:20px 22px 48px;margin:auto}
.note,.toolbar,.summary,.record{border:1px solid var(--border);background:var(--panel);border-radius:10px}
.note{padding:12px 14px;color:var(--muted);font-size:13px;margin-bottom:14px}
.toolbar{padding:12px;display:grid;grid-template-columns:minmax(220px,1fr) repeat(4,minmax(130px,auto)) auto;gap:9px;align-items:center}
input,select,button{background:#09111b;border:1px solid #2b4056;border-radius:7px;color:var(--text);padding:9px 10px;font:inherit}
button{cursor:pointer}button:hover,a.button:hover{border-color:var(--cyan)}
#count{color:var(--muted);font-size:13px;padding:12px 2px}
.records{display:grid;gap:10px}
.record{overflow:hidden}
.record-head{display:grid;grid-template-columns:180px 1.2fr 1fr 140px 120px 130px;gap:12px;padding:13px 15px;align-items:center}
.record-head strong{font-size:13px;overflow-wrap:anywhere}
.muted{color:var(--muted);font-size:12px}
.status{display:inline-block;border:1px solid #29415a;border-radius:999px;padding:4px 8px;font-size:11px}
.status.good{color:var(--green);border-color:#18553c}.status.warn{color:var(--amber);border-color:#66501d}.status.bad{color:var(--red);border-color:#632b38}
.record details{border-top:1px solid var(--border)}
.record summary{cursor:pointer;padding:10px 15px;color:var(--cyan);font-size:12px}
.timeline{padding:0 15px 16px;display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:9px}
.step{min-height:100px;padding:11px;border:1px solid var(--border);border-radius:8px;background:#080f18}
.step h3{font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);margin:0 0 8px}
.step .outcome{font-size:13px;font-weight:600;margin-bottom:6px}
.step p{font-size:12px;line-height:1.45;color:var(--muted);margin:0;overflow-wrap:anywhere}
.step.ok{border-color:#18553c}.step.wait{border-color:#66501d}.step.fail{border-color:#632b38}
.missing{margin:0 15px 14px;border:1px solid #66501d;border-radius:8px;padding:10px 12px;color:var(--amber);font-size:12px}
.empty,.error{padding:28px;text-align:center;color:var(--muted);border:1px dashed var(--border);border-radius:8px}
.footer{display:flex;justify-content:center;padding-top:14px}
@media(max-width:950px){.toolbar{grid-template-columns:1fr 1fr}.record-head{grid-template-columns:1fr 1fr}.actions{gap:9px}}
</style>
</head>
<body>
<header>
  <div><h1>PROJECT PULSE LIFECYCLE LEDGER</h1><div class="sub">Recorded milestones for each processing attempt</div></div>
  <div class="actions">
    <span class="build-version" title="Version baked into this container image">BUILD v{{BUILD_VERSION}} · {{ROLE}}</span>
    <span class="signed-in-user">Signed in as <strong>{{SIGNED_IN_USER}}</strong></span>
    <a href="/requests.html">Request history</a><a href="/batches.html">Batch reconciliation</a><a href="/">Home</a><a href="/.auth/logout?post_logout_redirect_uri=%2F">Sign out</a>
  </div>
</header>
<main>
  <div class="note">This view uses the milestones saved in request history. A missing later milestone means Project Pulse has no outcome recorded for that stage; it does not prove what APIM, Service Bus, or RTA did afterward. Payloads are not shown here.</div>
  <section class="toolbar" aria-label="Ledger filters">
    <input id="search" type="search" placeholder="Search correlation ID, RTA request ID, or batch ID">
    <select id="range" aria-label="Time range"><option value="5m">Last 5 minutes</option><option value="1h">Last hour</option><option value="24h" selected>Last 24 hours</option><option value="7d">Last week</option><option value="all">All time</option></select>
    <select id="status" aria-label="Status"><option value="">All statuses</option><option>SentToApim</option><option>Buffered</option><option>Completed</option><option>Alerted</option><option>WritebackFailed</option><option>BufferFailed</option><option>Rejected</option></select>
    <select id="role" aria-label="Processing role"><option value="">All roles</option><option value="RTA">RTA Poller</option><option value="SERVICEBUS">Service Bus Worker</option></select>
    <select id="destination" aria-label="Destination"><option value="">All destinations</option><option value="APIM">APIM</option><option value="ServiceBus">Service Bus</option></select>
    <button id="refresh" type="button">Search</button>
  </section>
  <div id="count" role="status" aria-live="polite">Loading ledger…</div>
  <section id="records" class="records" aria-live="polite"></section>
  <div class="footer"><button id="loadMore" type="button" hidden>Load more</button></div>
</main>
<script>
const pageSize=200;
let items=[],nextToken=null,fromUtc=null,totalCount=0,searchTimer=null;
const $=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function stamp(value){if(!value)return 'Not recorded';const d=new Date(value);return Number.isNaN(d.getTime())?'Not recorded':d.toLocaleString()}
function stateClass(ok,waiting=false){return ok?'ok':waiting?'wait':'fail'}
function roleName(value){return value==='SERVICEBUS'?'Service Bus Worker':value==='RTA'?'RTA Poller':value||'Unknown'}
function statusClass(value){if(['Completed'].includes(value))return 'good';if(['SentToApim','Buffered'].includes(value))return 'warn';return 'bad'}
function step(title,outcome,detail,kind=''){return '<article class="step '+kind+'"><h3>'+esc(title)+'</h3><div class="outcome">'+esc(outcome)+'</div><p>'+esc(detail)+'</p></article>'}
function apimAuthEvidence(x){
  const sent=x.ApimBearerTokenAttached===true?'🔑 Bearer token attached':x.ApimBearerTokenAttached===false?'🔑❌ No bearer token attached':'';
  if(x.ApimAuthConfirmed===true)return {sent,response:'🔑 APIM confirmed Entra token validation'};
  if(x.ApimAuthConfirmed===false)return {sent,response:'🔑❌ APIM did not return the auth-confirmation header'};
  if(x.ApimBearerTokenAttached===true)return {sent,response:'🔑 Waiting for APIM auth confirmation'};
  if(x.ApimBearerTokenAttached===false)return {sent,response:'APIM auth confirmation not recorded'};
  return {sent,response:''};
}
function timeline(x){
  const source=String(x.InputSource||'').toUpperCase();
  const sourceLabel=source==='MOCK'?'MOCK API':source==='RTA'?'RTA':'SOURCE';
  const pull=x.RtaPullDurationMs;
  const steps=[];
  steps.push(step(
    'RECEIVED FROM '+sourceLabel,
    pull!=null?'GET completed':'No GET timing recorded',
    pull!=null?(sourceLabel==='SOURCE'?'Pull took ':'GET took ')+pull+' ms':'No poll duration is stored for this record.',
    pull!=null?'ok':'wait'));
  const sent=x.SentToApimAtUtc;
  const apimAt=x.ApimResponseAtUtc;
  const postOutcome=apimAt?'POST sent':sent?'Attempt logged':'No POST recorded';
  const authEvidence=apimAuthEvidence(x);
  const postDetail=(apimAt
    ?'Started '+stamp(sent||apimAt)
    :sent?'Attempt logged '+stamp(sent)+' · no APIM response recorded':'No APIM POST attempt is stored.')+
    (authEvidence.sent?' · '+authEvidence.sent:'');
  steps.push(step(
    'SENT TO APIM / DOCMP',
    postOutcome,
    postDetail,
    apimAt?'ok':sent?'wait':'wait'));
  const apimDetails=[];
  if(apimAt)apimDetails.push('Returned '+stamp(apimAt));
  if(x.ApimRoundTripDurationMs!=null)apimDetails.push('APIM round trip '+x.ApimRoundTripDurationMs+' ms');
  if(x.BusinessResponseCode&&x.BusinessResponseCode!=='BatchCompleted')apimDetails.push(x.BusinessResponseCode);
  if(x.Reason&&x.Reason!=='BatchCompleted'&&!apimDetails.includes(x.Reason))apimDetails.push(x.Reason);
  if(apimAt&&authEvidence.response)apimDetails.push(authEvidence.response);
  const businessFailure=String(x.Reason||'').startsWith('ApimJsonError:');
  const apimKind=!apimAt?'wait':(x.HttpStatusCode>=400||businessFailure?'fail':x.ApimAuthConfirmed===false?'wait':'ok');
  steps.push(step(
    'RETURNED FROM APIM',
    apimAt?(x.HttpStatusCode!=null?'HTTP '+x.HttpStatusCode:'Response received'):'No response recorded',
    apimAt?(apimDetails.join(' · ')||'Response timestamp recorded.'):'No APIM response time or status is stored.'+(authEvidence.response?' · '+authEvidence.response:''),
    apimKind));
  const sbAt=x.BufferedToServiceBusAtUtc;
  steps.push(step(
    'SERVICE BUS',
    sbAt?'Enqueued':'No event recorded',
    sbAt?'Enqueued '+stamp(sbAt):'No Service Bus enqueue time was stored for this run.',
    sbAt?'ok':'wait'));
  let wbOutcome='Not recorded',wbKind='wait';
  const wbDetails=[];
  if(x.RtaWritebackEnabled===false){
    wbOutcome='Disabled';
    wbDetails.push('RTA status writeback was disabled.');
  }else if(x.RtaWritebackAttempted===true){
    wbOutcome=x.RtaWritebackSucceeded?'Succeeded':'Failed';
    if(x.RtaWritebackHttpStatusCode!=null)wbDetails.push('HTTP '+x.RtaWritebackHttpStatusCode);
    if(x.RtaWritebackAttempts!=null)wbDetails.push(x.RtaWritebackAttempts+' attempt(s)');
    if(x.RtaWritebackDurationMs!=null)wbDetails.push('Writeback took '+x.RtaWritebackDurationMs+' ms');
    wbDetails.push(x.RtaWritebackCompletedAtUtc?'Completed '+stamp(x.RtaWritebackCompletedAtUtc):'Completion time unavailable for this record');
    if(x.RtaWritebackFailureKind)wbDetails.push(x.RtaWritebackFailureKind);
    wbKind=x.RtaWritebackSucceeded?'ok':'fail';
  }else{
    wbDetails.push('No RTA writeback outcome is stored.');
  }
  steps.push(step('SENT BACK TO RTA',wbOutcome,wbDetails.join(' · '),wbKind));
  return steps.join('');
}
function render(){
  const host=$('records');
  if(!items.length){host.innerHTML='<div class="empty">No ledger records match these filters.</div>';return}
  host.innerHTML=items.map(x=>{
    const pending=x.Status==='SentToApim'&&!x.ApimResponseAtUtc;
    const callout=pending?'<div class="missing">Project Pulse recorded an APIM POST attempt at '+esc(stamp(x.SentToApimAtUtc))+', but no response was stored. This does not confirm APIM received the request.</div>':'';
    return '<article class="record">'+
      '<div class="record-head"><div>'+esc(stamp(x.UpdatedAtUtc))+'<div class="muted">Updated</div></div>'+
      '<div><strong>'+esc(x.UniqueId)+'</strong><div class="muted">Correlation / unique ID</div></div>'+
      '<div><strong>'+esc(x.RtaRequestId||'Not recorded')+'</strong><div class="muted">RTA request ID</div></div>'+
      '<div><span class="status '+statusClass(x.Status)+'">'+esc(x.Status)+'</span></div>'+
      '<div>'+esc(roleName(x.ProcessingRole))+'<div class="muted">Processed by</div></div>'+
      '<div>'+esc(x.HttpStatusCode??'—')+'<div class="muted">HTTP</div></div></div>'+
      callout+
      '<details><summary>Show lifecycle · Batch '+esc(x.BatchId)+'</summary><div class="timeline">'+timeline(x)+'</div></details>'+
      '</article>';
  }).join('');
}
function queryUrl(token){
  const q=new URLSearchParams({limit:String(pageSize),range:$('range').value});
  const search=$('search').value.trim(),status=$('status').value,role=$('role').value,destination=$('destination').value;
  if(search)q.set('search',search);
  if(status)q.set('status',status);
  if(role)q.set('role',role);
  if(destination)q.set('destination',destination);
  if(token)q.set('pageToken',token);
  if(token&&fromUtc)q.set('fromUtc',fromUtc);
  return '/requests?'+q.toString();
}
async function load(reset=true){
  if(reset){items=[];nextToken=null;fromUtc=null;totalCount=0}
  $('refresh').disabled=true;$('count').textContent='Loading…';
  try{
    const response=await fetch(queryUrl(reset?null:nextToken),{cache:'no-store'});
    if(!response.ok)throw new Error('History request returned HTTP '+response.status);
    const page=await response.json();
    items=reset?page:items.concat(page);
    nextToken=response.headers.get('X-Next-Page-Token');
    if(reset){totalCount=Number(response.headers.get('X-Total-Count')||page.length);fromUtc=response.headers.get('X-Range-Start-Utc')}
    $('count').textContent='Showing '+items.length+' of '+totalCount+' matching attempt(s)'+(nextToken?' · more available':'');
    $('loadMore').hidden=!nextToken;
    render();
  }catch(error){
    $('records').innerHTML='<div class="error">Could not load the lifecycle ledger: '+esc(error.message)+'</div>';
    $('count').textContent='Ledger unavailable';
  }finally{$('refresh').disabled=false}
}
$('refresh').addEventListener('click',()=>load(true));
$('loadMore').addEventListener('click',()=>load(false));
for(const id of ['range','status','role','destination'])$(id).addEventListener('change',()=>load(true));
$('search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>load(true),300)});
load();
</script>
</body>
</html>
""";
}
