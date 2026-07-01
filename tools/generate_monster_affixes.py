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
    # High-impact rare and Abyss affixes curated for default ThreatSense warnings.
    "MonsterAbyssMeteor",
    "PlayerMonsterAbyssMeteor",
    "MonsterBombardier1",
    "PlayerMonsterBombardier1",
    "MonsterGlacialPrison1",
    "PlayerMonsterGlacialPrison1",
    "MonsterAbyssLastGasp1",
    "PlayerMonsterAbyssLastGasp1",
    "MonsterAbyssPitSplitting",
    "PlayerMonsterAbyssPitSplitting",
    "MonsterAbyssImmuneAura1",
    "PlayerMonsterAbyssImmuneAura1",
    "MonsterImmuneAura1",
    "MonsterImmuneAura2",
    "PlayerMonsterImmuneAura1",
    "PlayerMonsterImmuneAura2",
    "MonsterPreventRecoveryAura1",
    "MonsterTemporalAura1",
    "PlayerMonsterTemporalAura1",
    "PlayerMonsterTemporalAuraMinion1",
    "MonsterProximalTangibility1",
    "PlayerMonsterProximalTangibility1",
}

DISPLAY_NAME_OVERRIDES = {
    "MonsterAbyssLightlessFaction1": "Amanamu's Void",
    "PlayerMonsterAbyssLightlessFaction1": "Amanamu's Void",
    "MonsterAbyssMeteor": "Meteoric Demise",
    "PlayerMonsterAbyssMeteor": "Meteoric Demise",
    "MonsterBombardier1": "Bombardier",
    "PlayerMonsterBombardier1": "Bombardier",
    "MonsterGlacialPrison1": "Glacial Prison",
    "PlayerMonsterGlacialPrison1": "Glacial Prison",
    "MonsterAbyssLastGasp1": "Kurgal's Last Gasp",
    "PlayerMonsterAbyssLastGasp1": "Kurgal's Last Gasp",
    "MonsterAbyssPitSplitting": "Ulaman's Legion / Pit Splitting",
    "PlayerMonsterAbyssPitSplitting": "Ulaman's Legion / Pit Splitting",
    "MonsterAbyssImmuneAura1": "Invulnerability Aura",
    "PlayerMonsterAbyssImmuneAura1": "Invulnerability Aura",
    "MonsterImmuneAura1": "Invulnerability Aura",
    "MonsterImmuneAura2": "Invulnerability Aura",
    "PlayerMonsterImmuneAura1": "Invulnerability Aura",
    "PlayerMonsterImmuneAura2": "Invulnerability Aura",
    "MonsterPreventRecoveryAura1": "Prevent Recovery Aura",
    "MonsterTemporalAura1": "Temporal Aura",
    "PlayerMonsterTemporalAura1": "Temporal Aura",
    "PlayerMonsterTemporalAuraMinion1": "Temporal Aura",
    "MonsterProximalTangibility1": "Proximal Tangibility",
    "PlayerMonsterProximalTangibility1": "Proximal Tangibility",
}

CATEGORY_OVERRIDES = {
    "MonsterAbyssMeteor": "Death / explosion",
    "PlayerMonsterAbyssMeteor": "Death / explosion",
    "MonsterBombardier1": "Death / explosion",
    "PlayerMonsterBombardier1": "Death / explosion",
    "MonsterGlacialPrison1": "Utility danger",
    "PlayerMonsterGlacialPrison1": "Utility danger",
    "MonsterAbyssLastGasp1": "Death / explosion",
    "PlayerMonsterAbyssLastGasp1": "Death / explosion",
    "MonsterAbyssPitSplitting": "Utility danger",
    "PlayerMonsterAbyssPitSplitting": "Utility danger",
    "MonsterAbyssImmuneAura1": "Utility danger",
    "PlayerMonsterAbyssImmuneAura1": "Utility danger",
    "MonsterImmuneAura1": "Utility danger",
    "MonsterImmuneAura2": "Utility danger",
    "PlayerMonsterImmuneAura1": "Utility danger",
    "PlayerMonsterImmuneAura2": "Utility danger",
    "MonsterPreventRecoveryAura1": "Utility danger",
    "MonsterTemporalAura1": "Utility danger",
    "PlayerMonsterTemporalAura1": "Utility danger",
    "PlayerMonsterTemporalAuraMinion1": "Utility danger",
    "MonsterProximalTangibility1": "Utility danger",
    "PlayerMonsterProximalTangibility1": "Utility danger",
}

LABEL_OVERRIDES = {
    "MonsterAbyssMeteor": "METEOR",
    "PlayerMonsterAbyssMeteor": "METEOR",
    "MonsterBombardier1": "METEOR",
    "PlayerMonsterBombardier1": "METEOR",
    "MonsterGlacialPrison1": "PRISON",
    "PlayerMonsterGlacialPrison1": "PRISON",
    "MonsterAbyssLastGasp1": "LAST GASP",
    "PlayerMonsterAbyssLastGasp1": "LAST GASP",
    "MonsterAbyssPitSplitting": "SPLIT",
    "PlayerMonsterAbyssPitSplitting": "SPLIT",
    "MonsterAbyssImmuneAura1": "IMMUNE",
    "PlayerMonsterAbyssImmuneAura1": "IMMUNE",
    "MonsterImmuneAura1": "IMMUNE",
    "MonsterImmuneAura2": "IMMUNE",
    "PlayerMonsterImmuneAura1": "IMMUNE",
    "PlayerMonsterImmuneAura2": "IMMUNE",
    "MonsterPreventRecoveryAura1": "NO RECOVER",
    "MonsterTemporalAura1": "TEMPORAL",
    "PlayerMonsterTemporalAura1": "TEMPORAL",
    "PlayerMonsterTemporalAuraMinion1": "TEMPORAL",
    "MonsterProximalTangibility1": "PROXIMAL",
    "PlayerMonsterProximalTangibility1": "PROXIMAL",
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
    if mod_id in CATEGORY_OVERRIDES:
        return CATEGORY_OVERRIDES[mod_id]

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

        entry = {
            "id": mod_id,
            "name": display_name(mod_id, record),
            "type": record.get("type") or "",
            "generationType": record.get("generation_type") or "",
            "category": category_for(mod_id, record),
            "text": clean_game_text(record.get("text") or ""),
            "defaultEnabled": default_enabled(mod_id, record),
        }
        label = LABEL_OVERRIDES.get(mod_id)
        if label:
            entry["label"] = label
        entries.append(entry)

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
