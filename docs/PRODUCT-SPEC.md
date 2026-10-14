# Project Pulse Product Specification

## Purpose

Project Pulse orchestrates near-real-time claim adjudication. It retrieves queued claim work from an internal RTA/App API, transforms requests into the APIM contract, submits them for adjudication, writes successful results back to the source system when enabled, and buffers work to Azure Service Bus during defined downstream failures.

## Primary goals

- Keep Facets/RTA users from waiting on batch-oriented processing.
- Poll the source API on a predictable cadence.
- Support batches of up to 300 records per poll through paging.
- Keep RTA polling health-aware.
- Preserve correlation and idempotency metadata.
- Continue short-term intake during APIM outages by buffering to Service Bus.
- Stop taking new work when downstream failure exceeds the configured outage threshold.
- Resume automatically after dependency recovery.
- Keep behavior configuration-driven.
- Use Managed Identity wherever possible.

## Current POC flow

1. Timer trigger executes once per configured schedule.
2. Project Pulse checks RTA API health, RTA DB/App health, and APIM/backend health.
3. If RTA is unhealthy, the run stops.
4. If APIM is healthy, Project Pulse polls RTA/App API, transforms the returned records, and posts one batch to APIM.
5. If APIM is unavailable and outage policy allows buffering, Project Pulse still polls the source and writes each claim to Service Bus.
6. If APIM remains unavailable beyond the configured threshold, source polling stops.
7. If Service Bus buffering fails while APIM is unavailable, polling stops immediately.
8. When APIM becomes healthy again, normal processing resumes.

## Supported source modes

### RTA mode

`APP_API_MODE=RTA`

Expected queue endpoint:

`GET /requests/queued?page=N&limit=N`

Expected response contains an `Items` array plus paging metadata.

### Mock mode

`APP_API_MODE=MOCK`

Expected queue endpoint for the current POC:

`GET /claims/pending?maxRecords=N`

The mock response can be a raw array or an object containing an `Items` array.

## APIM behavior

Project Pulse posts transformed claims to the configured APIM adjudication path.

The current intended POC progression is:

- APIM health endpoint.
- APIM mock adjudication policy.
- Dynamic fake success response based on incoming claim IDs.
- Later replacement of the fake backend with the real adjudication backend.

## Service Bus behavior

Project Pulse is write-only to the retry queue.

Current retry messages contain:

- UniqueId
- RequestId
- IdempotencyKey
- Reason
- QueuedAtUtc
- OriginalPayload
- TransformedPayload

A separate retry/recovery application will consume and replay buffered work in a later milestone.

## Current success criteria

A healthy-path POC is successful when:

- the mock RTA/App API exposes a queued claim,
- Project Pulse retrieves it,
- Project Pulse transforms it,
- APIM accepts it,
- APIM returns a fake adjudication result,
- Project Pulse processes the response,
- result writeback can be enabled once the response contract is finalized,
- the completed claim is no longer exposed as pending.

## Non-goals for the current baseline

The current baseline does not yet guarantee:

- durable outage state across Function restarts,
- complete duplicate prevention,
- production-grade retry replay,
- production PHI handling controls,
- multi-instance coordination,
- final APIM response/writeback contracts,
- sustained high-throughput production performance.

Those items are handled by later milestones.
