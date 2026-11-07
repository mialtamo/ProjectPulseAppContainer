# Project Pulse Product Specification

## Purpose

Project Pulse moves queued claim requests from RTA through APIM/backend, translates the response, and writes the result back to RTA. It records operational history and timing, and can buffer eligible work in Azure Service Bus during configured downstream failures.

This specification describes the current Container Apps implementation and its POC boundaries, not a production-readiness claim.

## Runtime and deployment model

Project Pulse is a .NET 10 ASP.NET Core container. One codebase/image supports two runtime roles selected by `PROJECT_PULSE_ROLE`:

- **RTA role:** continuously polls the configured source API.
- **Service Bus role:** receives both claim replay messages and stored RTA writeback messages. Claim replays use APIM/backend; stored writeback retries call RTA directly.

The POC is configured for one replica. There is no Azure Functions TimerTrigger, `host.json`, or `POLL_SCHEDULE` execution model in this application.

## Request flow

1. The RTA role checks configured dependency health and polls the source.
2. In RTA mode, it requests paged data. In mock mode, the configured mock endpoint may use `maxRecords`; that parameter is not the RTA paging contract.
3. Each RTA item is translated to the APIM JSON contract and processed as an individual request. The incoming root-level `REQUEST_TYPE` selects the APIM route: `UPDATE_REQUEST` uses `ApimUpdateAdjudicationPath` with HTTP PUT; other request types use `ApimAdjudicationPath` with HTTP POST. If the update path is unset, update requests fall back to the general path for compatibility.
4. Project Pulse sends the request with correlation/idempotency information and validates the configured APIM response-token header.
5. It records the HTTP response body and translates a successful response into the RTA writeback contract.
6. When enabled and configured, it sends the writeback to RTA. If the PUT fails after its configured attempts, Project Pulse queues the exact translated RTA status body for the SERVICEBUS role.
7. The SERVICEBUS role retries that stored PUT directly to RTA with increasing scheduled delays until the message TTL deadline (10 minutes by default); it never sends that writeback through APIM/backend again. Eligible APIM claim failures are separately replayed through APIM/backend.

The source item, APIM request, APIM response, mapped RTA response, status, and timing values are captured according to request-history settings.

## Configuration and integrations

- `APP_API_MODE`: source behavior, including `RTA` and `MOCK`.
- `PROJECT_PULSE_ROLE`: polling or Service Bus runtime role.
- `PROJECT_PULSE_ENVIRONMENT`: environment label shown alongside the image version and runtime role on the landing, request-history, lifecycle, and batch pages.
- `POLL_INTERVAL_SECONDS`: RTA polling cadence, subject to application bounds.
- RTA paging settings control page size and number of pages; mock-mode `maxRecords` is separate.
- APIM endpoint, credentials/token validation, and response mapping are configured independently.
- RTA writeback is controlled by its writeback setting and target API configuration.
- Azure Table Storage stores searchable history metadata; Blob Storage holds captured payload bodies.
- `REQUEST_HISTORY_PAYLOAD_MODE` controls capture of new payloads:
  - `FULL`: retain complete captured payloads.
  - `PARTIAL`: mask selected sensitive fields while retaining diagnostic structure. This is targeted masking, not a guarantee of complete de-identification.
  - `OFF`: retain transaction metadata without payload bodies. Existing stored bodies are not deleted by changing the mode.
- Microsoft Entra authentication protects the request-history UI. Access is intended for users assigned the `ProjectPulse.Admin` app role, currently assigned through `PROJECT_PULSE_ADMINS`.

## Response translation and writeback

For `INITIAL_REQUEST`, Project Pulse reads RTA request fields from `INITIAL_REQUEST_METADATA`, then translates the downstream API response's root business fields into `INITIAL_REQUEST_RESPONSE`. The request-side RTA metadata field is not used to parse the downstream API response. `UPDATE_REQUEST_RESPONSE` is set to null. Project Pulse sends the RTA PUT envelope with the request type, success status, null substatus, and process timestamp.

For `UPDATE_REQUEST`, Project Pulse sends the mapped update body to the separate APIM endpoint with HTTP PUT. The default body takes `patientIcn`, `startDateOfService`, `claimNumber`, and `source` from `UPDATE_REQUEST_METADATA` and represents each provided adjustment as an `adjustments` array item containing an `updateAccumulator` with its accumulator name and delta. On writeback, `INITIAL_REQUEST_METADATA` is null and the raw DOCMP response JSON is placed in `UPDATE_REQUEST_METADATA`. This update-specific handling does not change the existing `INITIAL_REQUEST` response translation or metadata.

The envelope includes an application-created `API_CALL_HISTORY` object. It copies `REQUEST_ID`, `CORRELATION_ID`, and `IDEMPOTENCY_KEY` from the individual RTA item and leaves `EXTERNAL_CORRELATION_ID`, query parameters, and error fields null. `HTTP_METHOD` records the actual APIM method (`POST` for the general route or `PUT` for `UPDATE_REQUEST`); `API_URL` is the configured APIM base URL normalized with a trailing slash; and `API_ENDPOINT` records the selected adjudication path without leading or trailing slashes. `REQUEST_HEADER` (singular) contains the serialized headers received from APIM, with the configured response-token value redacted. `REQUEST_BODY` is the raw individual RTA item as a JSON string, `RESPONSE_BODY` is the raw APIM response as a JSON string, and response status and APIM round-trip duration are recorded.

A multi-item polling envelope is not used as the per-record request payload. Translation and successful APIM response receipt do not guarantee that an RTA PUT succeeds; the target environment's endpoint and contract must be validated.

## History, timings, and UI

The secured request-history pages show transaction status, destination, processor role, HTTP/business details when available, payload panels according to the selected payload mode, searchable time-range filters, matching-versus-loaded record counts, and per-record-run durations for:

- RTA pull
- Translation
- APIM round trip
- RTA writeback
- End-to-end processing

The averages view supports preset and custom time ranges. Its timing cards average one record-processing run at a time; concurrency means these durations overlap and must not be summed to estimate total wall time. The page separately reports observed RTA throughput from completed poll runs: received record count divided by the wall-clock span from the first receipt to the last poll completion. This includes idle gaps between polls and excludes later Service Bus replays. Use a range around a specific test when comparing its elapsed throughput. The request list supports 5-minute, 1-hour, 24-hour, 7-day, and all-time filters; the displayed match count reflects the selected range and search, while the loaded count reflects pagination. `Other processing` reports per-run end-to-end elapsed time less the measured translation, APIM round trip, and RTA writeback stages. Explanations of delays are based on observed history/status data and are diagnostic indicators, not definitive root-cause analysis. APIM round-trip timing includes the gateway/backend/network path; RTA writeback timing may include retries and backoff.

The admin-only `/ledger.html` page adds a lifecycle-oriented view over the same stored request-history API. It filters by time range, status, destination, and processor role; searches correlation/unique ID, RTA request ID, and batch ID; and reports total matches separately from the current page. Its timeline shows only stored milestones and intentionally omits payload bodies. The APIM timestamp marks when Project Pulse recorded an attempt before making the POST, not proof of receipt by APIM; a missing later outcome means that outcome is not present in stored history.

## Resilience and operational boundaries

- Service Bus buffering is for configured outage cases; it is not a guarantee that every failure is buffered.
- Outage/circuit tracking is process-local and is reset by restart.
- The POC's single-replica setting avoids some coordination concerns but does not establish scale-out safety.
- `/pulse-health` reports application health and is not a full proof of every downstream authentication or send operation.
- Service Bus TLS trust and RTA client-certificate requirements are separate integration concerns.
- Payload-mode changes do not purge existing history. Admins can manually delete request-history records for the last 24 hours, 7 days, 30 days, or all time from the secured history UI. Cleanup removes unreferenced batch payload blobs while preserving blobs still used by retained records. Automated retention remains a separate operational policy.
- APIM POC success, RTA writeback success, and Service Bus recovery success must be measured independently per environment.

## Product goals

- Process queued claims predictably and preserve correlation/idempotency context.
- Make each translation and external request observable.
- Return the appropriate translated result to RTA when writeback is enabled.
- Retain useful operational history while allowing payload capture to be reduced or disabled.
- Provide authorized operators with transaction and latency visibility.
- Validate durable recovery, retention, security, and scale before production use.
