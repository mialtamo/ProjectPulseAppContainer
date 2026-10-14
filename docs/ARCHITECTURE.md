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
          +--> APIM
          +--> RTA writeback
          +--> request history
          +--> Service Bus fallback
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
