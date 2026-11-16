import { useEffect, useState, type ReactNode } from "react";
import {
  Activity, ArrowDownToLine, ArrowUpFromLine, Cloud, Database,
  RefreshCw, Router, Wifi
} from "lucide-react";
import {
  getPlatformDataNetwork,
  type AuthTokens,
  type PlatformDataNetworkReport
} from "./api";

type RangeDays = 7 | 30 | 90;

function formatCount(value: number): string {
  return value.toLocaleString("en-IN");
}

function formatGb(bytes: number): string {
  return `${(bytes / 1_000_000_000).toLocaleString("en-IN", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2
  })} GB`;
}

function channelLabel(channel: string): string {
  switch (channel) {
    case "CLOUD_CUSTOMER_UPLOAD": return "Customer → Cloud";
    case "CLOUD_AGENT_DOWNLOAD": return "Cloud → Shop Agent";
    case "SHOP_LAN_CUSTOMER_UPLOAD": return "Phone → Shop Agent";
    default: return channel.replaceAll("_", " ").toLowerCase();
  }
}

function MetricCard({ icon, label, value, note }: {
  icon: ReactNode;
  label: string;
  value: string;
  note: string;
}) {
  return (
    <article className="platform-data-metric">
      <div className="platform-data-metric-icon">{icon}</div>
      <div className="platform-data-metric-label">{label}</div>
      <div className="platform-data-metric-value">{value}</div>
      <div className="platform-data-metric-note">{note}</div>
    </article>
  );
}

export default function PlatformDataNetworkSection({ tokens }: { tokens: AuthTokens }) {
  const [days, setDays] = useState<RangeDays>(30);
  const [refreshKey, setRefreshKey] = useState(0);
  const [report, setReport] = useState<PlatformDataNetworkReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError("");
    void getPlatformDataNetwork(tokens.accessToken, days)
      .then(value => { if (active) setReport(value); })
      .catch(exception => {
        if (active) setError(exception instanceof Error
          ? exception.message
          : "Could not load measured document transfer data.");
      })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [tokens.accessToken, days, refreshKey]);

  return (
    <section className="platform-data-page">
      <div className="platform-data-intro">
        <div>
          <div className="platform-plan-kicker"><Activity size={14}/> DATA &amp; NETWORK INTELLIGENCE</div>
          <h2>Where SmartPrint data moves</h2>
          <p>Measured original-document payload across cloud transfer and shop Wi-Fi paths. Each network leg is counted separately from unique files.</p>
        </div>
        <div className="platform-data-actions">
          <label className="platform-data-range">
            <span>Reporting period</span>
            <select value={days} onChange={event => setDays(Number(event.target.value) as RangeDays)}>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
              <option value={90}>Last 90 days</option>
            </select>
          </label>
          <button className="secondary-button" onClick={() => setRefreshKey(value => value + 1)} disabled={loading}>
            <RefreshCw size={14}/> Refresh
          </button>
        </div>
      </div>

      {error && <div className="inline-error" role="alert">{error}</div>}
      {loading && !report ? (
        <div className="panel platform-loading"><span className="button-spinner"/> Loading measured transfer data…</div>
      ) : report ? (
        <>
          <div className="platform-data-window">
            <span>Window: {new Date(report.fromUtc).toLocaleString()} – {new Date(report.toUtc).toLocaleString()}</span>
            <span>Generated {new Date(report.generatedUtc).toLocaleString()}</span>
          </div>
          {!report.coverageComplete && (
            <section className="platform-data-measurement-note" role="status">
              <div className="platform-data-note-icon"><Activity size={17}/></div>
              <div>
                <strong>Historical transfer measurement is not complete for this entire period</strong>
                <p>Measured collection began {new Date(report.measurementStartedUtc).toLocaleString()}. This report includes document and transfer activity only from that date; earlier documents are not backfilled as network traffic.</p>
              </div>
            </section>
          )}

          <section className="platform-data-measurement-note" role="status">
            <div className="platform-data-note-icon"><Activity size={17}/></div>
            <div>
              <strong>{report.telemetryHealth.failedWriteCount === 0
                ? "No telemetry write failures observed"
                : `${formatCount(report.telemetryHealth.failedWriteCount)} telemetry write failures observed`}</strong>
              <p>This health counter is local to the API instance serving this request and resets when that process restarts. It has recorded {formatCount(report.telemetryHealth.successfulWriteCount)} successful writes since {new Date(report.telemetryHealth.processStartedUtc).toLocaleString()}.
                {report.telemetryHealth.lastFailureUtc
                  ? ` Last failure: ${new Date(report.telemetryHealth.lastFailureUtc).toLocaleString()}.`
                  : ""}
                {report.telemetryHealth.failedWriteCount > 0
                  ? " Some transfer totals may be under-reported; check API logs and database health."
                  : ""}
              </p>
            </div>
          </section>

          <div className="platform-data-metrics-grid">
            <MetricCard
              icon={<Database size={18}/>}
              label="Unique documents"
              value={formatCount(report.summary.uniqueDocumentCount)}
              note={formatGb(report.summary.uniqueDocumentBytes) + " original file volume counted once"}
            />
            <MetricCard
              icon={<ArrowUpFromLine size={18}/>}
              label="Cloud ingress"
              value={formatGb(report.summary.cloudIngressBytes)}
              note="Customer document payload received by the cloud API; accepted, in-progress and failed attempts are separated by status"
            />
            <MetricCard
              icon={<ArrowDownToLine size={18}/>}
              label="Cloud egress"
              value={formatGb(report.summary.cloudEgressBytes)}
              note="Bytes written to the Agent download HTTP response; receiver-side delivery is not confirmed"
            />
            <MetricCard
              icon={<Wifi size={18}/>}
              label="Shop Wi-Fi / LAN"
              value={formatGb(report.summary.shopLanBytes)}
              note="Customer phone upload payload received by shop Agent"
            />
            <MetricCard
              icon={<Activity size={18}/>}
              label="Measured payload movement"
              value={formatGb(report.summary.measuredPayloadBytes)}
              note="Sum of recorded transfer legs; cloud upload and download count twice"
            />
            <MetricCard
              icon={<Router size={18}/>}
              label="Recorded transfer legs"
              value={formatCount(report.summary.transferAttemptCount)}
              note={`${formatCount(report.summary.startedTransferCount)} in progress · ${formatCount(report.summary.partialTransferCount)} partial · ${formatCount(report.summary.failedTransferCount)} failed`}
            />
          </div>

          <div className="platform-data-storage-grid">
            <article className="panel platform-data-storage-card">
              <div className="platform-data-panel-title"><Cloud size={16}/> Cloud document storage</div>
              <strong>{formatGb(report.storage.cloudLogicalDocumentBytes)}</strong>
              <span>{formatCount(report.storage.cloudLogicalDocumentCount)} active logical source documents</span>
              <small>Metadata-reported file bytes, not encrypted disk occupancy.</small>
            </article>
            <article className="panel platform-data-storage-card">
              <div className="platform-data-panel-title"><Router size={16}/> Protected Agent queue files</div>
              <strong>{report.storage.agentCount === 0 ? "No Agents" : report.storage.freshAgentCount === 0 ? "Unavailable" : formatGb(report.storage.localQueueDiskBytes)}</strong>
              <span>{formatCount(report.storage.freshAgentCount)} of {formatCount(report.storage.agentCount)} Agents have snapshots newer than {report.storage.snapshotFreshnessMinutes} minutes</span>
              <small>
                {formatGb(report.storage.localQueueLogicalBytes)} logical documents · {formatGb(report.storage.localOutputFileBytes)} tracked PDF exports
                {report.storage.storageMeasurementComplete ? " · all paired Agents reporting" : " · stale or missing Agent snapshots excluded"}
              </small>
              <small>Agent values are reported file lengths, not filesystem-allocated cluster usage; exact physical disk occupancy is not measured.</small>
            </article>
          </div>

          <section className="panel platform-data-table-panel">
            <div className="platform-data-table-heading">
              <div><h3>Shop-by-shop network usage</h3><p>Showing up to 100 shops, ordered by measured document payload. Shops without recorded transfers remain visible with zero usage.</p></div>
            </div>
            <div className="platform-data-table-scroll">
              <table className="platform-data-table">
                <thead>
                  <tr>
                    <th>Shop</th><th>Cloud ingress</th><th>Cloud egress</th><th>Shop LAN</th><th>Total payload</th><th>Agent queue files</th><th>Transfer events</th>
                  </tr>
                </thead>
                <tbody>
                  {report.shops.length === 0 ? (
                    <tr><td colSpan={7} className="platform-data-empty">No shops are available for this report.</td></tr>
                  ) : report.shops.map(shop => (
                    <tr key={shop.tenantId}>
                      <td><strong>{shop.shopName}</strong><small>{shop.shopStatus}</small></td>
                      <td>{formatGb(shop.cloudIngressBytes)}</td>
                      <td>{formatGb(shop.cloudEgressBytes)}</td>
                      <td>{formatGb(shop.shopLanBytes)}</td>
                      <td><strong>{formatGb(shop.measuredPayloadBytes)}</strong></td>
                      <td>
                        {shop.agentCount === 0 ? "No Agent" : shop.freshAgentCount === 0 ? "Unavailable" : formatGb(shop.localQueueDiskBytes)}
                        <small>{shop.freshAgentCount}/{shop.agentCount} fresh snapshots</small>
                      </td>
                      <td>{formatCount(shop.transferCount)}<small>{shop.startedTransferCount} started · {shop.partialTransferCount} partial · {shop.failedTransferCount} failed</small></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <section className="panel platform-data-table-panel">
            <div className="platform-data-table-heading">
              <div><h3>Instrumented transfer paths</h3><p>Payload byte totals grouped by path and transfer result.</p></div>
            </div>
            <div className="platform-data-channel-list">
              {report.channels.length === 0 ? (
                <div className="platform-data-empty">No transfer events have been recorded yet.</div>
              ) : report.channels.map((channel, index) => (
                <div className="platform-data-channel-row" key={`${channel.channel}-${channel.direction}-${channel.status}-${index}`}>
                  <div><strong>{channelLabel(channel.channel)}</strong><small>{channel.direction} · {channel.status}</small></div>
                  <strong>{formatGb(channel.payloadBytes)}</strong>
                  <span>{formatCount(channel.transferCount)} events</span>
                  <small>{channel.durationSampleCount > 0
                    ? `Avg. handling ${Math.round(channel.totalDurationMilliseconds / channel.durationSampleCount).toLocaleString("en-IN")} ms`
                    : "Duration unavailable"}</small>
                </div>
              ))}
            </div>
          </section>

          <section className="platform-data-measurement-note">
            <div className="platform-data-note-icon"><Activity size={17}/></div>
            <div>
              <strong>Payload measurement, not wire billing</strong>
              <p>GB uses decimal units (1 GB = 1,000,000,000 bytes). Payload bytes represent original document content. Cloud egress measures bytes written to the API response body, not end-to-end Agent acknowledgement. Elapsed handling time includes API/Agent processing and response writes; it is not a network-latency, bandwidth or throughput test. HTTP/multipart headers and TLS overhead are not added; provider-billed traffic must be reconciled against infrastructure or proxy metrics. Raw transfer events are kept for {report.retention.rawEventRetentionDays} days and then folded into durable daily aggregates. The current dashboard does not estimate paths that are not instrumented.</p>
              <div className="platform-data-unmeasured">
                <span>Not yet measured:</span>
                {report.measurementScope.unmeasuredPaths.map(item => <small key={item}>{item}</small>)}
              </div>
            </div>
          </section>
        </>
      ) : (
        <div className="panel platform-data-empty-state">No report is available for this period. Refresh to retry.</div>
      )}
    </section>
  );
}
