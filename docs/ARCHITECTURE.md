# Project Pulse App Container Architecture

Project Pulse App Container is a .NET 10 ASP.NET Core application hosted in Azure Container Apps.

## Runtime model

The container stays alive and runs a continuous `BackgroundService` instead of an Azure Functions timer trigger.

```text
ClaimPollingWorker
    |
    +--> HealthGate
    +--> AppApiClient
    +--> ClaimProcessor
          |
          +--> APIM/backend
          +--> RTA writeback
          +--> request history
          +--> Service Bus fallback

ServiceBusWorker
    |
    +--> claim retry envelope --> ClaimProcessor --> APIM/backend --> RTA
    +--> writeback retry envelope -----------------------------> RTA
```

## Request routing and translation

Project Pulse processes each RTA record separately. The root-level `REQUEST_TYPE` selects the APIM operation: `UPDATE_REQUEST` uses the configured relative update path with HTTP PUT; other requests use the general adjudication path with HTTP POST. If the update path is unset, update requests fall back to the general path. Update bodies are mapped from `UPDATE_REQUEST_METADATA` into the downstream update contract, including accumulator adjustment items. The record's `IDEMPOTENCY_KEY` is sent as the `Idempotency-Key` header for APIM to forward downstream. Update writeback sets `INITIAL_REQUEST_METADATA` to null and stores the raw APIM response JSON in `UPDATE_REQUEST_METADATA`; initial-request handling is unchanged.

## Polling model

`POLL_INTERVAL_SECONDS` controls the application polling interval. Default is 5 seconds; supported range is 1 through 300 seconds.

The worker does not overlap polling cycles within a single replica. For the POC, run exactly one replica.

### Per-record concurrency

Within an RTA polling batch, `PROCESSING_CONCURRENCY_MODE` selects which stages may overlap: `SEQUENTIAL` keeps APIM/backend and RTA PUTs serial; `RTA_ONLY` overlaps RTA PUTs; `APIM_ONLY` overlaps APIM/backend calls; `FULL` overlaps both. `PROCESSING_MAX_CONCURRENCY` caps each enabled stage (default 5, clamped to 1-100). The limit is per active batch and per replica, so replicas multiply potential downstream concurrency. Poll cycles remain non-overlapping. These settings do not control the Service Bus worker, which currently processes one queued record at a time.

## URL construction

Configured API base URLs are normalized with one trailing separator, and endpoint paths are normalized before requests are sent. This accepts path settings with or without leading/trailing slashes, removes duplicate separators at and within the path boundary, preserves a base URL path prefix, and keeps configured query parameters. The HTTP and HTTPS scheme separator remains intact.

## Container

The application listens on port 8080.

```text
ASPNETCORE_URLS=http://+:8080
```

Container Apps ingress should target port 8080.

## Identity

The application uses `DefaultAzureCredential`. In Azure Container Apps this resolves to the Container App managed identity when configured.

The Container App identity needs its own downstream RBAC and permissions.

## State

APIM outage tracking and circuit-breaker state are process-local for the POC and reset when the container restarts or a new revision starts.

Request history is stored in Azure Table and Blob storage. The secured `/ledger.html` view reads the existing request-history API and reconstructs a lifecycle timeline from those stored milestones; it is not a separate event ledger. Search and filters operate over the indexed history API. An APIM send timestamp is recorded before the network POST, so the ledger reports an attempt, not confirmed APIM receipt; missing downstream milestones mean no outcome was stored.


## Service Bus retry message types

The queue carries two independent retry paths:

- Claim retry messages preserve the original RTA request and transformed APIM request. The SERVICEBUS role sends these through the claim processor and APIM/backend.
- RTA writeback retry messages preserve the exact translated RTA status PUT body after APIM/backend has already responded. The SERVICEBUS role sends these directly to RTA and never replays APIM/backend. It schedules retries with increasing delay until the configured message TTL deadline, then dead-letters the message if the PUT still fails.

Direct writeback retries require RTA health only; they continue independently of APIM health.
