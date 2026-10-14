# Project Pulse Milestones

## M0 - Baseline and Development Framework

Goal: establish source-of-truth documentation and milestone-driven development without changing runtime behavior.

Deliverables:

- PRODUCT-SPEC.md
- ARCHITECTURE.md
- SECURITY.md
- MILESTONES.md
- CURRENT-STATE.md
- DEVELOPMENT-PROMPT.md

Acceptance:

- current behavior is documented,
- known limitations are documented,
- milestone boundaries are defined,
- no application behavior changes.

## M1 - APIM Mock Adjudication

Goal: use APIM as the real intermediary while returning a fake adjudication result.

Deliverables:

- APIM `/health`,
- APIM `/claims/adjudicate`,
- dynamic mock response based on incoming claims,
- APIM policies stored in source control,
- POC settings documented.

Acceptance:

- Pulse sees APIM healthy,
- Pulse retrieves and transforms a claim,
- APIM receives the batch,
- APIM returns a fake success response,
- no Service Bus fallback occurs during the healthy path.

## M2 - Complete Success Path and RTA Writeback

Goal: complete the claim lifecycle.

Flow:

```text
RTA -> Pulse -> APIM -> fake adjudication -> Pulse -> RTA writeback
```

Deliverables:

- final POC APIM response contract,
- per-claim response parsing,
- RTA status mapping,
- writeback enabled,
- mock API records updated after processing,
- completed claims no longer returned as pending.

Acceptance:

- a claim is exposed once,
- successfully adjudicated,
- written back,
- reaches a terminal success state,
- does not reappear on the next poll.

## M3 - Failure Handling and Service Bus

Goal: validate the complete failure-state matrix.

Scenarios:

- RTA down,
- APIM down under threshold,
- APIM down beyond threshold,
- APIM recovery,
- APIM down plus Service Bus send failure,
- APIM timeout,
- 429,
- 5xx,
- 419 alert-only behavior.

Deliverables:

- automated tests,
- APIM test policies or switches for synthetic failures,
- outage/recovery telemetry.

Acceptance:

- every tested condition produces the intended state transition,
- no claim is silently discarded.

## M4 - Idempotency and Duplicate Protection

Goal: prevent duplicate business transactions.

Define the roles of:

- CORRELATION_ID,
- IDEMPOTENCY_KEY,
- REQUEST_ID,
- Service Bus MessageId,
- APIM Idempotency-Key.

Deliverables:

- documented idempotency strategy,
- duplicate claim protections,
- Service Bus duplicate strategy,
- repeated-delivery tests,
- source-side claim reservation/claiming strategy if available.

Acceptance:

- repeat delivery cannot accidentally create two adjudication transactions.

## M5 - Service Bus Recovery Processor

Goal: add a separate retry/recovery component.

Flow:

```text
Project Pulse -> Service Bus -> Recovery Processor -> APIM -> RTA
```

Deliverables:

- separate Function/App,
- Service Bus receiver,
- retry/backoff,
- dependency checks,
- poison-message handling,
- DLQ handling,
- APIM replay,
- RTA writeback.

Acceptance:

- a buffered claim survives an outage and completes after recovery,
- Project Pulse itself remains write-only to the retry queue.

## M6 - Security Hardening

Goal: harden production data handling and access.

Deliverables:

- payload logging disabled outside approved POC/dev environments,
- final decision on retry payload vs identifier-only design,
- protected health endpoint,
- least-privilege RBAC,
- private networking,
- TLS enforcement,
- Key Vault integration for unavoidable secrets,
- PHI-safe telemetry.

Acceptance:

- no claim body appears in production telemetry,
- access is least privilege,
- sensitive data locations are documented and controlled.

## M7 - Observability and Operations

Goal: make every claim traceable end-to-end.

Deliverables:

- consistent correlation across RTA, Pulse, APIM, Service Bus, recovery,
- dashboards,
- alerts,
- latency/failure/queue metrics,
- better Service Bus health validation,
- operational runbook inputs.

Acceptance:

- operations can trace a claim by correlation ID,
- major failure modes generate actionable telemetry.

## M8 - Resiliency and Durable State

Goal: make outage behavior survive restarts and scaling.

Deliverables:

- durable APIM outage timestamp,
- durable circuit state if retained,
- recycle/restart tests,
- safe multi-instance behavior,
- concurrency/race analysis.

Acceptance:

- host recycle or scaling does not reset outage rules in an unsafe way or cause duplicate processing.

## M9 - Performance and Scale

Goal: validate production throughput and choose the long-term execution model.

Test targets where practical:

- 100 claims/sec,
- 300 claims/sec,
- 1,000 claims/sec,
- 3,000+ claims/sec.

Deliverables:

- batch-size testing,
- paging testing,
- APIM latency testing,
- Service Bus throughput testing,
- Function CPU/memory testing,
- architecture decision: continuous polling worker vs Container Apps worker vs Service Bus-first distribution.

Acceptance:

- sustainable throughput and bottlenecks are documented,
- production execution model is selected.

## M10 - Production Readiness

Goal: final production gate.

Deliverables:

- CI regression tests,
- dependency/vulnerability scanning,
- environment validation,
- deployment strategy,
- rollback procedure,
- DR considerations,
- operational runbook,
- final security review,
- final architecture documentation.

Acceptance:

- automated build and regression suite pass,
- production configuration is validated,
- failure/recovery tests pass,
- accepted limitations are explicitly documented.
