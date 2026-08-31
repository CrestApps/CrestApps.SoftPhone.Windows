# Store submission payload (`packaging/store/`)

StoreBroker reads everything under this folder to build a Microsoft Store submission.
[`scripts/Submit-Store.ps1`](../../scripts/Submit-Store.ps1) consumes it from CI
(`release.yml` on a tag, and `publish-manual.yml` on demand).

```
packaging/store/
├─ SBConfig.json          # submission-shaping config (see comments inside)
└─ PDP/
   └─ en-US/
      ├─ ProductDescription.xml   # the listing text (name, description, keywords, captions)
      └─ screenshot-*.png         # screenshots referenced by DesktopImage= in the PDP
```

## These files are a starting point — regenerate from the live listing

The committed `SBConfig.json` and `ProductDescription.xml` are hand-written drafts so the
pipeline has something to submit. Once the Partner Center listing exists, generate the
authoritative versions against it and commit those:

```powershell
Install-Module StoreBroker -Scope CurrentUser
New-StoreBrokerConfigFile -AppId <STORE_APP_ID> -Path .\packaging\store\SBConfig.json
New-StorePdp -AppId <STORE_APP_ID> -Release "<friendly name>" -OutPath .\packaging\store\PDP
```

Then re-apply the path comments in `SBConfig.json` (the per-run paths are supplied by
`Submit-Store.ps1`, not hard-coded here).

## Screenshots

Drop the PNGs referenced by each `<Caption DesktopImage="…">` into the same language folder
(`PDP/en-US/`). Store screenshot requirements (min 1, 1366×768 or larger) apply. Add more
languages by creating sibling folders (`PDP/fr-FR/…`); en-US fills in for any locale without
its own PDP (`MediaFallbackLanguage` in `SBConfig.json`).

## Editing in Partner Center instead

Every field here can also be edited directly in the Partner Center listing UI. StoreBroker's
value is keeping the listing in source control and letting CI push it with each release; if
you edit in the portal, pull those changes back with `New-StorePdp` so the repo stays the
source of truth.
