## 2026-11-02 — Build 0.0.87

- Grouped request history by Unique ID so repeated processing runs appear as one transaction summary instead of separate top-level status rows.
- Added an expandable visual lifecycle for each loaded run: received from RTA/mock source, APIM/DOCMP attempt, APIM response, Service Bus activity, and RTA status PUT. HTTP status and business response code remain separate, and missing milestones are shown as unrecorded rather than assumed outcomes.
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
