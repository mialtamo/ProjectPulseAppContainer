# Project Pulse Milestones

Statuses reflect the repository and POC evidence reviewed on 2026-10-22. “Complete” means the stated scope is implemented/documented for the POC; it does not certify production readiness. Environment-specific integrations must be verified in their target environment.

## M0 - Baseline and development framework — Complete

The repository has product, architecture, security, milestone, current-state, and development documentation. Current behavior and known limitations are recorded.

## M1 - APIM/DOCMP request and response path — Complete for POC

Project Pulse transforms and submits claim requests, validates the configured response token, processes the APIM response, and records the request/response. A successful POC response has been observed. APIM policy and environment configuration remain external to this repository.

## M2 - RTA writeback — In progress

The response translation and configurable RTA writeback path are implemented. The request payload is the individual source item; the APIM response is preserved as raw JSON; mapped response fields are written in the RTA response contract.

Remaining: verify successful PUT and expected status transitions in each target environment, including failure/retry behavior. A successful APIM response is not sufficient acceptance evidence.

## M3 - Failure handling and Service Bus — In progress

Configured APIM outage handling can buffer work; the Service Bus runtime role can receive and reprocess retry envelopes. Health gating, failure logging, and RTA notifications for actual APIM HTTP non-200 responses and Service Bus enqueue failures are implemented in the repository. DOCMP error codes embedded in an HTTP 200 JSON response remain application responses, not HTTP retry or notification triggers.

Remaining: exercise and document the full failure/recovery matrix in the target environment, including Service Bus TLS/configuration, APIM timeout/429/5xx, buffering failure, and recovery. Confirm no claim is silently lost.

## M4 - Idempotency and duplicate protection — In progress

Correlation and idempotency values are propagated through the processing path.

Remaining: establish and validate duplicate behavior across redelivery, Service Bus replay, process restarts, and RTA polling. Propagating an idempotency key alone does not prove end-to-end duplicate prevention.

## M5 - Retry processing — In progress

A Service Bus consumer role exists in the same application/image; the older plan for a separate Function/App is not the current implementation.

Remaining: verify retry scheduling, poison/dead-letter handling, operational recovery, and behavior under dependency outages. Decide whether a separate deployment is needed based on operational evidence.

## M6 - Security and payload controls — In progress

Entra authentication and the `ProjectPulse.Admin` app role are implemented for request-history access. `REQUEST_HISTORY_PAYLOAD_MODE` supports FULL, PARTIAL, and OFF for new records.

Remaining: production security review, least-privilege identity/access checks, networking/TLS review, and confirmation of sensitive-data handling. PARTIAL masking is not a complete de-identification guarantee; changing to OFF does not delete existing stored payloads.

## M7 - Observability and operations — In progress

Request history, per-batch stage timings, selectable average windows, status-aware delay hints, Application Insights telemetry, and UI navigation are present.

Remaining: validate metrics and explanations against real environment logs, define actionable alerts and automated retention, and complete operational runbooks. Admin-triggered history cleanup is available in the secured UI. Delay hints are evidence-based possibilities, not automatic root-cause determinations.

## M8 - Durable state and scale-out safety — Planned

Outage/circuit state is currently in process memory and resets on restart. The POC uses one replica.

Deliverables: durable state if required, restart/recovery validation, concurrency analysis, and safe multi-replica processing.

## M9 - Performance and scale — In progress

Azure Container Apps with a continuous worker is the selected POC execution model. The UI includes timing averages and configurable date ranges.

Remaining: load and soak testing, throughput/bottleneck analysis, scale behavior, and a production sizing decision.

## M10 - Production readiness — Planned

Deliverables: regression and integration evidence, environment validation, security and dependency review, deployment/rollback procedure, disaster-recovery considerations, retention policy, alerts, and operational runbooks.

Acceptance requires documented production configuration and successful failure/recovery validation, not merely a successful POC APIM call.
