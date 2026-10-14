# Project Pulse App Container Development Context

Repository: `mialtamo/ProjectPulseAppContainer`

Project Pulse App Container is the Azure Container Apps version of Project Pulse. It is a .NET 10 ASP.NET Core application with a continuously running `BackgroundService`.

## Hosting rule

Do not reintroduce Azure Functions timer triggers into this repository.

Sub-minute polling is controlled by `POLL_INTERVAL_SECONDS`.

The POC should normally use one Container App replica.

## Preserve

- existing Project Pulse processing behavior
- APIM response validation
- RTA writeback behavior
- Service Bus fallback
- request-history persistence
- managed-identity authentication
- Application Insights telemetry

## Deployment

The root Dockerfile listens on port 8080.

Azure Container Apps Continuous Deployment may generate the GitHub Actions deployment workflow. Do not copy the legacy Function App workflow from `mialtamo/Project-Pulse`.
