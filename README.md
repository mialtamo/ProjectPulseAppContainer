# Project Pulse App Container

## Disclaimer and license

Project Pulse App Container is an experimental proof-of-concept and reference implementation. It is not an officially supported product or service.

This software is provided "AS IS", without warranty of any kind, express or implied. No support, maintenance, service-level commitment, or obligation to correct defects is provided.

Use of this software is entirely at your own risk. You are solely responsible for evaluating its suitability, security, configuration, compliance, and use in any environment, including production environments.

Project Pulse App Container is licensed under the MIT License. See [LICENSE](LICENSE) for the complete license terms.

## Purpose

This repository is the Azure Container Apps version of Project Pulse. It is intentionally isolated from the original Azure Functions implementation so the POC can validate sub-minute polling without changing the working Function App.

The application is a .NET 10 ASP.NET Core container that supports two runtime roles from one codebase and one image. `PROJECT_PULSE_ROLE=RTA` runs the continuous RTA/App API poller. `PROJECT_PULSE_ROLE=SERVICEBUS` runs the Service Bus-only retry worker. Both roles reuse the same APIM, writeback, request-history, managed-identity, and telemetry services.

## Runtime roles

Use the same image for two independent Azure Container Apps:

```text
PROJECT_PULSE_ROLE=RTA
```

The RTA role starts `ClaimPollingWorker`, polls the RTA/App API, sends claims to APIM, writes results back to RTA, and buffers retryable work to Service Bus.

```text
PROJECT_PULSE_ROLE=SERVICEBUS
```

The Service Bus role does not poll RTA. It receives `RetryEnvelope` messages from the configured Service Bus queue, waits for RTA/APIM health, retries processing through the shared `ClaimProcessor`, completes successful/requeued messages, and dead-letters malformed payloads.

For backward compatibility, if `PROJECT_PULSE_ROLE` is not yet configured the container starts in `RTA` mode. Any non-empty unsupported role value fails startup.

## Why Container Apps

The original Project Pulse implementation uses an Azure Functions timer trigger. This POC requires polling more frequently than once per minute.

The Container Apps worker stays running and uses:

```text
POLL_INTERVAL_SECONDS
```

Default: 5 seconds. Supported range: 1 through 300 seconds.

For the POC, use one replica:

```text
minReplicas = 1
maxReplicas = 1
```

This prevents multiple independent workers from polling the same queue while we validate the design.

## Processing flow

```text
RTA / Mock API
     |
     v
Project Pulse Container App
     |
     +--> HealthGate
     +--> transform queued requests
     |
     v
Azure API Management
     |
     v
Adjudication response
     |
     v
Project Pulse
     |
     +--> validate response
     +--> write status back to RTA
     +--> record request history
     +--> Service Bus fallback when required
```

## Runtime

- .NET 10
- ASP.NET Core
- Azure Container Apps
- Continuous `BackgroundService` polling
- Managed identity through `DefaultAzureCredential`
- Azure Service Bus fallback/retry
- Azure Table + Blob request history
- Application Insights
- HTTP port 8080

## HTTP endpoints

```text
/
/pulse
/pulse-health
/requests
/requests/{id}
/requests.html
```

Container Apps ingress target port:

```text
8080
```

## Docker

The Dockerfile is at:

```text
./Dockerfile
```

Build:

```bash
docker build -t projectpulse-appcontainer .
```

## Azure deployment

This repository intentionally does not contain the legacy Function App GitHub Actions workflow.

For the POC, connect this repository directly from Azure Container Apps Continuous Deployment and allow Azure to generate the GitHub Actions workflow.

Recommended settings:

```text
Target port: 8080
Min replicas: 1
Max replicas: 1
```

The Container App managed identity needs its own downstream permissions. Permissions assigned to the original Function App identity do not automatically transfer.

## Core configuration

```text
PROJECT_PULSE_ROLE=RTA
POLL_INTERVAL_SECONDS=5

APP_API_MODE=MOCK
AppApiBaseUrl=<RTA or mock API URL>
AppApiTimeoutSeconds=10

ApimBaseUrl=<APIM URL>
ApimAdjudicationPath=pulse/pending
ApimRequestTimeoutSeconds=15

ServiceBusFullyQualifiedNamespace=<namespace>.servicebus.windows.net
ServiceBusQueueName=projectpulsequeue

RtaQueuedPath=/claims/pending
RtaHealthPath=/health
RtaStatusPathTemplate=/requests/{correlationId}/status
RtaStatusWritebackEnabled=true

ApimResponseHeaderName=X-Pulse-Response-Token
ApimResponseToken=<secret>

RequestHistoryStorageConnectionString=<storage connection string>
```

Do not commit secrets. Use Container Apps secrets, managed identity, and/or Key Vault.

## Repository structure

```text
ContainerApp/
  ClaimPollingWorker.cs
  ServiceBusWorker.cs
  ProjectPulseRole.cs

Models/
Options/
Services/

Program.cs
ProjectPulse.Processor.csproj
Dockerfile
environment.example.json
docs/
```

The Azure Functions trigger classes, `host.json`, Function App workflow, and `POLL_SCHEDULE` configuration are intentionally excluded.

## Relationship to Project Pulse

The original Azure Functions POC remains in `mialtamo/Project-Pulse`.

This repository is the isolated Container Apps implementation for continuous sub-minute polling.
