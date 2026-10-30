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

The Service Bus role does not poll RTA. It receives `RetryEnvelope` messages from the configured Service Bus queue, waits for RTA/APIM health, retries processing through the shared `ClaimProcessor`, completes successful/requeued messages, and dead-letters malformed payloads.

For backward compatibility, if `PROJECT_PULSE_ROLE` is not yet configured the container starts in `RTA` mode. Any non-empty unsupported role value fails startup.

### Service Bus retry worker setup

Create the retry queue, then configure the **same** `ServiceBusFullyQualifiedNamespace` and `ServiceBusQueueName` on both Container Apps. The RTA app's managed identity needs **Azure Service Bus Data Sender** on the queue. The Service Bus app's identity needs **Azure Service Bus Data Receiver** and **Azure Service Bus Data Sender**, because a retry can be requeued. Project Pulse uses managed identity through `.NET DefaultAzureCredential`; no Service Bus connection string or SAS key is needed.

The Service Bus app must use `PROJECT_PULSE_ROLE=SERVICEBUS` and set `ServiceBusReceiveBatchSize`, `ServiceBusReceiveWaitSeconds`, and `ServiceBusDependencyRetrySeconds` as needed. It also requires the shared RTA/App API and APIM settings because it health-checks and processes through those services. See the [Service Bus setup and complete variable reference](docs/CONFIGURATION.md#azure-service-bus-retry-setup).

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

## Request timing diagnostics

The request-history page reports per-batch averages across stored history. When RTA writeback is enabled, its duration includes status PUT calls, retries, and configured backoff delays. Its summary also counts batches recorded with an `RtaStatusWritebackFailed` reason; that count identifies failed status PUT outcomes, not proof that every such batch retried. Project Pulse logs include correlation IDs, attempt counts, and the final observed HTTP status or exception category for exhausted writebacks. Match a batch's correlation ID in those logs to investigate the specific failure. APIM round-trip duration combines APIM, DOCMP/backend, and network time and cannot isolate those components.

## HTTP endpoints

```text
/
/pulse
/pulse-health
/requests
/requests/averages
/requests/{id}
/requests.html
/live
```

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
| `/requests` | Returns `401` without an authenticated principal and `403` when the caller lacks `ProjectPulse.Admin`. |
| `/requests/averages` | Returns average timing metrics for `range=7d`, `30d`, `365d`, or `all`; `range=custom` accepts `fromUtc` and `toUtc` ISO timestamps. Uses the same authorization behavior as `/requests`. |
| `/requests/{id}` | Same authorization behavior as `/requests`. |
| `/`, `/pulse`, `/pulse-health`, `/live` | Not restricted by the application role check in the current implementation. |

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

Recommended settings:

```text
Target port: 8080
Min replicas: 1
Max replicas: 1
```

The Container App managed identity needs its own downstream permissions. Permissions assigned to the original Function App identity do not automatically transfer.

## Configuration

The complete environment-variable and secret reference is in [docs/CONFIGURATION.md](docs/CONFIGURATION.md). The deployable template is [environment.example.json](environment.example.json). The request-history page loads its newest 100 records first and fetches older records on demand. A time-ordered index is maintained in the existing history table; the first page request after this version is deployed builds that index from existing records once.

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
RtaStatusPathTemplate=/requests/{correlationId}/status
RtaStatusWritebackEnabled=true

ApimResponseHeaderName=X-Pulse-Response-Token
ApimResponseToken=<secret reference>

RequestHistoryStorageConnectionString=<secret reference>
REQUEST_HISTORY_PAYLOAD_MODE=OFF
```

### Request-history payload capture

Set `REQUEST_HISTORY_PAYLOAD_MODE` to control payload logging and request-history details:

- `FULL`: store and log complete payloads. Use only with synthetic POC data.
- `PARTIAL`: store and log payloads after masking common ICN, SSN, date-of-birth, member/claimant ID, and personal-name fields. Unparseable payloads are fully masked.
- `OFF`: do not store or emit payload bodies. Transaction metadata remains in Azure Table Storage, and the request-history page still shows transactions without JSON details.

The legacy `LOG_POLL_PAYLOADS` setting is honored only when `REQUEST_HISTORY_PAYLOAD_MODE` is unset (`true` maps to `FULL`; `false` maps to `OFF`). Set the new variable explicitly to avoid ambiguity. Switching to `OFF` hides existing payloads but does not delete existing blobs; `PARTIAL` masks payloads when they are saved and when older blobs are read.

### RTA failure notifications

Configure `RTANotificationsBaseURL` and `RTANotificationsPath` to enable POSTs to RTA's `/admin/notifications` endpoint. A DOCMP error envelope identified by `errorCode` (for example, `ACC-NOTF-001`) is treated as an application error and written back to RTA, even if APIM mirrors its status as HTTP 404. It is not queued to Service Bus or sent to the notification endpoint. Other actual APIM HTTP statuses other than 200 use the HTTP retry/notification path; configured retryable HTTP statuses are sent to Service Bus. A Service Bus enqueue failure triggers a notification too. Status values inside HTTP 200 DOCMP JSON are application data, not HTTP statuses. Notifications set `EXTERNAL_CORRELATION_ID` to JSON `null`, include request IDs when available, and omit raw payloads and patient identifiers.

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
