# Project Pulse Container App configuration

This document is the canonical deployment reference for environment variables used by the Project Pulse Container App.

The same image supports two runtime roles:

- `PROJECT_PULSE_ROLE=RTA` starts the continuous RTA/App API polling worker.
- `PROJECT_PULSE_ROLE=SERVICEBUS` starts the Service Bus retry worker.

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

`RequestHistoryStorageConnectionString` is the Azure Storage connection string used by the request-history implementation for its Azure Table metadata and Blob payload storage.

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
| `APIM_OUTAGE_MODE` | Both | No | `BUFFER_THEN_STOP` | No | Controls behavior during APIM outage: `BUFFER_THEN_STOP`, `STOP_IMMEDIATELY`, or `BUFFER_CONTINUOUSLY`. |
| `APIM_OUTAGE_POLL_STOP_SECONDS` | RTA | No | `300` | No | Outage duration used before the RTA poller stops in buffer-then-stop mode. |
| `CorrelationIdField` | Both | No | `CORRELATION_ID` | No | Input JSON field containing the correlation ID. |
| `ApimBatchRootProperty` | Both | No | `claims` | No | Root property wrapping the single mapped claim in each APIM request body. |
| `ApimFieldMapping` | Both | No | built-in JSON mapping | No | Fallback JSON mapping for request types without a dedicated mapping. |
| `ApimInitialRequestFieldMapping` | Both | No | built-in initial-request mapping | No | JSON mapping for `INITIAL_REQUEST` fields. |
| `ApimUpdateRequestFieldMapping` | Both | No | built-in update-request mapping | No | JSON mapping for `UPDATE_REQUEST` fields, including nested adjustment deltas. |

For `INITIAL_REQUEST`, the outbound target field names use APIM's camel-case JSON contract (`patientIcn`, `sponsorIcn`, `startDateOfService`, `endDateOfService`, `program`, `includeDetails`). Source paths remain the uppercase RTA names under `METADATA`. `IDEMPOTENCY_KEY` is sent in the `Idempotency-Key` HTTP header and is not duplicated in the claim body.
| `RetryHttpStatusCodes` | Both | No | `404,408,429,500,502,503,504` | No | HTTP status codes considered retryable. |
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
| `LOG_POLL_PAYLOADS` | RTA | No | `false` | No | Logs polled claim payloads when true. Keep false for real sensitive/production data unless explicitly required. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Both | Recommended | Azure-provided value | **Yes** | Standard Application Insights connection string consumed by ASP.NET Core Application Insights telemetry. |

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
