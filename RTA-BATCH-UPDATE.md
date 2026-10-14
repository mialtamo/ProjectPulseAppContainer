# Project Pulse RTA polling and APIM outage behavior

Project Pulse polls the RTA/App API, transforms queued requests, and sends them to APIM. Project Pulse is write-only to Service Bus. A separate recovery application is responsible for consuming Service Bus messages.

## APIM outage modes

`APIM_OUTAGE_MODE=BUFFER_THEN_STOP` is the recommended mode.

- APIM healthy: poll RTA and send the transformed batch to APIM.
- APIM unhealthy for less than `APIM_OUTAGE_POLL_STOP_SECONDS`: continue polling RTA and buffer each record to Service Bus.
- APIM unhealthy at or beyond the threshold: stop polling RTA.
- APIM recovery: clear the outage state and resume normal polling.
- RTA API or DB/App unhealthy: stop polling immediately.
- APIM unavailable and Service Bus buffering fails: stop polling until APIM recovers.

Other supported modes:

- `STOP_IMMEDIATELY`
- `BUFFER_CONTINUOUSLY`

## POC mock API

Set `APP_API_MODE=MOCK` to use the existing mock API response shape. In mock mode Project Pulse calls `RtaQueuedPath` with `?maxRecords=<RtaPageSize>` and accepts either a raw JSON array or an object containing an `Items` array.

For the current Project Pulse API POC:

- `APP_API_MODE=MOCK`
- `RtaQueuedPath=/claims/pending`
- `RtaHealthPath=/health`
- `RtaDbHealthPath=/health`

Set `LOG_POLL_PAYLOADS=true` only for fake/POC data. Disable it before processing real claim data.

## Service Bus retry envelope

Fallback messages contain:

- `UniqueId` (RTA correlation ID, or mock `UniqueId`)
- `RequestId`
- `IdempotencyKey`
- `Reason`
- `QueuedAtUtc`
- `OriginalPayload`
- `TransformedPayload`

Project Pulse does not consume these messages.
