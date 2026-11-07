# Project Pulse Container App configuration

This document is the canonical deployment reference for environment variables used by the Project Pulse Container App.

The same image supports two runtime roles:

- `PROJECT_PULSE_ROLE=RTA` starts the continuous RTA/App API polling worker.
- `PROJECT_PULSE_ROLE=SERVICEBUS` starts the Service Bus retry worker.

Request history also stores the runtime role that handled each attempt (`RTA` or `SERVICEBUS`). The Requests page displays this as **RTA Poller** or **Service Bus Worker**, separately from the request destination. Existing records without the field are shown as unknown.

Values shown as defaults are the defaults implemented by the application unless noted otherwise. The deployment workflow reads the base release from the repository root `VERSION` file and appends the GitHub Actions run number to create a unique image tag for every run (for example, `0.0.93-build.94`). That exact tag is baked into the image and displayed on the home page, on `requests.html` with the runtime role, and as JSON at `/version`; no per-deployment version environment setting or manual build-number edit is needed. Change `VERSION` when intentionally starting a new base release. Azure Container Apps revision counters are separate.

## Secrets

### ApimResponseToken

`ApimResponseToken` is the expected value returned by APIM in the response header named by `ApimResponseHeaderName` (default `X-Pulse-Response-Token`).

Project Pulse validates this value before accepting the APIM adjudication response. The token must match the value APIM is configured to return.

Store it as a Container Apps secret and expose it to the container as:

```text
Environment variable: ApimResponseToken
Source: secret reference
Recommended secret name: apim-response-token
```

Do not store the token directly in source control or in `environment.example.json`.

### RequestHistoryStorageConnectionString

`RequestHistoryStorageConnectionString` is the Azure Storage connection string used by the request-history implementation for its Azure Table metadata and Blob payload storage. The recent-history index uses the same configured table and requires no additional environment variable. After the index feature is first deployed, the first request-history list call backfills the index from existing table rows; later list calls read only the requested page.

Store it as a Container Apps secret and expose it to the container as:

```text
Environment variable: RequestHistoryStorageConnectionString
Source: secret reference
Recommended secret name: request-history-storage
```

The configured storage account must be reachable from the Container App environment. Do not commit the connection string to source control.

## Configuration reference

### URL and path formatting

The base URL settings (`AppApiBaseUrl`, `ApimBaseUrl`, and `RTANotificationsBaseURL`) accept a trailing slash or no trailing slash. They must be absolute HTTP or HTTPS URLs without a query string or fragment. Endpoint path settings for RTA, APIM, health checks, status writebacks, and notifications accept leading or trailing slashes. Project Pulse removes redundant separators when joining a path to its base URL, producing one separator at the boundary while preserving any path prefix in the base URL. Query parameters already present in a configured endpoint path are preserved.

| Variable | Role | Required | Default / example | Secret | Purpose |
| --- | --- | --- | --- | --- | --- |
| `PROJECT_PULSE_ENVIRONMENT` | Both | No | `POC` | No | Display/environment label used by the UI. |
| `PROJECT_PULSE_ROLE` | Both | Recommended | `RTA` | No | Selects `RTA` or `SERVICEBUS`. Missing value defaults to `RTA`. |
| `POLL_INTERVAL_SECONDS` | RTA | No | `5` | No | Minimum delay between RTA polling cycles. Supported range is 1-300 seconds. |
| `APP_API_MODE` | RTA | No | `RTA` in code; `MOCK` for POC | No | Selects mock API behavior when set to `MOCK`. |
| `AppApiBaseUrl` | Both | Yes | RTA/mock API base URL | No | Base URL for RTA/App API calls and health checks. |
| `AppApiTimeoutSeconds` | Both | No | `10` | No | HTTP timeout for App API requests. |
| `AppApiScope` | Both | No | empty | No | Optional managed-identity OAuth scope for the App API. |
| `x-api-key` | Both | No | empty | **Yes** | Secret API key sent as the `x-api-key` HTTP header on RTA queued-request GETs, status PUTs, and RTA health checks. Configure it as a Container Apps secret reference; the key is not logged or placed in request bodies. |
| `RtaQueuedPath` | RTA | No | `/requests/queued` in code | No | Path used to retrieve queued requests. |
| `RtaHealthPath` | Both | No | `/healthchecks` in code | No | RTA health endpoint path. |
| `RtaDbHealthPath` | Both | No | `/healthchecks/db/app` in code | No | RTA database health endpoint path. |
| `RtaStatusPathTemplate` | Both | No | `/requests/{correlationId}/status` | No | Path template used for result writeback. |
| `RTANotificationsBaseURL` | Both | Required to send notifications | RTA admin API base URL | No | Base URL for the RTA catastrophic-error notification endpoint. |
| `RTANotificationsPath` | Both | No | `/admin/notifications` | No | Path for RTA admin notifications. |
| `RtaPageSize` | RTA | No | `100` | No | Requested records per RTA GET page, clamped to `RtaMaxPageSize`. |
| `RtaMaxPageSize` | RTA | No | `5000` | No | Configurable upper bound for `RtaPageSize`; set this to the largest page size the RTA endpoint and container can safely support. Values below 1 are treated as 1. |
| `RtaPagesPerPoll` | RTA | No | `1` | No | Number of pages processed per poll. Clamped to 1-3. |
| `RtaStatusWritebackEnabled` | Both | No | `false` in code | No | Enables result writeback to RTA. |
| `PROCESSING_CONCURRENCY_MODE` | Both | No | `SEQUENTIAL` | No | Selects `SEQUENTIAL`, `RTA_ONLY`, `APIM_ONLY`, or `FULL` processing concurrency. |
| `PROCESSING_MAX_CONCURRENCY` | Both | No | `5` (clamped to the configured limit) | No | Concurrent per-record workers and external calls allowed by the selected mode for RTA polling records and Service Bus retry messages. The Service Bus role uses this value as its maximum number of concurrent messages. |
| `PROCESSING_MAX_CONCURRENCY_LIMIT` | Both | No | `300` | No | Configurable upper bound for `PROCESSING_MAX_CONCURRENCY`; values below 1 are treated as 1. Set both variables to control the desired concurrency within this bound. |
| `ApimBaseUrl` | Both | Yes | APIM base URL | No | Base URL for APIM. |
| `ApimHealthPath` | Both | No | `/health` | No | APIM health endpoint path. |
| `ApimAdjudicationPath` | Both | No | `/claims/adjudicate` in code | No | APIM adjudication endpoint path used for non-update requests. |
| `ApimUpdateAdjudicationPath` | Both | No | Empty | No | APIM endpoint path for records whose incoming `REQUEST_TYPE` is `UPDATE_REQUEST`. These requests use HTTP PUT. Leading and trailing slashes are optional and normalized when the request URL is built. If unset, update requests fall back to `ApimAdjudicationPath` for compatibility. |
| `ApimRequestTimeoutSeconds` | Both | No | `15` | No | APIM request timeout used by Project Pulse processing logic. |
| `APIM_AUTHENTICATION_ENABLED` | Both | No | `true` | No | Enables Pulse's APIM bearer-token authentication and APIM response-token validation. Set to `false` only to test a legacy APIM policy that does not validate the bearer token or return the response-token header. |
| `ApimScope` | Both | Required when authentication is enabled | `api://<API-application-client-id>/.default` | No | Managed-identity OAuth scope for APIM. Required when `APIM_AUTHENTICATION_ENABLED=true`; use the Application ID URI of the APIM resource app registration with `/.default`. |
| `ApimResponseHeaderName` | Both | No | `X-Pulse-Response-Token` | No | Response header containing the APIM response-validation token. |
| `ApimResponseToken` | Both | Yes | secret | **Yes** | Expected APIM response token. Use a Container Apps secret reference. Always required; `APIM_AUTHENTICATION_ENABLED=false` does not disable response-token validation. |
| `APIM_MOCK_SCENARIO` | Both | No | empty | No | Optional test control forwarded to APIM as `X-Mock-Scenario`. Supported scenarios include `success`, application-error cases defined by the APIM mock policy, and `http-404`, `http-429`, `http-500`, `http-503`, and `http-504`. Blank omits the header. Configure only when the APIM policy supports mock scenarios. |
| `APIM_OUTAGE_MODE` | Both | No | `BUFFER_THEN_STOP` | No | Controls behavior during APIM outage: `BUFFER_THEN_STOP`, `STOP_IMMEDIATELY`, or `BUFFER_CONTINUOUSLY`. The RTA role sends one admin notification when it first detects an APIM outage, then follows the selected buffering/polling behavior. |
| `APIM_OUTAGE_POLL_STOP_SECONDS` | RTA | No | `300` | No | Outage duration used before the RTA poller stops in buffer-then-stop mode. |
| `CorrelationIdField` | Both | No | `CORRELATION_ID` | No | Input JSON field containing the correlation ID. |
| `ApimFieldMapping` | Both | No | built-in JSON mapping | No | Fallback JSON mapping for request types without a dedicated mapping. |
| `ApimInitialRequestFieldMapping` | Both | No | built-in initial-request mapping | No | JSON mapping for `INITIAL_REQUEST` fields. |
| `ApimUpdateRequestFieldMapping` | Both | No | built-in update-request mapping | No | JSON mapping for `UPDATE_REQUEST` fields. The default reads `patientIcn`, `startDateOfService`, `claimNumber`, and `source` from `UPDATE_REQUEST_METADATA`, then creates an `adjustments` array with `individual`, `family`, and `costshare` delta updates. |

`RtaPageSize` controls how many records Project Pulse requests in each RTA GET page (default 100). `RtaMaxPageSize` sets its configurable upper bound (default 5,000); set it higher when the RTA endpoint supports larger pages. The RTA endpoint may impose its own lower maximum. Larger pages increase response size and in-memory batch size; they do not increase processing concurrency. `RtaPagesPerPoll` remains clamped to 1-3, so the theoretical per-poll maximum is `RtaMaxPageSize × RtaPagesPerPoll`. Increase the page limit only after confirming the RTA limit and checking memory and request duration.

`PROCESSING_CONCURRENCY_MODE` applies to both roles. For RTA polling, it controls per-record concurrency in a polling batch. For the Service Bus role, `SEQUENTIAL` (default) processes one queued message at a time; the other modes allow up to `PROCESSING_MAX_CONCURRENCY` messages to be processed concurrently. `RTA_ONLY` keeps APIM/backend calls serial and allows concurrent RTA status PUTs. `APIM_ONLY` allows concurrent APIM/backend calls and keeps RTA PUTs serial. `FULL` allows both stages to overlap. `PROCESSING_MAX_CONCURRENCY` selects the requested concurrency (default 5), while `PROCESSING_MAX_CONCURRENCY_LIMIT` sets its configurable upper bound (default 300). The effective concurrency is clamped from 1 through that limit. The Service Bus role maintains the same shared APIM and RTA writeback gates across all active messages. Higher concurrency increases simultaneous load on downstream API and RTA; ensure their supported limits and the container's CPU/memory are sufficient. Limits are per replica, so multiple replicas multiply aggregate concurrency.

For `INITIAL_REQUEST`, Project Pulse sends one APIM request per RTA record. The body is a root JSON object containing only APIM's six initial-request fields: `patientIcn`, `sponsorIcn`, `startDateOfService`, `endDateOfService`, `program`, and `includeDetails`; it is not wrapped in a `claims` array. Initial-request source fields are read from RTA `INITIAL_REQUEST_METADATA`. The downstream API response fields are read from the root response object (for example, `patientIcn`, `benefitPeriod`, and the accumulator objects), then translated to RTA's uppercase `INITIAL_REQUEST_RESPONSE` shape. The update-request mapping remains independently configurable.

For `UPDATE_REQUEST`, the root-level request type selects `ApimUpdateAdjudicationPath` and HTTP PUT. The configurable mapping reads update fields from `UPDATE_REQUEST_METADATA` and builds a root JSON object containing the patient identifier, service start date, claim number, source, and an `adjustments` array. Each adjustment item contains an `updateAccumulator` object with the accumulator name and delta; the default mapping supports individual, family, and cost-share adjustments. The record's `IDEMPOTENCY_KEY` is sent in the `Idempotency-Key` request header, and APIM is expected to forward it to the downstream service. Correlation is sent in the `X-Correlation-ID` request header. Query parameters such as `pageSize` and `sort` are not part of the JSON body.

For enabled initial-request writeback, Project Pulse builds the RTA status PUT body with an `API_CALL_HISTORY` object. Its request and response bodies are JSON strings: `REQUEST_BODY` contains the exact mapped JSON body Project Pulse POSTed to APIM (for example, the six initial-request fields), and `RESPONSE_BODY` contains the raw APIM response. The raw RTA item remains available separately in request history. `HTTP_METHOD` is `POST`; `API_URL` is the APIM base URL from `ApimBaseUrl`, normalized to end with a slash; `API_ENDPOINT` is the selected configured path with leading and trailing slashes removed. `REQUEST_HEADER` (singular) contains the serialized headers received from APIM, with the configured APIM response-token value redacted. `RESPONSE_HTTP_STATUS_CODE` and `RESPONSE_TIME_MS` describe the APIM response. `EXTERNAL_CORRELATION_ID`, `REQUEST_QUERY_PARAMETER`, `ERROR_MESSAGE`, and `ERROR_STACK` are null.

For `UPDATE_REQUEST` writeback, the RTA status PUT body sets `INITIAL_REQUEST_RESPONSE` to null and translates DOCMP's response into `UPDATE_REQUEST_RESPONSE`. Patient and program are carried from the original `UPDATE_REQUEST_METADATA` (with DOCMP values as fallback); accumulator balances and last-adjustment details are mapped from DOCMP. The `INITIAL_REQUEST` response mapping remains unchanged.

| `JsonResponseCodeField` | Both | No | `RESPONSE_CODE` | No | JSON field inspected for application response codes. |
| `JsonRetryCodes` | Both | No | empty | No | Optional comma-separated application response codes that should be retried. |
| `JsonAlertCodes` | Both | No | `419` | No | Comma-separated application response codes that should be alerted. |
| `ServiceBusFullyQualifiedNamespace` | Both | Yes | `<namespace>.servicebus.windows.net` | No | Service Bus namespace used by the retry queue. Authentication uses managed identity. |
| `ServiceBusQueueName` | Both | No | `claim-retry` in code | No | Retry queue name. |
| `ServiceBusMessageTtlMinutes` | Both | No | `10` | No | Message lifetime for claim retries and direct RTA writeback retries. Direct writeback retries are scheduled with backoff until this deadline; unsuccessful messages are dead-lettered. Configure the queue to dead-letter expired messages so messages expiring while the worker is unavailable remain inspectable. |
| `ServiceBusReceiveBatchSize` | SERVICEBUS | No | `10` | No | Maximum messages retrieved in one Service Bus receive call (the worker continues receiving while other messages are being processed). It does not cap total concurrent processing. |
| `ServiceBusReceiveWaitSeconds` | SERVICEBUS | No | `5` | No | Maximum wait for a Service Bus receive operation when the queue is empty. |
| `ServiceBusDependencyRetrySeconds` | SERVICEBUS | No | `5` | No | Delay before retrying dependency health checks. |
| `ResultWriteMaxAttempts` | Both | No | `3` | No | Maximum number of RTA status PUT attempts in each writeback call. After the initial RTA-role attempts fail, the exact translated payload is queued for direct SERVICEBUS-role retries. |
| `ResultWriteRetryDelayMilliseconds` | Both | No | `500` | No | Delay between RTA result-write attempts. |
| `CircuitFailureThreshold` | Both | No | `5` | No | Consecutive failures before the outbound circuit breaker opens. |
| `CircuitOpenSeconds` | Both | No | `30` | No | Duration the outbound circuit breaker remains open. |
| `RequestHistoryStorageConnectionString` | Both | Required for history | secret | **Yes** | Storage connection string used for request-history Table and Blob data. Use a Container Apps secret reference. |
| `RequestHistoryTableName` | Both | No | `ProjectPulseRequestHistory` | No | Azure Table name for request-history metadata. |
| `RequestHistoryBlobContainerName` | Both | No | `projectpulse-history-payloads` | No | Blob container for request/response payload history. |
| `REQUEST_HISTORY_PAYLOAD_MODE` | Both | No | `OFF` (when unset, falls back to legacy setting) | No | `OFF` suppresses payload bodies while preserving transaction metadata; `PARTIAL` masks common identifiers and personal-name fields; `FULL` captures unredacted payloads for synthetic POC data only. |
| `LOG_POLL_PAYLOADS` | RTA | No | `false` | No | Deprecated compatibility setting. Used only when `REQUEST_HISTORY_PAYLOAD_MODE` is unset; `true` maps to `FULL`, `false` maps to `OFF`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Both | Recommended | Azure-provided value | **Yes** | Standard Application Insights connection string consumed by ASP.NET Core Application Insights telemetry. |

## Automatic request-history cleanup

The Requests page has an **Automatically delete old history** checkbox. It is off by default. Configure the retention age (how old a record must be before deletion) separately from the check interval (how often the RTA role looks for expired records). Both values are saved in the shared Azure Table configured by `RequestHistoryStorageConnectionString`, so the setting is shared across the RTA and SERVICEBUS roles and survives app restarts.

When enabled, the RTA role deletes request-history records and poll reconciliation details whose creation/receipt time is older than the configured retention age. It also deletes payload blobs for removed records when no remaining history record references that batch. A successful save that enables the checkbox requires confirmation in the page because the deletion is permanent. Turning the checkbox off stops future scheduled deletion; it does not restore records already deleted.

Retention can be set from 1 minute through 365 days. The check interval can be set from 1 minute through 24 hours. The worker checks the schedule once per minute, so a configured run can begin up to about one minute after its due time. If no settings row exists, the feature remains disabled with defaults of 10 minutes retention and a 10 minute interval. No additional environment variable is required; the shared history Table storage must be available to the RTA role.

## APIM managed-identity authentication

By default, Project Pulse sends an Entra access token with every APIM adjudication POST and validates the configured APIM response token. Configure this for **both** Container Apps (`PROJECT_PULSE_ROLE=RTA` and `PROJECT_PULSE_ROLE=SERVICEBUS`), because both roles can call APIM. The token is acquired through the Container App's managed identity; no client secret or certificate is stored by Project Pulse.

Set `APIM_AUTHENTICATION_ENABLED=false` on both apps only when temporarily testing a legacy APIM policy that does not validate Pulse's bearer token. With this flag false, Project Pulse sends no APIM bearer token, while APIM response-token validation remains enforced. The default is `true`. Disabling bearer-token sending removes APIM's Entra authorization check for the caller, so limit this to a controlled test environment. It does not change RTA/App API or Service Bus authentication.

Set this required environment variable on both apps:

```text
ApimScope=api://<Project-Pulse-API-Verify-application-client-id>/.default
```

Replace the placeholder with the Application (client) ID of the API registration named `Project-Pulse API Verify`. If the registration uses a custom Application ID URI, use that URI followed by `/.default`. The scope must identify the API resource that APIM validates, not the Container App's managed identity client ID.

In **App registrations → Project-Pulse API Verify → App roles**, define an enabled application role:

- Display name: `Project Pulse API Invoke`
- Allowed member types: `Applications`
- Value: `ProjectPulse.Invoke`

Assign this app role to the managed identity service principal for **each** Container App in **Enterprise applications → Project-Pulse API Verify → Users and groups** (or the equivalent app-role assignment workflow). Use the identity each app actually runs as. Role assignments may take several minutes to propagate.

In APIM, add JWT validation to the inbound policy before the existing downstream API client-certificate policy. Keep the existing `<base />` and backend certificate policy. For example:

```xml
<inbound>
    <base />
    <validate-azure-ad-token tenant-id="{{entra-tenant-id}}"
                             failed-validation-httpcode="401"
                             failed-validation-error-message="Unauthorized">
        <audiences>
            <audience>api://<Project-Pulse-API-Verify-application-client-id></audience>
        </audiences>
        <required-claims>
            <claim name="roles" match="any">
                <value>ProjectPulse.Invoke</value>
            </claim>
        </required-claims>
        <!-- Recommended: restrict this to the RTA and SERVICEBUS managed-identity client IDs. -->
        <!--
        <client-application-ids>
            <application-id>{{project-pulse-rta-managed-identity-client-id}}</application-id>
            <application-id>{{project-pulse-servicebus-managed-identity-client-id}}</application-id>
        </client-application-ids>
        -->
    </validate-azure-ad-token>
    <authentication-certificate certificate-id="backend-certificate" />
</inbound>
```

Set the audience to the API registration's Application ID URI (for the default URI, `api://<application-client-id>`). The tenant named value must contain the tenant ID that issued the token. Restricting accepted client application IDs to the two managed identities is recommended defense in depth; use the managed identity **client IDs**, not their principal/object IDs. Keep APIM's existing backend authentication and policies in place. Set the certificate ID to the APIM backend certificate configured in your service. This is separate from Pulse-to-APIM bearer authentication.

After configuration, a request without a valid token or without the `ProjectPulse.Invoke` role should receive HTTP 401 from APIM before reaching downstream API. A valid request should continue to the existing backend flow. The RTA/App API authentication scope remains separate and is not enabled by this APIM change.

To return an explicit diagnostic acknowledgment after APIM validates the Entra token, add this to the existing `<outbound>` policy section (after `<base />`), preserving all current outbound policies:

```xml
<outbound>
    <base />
    <set-header name="X-Pulse-Auth-Validated" exists-action="override">
        <value>true</value>
    </set-header>
</outbound>
```

Project Pulse records whether it attached a bearer token and whether the APIM response included `X-Pulse-Auth-Validated: true`. A present `true` means the request reached APIM's outbound pipeline after inbound token validation succeeded. A missing header means validation was not confirmed; it is not proof by itself that the token was rejected. The access token itself is never stored or shown. The header is diagnostic only and does not replace Entra bearer-token or APIM response-token validation.

## Azure Service Bus retry setup

Project Pulse authenticates to Azure Service Bus with the Container App's managed identity through .NET `DefaultAzureCredential`. Do not configure a Service Bus connection string or SAS key for this integration.

1. Create the retry queue in the Service Bus namespace.
2. Enable a system-assigned managed identity on each Container App, or use the identity already selected for that app.
3. At the queue's **Access control (IAM)** scope, grant these built-in data roles:

| Container App role (`PROJECT_PULSE_ROLE`) | Azure Service Bus role | Why |
| --- | --- | --- |
| `RTA` | `Azure Service Bus Data Sender` | Sends records to the retry queue when a retry is needed. |
| `SERVICEBUS` | `Azure Service Bus Data Receiver` and `Azure Service Bus Data Sender` | Receives, completes, and dead-letters queued messages; it also needs to send replacement messages when a retry is needed. |

Use the queue as the role-assignment scope when possible. The Service Bus role assignments can take several minutes to propagate.

4. Configure the namespace and queue on **both** Container Apps. The namespace is the host name only, without `https://`:

```text
# RTA Container App
PROJECT_PULSE_ROLE=RTA
ServiceBusFullyQualifiedNamespace=<namespace>.servicebus.windows.net
ServiceBusQueueName=<queue-name>
ServiceBusMessageTtlMinutes=10

# Service Bus worker Container App
PROJECT_PULSE_ROLE=SERVICEBUS
ServiceBusFullyQualifiedNamespace=<same-namespace>.servicebus.windows.net
ServiceBusQueueName=<same-queue-name>
ServiceBusMessageTtlMinutes=10
ServiceBusReceiveBatchSize=10
ServiceBusReceiveWaitSeconds=5
ServiceBusDependencyRetrySeconds=5
```

On the admin-only requests page, **Test Service Bus** sends a two-minute diagnostic message from the RTA app to the configured queue. A successful send confirms the RTA managed identity can send; the SERVICEBUS app log stream confirms receipt and completion. The diagnostic is automatically completed without processing it as a claim retry. It does not alter RTA or APIM data.

The namespace and queue name must match between the apps. The `SERVICEBUS` app also needs the shared RTA/App API and APIM settings used by the processor and health gate (including their base URLs, required scopes, and APIM response-token secret reference). Configure request-history storage and RTA notifications there too if those features should record or report Service Bus processing outcomes. The full shared settings are listed in the table above.

## RTA failure notifications

Project Pulse POSTs to `RTANotificationsBaseURL` + `RTANotificationsPath` for catastrophic failures. When the RTA poller's APIM health probe first reports an outage, it also sends one admin notification with the health-check status/detail and configured outage mode. Under the default `BUFFER_THEN_STOP` mode, the alert is sent while the poller is still fetching RTA records and buffering them to Service Bus; after the configured threshold the RTA GET polling suspends until APIM recovers. It does not send a separate alert for each buffered record. A downstream API JSON error envelope containing `errorCode` (for example, `ACC-NOTF-001` with HTTP 404) is treated as an application error and written directly back to RTA. Other actual APIM HTTP statuses except 200 are buffered to Service Bus for one fixed `ServiceBusMessageTtlMinutes` retry window (10 minutes by default); successfully queuing that APIM failure does not trigger an admin notification. Retries use bounded backoff and preserve the latest HTTP status, response body, and response headers. If no successful APIM response arrives before the deadline, Project Pulse sends a final RTA failure status containing the last APIM response details in `API_CALL_HISTORY`. An inability to enqueue or schedule a retry triggers an admin notification. If that final RTA writeback cannot be delivered during its own direct retry window, the Service Bus worker sends an admin notification and dead-letters the message. A completely stopped worker cannot report its own outage and must be monitored through Azure.

Status-like values inside downstream API JSON are application data, not HTTP statuses. For example, an HTTP 200 response whose JSON contains `"status": 404` does not trigger HTTP retry or notification handling. Notifications set `EXTERNAL_CORRELATION_ID` to JSON `null`, include `REQUEST_ID` and `REQUEST_QUEUE_ID` when present in the RTA item, and include concise diagnostics in `NOTIFICATION_ATTRS`; raw request/response payloads and patient identifiers are not included. Notifications use `AppApiScope` for RTA authorization.

## POC RTA settings currently used

The current POC RTA deployment uses overrides including:

```text
PROJECT_PULSE_ROLE=RTA
POLL_INTERVAL_SECONDS=3
APP_API_MODE=MOCK
x-api-key=<secret reference>

RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaDbHealthPath=/health
RtaStatusWritebackEnabled=true

ApimAdjudicationPath=pulse/pending
ApimResponseHeaderName=X-Pulse-Response-Token

ServiceBusQueueName=projectpulsequeue

CircuitFailureThreshold=5
CircuitOpenSeconds=30
```

Environment-specific URLs, namespaces, tokens, storage connection strings, and telemetry values should be supplied by the deployment rather than committed to the repository.

## Container Apps secret mapping example

Create the secret, then map the exact lowercase environment variable `x-api-key` to it on both Container Apps:

```text
Secret: rta-api-key
Environment variable: x-api-key
Source: Reference a secret -> rta-api-key
```

Project Pulse sends this value as an HTTP header on RTA queued-request GETs, status PUTs, and RTA health probes. It is not put in request bodies or written to logs.


Create the Container Apps secrets first, then reference them from environment variables:

```text
Secret: apim-response-token
Environment variable: ApimResponseToken
Source: Reference a secret -> apim-response-token

Secret: request-history-storage
Environment variable: RequestHistoryStorageConnectionString
Source: Reference a secret -> request-history-storage
```

For real claims or other sensitive data, keep `LOG_POLL_PAYLOADS=false`.
