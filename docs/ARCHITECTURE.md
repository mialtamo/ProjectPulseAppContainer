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

Request history is stored in Azure Table and Blob storage.


## Service Bus retry message types

The queue carries two independent retry paths:

- Claim retry messages preserve the original RTA request and transformed APIM request. The SERVICEBUS role sends these through the claim processor and APIM/DOCMP.
- RTA writeback retry messages preserve the exact translated RTA status PUT body after APIM/DOCMP has already responded. The SERVICEBUS role sends these directly to RTA and never replays APIM/DOCMP. It schedules retries with increasing delay until the configured message TTL deadline, then dead-letters the message if the PUT still fails.

Direct writeback retries require RTA health only; they continue independently of APIM health.
