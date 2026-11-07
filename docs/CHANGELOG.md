# Change log

## 2026-11-06 — Build 0.0.108

- Normalized configured HTTP base URLs and endpoint paths across RTA, APIM, health checks, status writebacks, and admin notifications. Leading/trailing slashes are optional; URL joins avoid duplicate path separators and preserve base URL prefixes.
- Bumped the base image version in `VERSION` to `0.0.108`.

## 2026-11-06 — Build 0.0.107

- Updated deployment and product documentation to describe update-request routing, HTTP method, field mapping, idempotency header, and update-specific writeback.
- Replaced the concrete update endpoint example with generic relative-path guidance.
- Bumped the base image version in `VERSION` to `0.0.107`.

## 2026-11-06 — Build 0.0.106

- Routed incoming `UPDATE_REQUEST` records to the separately configurable `ApimUpdateAdjudicationPath` using HTTP PUT; other request types continue to use `ApimAdjudicationPath` with HTTP POST.
- Kept backward compatibility: if the update path is unset, update requests use the existing adjudication path and a warning is logged.
- Recorded the selected APIM endpoint and HTTP method in the generated RTA API call history for applicable status payloads.
- Mapped `UPDATE_REQUEST` request fields from `UPDATE_REQUEST_METADATA` into the DOCMP payload, including a list of individual, family, and costshare accumulator deltas.
- Added update-specific RTA writeback: `INITIAL_REQUEST_METADATA` is null and the DOCMP response JSON is stored in `UPDATE_REQUEST_METADATA`. The existing initial-request response handling is unchanged.
- Bumped the image version in `VERSION` to `0.0.106`.

## 2026-11-02 — Build 0.0.87

- Grouped request history by Unique ID so repeated processing runs appear as one transaction summary instead of separate top-level status rows.
- Added an expandable visual lifecycle for each loaded run: received from RTA/mock source, APIM POST attempt, APIM response, Service Bus activity, and RTA status PUT. HTTP status and business response code remain separate, and missing milestones are shown as unrecorded rather than assumed outcomes.
- Counts distinguish visible transactions from loaded history records and the total matching history-record count. Loading more pages can add earlier runs to an existing transaction.
- Bumped the ACR image tag in `VERSION` to `0.0.87`; the deployment workflow uses this tag for both RTA and Service Bus Container Apps.

# Change log

## 2026-11-02 — Build 0.0.86

- Added the admin-only `/ledger.html` request lifecycle ledger, linked from request history.
- Added filters for time range, status, destination, and processing role; search supports correlation/unique ID, RTA request ID, and batch ID. Results show matching counts separately from loaded pages.
- The ledger reconstructs its timeline from existing request-history milestones and does not add a separate event store or display payload bodies. It identifies APIM as an attempt recorded before the POST, not confirmed receipt; absent later milestones mean no outcome is stored for that stage.
- Updated the request-history API to accept status, destination, and processing-role filters used by the ledger.
- Bumped the ACR image tag in `VERSION` to `0.0.86`. This tag is separate from the UI version label and Azure revision number. The deployment workflow targets both the RTA and Service Bus Container Apps; rollout completion should be confirmed by checking each app's latest ready revision.

The ledger was merged in [PR #37](https://github.com/mialtamo/ProjectPulseAppContainer/pull/37). The build tag is an image identifier, not an assertion that Azure has finished making the resulting revision ready.
