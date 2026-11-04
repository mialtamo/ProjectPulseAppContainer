# Project Pulse RTA polling and APIM outage behavior

Project Pulse supports two runtime roles in the same Container App image. The RTA role polls the RTA/App API, transforms each returned record, sends individual requests to APIM, writes results back to RTA, and buffers eligible failures in Azure Service Bus. The SERVICEBUS role consumes retry messages, replays claim requests through APIM when appropriate, and retries stored RTA writebacks directly.

## APIM outage modes

`APIM_OUTAGE_MODE=BUFFER_THEN_STOP` is the recommended mode.

- APIM healthy: poll RTA and send each transformed record to APIM.
- APIM unavailable below `APIM_OUTAGE_POLL_STOP_SECONDS`: continue polling and buffer eligible APIM failures to Service Bus.
- APIM outage reaches the configured threshold: stop polling RTA.
- APIM recovers: the RTA poller clears its outage state and resumes normal polling.
- RTA API or DB/App unhealthy: stop polling immediately.
- APIM unavailable and Service Bus buffering fails: stop polling until APIM recovers.

Other supported modes are `STOP_IMMEDIATELY` and `BUFFER_CONTINUOUSLY`.

## Mock API paging

Set `APP_API_MODE=MOCK` to use the mock source API. Project Pulse calls `RtaQueuedPath` with `?maxRecords=<RtaPageSize>` and accepts either a raw JSON array or an object containing an `Items` array. This mock query parameter is separate from the RTA endpoint's paging contract. `RtaPageSize` controls the requested records per RTA GET page and `RtaMaxPageSize` controls the application's configurable upper bound. The remote API can impose a lower limit.

For the current Project Pulse API POC:

- `APP_API_MODE=MOCK`
- `RtaQueuedPath=/claims/pending`
- `RtaHealthPath=/health`
- `RtaDbHealthPath=/health`

Set `REQUEST_HISTORY_PAYLOAD_MODE=FULL` only for fake/POC data. Use `PARTIAL` for masked diagnostics or `OFF` for no payload bodies. Transaction metadata remains available in request history. `LOG_POLL_PAYLOADS` is a deprecated compatibility fallback used only when the new setting is unset.

## Service Bus claim retries

The RTA role buffers eligible actual APIM HTTP failures in Service Bus. DOCMP business-error envelopes are handled as application errors and are not retried as transport failures. A successfully enqueued retry does not trigger an admin notification.

Claim retry messages preserve the original RTA request and transformed APIM request. The SERVICEBUS worker retries APIM within the original message deadline, 10 minutes by default. Retry delays grow from 5 to 10, 20, and 40 seconds, then cap at 60 seconds. Each retry remains bounded by the original deadline; it does not restart or extend the window. If APIM does not succeed before expiry, Project Pulse writes a final failure result to RTA containing the latest APIM response details. A failure to enqueue or schedule the retry is notified.

## Direct RTA writeback retries

If APIM has already responded but the RTA status PUT fails, Project Pulse stores the exact translated PUT body in a distinct retry message. The SERVICEBUS worker calls RTA directly and never replays APIM for that message. These attempts use increasing scheduled delays capped at 60 seconds until the message TTL deadline, 10 minutes by default. A retry is attempted only when its scheduled delay fits before the deadline. If unsuccessful, the message is dead-lettered; configure Service Bus dead-letter-on-expiration so messages expiring while the worker is offline remain inspectable. Direct writeback retries are gated by RTA health, not APIM health.

## Service Bus concurrency

The SERVICEBUS role uses the shared `PROCESSING_CONCURRENCY_MODE`, `PROCESSING_MAX_CONCURRENCY`, and `PROCESSING_MAX_CONCURRENCY_LIMIT` settings. The default mode `SEQUENTIAL` processes one message at a time. `RTA_ONLY`, `APIM_ONLY`, and `FULL` enable message concurrency while shared gates limit APIM and RTA calls according to the selected stages. The requested concurrency defaults to 5 and is clamped by the configurable limit, default 300. `ServiceBusReceiveBatchSize` controls the size of each receive call, not total in-flight concurrency. Concurrency applies per replica.
