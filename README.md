# Hockey Roller

A Windows desktop tool for **Build a Hockey Team!** (Roblox). It watches the game window,
rolls/refreshes for you, works out the **rarity** of every card that appears, and clicks the
cards you actually want.

![tabs](docs/screenshot.png)

---

## Features

| Area | What it does |
|---|---|
| **Window picker** | Lists all visible top-level windows; pick the Roblox one. |
| **Capture** | Screen-grab or `PrintWindow`; *Auto* grabs both and keeps the one with real picture content (works in windowed, borderless and exclusive fullscreen). |
| **Point picking** | Click/drag on a captured preview to set the ROLL button, REFRESH button, ODDS button, ODDS close X, card region and odds region. |
| **Tier detection** | **OVR only.** The rarity comes solely from the card's OVR number read by OCR, mapped to tier ranges. No reference pixel, no colour matching, no reference images. Shiny (+2 OVR) and Prismatic/Franchise rainbow are handled. |
| **Blocked cards** | Detects the black diagonal ribbon (already on your team / position full) and skips those cards. |
| **Target selection** | Only claims cards inside your **Min..Max** rarity range; picks the **highest rarity**, and for equal rarity the **higher OVR** number (read from the top-left of the card). An optional **Min OVR** sets a rating floor, and a checkbox additionally lets *any* rarity be taken when its rating is high enough. |
| **Odds-aware rolling** | Reads the game's odds panel: if the *current* team can produce an eligible tier it uses **Refresh**, otherwise it **Rerolls**. The odds are only read once per team (refreshing keeps the same team/year). |
| **Refresh cap** | After N refreshes without an eligible hit it rerolls for a new team. |
| **Auto-stop** | Stops on the **TEAM COMPLETE** screen, when **rerolls or refreshes run out**, or when you press **ESC**. |
| **Input** | Uses the legacy `mouse_event` API (`SendInput` is silently blocked on some systems), glides the pointer onto the target first so the game registers the hover, then presses. |

---

## Requirements

* Windows 10 / 11 (x64)
* [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) — only needed to
  *run from source*; the published exe is self-contained.
* Roblox with **Build a Hockey Team!** installed

---

## Building

### Run from source

```powershell
git clone <this repo>
cd HockeyRoller
dotnet restore
dotnet run
```

### Publish a single self-contained exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\HockeyRoller.exe`
(~160 MB, no .NET install needed on the target machine).

### Build helper

```powershell
.\build.ps1            # publishes and copies the exe to ..\dist\HockeyRoller.exe
```

---

## Using it

1. **Start Roblox** and open *Build a Hockey Team!* in **windowed or borderless** mode.
2. Run `HockeyRoller.exe` and pick the **Roblox** window.
3. **Tab 1 – Setup**
   * **Capture Preview** — a screenshot of the game appears.
   * Pick **Roll Button**, **Refresh Button**, **Odds Button** and **Odds Close point**.
   * Pick the **Card Region** — drag over the block where the 2×2 cards appear.
     (the four cards are not perfectly aligned). The red circles in the preview show the spot
     that is sampled on every card.
   * Pick the **Odds Region** over the rarity list in the **THIS TEAM** column.
   * Set **Cards in region = 4**, **Layout = 2 x 2 grid**.
4. **Tab 2 – Rarities** – choose **Min** and **Max** rarity (the tiers are pre-configured).
5. **Tab 3 – Run** – press **Start Rolling**.

Useful buttons: **Test Click** (moves the pointer onto the button and clicks it, so you can
see whether the coordinates are right), **Test Detection** (reports what the tool sees),
**Save Debug Shot** (saves the game window with markers on every configured point).

### Keyboard

* **ESC** – stop rolling (the tool stays open)

---

## Tier colours (the only rarity signal)

The rarity of a card is decided by **one pixel**: the *reference pixel* on the card. The
colour of that pixel is compared with the tier colours and the closest tier wins.

```
%APPDATA%\HockeyRoller\tiercolours.txt
```

```
Bronze           = #C8A882
Silver           = #F0F0F0
Gold             = #E3D3A8
Diamond          = #B0ECE4
Icon             = #C4B4E4
Mythic           = #EFA8A8
Franchise        = #CFC0E8
Prismatic        = #2A3A8C
Limit Breaker    = #A9C4F2
Generational     = #E8C15A
Rivalry          = #EDE3C8
Rivalry Captain  = #EDE3C8
```

The file is read every time the tool starts — edit it and restart, or use
**Rarities → Edit colour file** to open it in Notepad.

A card is only given a tier when

* the colour is within **Max colour distance** (default 35) of the tier colour,
* it differs from that colour by no more than **Channel tolerance** (default 28) in *any*
  single R/G/B channel, and
* it is at least **Ambiguity margin** (default 12) closer to that tier than to any other one.

Otherwise the card is reported as *no match* and is never claimed. The margin matters because
Icon `#C4B4E4` and Franchise `#CFC0E8` are only 17 apart.

All three numbers are on the **Setup** tab, so they can be tightened or loosened without
rebuilding.

A **holo/shiny** card can have its own colour on a second line:

```
Mythic shiny = #EFA8A8
```

Both variants are matched and the closer one wins, so no extra detection is needed.

### Placing the reference pixel on every card

The four cards are not aligned the same way inside the 2×2 grid, so each card gets its own
spot. Two ways to do it:

* **By hand** – Setup → *Reference pixel → Pick on preview*, then click the badge text on
  each card. A red circle in the preview shows the spot that is sampled on every card.
* **Automatically** – Setup → **Auto-assign from debug capture**. The tool reads the debug
  picture (`cards_last.png`, the exact frame it last looked at), finds the OVR number on
  every card and puts the reference pixel on the position badge right below it. Card 2 is
  the anchor: the spot you picked by hand is kept as long as it is still on the badge.

### Teaching the tool the colours the game really shows

The colours in the file are the reference colours. If the game renders a tier slightly
differently, let the tool read the colours straight from the cards on screen:

1. **Setup → Capture Preview** with the cards on screen
2. **Rarities → Learn ref colours from cards** — label each card with its tier

The sampled colours are written to `tiercolours.txt` and used from then on. Cards whose
reference spot was never picked are located automatically.

---

## Tier detection (OVR only)

Rarity comes **only** from the card's **OVR number** (read by OCR). No reference pixel, no
colour matching, no reference images, no frame colour. The number is mapped to a tier:

| Tier | OVR |
|---|---|
| Rivalry Captain | 116 |
| Generational | 112-115 |
| Rivalry | 112 + vs symbol (bottom right) |
| Limit Breaker | 108-111 |
| Prismatic | 105-107 |
| Franchise | 101-104 |
| Mythic | 99 |
| Icon | 95-98 |
| Diamond | 90-94 |
| Gold | 83-89 |
| Silver | 75-82 |
| Bronze | 70-74 |

* **Shiny** (rainbow border on a non-Prismatic card) adds **+2 OVR**; it is detected by a
  rainbow border and a check that pixels just inside it are not white.
* **Prismatic** is a white body inside a rainbow border and can **never** be shiny; a shiny
  lower-tier card (e.g. shiny Franchise at raw OVR 103-106) is still that lower tier.
* **Prismatic-and-above can never be shiny.**
* No card exists at OVR 100; **116 is the highest**.

---