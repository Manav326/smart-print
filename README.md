# SmartPrint by QuantaEdge

SmartPrint by QuantaEdge (repository: smart-print) is a multi-tenant SaaS platform for QR-initiated document printing, combining customer web experiences, a native Windows shop-management desktop application, a separate local Windows Print Agent service, and platform-owner administration.

## Product principles

- Support independent printing shops as the primary SaaS tenant.
- Keep customer print jobs, shop operations and platform administration isolated.
- Use the existing Windows Print Spooler and secure Agent execution path for supported physical office printers.
- Treat printer discovery, capability evidence, spooler completion and physical paper confirmation as distinct states.
- Qualify exact printer, driver, operating system and connection combinations; do not claim universal compatibility.
- Keep Bluetooth/BLE and thermal/label printing out of the current printer compatibility scope.

## Documentation

- [Product blueprint](docs/PRODUCT_BLUEPRINT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Premium design system](docs/DESIGN_SYSTEM.md)
- [Security and operations](docs/SECURITY_AND_OPERATIONS.md)
- [Delivery roadmap and acceptance criteria](docs/ROADMAP.md)
- [Public website and download launch checklist](docs/PUBLIC_LAUNCH_CHECKLIST.md)
- [Release signing setup](docs/RELEASE_SIGNING_SETUP.md)
- [Local native Windows and Android QA](docs/LOCAL_NATIVE_APP_QA.md)
- [Role-based Web, Android and Windows acceptance matrix](docs/ROLE_BASED_CROSS_PLATFORM_QA.md)
- [Production QA master test plan](docs/PRODUCTION_QA_TEST_PLAN.md)
- [Final pre-production release gate and blocker register](docs/FINAL_PRE_PRODUCTION_RELEASE_GATE.md)
- [Printer compatibility and qualification](docs/PRINTER_COMPATIBILITY_QUALIFICATION.md)
- [Printer driver qualification](docs/PRINTER_DRIVER_QUALIFICATION.md)

## Delivery principles

- Audit the current state before changing existing functionality.
- Keep the default branch buildable; use focused, reviewable changes.
- Never claim a physical print completed based only on spooler acceptance.
- Never automatically resubmit an uncertain physical print after restart.
- Never trust client-supplied prices or payment-success screenshots.
- Do not claim hardware compatibility until representative physical devices have been tested.

Platform administrators can manage versioned SaaS plans, entitlements and shop subscriptions. The shop subscription page supports INR Razorpay checkout with server-side checkout-signature verification and signed, idempotent webhook processing. Platform operators can also grant trial/free access or record an assisted paid assignment with the received bank/UPI reference in the append-only audit. This subscription flow is separate from customer print payments, which remain shop-configured UPI/cash and manually verified. Do not imply customer print payment gateway processing or automatic UPI/cash refunds.

After the GitHub repository is renamed, update an existing local checkout with:

```powershell
git remote set-url origin https://github.com/Manav326/smart-print.git
```
