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
//   https://dl.fileflipper.app/stats.json                      download counts
//
// Each completed download (not each resumed chunk) is counted in a Durable Object.

import { DurableObject } from "cloudflare:workers";

const SITE = "https://fileflipper.app/";

const TYPES = {
  zip: "application/zip",
  exe: "application/vnd.microsoft.portable-executable",
  json: "application/json; charset=utf-8",
};

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.hostname.startsWith("www.")) {
      url.hostname = url.hostname.slice(4);
      return Response.redirect(url.toString(), 301);
    }
    if (!url.hostname.startsWith("dl.")) return env.ASSETS.fetch(request);
    return download(request, env, ctx, url);
  },
};

async function download(request, env, ctx, url) {
  const key = decodeURIComponent(url.pathname.slice(1));

  if (request.method === "OPTIONS") return new Response(null, { headers: cors() });
  if (request.method !== "GET" && request.method !== "HEAD") {
    return new Response("Method not allowed", { status: 405, headers: { allow: "GET, HEAD" } });
  }
  if (key === "") return Response.redirect(SITE, 302);
  if (key === "stats.json") return stats(env);
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
    if (offset === 0 && request.method === "GET") ctx.waitUntil(count(env, key));
  } else {
    headers.set("content-length", String(object.size));
    if (request.method === "GET" && !key.endsWith(".json")) ctx.waitUntil(count(env, key));
  }
  return new Response(request.method === "HEAD" ? null : object.body, { status, headers });
}

function rangeOf(range, size) {
  // R2 fills in only some of offset / length / suffix; the others are present but undefined.
  if (range.suffix !== undefined) return { offset: size - range.suffix, length: range.suffix };
  const offset = range.offset ?? 0;
  return { offset, length: range.length ?? size - offset };
}

async function count(env, key) {
  const counter = env.COUNTER.get(env.COUNTER.idFromName("downloads"));
  await counter.increment(key);
}

async function stats(env) {
  const counter = env.COUNTER.get(env.COUNTER.idFromName("downloads"));
  const counts = await counter.counts();
  return new Response(JSON.stringify(counts, null, 2), {
    headers: { ...cors(), "content-type": TYPES.json, "cache-control": "no-store" },
  });
}

function notFound() {
  return new Response("Not found. Downloads: " + SITE, { status: 404, headers: cors() });
}

function cors() {
  return { "access-control-allow-origin": "*", "access-control-allow-methods": "GET, HEAD, OPTIONS" };
}

/** Download counts per file, stored in SQLite so increments never race. */
export class DownloadCounter extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS counts (file TEXT PRIMARY KEY, n INTEGER NOT NULL)");
  }

  increment(file) {
    this.ctx.storage.sql.exec(
      "INSERT INTO counts (file, n) VALUES (?, 1) ON CONFLICT(file) DO UPDATE SET n = n + 1", file);
  }

  counts() {
    const result = {};
    for (const row of this.ctx.storage.sql.exec("SELECT file, n FROM counts ORDER BY file")) {
      result[row.file] = row.n;
    }
    return result;
  }
}
