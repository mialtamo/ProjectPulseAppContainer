# Project Pulse Container App configuration

This document is the canonical deployment reference for environment variables used by the Project Pulse Container App.

The same image supports two runtime roles:

- `PROJECT_PULSE_ROLE=RTA` starts the continuous RTA/App API polling worker.
- `PROJECT_PULSE_ROLE=SERVICEBUS` starts the Service Bus retry worker.

Request history also stores the runtime role that handled each attempt (`RTA` or `SERVICEBUS`). The Requests page displays this as **RTA Poller** or **Service Bus Worker**, separately from the request destination. Existing records without the field are shown as unknown.

Values shown as defaults are the defaults implemented by the application unless noted otherwise.

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

| Variable | Role | Required | Default / example | Secret | Purpose |
| --- | --- | --- | --- | --- | --- |
| `PROJECT_PULSE_ENVIRONMENT` | Both | No | `POC` | No | Display/environment label used by the UI. |
| `PROJECT_PULSE_VERSION` | Both | No | `0.1.0` | No | Display version shown by the application. This is independent of the ACR image tag. |
| `PROJECT_PULSE_ROLE` | Both | Recommended | `RTA` | No | Selects `RTA` or `SERVICEBUS`. Missing value defaults to `RTA`. |
| `POLL_INTERVAL_SECONDS` | RTA | No | `5` | No | Minimum delay between RTA polling cycles. Supported range is 1-300 seconds. |
| `APP_API_MODE` | RTA | No | `RTA` in code; `MOCK` for POC | No | Selects mock API behavior when set to `MOCK`. |
| `AppApiBaseUrl` | Both | Yes | RTA/mock API base URL | No | Base URL for RTA/App API calls and health checks. |
| `AppApiTimeoutSeconds` | Both | No | `10` | No | HTTP timeout for App API requests. |
| `AppApiScope` | Both | No | empty | No | Optional managed-identity OAuth scope for the App API. |
| `RtaQueuedPath` | RTA | No | `/requests/queued` in code | No | Path used to retrieve queued requests. |
| `RtaHealthPath` | Both | No | `/healthchecks` in code | No | RTA health endpoint path. |
| `RtaDbHealthPath` | Both | No | `/healthchecks/db/app` in code | No | RTA database health endpoint path. |
| `RtaStatusPathTemplate` | Both | No | `/requests/{correlationId}/status` | No | Path template used for result writeback. |
| `RTANotificationsBaseURL` | Both | Required to send notifications | RTA admin API base URL | No | Base URL for the RTA catastrophic-error notification endpoint. |
| `RTANotificationsPath` | Both | No | `/admin/notifications` | No | Path for RTA admin notifications. |
| `RtaPageSize` | RTA | No | `100` | No | Requested RTA page size. Clamped to 1-100. |
| `RtaPagesPerPoll` | RTA | No | `1` | No | Number of pages processed per poll. Clamped to 1-3. |
| `RtaStatusWritebackEnabled` | Both | No | `false` in code | No | Enables result writeback to RTA. |
| `ApimBaseUrl` | Both | Yes | APIM base URL | No | Base URL for APIM. |
| `ApimHealthPath` | Both | No | `/health` | No | APIM health endpoint path. |
| `ApimAdjudicationPath` | Both | No | `/claims/adjudicate` in code | No | APIM adjudication endpoint path. |
| `ApimRequestTimeoutSeconds` | Both | No | `15` | No | APIM request timeout used by Project Pulse processing logic. |
| `ApimScope` | Both | No | empty | No | Optional managed-identity OAuth scope for APIM. |
| `ApimResponseHeaderName` | Both | No | `X-Pulse-Response-Token` | No | Response header containing the APIM response-validation token. |
| `ApimResponseToken` | Both | Yes for validation | secret | **Yes** | Expected APIM response token. Use a Container Apps secret reference. |
| `APIM_MOCK_SCENARIO` | Both | No | empty | No | Optional test control forwarded to APIM as `X-Mock-Scenario`. Supported values: `success`, `docmp-not-found`, `docmp-error`, `http-404`, `http-429`, `http-500`, `http-503`, and `http-504`. Blank omits the header. Configure only when the APIM policy supports mock scenarios. |
| `APIM_OUTAGE_MODE` | Both | No | `BUFFER_THEN_STOP` | No | Controls behavior during APIM outage: `BUFFER_THEN_STOP`, `STOP_IMMEDIATELY`, or `BUFFER_CONTINUOUSLY`. |
| `APIM_OUTAGE_POLL_STOP_SECONDS` | RTA | No | `300` | No | Outage duration used before the RTA poller stops in buffer-then-stop mode. |
| `CorrelationIdField` | Both | No | `CORRELATION_ID` | No | Input JSON field containing the correlation ID. |
| `ApimFieldMapping` | Both | No | built-in JSON mapping | No | Fallback JSON mapping for request types without a dedicated mapping. |
| `ApimInitialRequestFieldMapping` | Both | No | built-in initial-request mapping | No | JSON mapping for `INITIAL_REQUEST` fields. |
| `ApimUpdateRequestFieldMapping` | Both | No | built-in update-request mapping | No | JSON mapping for `UPDATE_REQUEST` fields, including nested adjustment deltas. |

For `INITIAL_REQUEST`, Project Pulse sends one APIM request per RTA record. The body is a root JSON object containing only APIM's six initial-request fields: `patientIcn`, `sponsorIcn`, `startDateOfService`, `endDateOfService`, `program`, and `includeDetails`; it is not wrapped in a `claims` array. Initial-request source fields are read from RTA `INITIAL_REQUEST_METADATA`. DOCMP's response fields are read from the root response object (for example, `patientIcn`, `benefitPeriod`, and the accumulator objects), then translated to RTA's uppercase `INITIAL_REQUEST_RESPONSE` shape. The update-request mapping remains independently configurable. Correlation is sent in the `X-Correlation-ID` HTTP header, and `IDEMPOTENCY_KEY` is sent in the `Idempotency-Key` HTTP header. Query parameters such as `pageSize` and `sort` are not part of the JSON body.

For enabled initial-request writeback, Project Pulse builds the RTA status PUT body with an `API_CALL_HISTORY` object. Its request and response bodies are JSON strings: `REQUEST_BODY` contains the individual raw RTA item and `RESPONSE_BODY` contains the raw APIM response. `HTTP_METHOD` is `POST`; `API_URL` is the APIM base URL from `ApimBaseUrl`, normalized to end with a slash; `API_ENDPOINT` is `ApimAdjudicationPath` with leading and trailing slashes removed (for example, `pulse/pending`). `REQUEST_HEADER` (singular) contains the serialized headers received from APIM, with the configured APIM response-token value redacted. `RESPONSE_HTTP_STATUS_CODE` and `RESPONSE_TIME_MS` describe the APIM response. `EXTERNAL_CORRELATION_ID`, `REQUEST_QUERY_PARAMETER`, `ERROR_MESSAGE`, and `ERROR_STACK` are null.

| `RetryHttpStatusCodes` | Both | No | `404,408,429,500,502,503,504` | No | Actual APIM HTTP status codes considered retryable. A status value inside a JSON response body is never treated as an HTTP status. |
| `JsonResponseCodeField` | Both | No | `RESPONSE_CODE` | No | JSON field inspected for application response codes. |
| `JsonRetryCodes` | Both | No | empty | No | Optional comma-separated application response codes that should be retried. |
| `JsonAlertCodes` | Both | No | `419` | No | Comma-separated application response codes that should be alerted. |
| `ServiceBusFullyQualifiedNamespace` | Both | Yes | `<namespace>.servicebus.windows.net` | No | Service Bus namespace used by the retry queue. Authentication uses managed identity. |
| `ServiceBusQueueName` | Both | No | `claim-retry` in code | No | Retry queue name. |
| `ServiceBusMessageTtlMinutes` | Both | No | `10` | No | TTL assigned to retry messages. |
| `ServiceBusReceiveBatchSize` | SERVICEBUS | No | `10` | No | Maximum number of retry messages received per Service Bus receive cycle. |
| `ServiceBusReceiveWaitSeconds` | SERVICEBUS | No | `5` | No | Maximum wait for a Service Bus receive operation when the queue is empty. |
| `ServiceBusDependencyRetrySeconds` | SERVICEBUS | No | `5` | No | Delay before retrying dependency health checks. |
| `ResultWriteMaxAttempts` | Both | No | `3` | No | Maximum number of RTA result-write attempts. |
| `ResultWriteRetryDelayMilliseconds` | Both | No | `500` | No | Delay between RTA result-write attempts. |
| `CircuitFailureThreshold` | Both | No | `5` | No | Consecutive failures before the outbound circuit breaker opens. |
| `CircuitOpenSeconds` | Both | No | `30` | No | Duration the outbound circuit breaker remains open. |
| `RequestHistoryStorageConnectionString` | Both | Required for history | secret | **Yes** | Storage connection string used for request-history Table and Blob data. Use a Container Apps secret reference. |
| `RequestHistoryTableName` | Both | No | `ProjectPulseRequestHistory` | No | Azure Table name for request-history metadata. |
| `RequestHistoryBlobContainerName` | Both | No | `projectpulse-history-payloads` | No | Blob container for request/response payload history. |
| `REQUEST_HISTORY_PAYLOAD_MODE` | Both | No | `OFF` (when unset, falls back to legacy setting) | No | `OFF` suppresses payload bodies while preserving transaction metadata; `PARTIAL` masks common identifiers and personal-name fields; `FULL` captures unredacted payloads for synthetic POC data only. |
| `LOG_POLL_PAYLOADS` | RTA | No | `false` | No | Deprecated compatibility setting. Used only when `REQUEST_HISTORY_PAYLOAD_MODE` is unset; `true` maps to `FULL`, `false` maps to `OFF`. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Both | Recommended | Azure-provided value | **Yes** | Standard Application Insights connection string consumed by ASP.NET Core Application Insights telemetry. |

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

The namespace and queue name must match between the apps. The `SERVICEBUS` app also needs the shared RTA/App API and APIM settings used by the processor and health gate (including their base URLs, required scopes, and APIM response-token secret reference). Configure request-history storage and RTA notifications there too if those features should record or report Service Bus processing outcomes. The full shared settings are listed in the table above.

## RTA failure notifications

Project Pulse POSTs to `RTANotificationsBaseURL` + `RTANotificationsPath` for actual APIM HTTP statuses other than 200, except when the body is a recognized DOCMP error envelope containing `errorCode`. For example, a DOCMP `ACC-NOTF-001` response with HTTP 404 is treated as an application error: Project Pulse writes the failure and raw DOCMP response back to RTA and does not queue or send a catastrophic notification. Other non-200 APIM responses use the HTTP retry/notification path. Statuses listed in `RetryHttpStatusCodes` are buffered to Service Bus; if buffering fails, the notification includes both failures. A Service Bus enqueue failure in other buffering paths also generates an RTA notification.

Status-like values inside DOCMP JSON are application data, not HTTP statuses. For example, an HTTP 200 response whose JSON contains `"status": 404` does not trigger HTTP retry or notification handling. Notifications set `EXTERNAL_CORRELATION_ID` to JSON `null`, include `REQUEST_ID` and `REQUEST_QUEUE_ID` when present in the RTA item, and include concise diagnostics in `NOTIFICATION_ATTRS`; raw request/response payloads and patient identifiers are not included. Notifications use `AppApiScope` for RTA authorization.

## POC RTA settings currently used

The current POC RTA deployment uses overrides including:

```text
PROJECT_PULSE_ROLE=RTA
POLL_INTERVAL_SECONDS=3
APP_API_MODE=MOCK

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
