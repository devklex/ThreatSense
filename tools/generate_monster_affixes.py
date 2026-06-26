import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[4]
SOURCE = ROOT / "Data" / "repoe_data" / "mods.json"
OUTPUT = Path(__file__).resolve().parents[1] / "data" / "monster_affixes.json"

CURATED_DEFAULT_ENABLED_IDS = {
    # Current high-danger archnemesis-style hazards from the game data.
    "MonsterBurningGroundOnDeath1",
    "MonsterChilledGroundOnDeath1",
    "MonsterShockedGroundOnDeath1",
    "MonsterBurningGroundTrail1",
    "MonsterChilledGroundTrail1",
    "MonsterShockedGroundTrail1",
    "MonsterFlameBeacons1",
    "MonsterFrostBeacons1",
    "MonsterLightningBeacons1",
    "MonsterManaSiphonAura1",
    "MonsterManaSiphonAura2",
    "MonsterFlaskRemovalAura1",
    "MonsterLightningMirage1",
    "MonsterLightningMirage2",
    "MonsterMagmaBarrier1",
    "MonsterFlamewaller1",
    "MonsterLightningStorms1",
    "MonsterLivingCrystals1",
    "MonsterVolatilePlants1",
    "MonsterVolatilePlants2",
    "MonsterVolatileRocks1",
    "MonsterVolatileRocks2",
    "MonsterCorpseExploder1",
    "MonsterCausticCloudOnDeath",
    "MonsterGroundFireOnDeath1",
    "MonsterGroundIceOnDeath1",
    "MonsterGroundTarOnDeath1",
    "MonsterCastsGroundDesecrationText",
    "MonsterExplodesOnDeathFire1",
    "MonsterExplodesOnDeathLightning1",
    "MonsterExplodesOnDeathCold1",
    "MonsterExplodesOnDeathChaos1",
    "MonsterBloodlinesBeaconOnDeathFire",
    "MonsterBloodlinesBeaconOnDeathCold",
    "MonsterBloodlinesBeaconOnDeathLightning",
    "MonsterNemesisLightningStormDaemon",
    "MonsterUsesVaalDetonateDeadAtLowLifeDisplay_",
    "MonsterAfflictionVolatileOnDeath",
    "MonsterAfflictionFlameblastOnDeath",
    "MonsterAfflictionFirestormOnDeath",
    # Abyss variants of the same high-risk mechanics.
    "MonsterAbyssVolatileRocks1",
    "MonsterAbyssSiphonAura1",
    "MonsterAbyssApparitionMirage1",
    "MonsterAbyssApparitionBeamcaster",
    "MonsterAbyssFactionRunes",
    "MonsterAbyssPustuleGround1",
    "MonsterAbyssGeyserWalls1",
    "MonsterAbyssShadeWalker1",
    "MonsterAbyssSoulcano1",
    "MonsterAbyssLightlessFaction1",
    "PlayerMonsterAbyssLightlessFaction1",
}

DISPLAY_NAME_OVERRIDES = {
    "MonsterAbyssLightlessFaction1": "Amanamu's Void",
    "PlayerMonsterAbyssLightlessFaction1": "Amanamu's Void",
}

DEFAULT_ENABLED_FRAGMENTS = (
    "monstermodburninggroundondeath",
    "monstermodchilledgroundondeath",
    "monstermodshockedgroundondeath",
    "monstermodburninggroundtrail",
    "monstermodchilledgroundtrail",
    "monstermodshockedgroundtrail",
    "monstermodflamebeacon",
    "monstermodfrostbeacon",
    "monstermodlightningbeacon",
    "monstermodmanasiphonaura",
    "monstermodremoveflaskchargeaura",
    "monstermodlightningmirage",
    "monstermodmagmabarrier",
    "monstermodflamewaller",
    "monstermodstormherald",
    "monstermodexplodingcrystals",
    "monstermoddetonatecorpses",
    "monstermodshroudwalker",
    "monstervolatileplants",
    "monstervolatilerocks",
    "monsterexplodesondeath",
    "monstersummonsvolatilecoreondeath",
    "monsterbloodlinesbeaconondeath",
    "monsternemesislightningstormdaemon",
    "monsternemesissmokedaemon",
    "monstercausticcloudondeath",
    "monstergroundfireondeath",
    "monstergroundiceondeath",
    "monstergroundtarondeath",
    "monstercastsgrounddesecrationtext",
    "monsterusesvaaldetonatedeadatlowlifedisplay",
    "monsterafflictionvolatileondeath",
    "monsterafflictionflameblastondeath",
    "monsterafflictionfirestormondeath",
    "monsterafflictionhasdestroycorpse",
    "monsterabyssalcrystalminewall",
    "monsterabyssvolatilerocks",
    "monsterabysssiphonaura",
    "monsterabyssapparitionmirage",
    "monsterabyssapparitionbeamcaster",
    "monsterabyssfactionrunes",
    "monsterabysspustuleground",
    "monsterabyssgeyserwalls",
    "monsterabyssshadewalker",
    "monsterabysssoulcano",
    "monsterabysslightlessfaction",
)

DEFAULT_ENABLED_TEXT_FRAGMENTS = (
    "burns ground on death",
    "chills ground on death",
    "spreads caustic ground on death",
    "spreads tar on death",
    "shocked ground trails",
    "chance for a fire explosion on death",
    "chance for a cold explosion on death",
    "chance for a lightning explosion on death",
    "toxic volatile",
)


def spaced_identifier(value: str) -> str:
    value = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", value or "")
    value = re.sub(r"[_/\\-]+", " ", value)
    value = re.sub(r"\s+", " ", value)
    return value.strip()


def clean_game_text(value: str) -> str:
    value = value or ""
    value = re.sub(r"<<[^>]+>>", "", value)
    value = re.sub(r"<[^>]+>", "", value)
    value = value.replace("{", "").replace("}", "")
    value = re.sub(r"\[[^|\]]+\|([^\]]+)\]", r"\1", value)
    value = re.sub(r"\s+", " ", value)
    return value.strip()


def display_name(mod_id: str, record: dict) -> str:
    if mod_id in DISPLAY_NAME_OVERRIDES:
        return DISPLAY_NAME_OVERRIDES[mod_id]

    name = clean_game_text(record.get("name") or "")
    if name and name.upper() != "TBD":
        return name

    type_name = spaced_identifier(record.get("type") or "")
    if type_name:
        return type_name

    return spaced_identifier(mod_id)


def category_for(mod_id: str, record: dict) -> str:
    haystack = " ".join(
        [
            mod_id,
            record.get("type") or "",
            record.get("name") or "",
            record.get("text") or "",
        ]
    ).lower()

    if any(x in haystack for x in (
        "on death",
        "ondeath",
        "explode",
        "explosion",
        "explod",
        "volatile",
        "detonate",
        "corpse",
        "flameblood",
        "iceblood",
        "stormblood",
        "bearer",
    )):
        return "Death / explosion"
    if any(x in haystack for x in ("ground", "burning", "chilled", "shocked", "caustic", "desecrate", "desecrated", "tar")):
        return "Ground effect"
    if any(x in haystack for x in (
        "beacon",
        "crystal",
        "lightning storm",
        "lightningstorms",
        "storm herald",
        "stormherald",
        "stormcall",
        "mirage",
        "lightningmirage",
        "magma",
        "magmabarrier",
        "flamewall",
        "flamewaller",
        "shroud",
        "shroudwalker",
        "shade",
        "shadewalker",
        "siphon",
        "manasiphon",
    )):
        return "Area hazard"
    if any(x in haystack for x in ("lightning", "fire", "flame", "cold", "ice", "frost", "chaos", "poison", "bleed")):
        return "Element / ailment"
    if any(x in haystack for x in ("siphon", "curse", "mana")):
        return "Utility danger"
    if record.get("generation_type") in ("prefix", "suffix"):
        return "Rare affix"
    return "Monster mod"


def default_enabled(mod_id: str, record: dict) -> bool:
    if mod_id in CURATED_DEFAULT_ENABLED_IDS:
        return True

    haystack = " ".join(
        [
            mod_id,
            record.get("type") or "",
            record.get("name") or "",
            record.get("text") or "",
            category_for(mod_id, record),
        ]
    ).lower()
    compact_haystack = re.sub(r"[^a-z0-9]+", "", haystack)
    text = clean_game_text(record.get("text") or "").lower()
    return any(fragment in compact_haystack for fragment in DEFAULT_ENABLED_FRAGMENTS) or any(fragment in text for fragment in DEFAULT_ENABLED_TEXT_FRAGMENTS)


def main() -> None:
    with SOURCE.open("r", encoding="utf-8") as handle:
        mods = json.load(handle)

    entries = []
    for mod_id, record in mods.items():
        if record.get("domain") != "monster":
            continue

        entries.append(
            {
                "id": mod_id,
                "name": display_name(mod_id, record),
                "type": record.get("type") or "",
                "generationType": record.get("generation_type") or "",
                "category": category_for(mod_id, record),
                "text": clean_game_text(record.get("text") or ""),
                "defaultEnabled": default_enabled(mod_id, record),
            }
        )

    entries.sort(key=lambda x: (x["category"], x["name"].lower(), x["id"].lower()))
    output = {
        "source": str(SOURCE.relative_to(ROOT)).replace("\\", "/"),
        "count": len(entries),
        "entries": entries,
    }
    OUTPUT.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(entries)} monster affixes to {OUTPUT}")


if __name__ == "__main__":
    main()
