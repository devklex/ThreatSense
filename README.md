# ThreatSense

ThreatSense is an ExileCore2 plugin for Path of Exile 2 that highlights dangerous monster affixes, spawned ground/effect hazards, and Amanamu's Void encounters with configurable world, minimap, and radar-style alerts.

## What It Does

- Loads bundled monster affix data from `data/monster_affixes.json`.
- Lets each monster affix be enabled or disabled individually.
- Scans nearby hostile monsters for `ObjectMagicProperties.Mods`.
- Draws configurable world-space warning circles around monsters with enabled dangerous affixes.
- Draws configurable world-space warning circles around spawned ground/effect entities such as volatiles, drowning orbs, Amanamu's Void / Lightless Wells, beacons/bearers, mana siphon, lightning mirage, storm herald effects, flamewalls, magma barrier, corpse detonation, burning/chilled/shocked ground, caustic ground, desecrated ground, and tar ground.
- Adds a dedicated Amanamu's Void overlay for Abyss rares: red `IN VOID - PULL`, green `OUTSIDE - KILL`, or purple `AMANAMU VOID` when the cloud state cannot be confirmed. It has its own draw distance, can draw a line from the rare to the detected void cloud, marks the rare on the small minimap or large map overlay with a high-contrast yellow/state-color ring, and can draw guide lines toward the rare.
- Tracks Abyss Pit minimap-icon entities (`AbyssPitActive` / `AbyssPitInactive`) in the current area and overlays a configurable `closed/found` counter on screen, with terrain/path fallback kept as experimental troubleshooting options.
- Supports optional labels, filled circles, scan interval, draw distance, circle thickness, and label scale.
- Recommended high-danger monster affixes and ground/effect rules are enabled by default, and every rule can still be toggled individually.
- Ground-effect rules can require a real `GroundEffect` component so common ground warnings use the game's exposed effect radius instead of a broad visual-path guess.

## Data

The plugin ships with its own data files under `data/`.

Users do not need external game-data folders. When game data changes, update the bundled data files and release the plugin update.

For maintainers, `tools/generate_monster_affixes.py` regenerates `data/monster_affixes.json` from the local game-file database used during development.
