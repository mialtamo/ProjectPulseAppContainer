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
/live
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

## Configuration

The complete environment-variable and secret reference is in [docs/CONFIGURATION.md](docs/CONFIGURATION.md). The deployable template is [environment.example.json](environment.example.json).

Core RTA configuration:

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
ApimResponseToken=<secret reference>

RequestHistoryStorageConnectionString=<secret reference>
```

### Required secret references

`ApimResponseToken` is the expected value returned by APIM in the configured response-token header. Store it as a Container Apps secret, recommended name `apim-response-token`, and configure the `ApimResponseToken` environment variable to reference that secret.

`RequestHistoryStorageConnectionString` is the Azure Storage connection string used for request-history Table metadata and Blob payload storage. Store it as a Container Apps secret, recommended name `request-history-storage`, and configure the `RequestHistoryStorageConnectionString` environment variable to reference that secret.

Do not commit either secret value. See [docs/CONFIGURATION.md](docs/CONFIGURATION.md) for the full list, defaults, role applicability, and descriptions.

### APIM response token named value and mock policy

Project Pulse validates that the APIM response contains the header configured by `ApimResponseHeaderName` and that the returned value matches the Container Apps secret exposed as `ApimResponseToken`.

In APIM, create a Named Value for the shared token:

```text
Display name: Project Pulse response token
Name: rta-key
Type: Secret
Value: <generate/store the shared token value>
```

The value stored in the APIM Named Value `rta-key` must exactly match the value stored in the Container Apps secret referenced by the `ApimResponseToken` environment variable.

For the current POC, APIM generates the mock adjudication response directly from the `<inbound>` policy by using `<return-response>`. Because `return-response` short-circuits the normal APIM pipeline, the response headers and body are configured inside that `return-response` block even though the block itself is located in `<inbound>`.

The response-token header is therefore configured like this inside the existing inbound `return-response`:

```xml
<return-response>
    <set-status code="200" reason="OK" />
    <set-header name="Content-Type" exists-action="override">
        <value>application/json</value>
    </set-header>
    <set-header name="X-Pulse-Policy-Version" exists-action="override">
        <value>N50983445987</value>
    </set-header>
    <set-header name="X-Pulse-Response-Token" exists-action="override">
        <value>{{rta-key}}</value>
    </set-header>
    <!-- set-body omitted here -->
</return-response>
```

The important distinction is that `X-Pulse-Response-Token` is still an HTTP **response** header. It is not being added to the backend request. It appears under `<inbound>` only because the POC policy returns the mock response immediately from that section.

The current policy structure is therefore:

```xml
<policies>
    <inbound>
        <base />

        <!-- request validation and requestBody variable setup -->

        <return-response>
            <set-status code="200" reason="OK" />
            <set-header name="Content-Type" exists-action="override">
                <value>application/json</value>
            </set-header>
            <set-header name="X-Pulse-Policy-Version" exists-action="override">
                <value>N50983445987</value>
            </set-header>
            <set-header name="X-Pulse-Response-Token" exists-action="override">
                <value>{{rta-key}}</value>
            </set-header>
            <!-- mock adjudication response body -->
        </return-response>
    </inbound>
    <backend>
        <base />
    </backend>
    <outbound>
        <base />
    </outbound>
    <on-error>
        <base />
    </on-error>
</policies>
```

If the POC is later changed so APIM forwards the request to a real backend instead of returning the response from `<inbound>`, then the response-token header should be added in the appropriate response path, typically `<outbound>`.

If `ApimResponseHeaderName` is changed from `X-Pulse-Response-Token`, change the APIM `set-header` name to the same value.

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
  CONFIGURATION.md
```

The Azure Functions trigger classes, `host.json`, Function App workflow, and `POLL_SCHEDULE` configuration are intentionally excluded.

## Relationship to Project Pulse

The original Azure Functions POC remains in `mialtamo/Project-Pulse`.

This repository is the isolated Container Apps implementation for continuous sub-minute polling.
