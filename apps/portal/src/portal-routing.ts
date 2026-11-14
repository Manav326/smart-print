export type PortalRoute =
  | { kind: "public" }
  | { kind: "admin" }
  | { kind: "shop" }
  | { kind: "station"; publicCode: string }
  | { kind: "photo-studio" };

function normalizePath(pathname: string): string {
  if (!pathname || pathname === "/") return "/";
  return pathname.replace(/\/+$/, "") || "/";
}

/**
 * Resolve the top-level application by URL path, not by whichever account
 * happens to be present in browser storage.
 */
export function resolvePortalRoute(pathname: string): PortalRoute {
  const path = normalizePath(pathname);
  const station = path.match(/^\/s\/([A-Za-z0-9_-]+)$/);
  if (station) return { kind: "station", publicCode: station[1] };
  if (path === "/photo-studio") return { kind: "photo-studio" };

  if (path === "/admin" || path.startsWith("/admin/")) {
    return { kind: "admin" };
  }

  if (
    path === "/shop" ||
    path.startsWith("/shop/") ||
    path === "/login" ||
    path === "/signup"
  ) {
    return { kind: "shop" };
  }

  return { kind: "public" };
}

export type AuthScope = "admin" | "shop";

export function authStorageKey(scope: AuthScope): string {
  return `qrtoprint.auth.${scope}`;
}


export type ShopSection =
  | "Order Journey"
  | "Overview"
  | "Print Queue"
  | "Printers"
  | "QR Stations"
  | "Print Agent"
  | "Pricing"
  | "Subscription"
  | "Payments"
  | "Customers"
  | "Reports"
  | "Settings";

const shopSectionPaths: Record<ShopSection, string> = {
  "Order Journey": "/shop",
  "Overview": "/shop/overview",
  "Print Queue": "/shop/queue",
  "Printers": "/shop/printers",
  "QR Stations": "/shop/qr-stations",
  "Print Agent": "/shop/print-agent",
  "Pricing": "/shop/pricing",
  "Subscription": "/shop/subscription",
  "Payments": "/shop/payments",
  "Customers": "/shop/customers",
  "Reports": "/shop/reports",
  "Settings": "/shop/settings"
};

export function shopSectionFromPath(pathname: string): ShopSection {
  const path = normalizePath(pathname);
  const match = Object.entries(shopSectionPaths).find(([, sectionPath]) =>
    path === sectionPath || (sectionPath !== "/shop" && path.startsWith(`${sectionPath}/`))
  );
  return (match?.[0] as ShopSection | undefined) ?? "Overview";
}

export function shopPathForSection(section: ShopSection): string {
  return shopSectionPaths[section];
}


export type AdminSection = "Overview" | "Tenants" | "Plans" | "Subscriptions" | "Operations" | "Advertising";

const adminSectionPaths: Record<AdminSection, string> = {
  "Overview": "/admin",
  "Tenants": "/admin/tenants",
  "Plans": "/admin/plans",
  "Subscriptions": "/admin/subscriptions",
  "Operations": "/admin/operations",
  "Advertising": "/admin/advertising"
};

export function adminSectionFromPath(pathname: string): AdminSection {
  const path = normalizePath(pathname);
  const match = Object.entries(adminSectionPaths).find(([, sectionPath]) =>
    path === sectionPath || (sectionPath !== "/admin" && path.startsWith(`${sectionPath}/`))
  );
  return (match?.[0] as AdminSection | undefined) ?? "Overview";
}

export function adminPathForSection(section: AdminSection): string {
  return adminSectionPaths[section];
}
