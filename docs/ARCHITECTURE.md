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
          +--> APIM/DOCMP
          +--> RTA writeback
          +--> request history
          +--> Service Bus fallback

ServiceBusWorker
    |
    +--> claim retry envelope --> ClaimProcessor --> APIM/DOCMP --> RTA
    +--> writeback retry envelope -----------------------------> RTA
```

## Polling model

`POLL_INTERVAL_SECONDS` controls the application polling interval. Default is 5 seconds; supported range is 1 through 300 seconds.

The worker does not overlap polling cycles within a single replica. For the POC, run exactly one replica.

### Per-record concurrency

Both roles use `PROCESSING_CONCURRENCY_MODE` (`SEQUENTIAL`, `RTA_ONLY`, `APIM_ONLY`, or `FULL`), `PROCESSING_MAX_CONCURRENCY` (default 5), and `PROCESSING_MAX_CONCURRENCY_LIMIT` (default 300). In the RTA role, the mode selects which stages may overlap within the fetched batch; polling cycles remain non-overlapping. In the Service Bus role, `SEQUENTIAL` processes one message at a time and the other modes enable concurrent message processing. Shared gates apply the same APIM and RTA writeback limits across active work. `ServiceBusReceiveBatchSize` limits one receive call, not total in-flight processing. Effective concurrency is per replica, so replicas multiply downstream load. Concurrency configuration does not automatically scale the Container App replicas.

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

Request history is stored in Azure Table and Blob storage. The admin-only `/batches.html` view saves a manifest for every successful non-empty RTA GET before processing and tracks per-record APIM attempts/responses, Service Bus buffering, and RTA writeback outcomes. It helps find records with missing or failed milestones; a gap means no outcome was stored and does not by itself prove loss. The secured `/ledger.html` view reads the existing request-history API and reconstructs a lifecycle timeline from those stored milestones; it is not a separate event ledger. Search and filters operate over the indexed history API. An APIM send timestamp is recorded before the network POST, so the ledger reports an attempt, not confirmed APIM receipt; missing downstream milestones mean no outcome was stored.


## Service Bus retry message types

The queue carries two independent retry paths:

- Claim retry messages preserve the original RTA request and transformed APIM request. The SERVICEBUS role sends these through the claim processor and APIM/DOCMP.
- RTA writeback retry messages preserve the exact translated RTA status PUT body after APIM/DOCMP has already responded. The SERVICEBUS role sends these directly to RTA and never replays APIM/DOCMP. It schedules retries with increasing delay until the configured message TTL deadline, then dead-letters the message if the PUT still fails.

Direct writeback retries require RTA health only; they continue independently of APIM health.
