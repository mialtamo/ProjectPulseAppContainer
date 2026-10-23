# Project Pulse Security

## Current security model

Project Pulse is designed to use Managed Identity for Azure dependencies and avoid embedding credentials in application code.

### Service Bus

Project Pulse uses `DefaultAzureCredential` and should receive only:

`Azure Service Bus Data Sender`

on the required queue or namespace.

Project Pulse should not receive Service Bus Data Receiver because it does not consume retry messages.

### RTA/App API and APIM

When `AppApiScope` or `ApimScope` is configured, Project Pulse requests an Entra token and sends it as a bearer token.

If a scope is empty, no Authorization header is added.

## Sensitive data

The current retry envelope contains:

- OriginalPayload
- TransformedPayload

Therefore the Service Bus queue can contain claim data and must be treated as a sensitive data store.

Production controls should include:

- least-privilege RBAC,
- private connectivity where practical,
- public network access disabled where architecture allows,
- appropriate retention,
- auditing and monitoring,
- explicit handling of dead-lettered messages.

## Request-history payload logging

Use `REQUEST_HISTORY_PAYLOAD_MODE`:

- `OFF` (default): do not store or emit payload bodies; transaction metadata remains available.
- `PARTIAL`: mask common ICN, SSN, date-of-birth, member/claimant ID, and personal-name fields before logging or storage. Unparseable payloads are masked completely.
- `FULL`: retain and emit unredacted payload bodies. Use only with synthetic POC data.

Partial masking is targeted and should not be treated as a complete de-identification guarantee. Use `OFF` for real claim data unless an approved data-protection review authorizes a different mode. Existing payload blobs are hidden in `OFF` mode and read with masking in `PARTIAL` mode; switching modes does not delete old blobs.

The deprecated `LOG_POLL_PAYLOADS` setting is used only when `REQUEST_HISTORY_PAYLOAD_MODE` is unset (`true` maps to `FULL`, `false` to `OFF`).

## Health endpoint

`/pulse-health` is currently anonymous.

It exposes dependency state for:

- App API,
- APIM,
- Service Bus network reachability.

This is acceptable for the current POC but should be protected or network-restricted before production.

The current Service Bus health test checks TCP port 5671 only. It does not validate authentication or sender authorization.

## Secrets

Do not commit:

- storage account keys,
- SQL credentials,
- client secrets,
- APIM subscription keys,
- passwords,
- production connection strings,
- PHI or real claim payloads.

Use Azure Key Vault references for unavoidable secrets.

## GitHub deployment

The current workflow authenticates to Azure using GitHub OIDC/federated identity rather than an Azure client secret.

Recommended hardening:

- modern action versions,
- dependency scanning,
- vulnerability scanning,
- pinned action SHAs for stricter supply-chain controls,
- branch protection and required checks.

## Production security milestones

Before production:

1. Lock payload logging off.
2. Decide whether retry messages should retain full payloads or identifiers only.
3. Protect `/pulse-health`.
4. Validate private networking.
5. Validate least-privilege RBAC.
6. Validate APIM and RTA token audiences/scopes.
7. Review Service Bus retention and DLQ access.
8. Add security-focused tests and operational monitoring.
