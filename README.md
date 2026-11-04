# Project Pulse App Container

## Disclaimer and license

Project Pulse App Container is an experimental proof-of-concept and reference implementation. It is not an officially supported product or service.

This software is provided "AS IS", without warranty of any kind, express or implied. No support, maintenance, service-level commitment, or obligation to correct defects is provided.

Use of this software is entirely at your own risk. You are solely responsible for evaluating its suitability, security, configuration, compliance, and use in any environment, including production environments.

Project Pulse App Container is licensed under the MIT License. See [LICENSE](LICENSE) for the complete license terms.

## Purpose

This repository is the Azure Container Apps version of Project Pulse. It is intentionally isolated from the original Azure Functions implementation so the POC can validate sub-minute polling without changing the working Function App.

The application is a .NET 10 ASP.NET Core container that supports two runtime roles from one codebase and one image. `PROJECT_PULSE_ROLE=RTA` runs the continuous RTA/App API poller. `PROJECT_PULSE_ROLE=SERVICEBUS` runs the Service Bus-only retry worker. Both roles reuse the same APIM, writeback, request-history, managed-identity, and telemetry services.

## Runtime roles

Use the same image for two independent Azure Container Apps:

```text
PROJECT_PULSE_ROLE=RTA
```

The RTA role starts `ClaimPollingWorker`, polls the RTA/App API, sends claims to APIM, writes results back to RTA, and buffers retryable work to Service Bus.

```text
PROJECT_PULSE_ROLE=SERVICEBUS
```

The Service Bus role does not poll RTA. It receives claim-replay messages and `RtaWritebackRetryEnvelope` messages. Claim-replay messages go through the shared `ClaimProcessor` and APIM/DOCMP. A writeback retry message contains the exact translated RTA PUT body from the earlier APIM/DOCMP response; the worker sends that payload directly to RTA and never replays APIM/DOCMP. Direct writeback retries use increasing scheduled delays until the `ServiceBusMessageTtlMinutes` deadline (10 minutes by default), then move to the dead-letter queue if still unsuccessful. The worker requires RTA health for writeback retries; APIM health is not a gate for those messages.

Request history records which runtime handled each processing attempt: `RTA` for the poller and `SERVICEBUS` for the retry worker. The Requests page shows this as **RTA Poller** or **Service Bus Worker**, separately from the destination (APIM or Service Bus). Older history rows without this field appear as unknown.

For backward compatibility, if `PROJECT_PULSE_ROLE` is not yet configured the container starts in `RTA` mode. Any non-empty unsupported role value fails startup.

### APIM authentication

Project Pulse uses each Container App's managed identity to get an Entra bearer token for APIM. Configure the required `ApimScope` and assign the `ProjectPulse.Invoke` app role to the managed identities used by both the RTA and SERVICEBUS apps. Then configure APIM inbound token validation for the tenant, API audience, and role claim. The [APIM managed-identity setup and inbound policy example](docs/CONFIGURATION.md#apim-managed-identity-authentication) explains the steps. RTA API authentication is a separate later configuration.

### Service Bus retry worker setup

Create the retry queue, then configure the **same** `ServiceBusFullyQualifiedNamespace` and `ServiceBusQueueName` on both Container Apps. The RTA app's managed identity needs **Azure Service Bus Data Sender** on the queue. The Service Bus app's identity needs **Azure Service Bus Data Receiver** and **Azure Service Bus Data Sender**, because a retry can be requeued. Project Pulse uses managed identity through `.NET DefaultAzureCredential`; no Service Bus connection string or SAS key is needed.

The Service Bus app must use `PROJECT_PULSE_ROLE=SERVICEBUS` and set `ServiceBusReceiveBatchSize`, `ServiceBusReceiveWaitSeconds`, and `ServiceBusDependencyRetrySeconds` as needed. It requires the same `PROCESSING_CONCURRENCY_MODE`, `PROCESSING_MAX_CONCURRENCY`, and `PROCESSING_MAX_CONCURRENCY_LIMIT` settings as the RTA app when you want it to process multiple messages concurrently. `SEQUENTIAL` is the default; choose another mode to enable concurrency. It requires RTA settings for direct status writebacks. Claim-replay messages go through APIM/DOCMP; stored RTA-writeback retry messages go directly to RTA and do not require APIM health. See the [Service Bus setup and complete variable reference](docs/CONFIGURATION.md#azure-service-bus-retry-setup).

## Why Container Apps

The original Project Pulse implementation uses an Azure Functions timer trigger. This POC requires polling more frequently than once per minute.

The Container Apps worker stays running and uses:

```text
POLL_INTERVAL_SECONDS
```

Default: 5 seconds. Supported range: 1 through 300 seconds.

For the POC, use one replica:

```text
minReplicas = 1
maxReplicas = 1
```

This prevents multiple independent workers from polling the same queue while we validate the design.

## Processing flow

```text
RTA / Mock API
     |
     v
Project Pulse Container App
     |
     +--> HealthGate
     +--> transform queued requests
     |
     v
Azure API Management
     |
     v
Adjudication response
     |
     v
Project Pulse
     |
     +--> validate response
     +--> write status back to RTA
     +--> record request history
     +--> Service Bus fallback when required
```

## Runtime

- .NET 10
- ASP.NET Core
- Azure Container Apps
- Continuous `BackgroundService` polling
- Managed identity through `DefaultAzureCredential`
- Azure Service Bus fallback/retry
- Azure Table + Blob request history
- Application Insights
- HTTP port 8080

## APIM mock scenarios

For testing with an APIM policy that supports mock scenarios, set `APIM_MOCK_SCENARIO` on the Container App. Project Pulse forwards the value in the `X-Mock-Scenario` header on every APIM adjudication request. Leave it blank to omit the header and use APIM's normal behavior. The setting is available to both the `RTA` and `SERVICEBUS` roles because they share the same claim processor.

| `APIM_MOCK_SCENARIO` | Simulated result |
| --- | --- |
| `success` | Successful mock response. |
| `docmp-not-found` | HTTP 200 with a DOCMP not-found error envelope. |
| `docmp-error` | HTTP 200 with a DOCMP application-error envelope. |
| `http-404` | Actual HTTP 404 response. |
| `http-429` | Actual HTTP 429 response. |
| `http-500` | Actual HTTP 500 response. |
| `http-503` | Actual HTTP 503 response. |
| `http-504` | Actual HTTP 504 response. |

These values exercise different paths: DOCMP errors inside HTTP 200 responses are application errors; `http-*` scenarios simulate actual HTTP failures and the configured retry/notification behavior. Only set this variable when the APIM policy is configured to recognize the header. See the [full configuration reference](docs/CONFIGURATION.md#configuration-reference).

## Request timing diagnostics

The request-history page reports per-batch averages across stored history. RTA pull is timed separately from end-to-end: end-to-end starts after the poll response is read and parsed and records are available, then ends when the record outcome is recorded. When RTA writeback is enabled, its duration includes status PUT calls, retries, and configured backoff delays. The **Other processing** duration is end-to-end time minus translation, APIM round trip, and RTA writeback; it accounts for orchestration and other uninstrumented work so the processing timings reconcile. Records stored before this timing change used a poll-start timestamp, so all-time averages can mix the two definitions; use a range starting after the rollout for a consistent comparison. Its summary also counts batches recorded with an `RtaStatusWritebackFailed` reason; that count identifies failed status PUT outcomes, not proof that every such batch retried. Project Pulse logs include correlation IDs, attempt counts, and the final observed HTTP status or exception category for exhausted writebacks. Match a batch's correlation ID in those logs to investigate the specific failure. APIM round-trip duration combines APIM, DOCMP/backend, and network time and cannot isolate those components.

The Requests list can be filtered to the last 5 minutes, last hour, last 24 hours, last 7 days, or all time. It reports the number of records matching the selected time range and search, alongside the number currently loaded. Search narrows both the matching count and displayed rows; paging keeps the selected time cutoff fixed.

The admin-only `/ledger.html` page presents a searchable lifecycle view of stored request-history attempts. Filter by time range, status, destination, or processing role; search by correlation/unique ID, RTA request ID, or batch ID. It reports matching attempts separately from those loaded and paginates older results. The timeline is reconstructed from milestones already recorded in request history, not a separate event log. “APIM attempt recorded” means the attempt timestamp was saved before the outbound POST; it does not prove APIM received the request. A missing later milestone means no outcome is stored for that stage. The ledger does not display payload bodies.

## HTTP endpoints

```text
/
/pulse
/pulse-health
/requests
/requests/averages
/requests/poll (POST, RTA role only)
/requests/cleanup/preview (GET)
/requests (DELETE, starts history cleanup)
/requests/cleanup/{operationId} (GET, cleanup progress)
/requests/{id}
/requests.html
/ledger.html (admin-only lifecycle ledger)
/batches.html (admin-only poll-batch reconciliation)

/live
```

The admin-only `Poll now` button on `/requests.html` queues an immediate RTA polling cycle. It uses the normal worker path, including existing health checks and APIM outage handling, and does not start a concurrent poll. Repeated clicks while a poll is already queued are coalesced. The endpoint is available only on the `RTA` role; the `SERVICEBUS` role does not poll RTA.

Both runtime roles use `PROCESSING_CONCURRENCY_MODE` (`SEQUENTIAL`, `RTA_ONLY`, `APIM_ONLY`, or `FULL`), `PROCESSING_MAX_CONCURRENCY`, and `PROCESSING_MAX_CONCURRENCY_LIMIT`. In the RTA role, the mode controls per-record APIM/DOCMP and RTA status PUT concurrency within the fetched batch; polling cycles do not overlap. In the Service Bus role, `SEQUENTIAL` processes one message at a time, while the other modes allow concurrent messages with shared APIM and RTA writeback gates. The requested concurrency defaults to 5 and is clamped by the configurable limit, which defaults to 300. Effective limits apply per replica, so replicas multiply downstream load. `ServiceBusReceiveBatchSize` controls each receive operation, not the total number of messages in flight.

The request-history page also lets admins delete history created in the last 24 hours, last 7 days, last 30 days, or all time. The page previews the matching record count and requires confirmation, then shows progress while cleanup runs in the background. The progress endpoint reports persisted operation status, phase, and cleanup-item counts so the browser can poll without waiting on a long-running delete request. Cleanup removes Azure Table history rows and their recent-history index entries in parallel transactions, scans the payload container once, and deletes selected batch payload files with bounded parallelism only when no retained history row references the batch. Progress separately shows how many history records and payload files have been removed. It does not delete Service Bus messages or platform logs.

Container Apps ingress target port:

```text
8080
```

## Entra authentication for request history

The request-history UI and APIs require the Microsoft Entra app role `ProjectPulse.Admin`. Azure Container Apps authentication handles the Entra sign-in and supplies the authenticated principal to the application. Project Pulse checks that principal and role in the application; it does not query Microsoft Graph to authorize each request.

### Entra app registration

Create or use a **single-tenant workforce** app registration for Project Pulse. The Container Apps Authentication setup can create the registration and configure its callback URI, or you can select an existing registration. If configuring an existing registration, make sure its Web redirect URI includes:

```text
https://<container-app-fqdn>/.auth/login/aad/callback
```

In **App registrations → Project-Pulse → App roles**, add this role:

| Setting | Value |
| --- | --- |
| Display name | `ProjectPulse.Admin` |
| Allowed member types | Users/Groups |
| Value | `ProjectPulse.Admin` |
| Enabled | Yes |

The role **Value** must be `ProjectPulse.Admin`; Project Pulse authorizes against that value in the role claim.

The Container Apps Microsoft identity provider may request the delegated Microsoft Graph `User.Read` permission for basic sign-in profile information. The application does not use Microsoft Graph for its role check, so it does not need Graph group-read permissions.

### Assign the admin group

In **Enterprise applications → Project-Pulse → Users and groups**:

1. Select **Add user/group**.
2. Select the `PROJECT_PULSE_ADMINS` security group.
3. Select the `ProjectPulse.Admin` role and save the assignment.

Assign the app role to the group on the Enterprise Application (service principal), not an Entra directory administrator role. Users receive the app role through their group membership. After adding a user or changing a role assignment, have them sign out and sign in again so their session receives an updated role claim.

### Configure Container Apps authentication

In the Container App, open **Authentication → Add identity provider → Microsoft** and select the Project-Pulse registration.

- Use the workforce/single-tenant configuration for your directory.
- Confirm the issuer is for your tenant and the allowed token audience matches the app registration's Application (client) ID or configured Application ID URI. Keep these values consistent with the registration and provider configuration.
- Allow requests from the application itself and allow identities to sign in; the `ProjectPulse.Admin` role is the authorization gate.
- Set **Restrict access** to **Allow unauthenticated access**. This lets public health and landing routes continue to work while the application enforces authorization on the request-history routes below. Do not set the platform-wide option to **Require authentication** if those other routes need to remain public.
- Keep the provider's generated client secret in the Container Apps authentication configuration and rotate it before it expires. Do not put it in source control or add it as an application environment variable.

Container Apps authentication exposes authenticated identity information in the `X-MS-CLIENT-PRINCIPAL` request header. Project Pulse uses that header for the role check. Keep the Container App behind its configured Container Apps ingress/authentication path so callers cannot bypass that identity handling.

### Routes and expected access behavior

| Route | Behavior |
| --- | --- |
| `/requests.html` | Redirects unauthenticated browsers to the Entra sign-in page; requires the `ProjectPulse.Admin` role after sign-in. |
| `/ledger.html` | Admin-only lifecycle ledger; uses the same Entra role gate and displays stored milestones without payload bodies. |
| `/batches.html` | Admin-only poll-batch reconciliation; uses the same Entra role gate and shows counts and record-level stage gaps for each RTA GET poll run. |
| `/requests` | Returns `401` without an authenticated principal and `403` when the caller lacks `ProjectPulse.Admin`. |
| `/requests/averages` | Returns average timing metrics for `range=7d`, `30d`, `365d`, or `all`; `range=custom` accepts `fromUtc` and `toUtc` ISO timestamps. Uses the same authorization behavior as `/requests`. |
| `/requests/cleanup/preview?range=24h|7d|30d|all` | Admin-only preview count for the selected history cleanup range. |
| `DELETE /requests?range=24h|7d|30d|all` | Admin-only, same-origin cleanup of request history and eligible payload blobs. |
| `/requests/{id}` | Same authorization behavior as `/requests`. |
| `/`, `/pulse`, `/pulse-health`, `/live` | Not restricted by the application role check in the current implementation. |

The admin-only `/batches.html` page starts with the complete set of records returned by each successful, non-empty RTA GET. It saves a small manifest in the existing request-history table before processing begins, then tracks APIM POST attempts, APIM responses, Service Bus buffering, and RTA writeback outcomes. The batch view summarizes counts and lets admins filter to records with a missing or failed stage and search by correlation ID or RTA request ID. “APIM attempt” means the request was prepared and recorded immediately before sending; the APIM response stage means an HTTP response was received. RTA writeback success is recorded only when the RTA API reports success. A missing milestone is a potential gap for investigation; it does not by itself prove that APIM, RTA, or Service Bus lost the record. No request or response payload bodies are copied into the batch manifest.

The requests page shows average RTA pull, translation, APIM round-trip, RTA writeback, and end-to-end durations for the selected time range: last 7 days, last 30 days, last year (rolling 365 days), all time, or a custom date/time interval. The date window is applied to each batch's creation time, and the failure count uses the same window. Each average is calculated once per batch and includes only batches with a recorded value for that timing; its sample count is shown with the average. The summary is fetched separately from the first 100 displayed records, so it does not slow the initial transaction list. Results are cached in memory for up to one minute per app instance and selected range.

Initial color bands are operating signals, not configured SLAs: RTA pull, APIM round trip, and RTA writeback are green through 1 second, yellow through 5 seconds, and red above 5 seconds; translation is green through 100 ms, yellow through 500 ms, and red above 500 ms; end-to-end is green through 5 seconds, yellow through 15 seconds, and red above 15 seconds. Tune these thresholds once representative baselines are available. The information icon beside the averages explains what each timer covers and common sources of delay. APIM round trip combines gateway, DOCMP/backend, and network time; it cannot identify which component caused a delay. Compare an individual batch with the corresponding Project Pulse, RTA, and APIM/DOCMP logs to isolate the cause.

For details, see Microsoft's documentation on [Container Apps authentication](https://learn.microsoft.com/azure/container-apps/authentication), [Microsoft Entra authentication for Container Apps](https://learn.microsoft.com/azure/container-apps/authentication-entra), [app roles](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps), and [assigning users and groups to an enterprise application](https://learn.microsoft.com/entra/identity/enterprise-apps/assign-user-or-group-access-portal).

## Docker

The Dockerfile is at:

```text
./Dockerfile
```

Build:

```bash
docker build -t projectpulse-appcontainer .
```

## Azure deployment

This repository intentionally does not contain the legacy Function App GitHub Actions workflow.

For the POC, connect this repository directly from Azure Container Apps Continuous Deployment and allow Azure to generate the GitHub Actions workflow.

The repository's main-branch deployment workflow reads the base release from the root `VERSION` file, appends the GitHub Actions run number, and builds/pushes a unique image tag such as `0.0.93-build.94`. It deploys that image to `projectpulse-appcontainer`, then updates `projectpulse-sb-appcontainer` to use the same image tag. Both apps therefore run the same build after a successful workflow run. The exact image tag is baked into the image and appears on `requests.html` as `BUILD v<version> · <role>` and at `/version`; every workflow run gets a new build number automatically, with no per-build environment-variable edit. Change `VERSION` when intentionally starting a new base release; the Actions run number distinguishes builds of that release. The workflow can succeed while a new revision is still becoming ready, so confirm each app's **Latest ready revision** advances before considering the rollout complete.

Recommended settings:

```text
Target port: 8080
Min replicas: 1
Max replicas: 1
```

The Container App managed identity needs its own downstream permissions. Permissions assigned to the original Function App identity do not automatically transfer.

## Configuration

The complete environment-variable and secret reference is in [docs/CONFIGURATION.md](docs/CONFIGURATION.md). The deployable template is [environment.example.json](environment.example.json). The request-history page loads its newest 100 records first and fetches older records on demand. A time-ordered index is maintained in the existing history table; the first page request after this version is deployed builds that index from existing records once. Search by correlation ID, RTA request ID, batch ID, status, HTTP/business code, destination, or reason queries the full indexed history across pages. RTA request IDs are stored as searchable metadata independently of payload capture mode.

Core RTA configuration:

```text
PROJECT_PULSE_ROLE=RTA
POLL_INTERVAL_SECONDS=5

APP_API_MODE=MOCK
AppApiBaseUrl=<RTA or mock API URL>
AppApiTimeoutSeconds=10

ApimBaseUrl=<APIM URL>
ApimAdjudicationPath=pulse/pending
ApimRequestTimeoutSeconds=15

ServiceBusFullyQualifiedNamespace=<namespace>.servicebus.windows.net
ServiceBusQueueName=projectpulsequeue

RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaPageSize=100
RtaMaxPageSize=5000
RtaPagesPerPoll=1
RtaStatusPathTemplate=/requests/{correlationId}/status
RtaStatusWritebackEnabled=true
PROCESSING_CONCURRENCY_MODE=SEQUENTIAL
PROCESSING_MAX_CONCURRENCY=5
PROCESSING_MAX_CONCURRENCY_LIMIT=300

ApimResponseHeaderName=X-Pulse-Response-Token
ApimResponseToken=<secret reference>

RequestHistoryStorageConnectionString=<secret reference>
REQUEST_HISTORY_PAYLOAD_MODE=OFF
```

### RTA page size

`RtaPageSize` controls how many records Project Pulse requests in each RTA GET page (default 100). `RtaMaxPageSize` sets the configurable upper bound (default 5,000); set it higher if the RTA endpoint supports larger pages. The RTA endpoint may impose its own lower maximum. Larger pages increase response size and memory use but do not increase processing concurrency. `RtaPagesPerPoll` remains clamped to 1-3, so the theoretical per-poll maximum is `RtaMaxPageSize × RtaPagesPerPoll`. Increase the page limit only after confirming the RTA limit and checking container memory and request duration.

### Processing concurrency

Set `PROCESSING_CONCURRENCY_MODE` to choose which external calls may overlap:

- `SEQUENTIAL` (default): process one record at a time; APIM/DOCMP and RTA are both serial.
- `RTA_ONLY`: APIM/DOCMP stays serial; RTA status PUTs may overlap.
- `APIM_ONLY`: APIM/DOCMP calls may overlap; RTA status PUTs stay serial.
- `FULL`: both APIM/DOCMP and RTA status PUTs may overlap.

`PROCESSING_MAX_CONCURRENCY` sets the requested concurrency (default `5`). `PROCESSING_MAX_CONCURRENCY_LIMIT` sets its configurable upper bound (default `300`); the effective value is clamped from 1 through that limit. Raise both values when intentionally increasing concurrency. For batches larger than the configured concurrency, remaining records wait while the configured number are processed concurrently. APIM concurrency increases simultaneous requests that can reach DOCMP through APIM; RTA concurrency increases simultaneous status PUTs. The Service Bus role uses the same mode and limits, with shared gates across active messages. `ServiceBusReceiveBatchSize` controls one receive call and does not cap total in-flight work. Limits apply per replica, so replicas multiply aggregate downstream load.

### Request-history payload capture

Set `REQUEST_HISTORY_PAYLOAD_MODE` to control payload logging and request-history details:

- `FULL`: store and log complete payloads. Use only with synthetic POC data.
- `PARTIAL`: store and log payloads after masking common ICN, SSN, date-of-birth, member/claimant ID, and personal-name fields. Unparseable payloads are fully masked.
- `OFF`: do not store or emit payload bodies. Transaction metadata remains in Azure Table Storage, and the request-history page still shows transactions without JSON details.

The legacy `LOG_POLL_PAYLOADS` setting is honored only when `REQUEST_HISTORY_PAYLOAD_MODE` is unset (`true` maps to `FULL`; `false` maps to `OFF`). Set the new variable explicitly to avoid ambiguity. Switching to `OFF` hides existing payloads but does not delete existing blobs; `PARTIAL` masks payloads when they are saved and when older blobs are read.

### RTA failure notifications

Configure `RTANotificationsBaseURL` and `RTANotificationsPath` to enable POSTs to RTA's `/admin/notifications` endpoint. A DOCMP error envelope identified by `errorCode` (for example, `ACC-NOTF-001`) is treated as an application error and written back to RTA, even if APIM mirrors its status as HTTP 404. It is not queued to Service Bus or sent to the notification endpoint. Other actual APIM HTTP statuses other than 200 are sent to Service Bus for a bounded 10-minute retry window, with the latest HTTP status and response details retained. Successful enqueueing does not send an admin notification. Retries use bounded backoff and cannot extend the original deadline. If APIM/DOCMP never succeeds, Project Pulse writes a final failure status to RTA with the last HTTP response in `API_CALL_HISTORY`. Failure to enqueue/schedule a retry triggers an admin notification. If that final RTA writeback also fails through its direct retry window, the worker sends an admin notification and dead-letters it. A worker process that is completely stopped cannot report its own outage and requires Azure monitoring. Status values inside HTTP 200 DOCMP JSON are application data, not HTTP statuses. Notifications set `EXTERNAL_CORRELATION_ID` to JSON `null`, include request IDs when available, and omit raw payloads and patient identifiers.

### Required secret references

`ApimResponseToken` is the expected value returned by APIM in the configured response-token header. Store it as a Container Apps secret, recommended name `apim-response-token`, and configure the `ApimResponseToken` environment variable to reference that secret.

`RequestHistoryStorageConnectionString` is the Azure Storage connection string used for request-history Table metadata and Blob payload storage. Store it as a Container Apps secret, recommended name `request-history-storage`, and configure the `RequestHistoryStorageConnectionString` environment variable to reference that secret.

Do not commit either secret value. See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full list, defaults, role applicability, and descriptions.

### APIM response token named value and mock policy

Project Pulse validates that the APIM response contains the header configured by `ApimResponseHeaderName` and that the returned value matches the Container Apps secret exposed as `ApimResponseToken`.

In APIM, create a Named Value for the shared token:

```text
Display name: Project Pulse response token
Name: rta-key
Type: Secret
Value: <generate/store the shared token value>
```

The value stored in the APIM Named Value `rta-key` must exactly match the value stored in the Container Apps secret referenced by the `ApimResponseToken` environment variable.

For the current POC, APIM generates the mock adjudication response directly from the `<inbound>` policy by using `<return-response>`. Because `return-response` short-circuits the normal APIM pipeline, the response headers and body are configured inside that `return-response` block even though the block itself is located in `<inbound>`.

The response-token header is therefore configured like this inside the existing inbound `return-response`:

```xml
<return-response>
    <set-status code="200" reason="OK" />
    <set-header name="Content-Type" exists-action="override">
        <value>application/json</value>
    </set-header>
    <set-header name="X-Pulse-Policy-Version" exists-action="override">
        <value>N50983445987</value>
    </set-header>
    <set-header name="X-Pulse-Response-Token" exists-action="override">
        <value>{{rta-key}}</value>
    </set-header>
    <!-- set-body omitted here -->
</return-response>
```

The important distinction is that `X-Pulse-Response-Token` is still an HTTP **response** header. It is not being added to the backend request. It appears under `<inbound>` only because the POC policy returns the mock response immediately from that section.

The current policy structure is therefore:

```xml
<policies>
    <inbound>
        <base />

        <!-- request validation and requestBody variable setup -->

        <return-response>
            <set-status code="200" reason="OK" />
            <set-header name="Content-Type" exists-action="override">
                <value>application/json</value>
            </set-header>
            <set-header name="X-Pulse-Policy-Version" exists-action="override">
                <value>N50983445987</value>
            </set-header>
            <set-header name="X-Pulse-Response-Token" exists-action="override">
                <value>{{rta-key}}</value>
            </set-header>
            <!-- mock adjudication response body -->
        </return-response>
    </inbound>
    <backend>
        <base />
    </backend>
    <outbound>
        <base />
    </outbound>
    <on-error>
        <base />
    </on-error>
</policies>
```

If the POC is later changed so APIM forwards the request to a real backend instead of returning the response from `<inbound>`, then the response-token header should be added in the appropriate response path, typically `<outbound>`.

If `ApimResponseHeaderName` is changed from `X-Pulse-Response-Token`, change the APIM `set-header` name to the same value.

## Repository structure

```text
ContainerApp/
  ClaimPollingWorker.cs
  ServiceBusWorker.cs
  ProjectPulseRole.cs

Models/
Options/
Services/

Program.cs
ProjectPulse.Processor.csproj
Dockerfile
environment.example.json
docs/
  CONFIGURATION.md
```

The Azure Functions trigger classes, `host.json`, Function App workflow, and `POLL_SCHEDULE` configuration are intentionally excluded.

## Relationship to Project Pulse

The original Azure Functions POC remains in `mialtamo/Project-Pulse`.

This repository is the isolated Container Apps implementation for continuous sub-minute polling.
