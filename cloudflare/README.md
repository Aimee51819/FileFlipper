# FileFlipper on Cloudflare (fileflipper.app and dl.fileflipper.app)

GitHub's servers are often unreachable from mainland China, so the website (the files in `docs/`)
and every release are served from Cloudflare by the Worker in this folder. `www.fileflipper.app`
redirects to `fileflipper.app`.

| URL | What |
|---|---|
| `https://dl.fileflipper.app/FileFlipper.zip` | Mac, latest release |
| `https://dl.fileflipper.app/FileFlipper-Windows-Setup.exe` | Windows installer, latest release |
| `https://dl.fileflipper.app/FileFlipper-Windows.zip` | Windows portable, latest release |
| `https://dl.fileflipper.app/v1.6.0/<file>` | A specific release |
| `https://dl.fileflipper.app/latest.json` | Latest version and file sizes (used by the website) |
| `https://dl.fileflipper.app/stats.json` | Download counts per file |

`.github/workflows/cloudflare.yml` uploads the latest GitHub release and deploys the Worker. Releases
upload and deploy; changes to `docs/` or this folder only redeploy. It
runs after every release, whenever this folder changes, or by hand (Actions → Cloudflare →
Run workflow). It needs the repository secrets `CLOUDFLARE_API_TOKEN` (the "Edit Cloudflare
Workers" token template, limited to this account and the fileflipper.app zone) and
`CLOUDFLARE_ACCOUNT_ID`, and R2 must be enabled in the Cloudflare dashboard.
