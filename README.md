# Scenery Atlas — desktop preview 0.4.0

A standalone Windows scenery library built around the BA Virtual globe renderer, with separate local accounts. The installed application does not need the BA website, its database, or a BA account.

## Run

For local testing, use `npm run preview` from this folder. It builds and opens a separate **Scenery Atlas Preview** window without installing anything or closing your installed copy. The preview keeps its own local account and folder choices in `artifacts/preview-profile`. After editing the renderer, run `npm run preview` again; the open preview reloads the rebuilt UI automatically. For native C# changes, close only the preview window and run the command again. Build the installer only when a version is ready to release.

Run `artifacts/releases/SceneryAtlas-0.4.0-Setup.exe` for a per-user Windows install, Start Menu shortcut and uninstaller. The installer does not request administrator access. The portable build remains at `artifacts/SceneryAtlas-0.4.0/SceneryAtlas.exe`; keep its accompanying files and `Web` folder alongside the executable. Register a local username and password in the app. Microsoft Edge WebView2 Runtime is required; the app reports a clear startup message if it is missing. The installer is not code-signed during local testing, so Windows may show an unknown-publisher prompt.

Accounts, purchase confirmations, folder choices and manual map locations are local to the Windows user. Passwords use PBKDF2-SHA256 with 600,000 iterations and individual random salts. Account separation is an app feature, not protection against someone with access to the same Windows account and its files. The app does not collect store credentials, email addresses or cloud profiles.

## Working features

- The website's WGS84 globe renderer, locally bundled, with orbit, pan, zoom, adaptive quality and terrain imagery. Scenery markers are drawn in one GPU pass, with horizon culling and click selection.
- Optional 3D cloud volumes from the public BA Virtual NOAA GFS global forecast or GOES satellite observation artifacts hosted on GitHub Releases, plus aurora curtains from NOAA SWPC's public OVATION feed. The desktop app reads these sources directly, without relying on the website's cloud API. They require an internet connection and a current artifact. Forecast and satellite modes are labelled separately; unavailable data never turns into invented live weather. Settings now provides a cloud strength slider that changes visual opacity only, plus source, quality and aurora controls. Choices persist per local account.
- **Settings → Simulator folders** lets each local account select the actual MSFS 2020/2024 Community folder, on any drive. Addon Linker junctions inside it are treated as active for the next simulator launch; separately selected Addon Linker storage folders expose unlinked packages as disabled. A bounded scan checks grouped library folders without changing them. The Settings page also has sections for volumetric clouds and globe display.
- Installed Community aircraft and utilities appear in the library under their own category. Only scenery with a known location becomes a globe marker. Choose a local departure and arrival from active scenery airports and focus the arrival on the globe; this does not book a flight.
- MSFS 2024 and 2020 Community/Official folder discovery from the current `UserCfg.opt` installation path. `InstalledPackagesPathNextBoot` is deliberately not treated as the current installation.
- X-Plane 11 and 12 `Custom Scenery` scanning and `scenery_packs.ini` enabled/disabled state where the simulator installation is found. Add another Custom Scenery folder manually if auto-discovery misses a Steam library or custom install path. Airport placement comes from `apt.dat` or a clear ICAO in the package name. Library packages with `library.txt` appear separately.
- Read-only package manifest scanning, airport placement using 10,666 ICAO-coded airports, and manual ICAO/coordinate placement for unlocated scenery or landmarks. Unmapped products remain visible in the list.
- Available, Purchased, Installed, Active and Disabled map views, plus ICAO, title, category, developer and simulator filtering. Selecting a view changes the list and globe markers together.
- Package details, versions, installation paths, activation evidence and potential same-airport overlap notices.
- Per-profile purchase confirmations, catalogue imports, and separately configured library folders. No package installation, relocation, deletion, or simulator configuration edits.
- Catalogue entries can list supported `simulators` (MSFS 2020/2024, X-Plane 11/12) and `requiredLibraries` as named public HTTPS download pages. The detail panel gives users the publisher link for each requirement. Compatibility and requirements remain publisher-supplied, and no third-party library files are bundled or silently installed. Installed copies are never assumed compatible with another simulator merely because the titles match.
- Online update checks via **Updates → Check GitHub now**. The app checks the latest public [ScneryAtlas release](https://github.com/TofferAviation/ScneryAtlas/releases/latest), compares its version, downloads the matching Windows installer only when requested, and verifies its SHA-256 from `latest.json` before opening it. A local releases folder remains available under **Local release testing**. Updates replace app files and retain local account/library data at `%LOCALAPPDATA%\SceneryAtlas`.

## Activation means evidence, not a guess

For MSFS, `Active` means a package is present in a Community root, including an Addon Linker junction, and is not explicitly disabled in `Content.xml`. It means available on the next launch, not verified loaded in a running flight. Both older top-level files and newer per-gamertag subfolders are checked. `UserDisabled`, `SystemDisabled`, `false` and `disabled` are treated as disabled. Contradictory entries are unknown. Packages in Official or Streamed roots without explicit activation remain unconfirmed. For X-Plane, `Active` and `Disabled` follow the matching line in `scenery_packs.ini`; an unlisted folder remains unknown because X-Plane may register it on its next launch. External libraries are unmounted unless the user confirms their simulator folder.

Enable/disable scenery through your simulator or existing add-on manager, then rescan in Atlas. The app intentionally does not move or rewrite scenery packages.

## Store coverage

- **Flightsim.to:** official API access must be requested from Flightsim.to before automatic catalogue/download integration can be enabled. Installed packages from Flightsim.to can still be scanned. https://flightsim.to/partners/
- **iniBuilds:** a bounded, public-page discovery adapter is included for the iniScenery page. It is a partial index, not a store-account or entitlement connection. The storefront returned HTTP 403 during the desktop check on 20 November 2026; the app reports this and retains existing records. It does not bypass store protections. Use an approved catalogue import until a supported feed is available.
- **Other stores/developers:** JSON catalogue import is implemented. Fill `catalogue-template.json` with real product data; its placeholders are not a product catalogue. A product can provide `icao` or numeric `latitude` and `longitude`. `packageName` enables exact matching to local packages. Products at the same airport are never merged solely by ICAO.
- **Purchased:** a manual personal record, not automatically verified ownership. No inferred purchase from an installed file. Store links open in the user's external browser.

There is no claim of complete worldwide store coverage, live prices, automatic purchases, store-login syncing or automatic downloads in this preview.

## Build and test

Requires Node.js, the .NET 10 SDK and NSIS 3.13 on Windows. The installer build expects the verified official NSIS ZIP extracted at `artifacts/tools/nsis/nsis-3.13`. Its upstream SHA-256 is `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`. Increment the matching versions in `package.json` and `app/SceneryAtlas.App.csproj` for a new release; the build scripts derive installer names and `latest.json` from that version.

```powershell
npm ci
npm run build
npm test
pwsh -File scripts/package.ps1
pwsh -File scripts/build-installer.ps1
```

The browser globe modules and textures are snapshotted in `vendor/website` from the user's website checkout at commit `0be31f4325eff92a0c5ae0e9d77cc2981bf9380d`. Build-time integration adds scenery overlays and camera focus without modifying the original website files. After the first snapshot, the website checkout is not needed. `data/airports.json` is bundled, so later builds do not need to download the airport CSV again.

## Publish an update

The public source repository is [TofferAviation/ScneryAtlas](https://github.com/TofferAviation/ScneryAtlas). For each public release, update the matching version in `package.json`, `package-lock.json` and `app/SceneryAtlas.App.csproj`, commit and push the source, then push a matching `vX.Y.Z` tag. `.github/workflows/release.yml` builds and tests on Windows, makes the installer, verifies its checksum, and publishes the installer plus `latest.json` to a GitHub Release. The app checks `https://github.com/TofferAviation/ScneryAtlas/releases/latest/download/latest.json`; only published stable releases are offered. Do not push a release tag until the build is ready for users.

Existing installed builds that only know about local release folders need one manual install of the first GitHub-enabled release. After that, later releases appear in the app's Updates screen. Preview builds remain separate and never require an installer for each change.

For an isolated test profile, set `SCENERY_ATLAS_DATA` before launching the executable. It changes only Atlas data storage. Automated checks cover password hashing/login, account isolation, discovery, activation parsing/conflicts, malicious XML, scenery filtering, incomplete metadata, ownership merging and manual map locations.

Verification: automated checks cover X-Plane scanning, MSFS Community activation, grouped Addon Linker libraries, multi-simulator catalogue records and both local and GitHub update checksum rejection. Live weather needs network access to BA Virtual and NOAA; its availability varies independently of the desktop app. Automatic store-wide catalogue connections remain limited as described above.

## Sources and attribution

- Globe: the user's BA Virtual application and its existing browser graphics modules. Three.js and React retain their package licences.
- Imagery: NASA GIBS Blue Marble, VIIRS city lights, and historical Landsat annual imagery. This is not live satellite ground imagery. https://gibs.earthdata.nasa.gov/
- Terrain: Mapzen Terrain Tiles / AWS Open Data, including USGS/NOAA and other contributors; source geoid heights are visual relief over WGS84, not surveyed navigation elevations. https://registry.opendata.aws/terrain-tiles/
- Airports: OurAirports public-domain data via https://github.com/datasets/airport-codes . ICAO-coded, non-closed facilities with valid coordinates are included; no affiliation or guarantee of accuracy.
- Store listings belong to their respective publishers. The app includes no purchased scenery files or third-party product artwork.
- X-Plane `Custom Scenery`, `scenery_packs.ini` and library behaviour follow Laminar Research's [WorldEditor manual](https://developer.x-plane.com/manuals/wed/) and [library format specification](https://developer.x-plane.com/article/library-library-txt-file-format-specification/). Installer built with [NSIS](https://nsis.sourceforge.io/); its compiler is not shipped inside the app.

The desktop app uses BA Virtual's public processed cloud artifacts on GitHub Releases and NOAA SWPC's public aurora feed. It does not connect to BA Virtual accounts, booking database or live traffic. No fake weather is generated to fill missing observations.
