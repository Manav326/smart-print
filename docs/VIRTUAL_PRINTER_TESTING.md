# Printer availability and platform holds

## Default access

Every printer queue successfully discovered by a paired SmartPrint Agent is available to the shop by default. There is no platform approval step, per-shop approval grant, expiry timer, or capability-data approval loop.

Shop selection remains a separate setting:

- `PrinterDevice.IsActive` records whether the shop selected the queue for new assignments.
- `PrinterDevice.IsPlatformHeld` records an explicit restriction placed by platform administration.
- A queue can be selected by the shop and temporarily held by the platform at the same time. The hold does not clear the shop selection or free its plan slot.

Physical queues, Microsoft Print to PDF, and other virtual Windows queues remain visible. The queue's technical readiness and output type still determine which operations it can perform.

## Platform hold and release

Only platform administrators can place or release a hold. Both actions require a reason between 5 and 500 characters and are recorded in `PrinterPlatformAccessAudits` with actor, action, reason, queue identity fingerprint, and timestamp.

An active hold blocks new use at the server:

- Shop selection changes through direct or portal APIs; the selected state is preserved while held
- New QR customer sessions and uploads for a station whose default printer is held
- QR-station assignment, job assignment and new Agent queue claims
- Private relay staging / dispatch and diagnostics
- The final authorization immediately before Windows spooler submission

A hold does not erase the queue, unselect it, cancel completed work, or release the shop's selected-printer quota slot. Releasing the hold clears only the hold fields. The prior shop selection is then effective again, subject to normal Agent and Windows readiness checks.

A job already authorized and submitted to the Windows spooler cannot be reliably recalled by a later hold. The hold prevents subsequent authorization/submission; it does not pretend to undo work Windows has already accepted.

## Output classification

- `PHYSICAL`: actual paper output through a supported physical printer. Only this output type can enter physical-output confirmation.
- `PDF_EXPORT`: a separate Agent-side PDF export path. It is not a Windows virtual-spooler print.
- `UNSUPPORTED_VIRTUAL`: a virtual Windows destination. A spooler submission is explicitly classified as `TEST_VIRTUAL`, uses the `VIRTUAL-TEST-` job prefix, and completes as `TEST_OUTPUT_READY`. It is never recorded as physical paper fulfilment.

Virtual output can be used for diagnostics and digital/test output. A Windows spooler receipt proves that the virtual destination accepted a test artifact; it does not prove paper was printed or physically handed to a customer.

## Private Printing

Private Printing is deliberately separate from platform printer availability. Physical output remains the standard path. PDF Export or virtual test output requires the central `AllowPrivatePrintingVirtualOutput` platform setting and a non-production environment. A per-printer approval or a platform hold does not bypass this central safeguard. Protected-source relay, acknowledgement, and purge rules remain in force.

## Shop-PC Android Virtual Printer acceptance

Run the operator-assisted acceptance harness on the Windows shop PC with the paired SmartPrint Agent and the Android IPP virtual queue installed:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\qa\\Invoke-SmartPrintVirtualPrinterAcceptance.ps1 `
  -QueueName "Android Virtual IPP Test" `
  -AgentVersion "<installed-agent-version>"
```

Before running the script:

1. Confirm the platform inventory shows the queue as Available, not On hold.
2. Confirm the shop can see the discovered queue and its output classification is Virtual test output.
3. Start exactly one diagnostic from the shop portal when prompted. Do not submit a Windows test page or print from another application.
4. Confirm that the complete diagnostic arrived and can be opened/rendered in the Android receiver.

The script snapshots existing queue job IDs, waits for a new job on the selected queue, and requires an explicit operator confirmation of Android receipt. Its JSON evidence records queue/driver/port identity, Agent and Windows versions, spooler metadata, timestamps, and operator confirmation. It excludes document contents and customer data.

A Windows spooler job proves submission/acceptance only. Android receipt remains operator-attested. A disappearing spooler entry is not proof of receiver delivery.

## Automated hosted-CI Android receiver acceptance

The Windows Agent CI job also runs `scripts/qa/Invoke-AndroidVirtualIppWindowsAcceptance.ps1`. It uses a disposable Android emulator, a pinned 402D IppVirtual APK, a temporary Windows IPP queue using the Microsoft IPP Class Driver, and the existing Windows Agent GDI/spooler executor.

A successful test demonstrates Windows IPP transport and Android receiver interoperability. It does not prove physical paper output, a real shop LAN, a physical Android handset, or live production hold administration. Those require the shop-PC acceptance above and separate API/policy tests for hold and release.
