# Store assets (`packaging/store/`)

The Microsoft Store **listing is managed in Partner Center**, not from this repo. CI only
uploads the built MSIX to a new submission (see [`scripts/Submit-Store.ps1`](../../scripts/Submit-Store.ps1)),
cloning the last submission so the existing listing carries forward unchanged.

So there is **no PDP or StoreBroker config here** — just the screenshot assets you upload to the
listing by hand.

```
packaging/store/
└─ screenshots/
   ├─ screenshot-desktop.png   # 1366x768 store screenshot (upload this in Partner Center)
   └─ _source-app.png          # the raw app-window capture the composite was built from
```

## Updating the screenshot

`screenshot-desktop.png` is the app window (`_source-app.png`) composited onto a Windows-style
desktop backdrop at the Store's 1366×768 size. To refresh it after a UI change: recapture the
window, replace `_source-app.png`, and rebuild the composite (the framing script lives in the
PR/commit history), then upload the new PNG in Partner Center → your app → Store listing →
Screenshots.

## First submission

CI's package-only flow can only clone an **existing** submission. Create and publish the first
submission (description, this screenshot, privacy policy URL, age rating) once in Partner Center;
after that, every tagged release swaps in the new build automatically.
