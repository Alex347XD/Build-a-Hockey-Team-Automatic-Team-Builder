# Hockey Roller

A Windows desktop tool for **Build a Hockey Team!** (Roblox). It watches the game window,
rolls/refreshes for you, works out the **rarity** of every card that appears, and clicks the
cards you actually want.

---

## Features

| Area | What it does |
|---|---|
| **Window picker** | Lists all visible top-level windows; pick the Roblox one. |
| **Capture** | Screen-grab or `PrintWindow`; *Auto* grabs both and keeps the one with real picture content (works in windowed, borderless and exclusive fullscreen). |
| **Point picking** | Click/drag on a captured preview to set the ROLL button, REFRESH button, ODDS button, ODDS close X, card region and odds region. There is no separate dismiss point — **reroll/refresh dismisses** a card. |
| **Tier detection** | **OVR only.** The rarity comes solely from the card's **OVR number read by OCR**, mapped to your tier ranges. |
| **Shiny** | Shiny adds **+2 OVR**, detected by a rainbow border (scanned across the whole frame, not just one spot) excluding Prismatic; `o = rawOvr − 2`, then ranges on **base OVR**. **Prismatic-and-above can't be shiny.** |
| **Prismatic vs shiny-Franchise** | Prismatic = white inside + rainbow border + OVR 105-107, never shiny. Franchise = dark-purple strip on black. Prismatic check only runs when in Prismatic range. |
| **Blocked cards** | Detects the black diagonal **POSITION FILLED / ALREADY ON TEAM** ribbon (`blockedRibbon`) and skips those cards. Ribbon cards are still eligible for the Prismatic/shiny checks (a ribbon doesn't change rarity); it's not used as a rarity signal. |
| **Target selection** | Only claims cards inside your **Min..Max** rarity range; picks the **highest rarity**, and for equal rarity the **higher OVR**. Optional **Min OVR**; **AcceptAnyTierByOvr** also takes any rarity at/above Min OVR; **OVR-only** ignores rarity for selection (rarity still drives Refresh). One pick per team, then reroll. |
| **Odds-aware rolling** | Reads the game's odds panel, **THIS TEAM / REFRESH (right) column only**, line by line; odds read once per team. Smart mode Refreshes if an eligible tier is available, else Rerolls. |
| **Don't waste refreshes** | **Odds + roster sheet + current claimed:** if every eligible-rarity player in the current team + year is already claimed, it **Rerolls instead of Refresh**. |
| **Team roster sheet** | Per-team roster + rarity from a Google Sheet, **embedded in the exe**. `2026 Olympics Team [Country] 2026` ("Team USA 2026" → United States). |
| **Refresh cap** | After N refreshes without an eligible hit it rerolls for a new team. On the cap-triggered cycle nothing is claimed unless a card is eligible. |
| **Auto-stop** | Stops on the **TEAM COMPLETE** screen, when **rerolls or refreshes run out**, or when you press **ESC**. ESC stops rolling only; the tool stays open. |
| **Input** | Legacy `mouse_event` (`SendInput` is silently blocked on some systems); glides the pointer onto the target first, then presses. |
| **Dismiss** | No separate dismiss point in game; dismissing = reroll/refresh. |

---

## Requirements

* Windows 10 / 11 (x64)
* [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) — only needed to
  *run from source*; the published exe is self-contained.
* Roblox installed.

---

## Building

### Run from source

```powershell
git clone https://github.com/Alex347XD/Build-a-Hockey-Team-Automatic-Team-Builder.git
cd Build-a-Hockey-Team-Automatic-Team-Builder
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
   * Pick the **Odds Region** over the rarity list in the **THIS TEAM** column.
   * Set **Cards in region = 4**, **Layout = 2 x 2 grid**.
   * **No reference pixel, no rarity colours, no reference images.**
4. **Tab 2 – Rarities** – choose **Min** and **Max** rarity (tiers are pre-configured), plus Min OVR / OVR-only / accept-any-by-OVR.
5. **Tab 3 – Run** – press **Start Rolling**.

Useful buttons: **Test Click**, **Test Detection**, **Save Debug Shot** (`debug_screen.png` + `debug_cards.png`),
**Save** config.

### Keyboard

* **ESC** – stop rolling (the tool stays open)

---

## Tier detection (OVR only)

Rarity comes **only** from the card's **OVR number** (read by OCR). No reference pixel, no colour
matching, no reference images, no frame colour, no `tiercolours.txt`. **Applied to base OVR; shiny is not part of any range.**

| Tier | OVR (base) |
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

* **Shiny adds +2 OVR.** `o = rawOvr − 2` when shiny; ranges apply to base. e.g. shiny Silver 82→84 → Silver; shiny Franchise 104→106 → Franchise; shiny Rivalry Captain → 116.
* **Shiny = rainbow border on a non-Prismatic card**, scanned across the whole frame (multiple offsets, multiple hue signals); **not** Prismatic. The shiny check runs regardless; the **Prismatic check only runs when in Prismatic range**.
* **Prismatic** = white inside + rainbow border + OVR 105-107, **never shiny**. **Prismatic-and-above can't be shiny.** (Pixels just inside + really close to the border, majority white; ribbon cards not excluded; offsets higher/lower to avoid a diagonal ribbon's white.)
* **Franchise** = dark-purple strip on black (not white inside).
* **Rivalry 112** needs a **vs symbol bottom right**; 112 without it is Generational. **Rivalry Captain = every 116.**
* **No card at OVR 100; 116 is the highest.**
* `blockedRibbon` is separate (not a rarity signal).

---

## Team roster sheet (still-claimable → reroll vs refresh)

Smart mode combines **odds + sheet + current claimed**:

* **Odds** – current team + eligible tiers in the **THIS TEAM / REFRESH** column.
* **Sheet** – per-team roster (name, OVR, position, year, rarity-column) + current team + year (`Team [Country] 2026`; `Team USA 2026` = United States; `Team [Country]` = sheet `2026 Olympics Team [Country]`).
* **Current claimed** – players already claimed this session (`_claimedPlayers`, normalised; claimed-card names read by OCR) + `blockedRibbon`.

**If `avail` has an eligible tier but every eligible-rarity player in the current team + year is already owned → REROLL instead of REFRESH.** Logs `eligible=N, already have=M` + per-rarity `claimable/eligible`.

**Data source.** `TeamRoster`. Reads the shared Google Sheet (`gid=0` + Olympic `gid=1630519299`), **and ships embedded:** `Resources/teams.csv` + `Resources/teams_olympic.csv`. Order: try refresh online → cache `%APPDATA%\HockeyRoller\teams.csv` → **embedded**. Offline works; nothing to find. **12 Olympic teams × 25.** No `References/`, no `tiercolours.txt`.

---

## Configuration

Settings are stored in `%APPDATA%\HockeyRoller\config.json`. The **Setup** tab's **Save** writes them.

| Setting | Meaning |
|---|---|
| Delay after roll (ms) | minimum wait after clicking before looking for cards |
| Wait for animation (max ms) | ceiling for the "wait until the screen stops changing" check |
| Refresh cap | refreshes without an eligible hit before forcing a reroll (0 = no cap) |
| Strategy | Smart (odds + roster) / always refresh / always reroll |
| Min / Max rarity | target range |
| Min OVR (any rarity) | rating floor (0 = off) |
| Also take anything at/above that OVR | `AcceptAnyTierByOvr` |
| OVR only - ignore rarity completely | `OvrOnly` (still uses rarity + odds for Refresh) |
| Blocked region / manual blocked | optional extra "already on team" check |
| Capture method | Auto / Screen grab / PrintWindow |
| Use mouse_event | input API |
| Stop on TEAM COMPLETE | stop when team is finished |

**Removed:** reference pixel + per-card picks, reference colours/images, `tiercolours.txt`, `TierColourFile`, `RefPixel`, `ReferenceLibrary`, `CardFinder`, `SparkleDetector`, `MatchText`, `MatchThreshold`, `ReferenceThreshold`, `AmbiguityMargin`, `RefMaxDistance`, `RefChannelTolerance`, `RefPixelRadius`, `RaritySample`, `ResetRarities`, **`DismissAsync` / `DismissX/Y` / `DismissWithEsc` / dismiss UI**.

---

## Troubleshooting

**Nothing happens when it clicks.** `mouse_event`; run as administrator if Roblox is elevated; not exclusive fullscreen.

**The preview only shows part of the game.** Use **Method = Auto**; *Screen grab* needs visible/unobstructed; *PrintWindow* can truncate DirectX.

**A card is not detected / wrong tier.** Check `cards_last.png` (`%APPDATA%\HockeyRoller\`) + log `ovr=` + `SHINY+2` + `vs` + `blockedRibbon`. OVR must read; `ovr=0` → no tier. Shiny/Prismatic share a rainbow; Prismatic needs white inside + OVR 105-107.

**Rivalry.** `ovr=112` + `vs`; else Generational. **Rivalry Captain = 116.**

**Odds not read.** Windows OCR, line by line, THIS TEAM / REFRESH column. See `odds_panel.png`. `OddsClose` point for the modal.

**No reroll-instead-of-refresh.** Needs current team match in the roster + no remaining claimable eligible. See `roster[...]`, `available per rarity`.

---

## Project layout

```
HockeyRoller/
├─ MainWindow.xaml(.cs)      UI, run loop, OVR + shiny/Prismatic detection, target selection
├─ Models/Config.cs          config + RarityCatalog (TierFromOvr ranges, TierOrder)
├─ Services/
│  ├─ Win32.cs               P/Invoke (windows, input, elevation)
│  ├─ WindowCapture.cs       screen / PrintWindow capture
│  ├─ InputSim.cs            mouse + keyboard injection
│  ├─ OcrService.cs          Windows.Media.Ocr wrapper
│  ├─ OddsReader.cs          line-by-line OCR of the odds panel
│  ├─ ReferenceProfile.cs    TierProfile for the shiny/rainbow check
│  ├─ ColorMatch.cs          colour maths (HSV, averaging)
│  ├─ DiagonalStripDetector.cs  POSITION FILLED / ALREADY ON TEAM ribbon
│  └─ TeamRoster.cs          roster sheet + claimable-per-rarity
├─ Resources/teams.csv       embedded NHL roster sheet
├─ Resources/teams_olympic.csv embedded 2026 Olympic roster sheet
├─ README.md
├─ LICENSE                  Non-commercial - no profit
├─ build.ps1
└─ .gitignore
```
