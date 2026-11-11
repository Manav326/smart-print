import type { ReactNode } from "react";

type BrandLockupProps = {
  className?: string;
  subline?: string;
  children?: ReactNode;
};

/** Canonical in-app SmartPrint lockup; launcher contexts use only the symbol. */
export default function BrandLockup({ className = "", subline, children }: BrandLockupProps) {
  const classes = ["brand-lockup", className].filter(Boolean).join(" ");
  return (
    <div className={classes} role="group" aria-label="SmartPrint by QuantaEdge">
      <img className="brand-lockup-full brand-lockup-logo-light"
        src="/branding/smartprint-logo-light.svg" alt="" aria-hidden="true" />
      <img className="brand-lockup-full brand-lockup-logo-dark"
        src="/branding/smartprint-logo-dark.svg" alt="" aria-hidden="true" />
      <img className="brand-mark-image" src="/smartprint-mark.svg" alt="" aria-hidden="true" />
      {subline && <span className="brand-lockup-subline">{subline}</span>}
      {children}
    </div>
  );
}
