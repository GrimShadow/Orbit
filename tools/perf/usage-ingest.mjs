#!/usr/bin/env node
// Load test for POST /api/v1/usage/events (spec Step 2.7: 1M events/day without API p95 above 500 ms).
//
//   node tools/perf/usage-ingest.mjs <api-url> <bearer-token> [total-events=100000] [concurrency=4]
//
// Plain Node 18+, no dependencies. It lists the assets the token can read (GET /assets, up to 500), then posts batches of 500 events (the API's
// limit) spread over those assets, event types, channels and the last 30 days, `concurrency` requests at a time, each with a unique clientEventId.
// It prints throughput and the p50/p95/p99/max latency of the requests and exits 1 if any request failed or the p95 is above 500 ms.
//
// Run it against a throwaway stack: it writes real usage events (and marks days for rollup) into whatever database the API uses.
import { performance } from 'node:perf_hooks';
import { randomBytes } from 'node:crypto';

const BATCH = 500;
const P95_BUDGET_MS = 500;
const [apiUrl, token, totalArg, concurrencyArg] = process.argv.slice(2);

if (!apiUrl || !token || apiUrl === '--help' || apiUrl === '-h') {
  console.error('usage: node tools/perf/usage-ingest.mjs <api-url> <bearer-token> [total-events=100000] [concurrency=4]');
  process.exit(apiUrl === '--help' || apiUrl === '-h' ? 0 : 2);
}
const total = Math.max(1, Number.parseInt(totalArg ?? '100000', 10) || 100000);
const concurrency = Math.max(1, Number.parseInt(concurrencyArg ?? '4', 10) || 4);
const base = apiUrl.replace(/\/+$/, '');
const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };

async function listAssets(limit = 500) {
  const ids = [];
  let cursor = null;
  while (ids.length < limit) {
    const url = `${base}/api/v1/assets?limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`;
    const res = await fetch(url, { headers });
    if (!res.ok) throw new Error(`GET /assets answered ${res.status}: ${await res.text()}`);
    const page = await res.json();
    for (const a of page.items ?? []) ids.push(a.id);
    cursor = page.nextCursor;
    if (!cursor) break;
  }
  return ids.slice(0, limit);
}

const TYPES = ['view', 'download', 'share', 'embed', 'place', 'play'];
const CHANNELS = ['console', 'portal', 'kiosk-lobby', 'kiosk-showroom', 'app-ios', 'app-android', 'web', 'signage-north', 'signage-south', 'signage-east', 'signage-west', null];
const pick = (list) => list[Math.floor(Math.random() * list.length)];

function batch(assets, runId, first, count) {
  const now = Date.now();
  const events = [];
  for (let i = 0; i < count; i++) {
    events.push({
      clientEventId: `perf-${runId}-${first + i}`,
      assetId: pick(assets),
      eventType: pick(TYPES),
      channel: pick(CHANNELS),
      occurredAt: new Date(now - Math.floor(Math.random() * 30 * 24 * 3600 * 1000)).toISOString(),
    });
  }
  return { events };
}

function percentile(sorted, p) {
  if (sorted.length === 0) return NaN;
  return sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1))];
}

const assets = await listAssets();
if (assets.length === 0) {
  console.error('The token can read no assets: create some first (events can only be recorded for assets the caller may read).');
  process.exit(2);
}

const runId = randomBytes(4).toString('hex');
const batches = Math.ceil(total / BATCH);
const latencies = [];
const failures = [];
let accepted = 0, duplicates = 0, rejected = 0, next = 0;

async function worker() {
  while (true) {
    const n = next++;
    if (n >= batches) return;
    const first = n * BATCH;
    const body = JSON.stringify(batch(assets, runId, first, Math.min(BATCH, total - first)));
    const started = performance.now();
    try {
      const res = await fetch(`${base}/api/v1/usage/events`, { method: 'POST', headers, body });
      const text = await res.text();
      latencies.push(performance.now() - started);
      if (!res.ok) { failures.push(`batch ${n}: ${res.status} ${text.slice(0, 200)}`); continue; }
      const r = JSON.parse(text);
      accepted += r.accepted; duplicates += r.duplicates; rejected += r.rejected;
    } catch (e) {
      latencies.push(performance.now() - started);
      failures.push(`batch ${n}: ${e.message}`);
    }
  }
}

console.log(`posting ${total} events in ${batches} batch(es) of up to ${BATCH}, ${concurrency} at a time, over ${assets.length} asset(s) ...`);
const t0 = performance.now();
await Promise.all(Array.from({ length: Math.min(concurrency, batches) }, worker));
const seconds = (performance.now() - t0) / 1000;

const sorted = [...latencies].sort((a, b) => a - b);
const ms = (v) => `${v.toFixed(1)} ms`;
const p95 = percentile(sorted, 95);
console.log('');
console.log(`events accepted     ${accepted}  (duplicates ${duplicates}, rejected ${rejected})`);
console.log(`requests            ${latencies.length}  (failed ${failures.length})`);
console.log(`duration            ${seconds.toFixed(2)} s`);
console.log(`throughput          ${Math.round(accepted / seconds)} events/s  (${(latencies.length / seconds).toFixed(1)} requests/s)`);
console.log(`                    = ${Math.round((accepted / seconds) * 86400).toLocaleString('en-US')} events/day at this rate`);
console.log(`latency per request p50 ${ms(percentile(sorted, 50))}   p95 ${ms(p95)}   p99 ${ms(percentile(sorted, 99))}   max ${ms(sorted[sorted.length - 1] ?? NaN)}`);
for (const f of failures.slice(0, 5)) console.log(`failure: ${f}`);

if (failures.length > 0) process.exit(1);
if (p95 > P95_BUDGET_MS) {
  console.log(`p95 is above the ${P95_BUDGET_MS} ms budget`);
  process.exit(1);
}
console.log(`p95 is within the ${P95_BUDGET_MS} ms budget`);
