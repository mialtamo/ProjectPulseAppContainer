# Project Pulse Security

Last reviewed: 2026-10-22  
Scope: repository implementation and the Container Apps POC. Environment configuration must be checked independently.

## Access to the web application

Microsoft Entra authentication is configured through Azure Container Apps built-in authentication (the Entra identity provider). The app registration is single-tenant and defines the `ProjectPulse.Admin` app role. The `PROJECT_PULSE_ADMINS` security group is assigned that role in the enterprise application.

The application checks the authenticated principal supplied by Container Apps and requires the `ProjectPulse.Admin` role for:

- `/requests.html`
- `/requests`
- `/requests/averages`
- `/requests/{id}`
- `/requests/cleanup/preview`
- `DELETE /requests`

The page redirects unauthenticated users to Entra sign-in; API endpoints return unauthorized, and authenticated users without the role receive forbidden. This check depends on Container Apps authentication being enabled and supplying a trusted `X-MS-CLIENT-PRINCIPAL` header. Do not expose an alternate route that bypasses that trusted authentication layer. History deletion is also admin-only and requires a same-origin request header; the UI previews the record count and confirms before sending the destructive request.

The landing page (`/`), `/pulse-health`, and `/live` are intentionally unauthenticated in the current design. The health endpoint returns dependency booleans, runtime role, and check time. It tests App API/APIM HTTP health and TCP connectivity to Service Bus port 5671; it does not prove successful downstream authentication, authorization, or message sending. Keep it network-restricted or protect it if its exposure is not acceptable.

The landing and history pages set no-cache and browser security headers, including content-type and frame protections and a Content Security Policy. These headers are defense in depth; they do not replace server-side authorization.

## Workload identity and dependency permissions

The application uses `DefaultAzureCredential` for managed-identity token requests. When `AppApiScope` or `ApimScope` is configured, the corresponding API calls use a bearer token. The Service Bus client also uses managed identity.

Grant only the access required by each deployed role:

- The RTA polling deployment needs Service Bus Data Sender access to its retry queue when buffering is enabled.
- The Service Bus worker deployment needs Service Bus Data Receiver access to consume retry messages.
- App API and APIM access must be limited to the configured audiences/scopes.

The two roles use the same image but should have separate Container Apps identities where practical, with role-specific queue permissions. Confirm the actual identity assignment and RBAC scope in each environment.

Request-history Table and Blob storage currently use `RequestHistoryStorageConnectionString`, not managed identity. Store it as a Container Apps secret reference, restrict the storage account and secret access, rotate it when required, and never commit the value. `ApimResponseToken` is also a secret reference used to validate the APIM response header. Application Insights uses its configured connection string; protect it as deployment configuration.

The GitHub Actions deployment workflow uses Azure federated identity/OIDC. Its Azure federated identity and deployment permissions should remain limited to the required repository, branch, and resources.

## Request history, logs, and sensitive claim data

Request history separates searchable transaction metadata in Azure Table Storage from captured request/response bodies in Blob Storage. Retry envelopes can include both the original RTA item and transformed APIM request, so the Service Bus queue and its dead-letter storage must also be treated as sensitive data stores.

`REQUEST_HISTORY_PAYLOAD_MODE` controls payload capture for new history records:

- `OFF` (default): do not capture or emit payload bodies; transaction metadata remains available.
- `PARTIAL`: mask common identifiers and personal-name fields before storage/logging; unparseable payloads are masked completely.
- `FULL`: retain and emit unredacted payloads. Use only with synthetic data or under an explicitly approved data-handling process.

Partial masking is targeted; it is not a complete de-identification guarantee. Identifiers and transaction metadata may still be sensitive even with payload mode OFF. Changing modes does not purge existing payload blobs. The secured request-history UI offers confirmed manual cleanup for the last 24 hours, 7 days, 30 days, or all history. Payload blobs are deleted only when no retained history record references the batch. The deprecated `LOG_POLL_PAYLOADS` setting is consulted only if `REQUEST_HISTORY_PAYLOAD_MODE` is unset (`true` maps to FULL, `false` to OFF).

Restrict access to storage, Service Bus, Application Insights, and exported logs. Manual request-history cleanup is available to admins; the application does not automatically purge history after a fixed age. Define separate retention and deletion policies for Service Bus retry/dead-letter messages, Application Insights, and exported logs.

## TLS and certificates

The checked-in Dockerfile does not install custom certificates, and the application code does not configure a client certificate. Adding a `.crt` file to the Linux CA trust store makes it a trust anchor for validating a remote server certificate; that alone does not present a client certificate for mutual TLS.

The locally used POC Dockerfile with `certs/*.crt` is not the Dockerfile in the repository and is not used by the normal GitHub Actions image build. Confirm which image and trust store are actually deployed when diagnosing certificate errors. Service Bus TLS hostname validation is separate from RTA client-certificate authentication; a certificate-name mismatch must be fixed by using the correct Service Bus namespace/hostname and certificate chain, not by disabling certificate validation.

## Secrets and data that must not enter source control

Do not commit storage connection strings/keys, APIM response tokens or subscription keys, client secrets, passwords, private keys, real claim payloads, or PHI. Use Container Apps secret references or an approved secret store for unavoidable secrets. Never put private keys or client identity certificates in the CA trust bundle.

## POC security gaps and production work

The POC is not yet a production security approval. Before production:

1. Confirm Entra sign-in, role claims, group-to-role assignment, and denial behavior using both an authorized and unauthorized account.
2. Confirm built-in authentication is enabled for the deployed app and trusted identity headers cannot be supplied by a bypass path.
3. Use separate, least-privilege managed identities for the RTA and Service Bus roles where practical; validate each RBAC scope.
4. Decide whether request payloads and retry envelopes may contain real claim data; keep payload capture OFF unless approved.
5. Define storage, telemetry, queue, and dead-letter retention and access auditing. Changing payload mode is not a deletion operation.
6. Restrict or protect the anonymous health endpoints according to the deployment's network exposure.
7. Review private networking, TLS, certificate chain and hostname configuration, dependency scanning, and deployment permissions.
8. Validate restart, retry, recovery, and multi-replica behavior. Outage/circuit state is process-local and resets when a process restarts; the POC is configured for one replica.
9. Establish security monitoring, incident response, backup/recovery, and a documented production review.
