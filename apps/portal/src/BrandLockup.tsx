import type { ReactNode } from "react";

type BrandLockupProps = {
  className?: string;
  subline?: string;
  children?: ReactNode;
};

/** One shared SmartPrint lockup for web and the Windows desktop workspace. */
export default function BrandLockup({ className = "", subline, children }: BrandLockupProps) {
  const classes = ["brand-lockup", className].filter(Boolean).join(" ");
  return (
    <div className={classes} aria-label="SmartPrint by QuantaEdge">
      <img className="brand-mark-image" src="/smartprint-mark.svg" alt="" />
      <div className="brand-lockup-copy">
        <div className="brand-name">Smart<span>Print</span></div>
        <div className="brand-lockup-by">by</div>
        {subline && <div className="brand-lockup-subline">{subline}</div>}
      </div>
      <img
        className="brand-parent-logo"
        src="/branding/quantaedge-logo.svg"
        alt="QuantaEdge Learning — Smarter Decisions. Greater Growth."
      />
      {children}
    </div>
  );
}
