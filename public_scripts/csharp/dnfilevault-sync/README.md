# DNFileVault Sync for .NET

A cross-platform .NET 8 global tool for synchronizing files from one
DNFileVault group. It has no third-party package dependencies.

The tool discovers and health-checks API endpoints, authenticates using
environment variables, finds an authorized group by name, downloads through
the Cloudflare link, and falls back to the authenticated API route.

Downloads are written to temporary .part files and moved into place only
after their advertised size is validated. Each completed file receives a
sidecar manifest containing its DNFileVault UUID, timestamps, and local
SHA-256 hash. A new UUID under an existing display name is preserved as a
separate revision instead of overwriting the earlier delivery.

## Build and run

~~~bash
export DNFV_EMAIL="you@example.com"
export DNFV_PASSWORD="your-password"
dotnet run --project ./DnFileVault.Sync.csproj -- --group eodLevel2 --output /data/dnfilevault/eodLevel2 --days 7
~~~

Windows PowerShell uses the same command after setting $env:DNFV_EMAIL and
$env:DNFV_PASSWORD.

## Install as a local global tool

~~~bash
dotnet pack -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg DnFileVault.Sync
dnfilevault-sync --group eodLevel2 --output ./downloads --days 7
~~~

## Configuration

Credentials are read only from environment variables:

| Variable | Required | Description |
| --- | --- | --- |
| DNFV_EMAIL | Yes | DNFileVault account email |
| DNFV_PASSWORD | Yes | DNFileVault account password |
| DNFV_GROUP | No | Default group when --group is omitted |
| DNFV_OUT_DIR | No | Default output when --output is omitted |
| DNFV_DAYS | No | File creation lookback; default 7, maximum 35 |

Command-line values take precedence over optional environment values.

## Scheduling

The tool performs one synchronization pass and returns a process exit code,
making it suitable for cron, systemd timers, Windows Task Scheduler, or a
container scheduler. An hourly cron example is:

~~~cron
15 * * * * /usr/local/bin/dnfilevault-sync --group eodLevel2 --output /data/dnfilevault/eodLevel2 --days 7
~~~

Do not put credentials directly in the scheduler command. Load them from a
permission-restricted environment file.

| Exit code | Meaning |
| --- | --- |
| 0 | Synchronization completed |
| 2 | Configuration or command-line error |
| 3 | Authentication or authorization error |
| 4 | Vendor endpoints or download failed |

The tool intentionally synchronizes one named group. It does not scrape or
guess file URLs, and it does not download unrelated purchases.
