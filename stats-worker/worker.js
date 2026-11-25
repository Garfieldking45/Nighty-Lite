// Nighty Lite anonymous stats.
// Stored per install (random id only): version, Windows generation, first/last seen, the day each feature was last used,
// and error counts per version. Stored per day: active / new / updated installs, errors, failed updates.
// No IPs, no user agents, no names or settings are stored. Everything is kept in KV key metadata so the dashboard
// needs only a couple of list calls, however many installs there are.

const ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const VER_RE = /^\d{1,3}\.\d{1,3}\.\d{1,5}$/;
const FEATURES = ["clicker", "crossbow", "autotool", "boost", "crosshair", "cps", "sprint", "afk"];
const FEATURE_LABEL = {
  clicker: "Auto Clicker", crossbow: "Auto Crossbow", autotool: "Auto Tool", boost: "Boost FPS",
  crosshair: "Crosshair", cps: "CPS counter", sprint: "Auto Sprint", afk: "Anti-AFK",
};
const OS_OK = ["Windows 10", "Windows 11"];
const DAY = 86400000;
const INSTALL_TTL = 60 * 86400;
const DAY_TTL = 400 * 86400;
const HISTORY_DAYS = 30;

const dayNum = (ms) => Math.floor(ms / DAY);
const dayKey = (n) => "day:" + new Date(n * DAY).toISOString().slice(0, 10);
const clampInt = (x, max) => (Number.isInteger(x) && x > 0 ? Math.min(x, max) : 0);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const cmpVer = (a, b) => a.localeCompare(b, undefined, { numeric: true });
const pct = (n, d) => (d ? Math.round((100 * n) / d) : 0);

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    if (url.pathname === "/ping" && req.method === "POST") return ping(req, env);
    if (url.pathname === "/stats") return stats(url, env);
    return new Response("Not found", { status: 404 });
  },
};

// ---------------------------------------------------------------- ping
async function ping(req, env) {
  let b;
  try { b = await req.json(); } catch { return new Response("bad", { status: 400 }); }
  if (!ID_RE.test(b?.id ?? "") || !VER_RE.test(b?.v ?? "")) return new Response("bad", { status: 400 });

  const id = b.id.toLowerCase();
  const os = OS_OK.includes(b.os) ? b.os : "Unknown";
  const used = Array.isArray(b.f) ? [...new Set(b.f.filter((x) => FEATURES.includes(x)))] : [];
  const err = clampInt(b.err, 1000);
  const uf = clampInt(b.uf, 100);

  const now = Date.now();
  const today = dayNum(now);

  // Older installs may still hold a plain JSON value instead of metadata.
  const cur = await env.STATS.getWithMetadata(id, "json");
  const old = cur.metadata ?? (cur.value && typeof cur.value === "object" ? cur.value : null);

  const fs = { ...(old?.fs ?? {}) };
  for (const f of used) fs[f] = today;
  const ev = { ...(old?.ev ?? {}) };
  if (err) ev[b.v] = (ev[b.v] ?? 0) + err;
  // Keep the metadata small (KV allows 1 KB): remember errors for only the 6 newest versions.
  for (const k of Object.keys(ev).sort(cmpVer).slice(0, Math.max(0, Object.keys(ev).length - 6))) delete ev[k];

  const meta = { v: b.v, os, first: old?.first ?? now, last: now, fs, ev };
  await env.STATS.put(id, "1", { metadata: meta, expirationTtl: INSTALL_TTL });

  // Daily totals.
  const isNew = !old;
  const firstToday = !old || dayNum(old.last) !== today;
  const updated = !!old && old.v !== b.v;
  if (firstToday || isNew || updated || err || uf) {
    const k = dayKey(today);
    const d = (await env.STATS.getWithMetadata(k, "json")).metadata ?? { active: 0, new: 0, upd: 0, err: 0, uf: 0 };
    if (firstToday) d.active++;
    if (isNew) d.new++;
    if (updated) d.upd++;
    d.err += err;
    d.uf += uf;
    await env.STATS.put(k, "1", { metadata: d, expirationTtl: DAY_TTL });
  }
  return new Response("ok");
}

// ---------------------------------------------------------------- data
async function listAll(env, prefix) {
  const out = [];
  let cursor;
  do {
    const page = await env.STATS.list({ prefix, cursor });
    out.push(...page.keys);
    cursor = page.list_complete ? undefined : page.cursor;
  } while (cursor);
  return out;
}

async function collect(env) {
  const now = Date.now();
  const today = dayNum(now);

  const installs = (await listAll(env)).filter((k) => !k.name.startsWith("day:") && k.metadata).map((k) => k.metadata);
  const dayMap = {};
  for (const k of await listAll(env, "day:")) if (k.metadata) dayMap[k.name] = k.metadata;

  const seen = (r, days) => now - r.last < days * DAY;
  const a24 = installs.filter((r) => seen(r, 1));
  const a7 = installs.filter((r) => seen(r, 7));
  const a30 = installs.filter((r) => seen(r, 30));

  // Versions.
  const latest = installs.map((r) => r.v).sort(cmpVer).pop() ?? "";
  const byVer = {};
  for (const r of a7) byVer[r.v] = (byVer[r.v] ?? 0) + 1;
  const errByVer = {};
  for (const r of installs) for (const [v, n] of Object.entries(r.ev ?? {})) errByVer[v] = (errByVer[v] ?? 0) + n;
  const versions = Object.keys({ ...byVer, ...errByVer }).sort((a, b) => cmpVer(b, a)).map((v) => ({
    version: v, users7d: byVer[v] ?? 0, errors: errByVer[v] ?? 0,
  }));

  // Retention: of installs old enough, how many were still seen a week / a month after first seen.
  const retention = (days) => {
    const cohort = installs.filter((r) => now - r.first >= days * DAY);
    const kept = cohort.filter((r) => r.last - r.first >= days * DAY);
    return { days, cohort: cohort.length, kept: kept.length, percent: pct(kept.length, cohort.length) };
  };

  // Features used in the last 30 days, among installs seen in the last 30 days.
  const features = FEATURES.map((f) => {
    const n = a30.filter((r) => r.fs?.[f] !== undefined && today - r.fs[f] < 30).length;
    return { feature: f, label: FEATURE_LABEL[f], users: n, percent: pct(n, a30.length) };
  }).sort((x, y) => y.users - x.users);

  // Windows split (installs seen in the last 30 days).
  const osCount = {};
  for (const r of a30) osCount[r.os ?? "Unknown"] = (osCount[r.os ?? "Unknown"] ?? 0) + 1;

  // Daily history.
  const history = [];
  for (let i = HISTORY_DAYS - 1; i >= 0; i--) {
    const n = today - i;
    const d = dayMap[dayKey(n)] ?? { active: 0, new: 0, upd: 0, err: 0, uf: 0 };
    history.push({ date: dayKey(n).slice(4), ...d });
  }
  const sum = (key) => history.reduce((s, d) => s + d[key], 0);

  return {
    total: installs.length,
    active24h: a24.length, active7d: a7.length, active30d: a30.length,
    stale: installs.length - a7.length,
    latestVersion: latest,
    onLatest7d: byVer[latest] ?? 0,
    adoptionPercent: pct(byVer[latest] ?? 0, a7.length),
    retention: [retention(7), retention(30)],
    versions, features, os: osCount, history,
    last30d: { newInstalls: sum("new"), updatesApplied: sum("upd"), updateFailures: sum("uf"), errors: sum("err") },
  };
}

// ---------------------------------------------------------------- dashboard
async function stats(url, env) {
  // Private: open /stats?key=YOUR_STATS_KEY  (set with: npx wrangler secret put STATS_KEY)
  if (!env.STATS_KEY || url.searchParams.get("key") !== env.STATS_KEY) return new Response("Not found", { status: 404 });
  const data = await collect(env);
  if (url.searchParams.has("json")) return Response.json(data);
  return new Response(page(data), {
    headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" },
  });
}

function bars(values, labels, color, tip) {
  const W = 600, H = 120, pad = 18, n = values.length;
  const max = Math.max(1, ...values);
  const bw = (W - 8) / n;
  let svg = `<svg viewBox="0 0 ${W} ${H + pad}" width="100%" role="img">`;
  values.forEach((v, i) => {
    const h = Math.round((v / max) * (H - 14));
    svg += `<rect x="${4 + i * bw + 1}" y="${H - h}" width="${Math.max(1, bw - 2)}" height="${h}" rx="2" fill="${color}"><title>${esc(labels[i])}: ${v} ${esc(tip)}</title></rect>`;
  });
  svg += `<text x="4" y="${H + 13}" class="ax">${esc(labels[0])}</text>`;
  svg += `<text x="${W - 4}" y="${H + 13}" class="ax" text-anchor="end">${esc(labels[n - 1])}</text>`;
  svg += `<text x="${W - 4}" y="10" class="ax" text-anchor="end">max ${max}</text></svg>`;
  return svg;
}

function hbar(label, n, percent, note = "") {
  return `<div class="hb"><div class="hl"><span>${esc(label)}</span><span>${n}${note ? " · " + esc(note) : ""}</span></div>` +
    `<div class="tr"><div class="fi" style="width:${Math.min(100, percent)}%"></div></div></div>`;
}

function page(d) {
  const h = d.history;
  const labels = h.map((x) => x.date);
  const card = (v, t) => `<div class="c"><b>${v}</b>${esc(t)}</div>`;
  const verRows = d.versions.map((v) =>
    `<tr><td>${esc(v.version)}${v.version === d.latestVersion ? " <span class=tag>latest</span>" : ""}</td><td>${v.users7d}</td>` +
    `<td>${pct(v.users7d, d.active7d)}%</td><td>${v.errors}</td><td>${v.users7d ? (v.errors / v.users7d).toFixed(1) : "-"}</td></tr>`).join("") ||
    `<tr><td colspan=5>No data yet</td></tr>`;
  const osMax = Math.max(1, ...Object.values(d.os));
  const osBars = Object.entries(d.os).sort((a, b) => b[1] - a[1]).map(([k, n]) => hbar(k, n, pct(n, osMax))).join("") || "<p class=m>No data yet</p>";
  const featBars = d.features.map((f) => hbar(f.label, f.users, f.percent, f.percent + "%")).join("");
  const ret = d.retention.map((r) =>
    `<div class="c"><b>${r.cohort ? r.percent + "%" : "-"}</b>still using after ${r.days} days<small>${r.kept} of ${r.cohort} installs</small></div>`).join("");

  return `<!doctype html><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
<meta http-equiv=refresh content=60>
<title>Nighty Lite stats</title>
<style>
:root{--bg:#111;--card:#1b1b1b;--line:#2a2a2a;--fg:#eee;--mut:#9a9aa4;--a:#2dd4a7;--b:#6aa8ff;--c:#f0b429;--d:#ff6b6b}
@media(prefers-color-scheme:light){:root{--bg:#f6f6f7;--card:#fff;--line:#e3e3e6;--fg:#18181b;--mut:#6b6b75;--a:#0f9d7a;--b:#2563eb;--c:#b7791f;--d:#dc2626}}
body{font:15px system-ui,sans-serif;background:var(--bg);color:var(--fg);max-width:720px;margin:32px auto;padding:0 16px}
h1{margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px}.m{color:var(--mut);font-size:13px;margin:0 0 10px}
.g{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.c{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px}.c b{display:block;font-size:28px}.c small{display:block;color:var(--mut);margin-top:4px}
.p{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
table{width:100%;border-collapse:collapse}td,th{padding:8px 6px;text-align:left;border-bottom:1px solid var(--line)}th{color:var(--mut);font-weight:500}
.tag{background:var(--a);color:#000;border-radius:4px;padding:1px 6px;font-size:11px}
.hb{margin:8px 0}.hl{display:flex;justify-content:space-between;font-size:13px;margin-bottom:3px}.tr{background:var(--line);border-radius:4px;height:8px}.fi{background:var(--a);height:8px;border-radius:4px}
.ax{fill:var(--mut);font-size:10px}svg{display:block}
</style>
<h1>Nighty Lite users</h1>
<p class=m>Refreshes every minute. Active = opened the app. Data is anonymous (random install ID).</p>

<div class=g>
${card(d.active24h, "active last 24h")}${card(d.active7d, "active last 7 days")}${card(d.active30d, "active last 30 days")}
${card(d.total, "installs seen (60 days)")}${card(d.stale, "inactive 7+ days")}${card(d.latestVersion ? d.adoptionPercent + "%" : "-", "on latest version " + d.latestVersion)}
</div>

<h2>Active users per day</h2><div class=p>${bars(h.map((x) => x.active), labels, "var(--a)", "active")}</div>
<h2>New installs per day</h2><div class=p>${bars(h.map((x) => x.new), labels, "var(--b)", "new")}</div>
<h2>Updates applied per day</h2><div class=p>${bars(h.map((x) => x.upd), labels, "var(--c)", "updated")}</div>

<h2>Retention</h2><div class=g>${ret}</div>

<h2>Versions</h2><div class=p><table><tr><th>Version<th>Users (7d)<th>Share<th>Errors<th>Per user</tr>${verRows}</table>
<p class=m>Errors = logged errors reported by installs on that version (all time, last 6 versions per install).</p></div>

<h2>Features used (last 30 days)</h2><div class=p>${featBars}</div>
<h2>Windows</h2><div class=p>${osBars}</div>

<h2>Last 30 days</h2><div class=g>
${card(d.last30d.newInstalls, "new installs")}${card(d.last30d.updatesApplied, "updates applied")}
${card(d.last30d.updateFailures, "failed updates")}${card(d.last30d.errors, "errors reported")}
</div>`;
}
