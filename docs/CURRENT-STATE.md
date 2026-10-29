# Current State

Last reviewed: 2026-10-28  
Repository version: `0.0.66` (next build; deployment and active Container Apps revision are verified separately.)

Project Pulse App Container is the ASP.NET Core / .NET 10 Azure Container Apps implementation of Project Pulse. This file records repository behavior and the POC observations shared so far; environment-specific configuration and production readiness must be verified separately.

## Implemented in the repository

- The same container image supports the `RTA` polling role and the `SERVICEBUS` retry-processing role, selected with `PROJECT_PULSE_ROLE`. The RTA role continuously polls; this is not an Azure Functions TimerTrigger application.
- `APP_API_MODE` selects RTA or mock source behavior. RTA uses paging; the mock path uses `maxRecords`. Do not assume the mock query parameter is sent to the RTA endpoint.
- The processor reads initial request input fields from RTA `INITIAL_REQUEST_METADATA`, translates them to the APIM/DOCMP request contract, and processes each claim individually. APIM requests carry correlation/idempotency information and validate the configured response-token header.
- For initial-request writeback, DOCMP business fields are read from the root response object and translated into RTA `INITIAL_REQUEST_RESPONSE`; RTA's `INITIAL_REQUEST_METADATA` is only the request-side source shape.
- APIM responses are translated into the RTA writeback shape. The original individual RTA item and raw APIM response are retained as JSON strings in `PAYLOAD_REQUEST` and `PAYLOAD_RESPONSE`; the mapped response is included in the appropriate request-type response field. RTA status writeback is configurable.
- Request-history exchange outcomes show stored RTA status PUT attempt metadata when available. If the final batch status confirms a PUT failure but attempt details are missing, the page shows the failure and directs operators to the log stream.
- APIM transport errors and DOCMP JSON errors are separate. Only an actual APIM HTTP status other than 200 enters the HTTP retry/notification path: statuses in `RetryHttpStatusCodes` are queued to Service Bus, and every actual non-200 response triggers an RTA `/admin/notifications` POST. A JSON field such as `"status": 404` inside an HTTP 200 DOCMP response is application data; it does not trigger HTTP queueing or notification.
- When an HTTP 200 APIM response body contains a DOCMP error object (for example, `errorCode`, `message`, `errors`, or an embedded error `status`), Project Pulse treats it as a final application error and sends an RTA status update with top-level `STATUS: FAILURE` and both `INITIAL_REQUEST_RESPONSE` and `UPDATE_REQUEST_RESPONSE` set to `null`. `API_CALL_HISTORY.RESPONSE_HTTP_STATUS_CODE` records the actual HTTP code; the raw APIM JSON is kept in `RESPONSE_BODY`, error code/message/details go in `ERROR_MESSAGE`, and `ERROR_STACK` is set only when a stack field is supplied. History retains the response body subject to `REQUEST_HISTORY_PAYLOAD_MODE` redaction.
- RTA notifications use `RTANotificationsBaseURL` and `RTANotificationsPath`. They keep `EXTERNAL_CORRELATION_ID` null, include the RTA `REQUEST_ID`/`REQUEST_QUEUE_ID` when available, and carry concise non-payload diagnostics in notification attributes. Failure to enqueue a retry envelope also triggers an RTA notification; if the APIM response was non-200, that notification reports both failures.
- RTA writeback failure logs report the actual number of attempts made, which may be fewer than the configured maximum for non-retryable HTTP responses.
- Configured APIM failure handling can buffer retry envelopes in Azure Service Bus. A Service Bus role can consume and reprocess those envelopes through the shared processor. Service Bus connectivity/certificate configuration is independent of the RTA client certificate trust setup.
- Request history stores transaction metadata in Azure Table Storage and payload bodies in Blob Storage. `REQUEST_HISTORY_PAYLOAD_MODE` supports `FULL`, `PARTIAL`, and `OFF`. `OFF` suppresses payload capture for new history records; it does not remove payloads already stored.
- The authenticated request-history UI uses Microsoft Entra sign-in and the `ProjectPulse.Admin` app role, assigned to the `PROJECT_PULSE_ADMINS` group. The UI shows the signed-in user and provides sign-out/navigation.
- Request details show RTA/mock input, APIM/DOCMP HTTP outcome, and RTA status PUT outcome when recorded. Recent writeback failures include the final attempt's HTTP status or exception category/message; full stack traces remain in the log stream. RTA poll HTTP status is not currently stored per request.
- Per-batch timing records cover RTA pull, translation, APIM round trip, RTA writeback, and end-to-end duration. The request-history UI summarizes averages over selectable time windows and explains possible delay causes using available recorded statuses.
- Health endpoints, Application Insights logging/telemetry, Docker image build, and GitHub Actions deployment are present.

## POC observations and limits

- A POC transaction has returned a successful APIM/DOCMP payload to Project Pulse. That confirms the APIM response path for that transaction; it does not by itself confirm RTA writeback.
- RTA PUT/writeback must be verified against the target environment. Prior Dev observations included writeback failures because the target endpoint was unavailable or not configured for the attempted request.
- Service Bus errors observed in POC included a TLS certificate-name mismatch. That indicates a Service Bus endpoint/certificate validation problem; it does not prove Project Pulse is presenting the RTA client certificate to Service Bus.
- `/pulse-health` is an application health endpoint, not proof that the application can authenticate to or send to every dependency.
- Timing averages describe the stored sample and selected date range. APIM round-trip time includes the gateway-to-backend path; writeback time can include retries and backoff. Use batch logs to confirm a suspected cause.
- Payload mode changes affect future capture. They do not purge old table or blob records.
- In-memory outage/circuit state resets on process restart. A single replica is the documented POC deployment configuration; safe multi-replica processing has not been established.

## POC deployment configuration

The documented POC target uses one minimum and one maximum replica. Production scale, availability, security, retention, and recovery behavior still require validation.

## Not yet established

- End-to-end RTA writeback success in every target environment.
- Durable outage/circuit state and safe multi-replica coordination.
- Production retention/deletion policy for history blobs and table rows.
- Production-grade security review, networking, and operational alerting.
- Sustained throughput limits and production scale targets.
