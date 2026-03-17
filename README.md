# HeadStart — ECO Server Mod `v1.1.0`

A server-side mod for [ECO](https://play.eco) that helps new and returning players catch up to an active server. It grants **welcome stars** on first join and **catch-up XP** when abandoned players return, bringing them up to the server's active-player average.

---

## Features

### 1. Welcome Stars
When a player joins the server for the **very first time**, they automatically receive a configurable number of usable stars. This lets newcomers immediately start specializing without waiting for their first XP thresholds.

### 2. Catch-Up XP
When a player who was marked as **abandoned** logs back in, the mod compares their total lifetime XP against the average of all currently active (non-abandoned) players. If they're sufficiently behind, they receive exactly enough XP to match the average — including any stars that XP naturally unlocks.

- Catch-up only fires if the player's XP gap is at least **30%** of the server average (configurable). Players who are close to the average are not boosted.
- The number of times a player can receive automatic catch-up is configurable (default: 1, or unlimited).
- New players on a server with existing active players also receive catch-up XP alongside their welcome stars.

### 3. Admin Chat Commands

| Command | Description |
|---|---|
| `/givestar <player> [count]` | Grant usable stars to any player (default: 1). |
| `/givexp <player> <amount>` | Grant raw XP to any player. |
| `/catchup <player>` | Manually run catch-up for any player, regardless of abandoned status. |
| `/resetwelcome <player>` | Reset a player's welcome-star grant so it fires again on next join. |
| `/resetcatchup <player>` | Reset a player's catch-up count so automatic catch-up can fire again. |

All commands require **Admin** authorization.

---

## Installation

1. Copy `HeadStartPlugin.cs` into your server's `Mods/UserCode/` folder.
2. Restart the server. The mod compiles automatically via ECO's ModKit.
3. On first run, a default config file is created at `Storage/Mods/HeadStart/config.txt`.

No additional dependencies or DLLs are required.

---

## Configuration

The config file is located at:

```
Storage/Mods/HeadStart/config.txt
```

It uses a simple `Key=Value` format with `#` comments:

```ini
# HeadStart Mod Configuration
# Lines starting with # are comments.

# Number of stars granted to first-time joiners (default: 1)
WelcomeStarCount=1

# Max automatic head-start catch-ups per player (default: 1, 0 = unlimited)
MaxHeadStartGrants=1

# Minimum XP gap as a % of the server average before catch-up fires (default: 30, range: 0-100)
MinCatchUpGapPercent=30
```

| Setting | Default | Description |
|---|---|---|
| `WelcomeStarCount` | `1` | Number of stars granted to a player on their first-ever join. Set to `0` to disable welcome stars. |
| `MaxHeadStartGrants` | `1` | Maximum number of times a player can receive automatic catch-up XP. Each return from abandoned status uses one grant. Set to `0` for unlimited. |
| `MinCatchUpGapPercent` | `30` | Minimum XP gap (as a % of the server average) required before catch-up is granted. A player must be at least this percentage below the average to qualify. Set to `0` to always boost anyone behind the average. Clamped to 0–100. |

Changes to the config file require a server restart to take effect.

---

## How It Works

### XP Calculation

ECO's `UserXP.XP` property only returns XP earned toward the *current* star level, not lifetime cumulative XP. The mod calculates **total lifetime XP** as:

```
TotalXP = CumulativeXPForStar(TotalStarsEarned) + CurrentLevelXP
```

using ECO's star XP thresholds (25, 100, 250, 500, 1000, 2000, 4000, then +2000 per star after star 8).

### Catch-Up Flow

1. Player logs in.
2. Mod checks if the player was **abandoned** at the moment of login (the flag is captured before ECO clears it).
3. Computes the **average total XP** of all active, non-abandoned players.
4. Checks whether the player's gap vs the average meets the minimum threshold: `(average - playerXP) / average ≥ MinCatchUpGapPercent%`. If the gap is too small, the check is skipped and no grant use is consumed.
5. If the returning player qualifies and has remaining grant uses, they receive `averageXP - theirXP` as experience.
6. `AddExperience()` automatically unlocks any star thresholds crossed — no manual star grants needed.

### Race Condition Handling

Both `NewUserJoinedEvent` and `OnUserLoggedIn` fire for new players. The mod uses atomic check-and-reserve with locking to prevent double welcome-star grants.

---

## Persistence

Player data is stored in per-player folders:

```
Storage/Mods/HeadStart/
├── config.txt
├── headstart.log
└── Players/
    ├── PlayerOne/
    │   ├── welcome.granted
    │   └── headstart.count
    └── PlayerTwo/
        ├── welcome.granted
        └── headstart.count
```

- **`welcome.granted`** — Marker file recording that the welcome star was given.
- **`headstart.count`** — Records how many times catch-up XP has been granted.

Both files contain the player's `StrangeId` (unique identifier), display name, timestamp, and (for `headstart.count`) the grant count. Data survives server restarts.

### Migration

If upgrading from an older version that used `catchup.granted` or `catchup.count` file names, the mod automatically migrates them to the new `headstart.count` format on startup.

---

## Logging

All mod activity is logged to:

```
Storage/Mods/HeadStart/headstart.log
```

Log entries include timestamps, star grants, XP grants, config load status, migration events, warnings, and errors.

---

## Compatibility

- **ECO Version:** Tested on v0.12.x (ModKit / Roslyn compilation)
- **Dependencies:** None — uses only assemblies included in ECO's ModKit
- **Platform:** Works on both Windows and Linux servers

---

## License

MIT License. See [LICENSE](LICENSE) for details.
