# Project Pulse Product Specification

## Purpose

Project Pulse moves queued claim requests from RTA through APIM/DOCMP, translates the response, and writes the result back to RTA. It records operational history and timing, and can buffer eligible work in Azure Service Bus during configured downstream failures.

This specification describes the current Container Apps implementation and its POC boundaries, not a production-readiness claim.

## Runtime and deployment model

Project Pulse is a .NET 10 ASP.NET Core container. One codebase/image supports two runtime roles selected by `PROJECT_PULSE_ROLE`:

- **RTA role:** continuously polls the configured source API.
- **Service Bus role:** receives buffered retry work and reprocesses it through the shared processing flow.

The POC is configured for one replica. There is no Azure Functions TimerTrigger, `host.json`, or `POLL_SCHEDULE` execution model in this application.

## Request flow

1. The RTA role checks configured dependency health and polls the source.
2. In RTA mode, it requests paged data. In mock mode, the configured mock endpoint may use `maxRecords`; that parameter is not the RTA paging contract.
3. Each RTA item is translated to the APIM/DOCMP JSON contract and processed as an individual claim request.
4. Project Pulse sends the request with correlation/idempotency information and validates the configured APIM response-token header.
5. It records the HTTP response body and translates a successful response into the RTA writeback contract.
6. When enabled and configured, it sends the writeback to RTA. Writeback failure is recorded and may be retried according to configured behavior.
7. Under configured APIM outage conditions, eligible work can be placed on Service Bus. The Service Bus role can consume and replay buffered work.

The source item, APIM request, APIM response, mapped RTA response, status, and timing values are captured according to request-history settings.

## Configuration and integrations

- `APP_API_MODE`: source behavior, including `RTA` and `MOCK`.
- `PROJECT_PULSE_ROLE`: polling or Service Bus runtime role.
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

The APIM/DOCMP response remains available as the original response JSON string in `PAYLOAD_RESPONSE`. The source RTA item associated with the processed claim is retained as `PAYLOAD_REQUEST`; a multi-item polling envelope is not used as the per-record request payload.

Project Pulse also maps response fields to the RTA PUT shape, including status, response code, process timestamp, identifiers, and the response object for the applicable request type. Translation and successful APIM response receipt do not guarantee that an RTA PUT succeeds; the target environment's endpoint and contract must be validated.

## History, timings, and UI

The secured request-history pages show transaction status, destination, HTTP/business details when available, payload panels according to the selected payload mode, and per-batch durations for:

- RTA pull
- Translation
- APIM round trip
- RTA writeback
- End-to-end processing

The averages view supports preset and custom time ranges. Explanations of delays are based on observed history/status data and are diagnostic indicators, not definitive root-cause analysis. APIM round-trip timing includes the gateway/backend/network path; RTA writeback timing may include retries and backoff.

## Resilience and operational boundaries

- Service Bus buffering is for configured outage cases; it is not a guarantee that every failure is buffered.
- Outage/circuit tracking is process-local and is reset by restart.
- The POC's single-replica setting avoids some coordination concerns but does not establish scale-out safety.
- `/pulse-health` reports application health and is not a full proof of every downstream authentication or send operation.
- Service Bus TLS trust and RTA client-certificate requirements are separate integration concerns.
- Payload-mode changes do not purge existing history. Retention and deletion need a separate operational policy.
- APIM POC success, RTA writeback success, and Service Bus recovery success must be measured independently per environment.

## Product goals

- Process queued claims predictably and preserve correlation/idempotency context.
- Make each translation and external request observable.
- Return the appropriate translated result to RTA when writeback is enabled.
- Retain useful operational history while allowing payload capture to be reduced or disabled.
- Provide authorized operators with transaction and latency visibility.
- Validate durable recovery, retention, security, and scale before production use.
