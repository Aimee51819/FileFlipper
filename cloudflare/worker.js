// FileFlipper on Cloudflare: the website (fileflipper.app) and the downloads (dl.fileflipper.app).
//
// GitHub's servers are often unreachable from mainland China, so the website is served from here
// (the files in docs/, as static assets) and every release is copied to a Cloudflare R2 bucket
// (see .github/workflows/cloudflare.yml):
//
//   https://fileflipper.app/                                   the website (www. redirects here)
//   https://dl.fileflipper.app/FileFlipper.zip, /FileFlipper-Windows-Setup.exe, /FileFlipper-Windows.zip
//                                                              latest release
//   https://dl.fileflipper.app/v1.6.0/<file>                   a specific release
//   https://dl.fileflipper.app/latest.json                     version and file sizes, for the website
//   https://dl.fileflipper.app/stats.json?key=…                visits and downloads (private)
//
// Statistics, kept in a Durable Object (SQLite): page views, visitors per day, downloads per file and
// per day, countries and referring sites. No cookies, and no IP addresses are stored: a visitor is a
// one-way hash of day + IP + browser, used only to count each visitor once per day and deleted after
// two days. Obvious bots are not counted. Each download that starts from the beginning counts once
// (resumed chunks don't).

import { DurableObject } from "cloudflare:workers";

const SITE = "https://fileflipper.app/";
// SHA-256 of the key that unlocks /stats.json. The key itself is only in the owner's stats page.
const STATS_KEY_SHA256 = "a93ba88bbabb14111046baffde5c302d27053b349adef82d58f7a50127936503";
const BOTS = /bot|spider|crawl|slurp|preview|facebookexternalhit|headless|lighthouse|pingdom|uptime|monitor/i;

const TYPES = {
  zip: "application/zip",
  exe: "application/vnd.microsoft.portable-executable",
  json: "application/json; charset=utf-8",
};

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.protocol === "http:") {
      url.protocol = "https:";
      return Response.redirect(url.toString(), 301);
    }
    if (url.hostname.startsWith("www.")) {
      url.hostname = url.hostname.slice(4);
      return Response.redirect(url.toString(), 301);
    }
    if (url.hostname.startsWith("dl.")) return download(request, env, ctx, url);

    const response = await env.ASSETS.fetch(request);
    if (response.ok && isPageView(request, url)) ctx.waitUntil(recordView(request, env));
    return response;
  },
};

// MARK: Statistics

function counter(env) {
  return env.COUNTER.get(env.COUNTER.idFromName("downloads"));
}

/** The day in China time, e.g. "2026-10-06". */
function today() {
  return new Date(Date.now() + 8 * 3600 * 1000).toISOString().slice(0, 10);
}

function isBot(request) {
  return BOTS.test(request.headers.get("user-agent") || "");
}

function isPageView(request, url) {
  if (request.method !== "GET" || isBot(request)) return false;
  const path = url.pathname;
  const page = path === "/" || path.endsWith(".html") || !path.split("/").pop().includes(".");
  const document = request.headers.get("sec-fetch-dest") === "document"
    || (request.headers.get("accept") || "").includes("text/html");
  return page && document;
}

async function hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function recordView(request, env) {
  const day = today();
  const ip = request.headers.get("cf-connecting-ip") || "";
  const visitor = (await hex(`${day}|${ip}|${request.headers.get("user-agent") || ""}`)).slice(0, 20);
  let referrer = "";
  try {
    const host = new URL(request.headers.get("referer") || "").hostname.replace(/^www\./, "");
    if (host && !host.endsWith("fileflipper.app")) referrer = host;
  } catch {}
  await counter(env).recordView(day, visitor, request.cf?.country || "XX", referrer);
}

async function recordDownload(request, env, key) {
  if (isBot(request)) return;
  await counter(env).increment(key, today(), request.cf?.country || "XX");
}

async function stats(env, url) {
  const key = url.searchParams.get("key") || "";
  if ((await hex(key)) !== STATS_KEY_SHA256) {
    return new Response("Forbidden", { status: 403, headers: cors() });
  }
  const report = await counter(env).report();
  return new Response(JSON.stringify(report, null, 2), {
    headers: { ...cors(), "content-type": TYPES.json, "cache-control": "no-store" },
  });
}

// MARK: Downloads

async function download(request, env, ctx, url) {
  const key = decodeURIComponent(url.pathname.slice(1));

  if (request.method === "OPTIONS") return new Response(null, { headers: cors() });
  if (request.method !== "GET" && request.method !== "HEAD") {
    return new Response("Method not allowed", { status: 405, headers: { allow: "GET, HEAD" } });
  }
  if (key === "") return Response.redirect(SITE, 302);
  if (key === "stats.json") return stats(env, url);
  if (!/^(v[\w.-]+\/)?[\w.-]+$/.test(key) || key.includes("..")) return notFound();

  // Range and conditional requests let browsers and download managers resume big files.
  const object = request.method === "HEAD"
    ? await env.FILES.head(key)
    : await env.FILES.get(key, { range: request.headers, onlyIf: request.headers });
  if (object === null) return notFound();

  const headers = new Headers(cors());
  object.writeHttpMetadata(headers);
  headers.set("etag", object.httpEtag);
  headers.set("accept-ranges", "bytes");
  const ext = key.split(".").pop().toLowerCase();
  if (!headers.has("content-type")) headers.set("content-type", TYPES[ext] ?? "application/octet-stream");
  if (key.endsWith(".json")) {
    headers.set("cache-control", "public, max-age=300");
  } else {
    headers.set("content-disposition", `attachment; filename="${key.split("/").pop()}"`);
    headers.set("cache-control", "public, max-age=600");
  }

  // A 304 / 412 from onlyIf: the object has no body.
  if (request.method === "GET" && !("body" in object)) {
    return new Response(null, { status: 304, headers });
  }

  let status = 200;
  if (object.range && request.headers.has("range")) {
    const { offset, length } = rangeOf(object.range, object.size);
    headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
    headers.set("content-length", String(length));
    status = 206;
    if (offset === 0 && request.method === "GET") ctx.waitUntil(recordDownload(request, env, key));
  } else {
    headers.set("content-length", String(object.size));
    if (request.method === "GET" && !key.endsWith(".json")) ctx.waitUntil(recordDownload(request, env, key));
  }
  return new Response(request.method === "HEAD" ? null : object.body, { status, headers });
}

function rangeOf(range, size) {
  // R2 fills in only some of offset / length / suffix; the others are present but undefined.
  if (range.suffix !== undefined) return { offset: size - range.suffix, length: range.suffix };
  const offset = range.offset ?? 0;
  return { offset, length: range.length ?? size - offset };
}

function notFound() {
  return new Response("Not found. Downloads: " + SITE, { status: 404, headers: cors() });
}

function cors() {
  return { "access-control-allow-origin": "*", "access-control-allow-methods": "GET, HEAD, OPTIONS" };
}

/** Visits and downloads, stored in SQLite so concurrent updates never race. */
export class DownloadCounter extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    const sql = ctx.storage.sql;
    sql.exec("CREATE TABLE IF NOT EXISTS counts (file TEXT PRIMARY KEY, n INTEGER NOT NULL)");
    sql.exec(`CREATE TABLE IF NOT EXISTS daily (day TEXT PRIMARY KEY, views INTEGER NOT NULL DEFAULT 0,
              visitors INTEGER NOT NULL DEFAULT 0, downloads INTEGER NOT NULL DEFAULT 0)`);
    sql.exec(`CREATE TABLE IF NOT EXISTS daily_files (day TEXT NOT NULL, file TEXT NOT NULL, n INTEGER NOT NULL,
              PRIMARY KEY (day, file))`);
    sql.exec(`CREATE TABLE IF NOT EXISTS daily_countries (day TEXT NOT NULL, country TEXT NOT NULL,
              visitors INTEGER NOT NULL DEFAULT 0, downloads INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (day, country))`);
    sql.exec(`CREATE TABLE IF NOT EXISTS daily_referrers (day TEXT NOT NULL, host TEXT NOT NULL, n INTEGER NOT NULL,
              PRIMARY KEY (day, host))`);
    sql.exec("CREATE TABLE IF NOT EXISTS visitors (day TEXT NOT NULL, id TEXT NOT NULL, PRIMARY KEY (day, id))");
  }

  recordView(day, visitor, country, referrer) {
    const sql = this.ctx.storage.sql;
    // Visitor ids are only needed to count each visitor once per day.
    sql.exec("DELETE FROM visitors WHERE day < ?", day);
    sql.exec("INSERT OR IGNORE INTO visitors (day, id) VALUES (?, ?)", day, visitor).toArray();
    const isNew = sql.exec("SELECT changes() AS n").one().n > 0 ? 1 : 0;
    sql.exec(`INSERT INTO daily (day, views, visitors) VALUES (?, 1, ?)
              ON CONFLICT(day) DO UPDATE SET views = views + 1, visitors = visitors + ?`, day, isNew, isNew);
    if (isNew) {
      sql.exec(`INSERT INTO daily_countries (day, country, visitors) VALUES (?, ?, 1)
                ON CONFLICT(day, country) DO UPDATE SET visitors = visitors + 1`, day, country);
      if (referrer) {
        sql.exec(`INSERT INTO daily_referrers (day, host, n) VALUES (?, ?, 1)
                  ON CONFLICT(day, host) DO UPDATE SET n = n + 1`, day, referrer);
      }
    }
  }

  increment(file, day, country) {
    const sql = this.ctx.storage.sql;
    sql.exec("INSERT INTO counts (file, n) VALUES (?, 1) ON CONFLICT(file) DO UPDATE SET n = n + 1", file);
    if (!day) return;
    const name = file.split("/").pop();
    sql.exec("INSERT INTO daily (day, downloads) VALUES (?, 1) ON CONFLICT(day) DO UPDATE SET downloads = downloads + 1", day);
    sql.exec(`INSERT INTO daily_files (day, file, n) VALUES (?, ?, 1)
              ON CONFLICT(day, file) DO UPDATE SET n = n + 1`, day, name);
    sql.exec(`INSERT INTO daily_countries (day, country, downloads) VALUES (?, ?, 1)
              ON CONFLICT(day, country) DO UPDATE SET downloads = downloads + 1`, day, country || "XX");
  }

  report() {
    const sql = this.ctx.storage.sql;
    const rows = (query, ...args) => [...sql.exec(query, ...args)];
    const since = new Date(Date.now() + 8 * 3600 * 1000 - 29 * 86400 * 1000).toISOString().slice(0, 10);
    const downloads = {};
    for (const row of rows("SELECT file, n FROM counts ORDER BY file")) downloads[row.file] = row.n;
    return {
      generated: new Date().toISOString(),
      downloads,
      days: rows("SELECT day, views, visitors, downloads FROM daily ORDER BY day DESC LIMIT 90"),
      files: rows("SELECT day, file, n FROM daily_files WHERE day >= ? ORDER BY day DESC", since),
      countries: rows(`SELECT country, SUM(visitors) AS visitors, SUM(downloads) AS downloads FROM daily_countries
                       WHERE day >= ? GROUP BY country ORDER BY visitors DESC, downloads DESC`, since),
      referrers: rows(`SELECT host, SUM(n) AS visitors FROM daily_referrers WHERE day >= ?
                       GROUP BY host ORDER BY visitors DESC LIMIT 20`, since),
    };
  }
}
