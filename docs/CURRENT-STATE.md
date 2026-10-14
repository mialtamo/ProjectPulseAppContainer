# Current State

Project Pulse App Container is the isolated Azure Container Apps variant of Project Pulse.

## Implemented

- .NET 10 ASP.NET Core container.
- Continuous claims polling through `ClaimPollingWorker`.
- Configurable sub-minute polling with `POLL_INTERVAL_SECONDS`.
- Project Pulse health gating.
- RTA / mock API polling.
- APIM batching and adjudication submission.
- APIM response-token validation.
- RTA result writeback.
- Service Bus buffering during configured APIM outage conditions.
- Request history backed by Azure Table and Blob storage.
- Application Insights telemetry.
- HTTP health and request-history endpoints.
- Docker port 8080.

## POC deployment model

```text
minReplicas = 1
maxReplicas = 1
```

This repository does not use Azure Functions runtime, TimerTrigger, `POLL_SCHEDULE`, `host.json`, or the original Function App workflow.
