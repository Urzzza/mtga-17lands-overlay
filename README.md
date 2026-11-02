# MTGA 17Lands Overlay

Standalone Windows 11 x64 draft overlay. Shows the **current pack**, with English card names, locally rendered mana tokens, and GIH win rate descending. Supports Premier, Traditional, and Quick Draft. No Overwolf or API token required.

**Status: Windows build and core checks available; live Arena / Alt+Enter fullscreen acceptance has NOT been performed. This is a candidate build, not a fullscreen-validated release.** See [WINDOWS-ACCEPTANCE.md](WINDOWS-ACCEPTANCE.md). No game-process injection, registry changes, or automatic fullscreen switching.

## Run

1. Extract the entire portable ZIP to a writable folder and launch `Overlay.Windows.exe`. The .NET runtime is included. Windows may show an unsigned-app prompt; binaries are not code-signed.
2. In Arena, enable **Options → Account → Detailed Logs (Plugin Support)**, then restart Arena.
3. Open the overlay's tray icon → **Settings / import data**. Check the detected `Player.log` path.
4. Click **Select sets and formats to download…**. The app loads the published [17Lands Public Datasets](https://www.17lands.com/public_datasets) catalog. Select one or more sets and any of Premier, Traditional (`TradDraft`), and Quick Draft, then click **Download and import selected**. Only published pairs are queued; the dialog shows how many requested pairs are unavailable. Choices are remembered for the next visit. Downloads run one at a time with byte progress, followed by game-import progress, and can be cancelled. Individual failures are reported while the remaining queue continues. Completed pairs remain installed after cancellation.

   Alternatively, import a local `.csv` or `.csv.gz`, entering its exact set code and format. A replacement overwrites that set/format's prior cache; do not import separate shards expecting them to merge.
5. Card metadata is fetched from Scryfall for missing Arena IDs and cached. For offline use, import a Scryfall bulk **card JSON array** that includes `arena_id`, `name`, and `mana_cost` / `card_faces`, from [Scryfall Bulk Data](https://scryfall.com/docs/api/bulk-data). You can combine metadata from several imports. Scryfall's oracle-only dataset may omit Arena printings; prefer default/all card data.
6. Enter a supported draft. The panel appears 16 logical pixels from Arena's lower-left client corner while Arena is foreground. Tray controls provide preview, hide/show, and exit. Set/format overrides, scale, opacity, and offsets are in settings. Preview can be shown without Arena; fullscreen probe requires Arena foreground.

For a first human pack absent from Arena logs, the app waits for pack data. It clears cards after a pick. First-pick data is not reconstructed with screenshots. If startup logs lack an event name, use the set and format overrides. Specialty draft formats are not supported automatically.

## Statistics and privacy

Statistics are calculated locally from **licensed public datasets**, not the restricted 17Lands website statistics API. Data attribution: **17Lands**, [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). This app is not endorsed by 17Lands.

GIH rate = wins / games where at least one copy was in the kept opening hand or drawn. A game is counted once per card even if multiple copies were seen. All games in the imported file are included, with no rank, player, color, or date filtering. Results may differ from the website due to its filters, data cleaning, and dataset coverage. Coverage is derived from `game_time`; unknown dates remain explicit. Set and format selection is supplied by the importer because CSVs do not always carry that metadata. Select it carefully.

Rows sort by full-precision rate, then English name, then Arena ID. Percentages display one decimal place. Missing metadata/rates are shown as unknown/`N/A`, last in the list. There is no fallback to other sets/formats. Public datasets lag release and may be unavailable for a new set.

Settings, download selections, merged card metadata, and compact aggregates are stored under `%LOCALAPPDATA%\MTGA17LandsOverlay`. Manually imported source files remain where selected. Automatic downloads use temporary files under the app data folder and delete them after success, failure, or cancellation. Existing aggregates are replaced only after a full successful download and import. No logs, gameplay, account IDs, or tokens are uploaded. Catalog discovery reads the public-data document on `https://17lands.cdn.prismic.io/api/v2`, the same content used by the public datasets page. Game downloads use exact published set/format URLs on `https://17lands-public.s3.amazonaws.com`. Redirects and other download hosts are rejected, downloads have an inactivity timeout and a 20 GiB compressed size limit, and the complete source file is verified for HTTP length when supplied before import. Catalog access failures leave manual import available. New sets and formats appear only after public game data is published; there is no fallback to restricted website statistics.

Only missing card IDs go to `https://api.scryfall.com/cards/collection`, in batches with a descriptive User-Agent and delay. Failed lookups are retried when settings are saved. Metadata can be imported locally instead. Dataset downloads occur only when you click Download; there are no scheduled refreshes, application auto-updates, or telemetry.

## Build and verify

Requires .NET 10 SDK. Run from the repository root:

```powershell
dotnet run --project tests/Overlay.Tests -c Release
# Optional read-only smoke check against the real published catalog:
dotnet run --project tests/Overlay.Tests -c Release -- --live-catalog
dotnet build src/Overlay.Windows -c Release
dotnet publish src/Overlay.Windows -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/MTGA17LandsOverlay
```

Core checks are a dependency-free executable test harness; failures return a nonzero exit code. The core targets `net10.0` and can run on macOS/Linux. The WPF UI targets Windows and can be cross-built but not run on macOS. Windows CI runs the checks, builds, and uploads an **unvalidated** portable candidate ZIP; it does not create a GitHub release or claim live fullscreen verification.

The source separates `Overlay.Core` (log records, tailing, metadata, ranking, streaming import and atomic storage) from `Overlay.Windows` (native window tracking, overlay, settings and tray). Logs are reopened with read/write/delete sharing. Partial UTF-8 lines are retained; truncation, changed creation time, or changed file prefix trigger a replay. Replaying ends in the latest logged state; scene transitions/completion hide the overlay. Unlogged transitions cannot be inferred reliably.

Unofficial Magic: The Gathering fan tool; not affiliated with Wizards of the Coast. Card names and mana designations belong to their respective owners.
