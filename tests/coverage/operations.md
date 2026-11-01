# Normal-player operation inventory

Phase-1 starting inventory for Linux / RimWorld 1.6, base game and **all five DLCs**. Operation families below span later milestones, not M1 actions or a full-game support claim. Expand families into scenario-specific cases as implementation exposes gaps; this is not the final exhaustive M4 inventory.

## Status and evidence rules

- **Implemented** means a working product observation route with linked code, tests and actual isolated game evidence for that row's stated observation. Only the rows marked implemented below have it, each for a narrow stated observation on one copied fixture and the final package; every other row is still unverified. This never certifies execution of the player operation. Compiled contracts are not routes.
- **Screenshot-only** means the intended M1 observation of that specialist surface has no structured schema: only pixels already rendered by the game. Every screenshot-only row is currently **unverified** in the game, despite the source implementation of the screenshot producer. No automatic tab opening, hovering, scrolling, camera movement or setup is authorized.
- **Unverified** means the row's stated observation was not fully shown in the game. Its evidence column is `pending`, or links a public-safe note on what was only partly shown or refused. Partial evidence never counts as implemented. A proposed baseline schema, an installed DLC, source inspection, synthetic fixtures and historical recipe/probe captures cannot promote it.
- `baseline: X` in the observation column names a candidate [wire v1](../../protocol/wire-v1.md) method, not a runtime capability. Only its audited fields qualify; the rest of that operation's screen remains screenshot-only. All rows also require actual-game proof of the implemented `screenshot` route. Unknown controls and specialist structured fields stay absent, never empty or inferred.
- Every **player operation** is something a normal player can do, not something M1 can execute. M1 has **no control route**, including save/load, pause, developer operations or raw input. Later M2 typed or screenshot-backed UI execution must be designed and separately proved; M3 needs independent-window co-play evidence and resulting-state comparison. M4 cannot claim full-game support while any required route remains unverified.
- All baseline candidates obey [visibility](../../protocol/visibility.md) and [publication/developer/completed-frame safety](../../protocol/observation-safety.md). Developer surfaces are refused, not screenshot-only exceptions. Hidden, undiscovered, unrendered or unaudited data are never fallback inputs.

**Current evidence:** [mod bootstrap](../../mod/RimPilotMod.cs) wires private transport/lifecycle/dispatch and source-audited summary, pawn, things/resources, selection/windows, public events and native screenshot producers. [Typed CLI commands](../../docs/observation-commands.md) and [host/source regression matrix](observation-adversarial.md) cover their stated implementation boundaries, not actual-game observation acceptance. [Build/package/install checks](../../docs/build.md), [wire fixtures](../../protocol/fixtures/responses.json), [visibility audit](../../docs/visibility-audit.md) and [safety tests](../JsonIngress/ObservationSafetyTests.cs) prove only their stated host/compilation/contract properties, not Unity Mono/game behavior. Separate [isolated Mono menu evidence](session-adversarial.md) proves authenticated bootstrap commands only and supports no operation row by itself. **Five game-proven operation rows (MENU-01, MENU-13, PAWN-15, PAWN-16, UI-13), each narrow, from one copied fixture and the final package; all other rows are unverified, and ten of those carry partial or refusal notes.** Limited phase-4 exact-release Mono evidence on one copied fixture exists and is summarized, with every unmet item, in the [phase-4 real-game evidence summary section of the host/source matrix](observation-adversarial.md); only the rows above were promoted from the final-package re-run, never from the earlier builds alone. Route registration (`available` capability) is distinct from this inventory's game-evidence status. All five DLC scenario families below remain unverified with the full solo profile. A loaded DLC manifest alone proves none of its mechanics.

Each row has stable ID, player operation, M1 observation plan, route state, evidence and required future proof. `pending` means no operation-specific product evidence. Proof describes ordinary disposable-fixture setup by the test harness/human, never permission for coach control. To promote a row, replace `pending` with public-safe relative evidence links that identify built artifact, scenario, checks and result; retain proprietary captures/saves/paths privately. Split rows if only part is proved. Record solo/co-play separately and never claim offscreen fields from pixels.

## Menus and setup

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| MENU-01 | Main menu, credits and expansions overview | baseline: summary | implemented | Final package (mod DLL 2a98517a, CLI 8120201c), one copied fixture, main menu: summary reported scene, build, the five DLCs and the mod list with no game or map; a complete decoded 1280x720 PNG equalled the normal menu except the pointer. [phase-4 and final-package evidence summary](observation-adversarial.md), [route code](../../mod/RimPilotMod.cs), [client tests](../JsonIngress/ObservationClientTests.cs). | Menu scene/build/DLC manifest and usable complete frame, without Game/Map/TickManager. Remaining: the credits page itself was not opened; other profiles and builds unproved. |
| MENU-02 | New colony, scenario selection and scenario editor | screenshot-only | unverified | pending | Already-open scenario pages, nested editors and modded entries render without hidden-field scraping. |
| MENU-03 | Storyteller and difficulty, custom difficulty | screenshot-only | unverified | pending | Existing selections and custom settings legible; observation does not change them. |
| MENU-04 | World generation seed, coverage and generation settings | screenshot-only | unverified | pending | Setup screen versus generation loading distinguished; no generation triggered by query. |
| MENU-05 | Landing tile, biome, terrain and site inspection | screenshot-only | unverified | pending | Normal visible tile info only; no undiscovered contents or world generation internals. |
| MENU-06 | Colonist selection, randomization and customization | screenshot-only | unverified | pending | Existing visible pawn/setup panels captured; no reroll, hidden traits or future-map lookup. |
| MENU-07 | Start colony and loading/initialization transitions | baseline: health | unverified | pending | Loading reports not-ready and old setup/root scope cannot resolve into loaded game. |
| MENU-08 | Load game, save game, rename, overwrite and delete dialogs | screenshot-only | unverified | pending | Already-open dialogs captured; product never reads save files or submits file operations. |
| MENU-09 | Options, audio/video/UI scale, key bindings and language | screenshot-only | unverified | pending | Scaled/localized screens captured without editing preferences or exposing filesystem paths in metadata. |
| MENU-10 | Mods list, dependencies, ordering and restart prompts | screenshot-only | unverified | pending | Rendered list only, no profile rewriting or reload; summary manifest is not list-control coverage. |
| MENU-11 | Return to menu, quit and confirmation dialogs | baseline: summary | unverified | Partial, see MENU-13. Confirmation dialogs and quit from a loaded colony were not shown. [phase-4 and final-package evidence summary](observation-adversarial.md). | Scene/root/game invalidation across ordinary transitions; no quit/menu action endpoint. |
| MENU-12 | Tutorials, learning helper and normal informational dialogs | screenshot-only | unverified | pending | Existing visible pages and help content captured without advancing steps or opening links. |
| MENU-13 | Return to menu, reload a colony and quit from the main menu, as observed through identities | baseline: summary | implemented | Final package (mod DLL 2a98517a, CLI 8120201c), one copied fixture: after return to menu and after reload every old root, game and pawn identity was rejected and never accepted again, a fresh scope worked, and a graceful quit from the main menu removed the session descriptor with a new session after restart. One reload-window read returned no reply (known intermittent). [phase-4 and final-package evidence summary](observation-adversarial.md), [client tests](../JsonIngress/ObservationClientTests.cs). | Quit from a loaded colony, confirmation dialogs and abrupt-death leftovers unproved. |

## Colony management

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| COLONY-01 | Map/colony overview, map chooser and colony statistics | baseline: summary | unverified | pending | Eligible loaded map summaries versus unsupported statistics; no raw wealth/hidden counts. |
| COLONY-02 | Inspect/select buildings and loose things | baseline: things.list | unverified | Not shown. things.list refused `unavailable` on every loaded map tried, the final package included (raw spawned-thing source over its cap); capabilities still reports it available. [phase-4 and final-package evidence summary](observation-adversarial.md). | Off-camera eligible and multi-cell things admitted; fogged/concealed/unknown types absent in every channel. |
| COLONY-03 | Inspect resource stocks and resource readout | baseline: resources | unverified | Not shown. resources refused `unavailable` on every loaded map tried, the final package included; capabilities still reports it available. [phase-4 and final-package evidence summary](observation-adversarial.md). | Explicit eligible-observed-stack-units basis; not resource-bar totals or contained/minified contents. |
| COLONY-04 | Architect categories, build placement, rotation and material choice | screenshot-only | unverified | pending | Existing ghost/placement/material screens shown; no placement, rotation or resource inference from hidden cells. |
| COLONY-05 | Blueprint/frame inspection, cancel, deconstruct and uninstall/reinstall | screenshot-only | unverified | pending | Current rendered designations/menus, not completion or execution proof. |
| COLONY-06 | Mining, hauling, chopping, harvesting, forbidding and priority orders | screenshot-only | unverified | pending | Visible designations/context menus only; no orders submitted or hidden-target labels. |
| COLONY-07 | Zones, stockpiles, dumping, shelves and storage filters/priorities | screenshot-only | unverified | pending | Existing filter tree, search and priority panels; no implicit inventory/holder enumeration. |
| COLONY-08 | Growing zones, plant selection, sow/cut controls and fertility overlays | screenshot-only | unverified | pending | Rendered normal overlays and custom plant browser; no unopened controls/tooltips. |
| COLONY-09 | Allowed/home/roof/snow areas and area painting/inversion | screenshot-only | unverified | pending | Existing visible area/roof overlay, not an unaudited map-cell dump. |
| COLONY-10 | Work priorities, firefighting and manual priorities | screenshot-only | unverified | pending | Existing work table and scrolling/column visibility; no new structured work-policy schema. |
| COLONY-11 | Schedule, timetable, restrictions and assignment tables | screenshot-only | unverified | pending | Existing rows/cells only; no schedule edits or offscreen row metadata. |
| COLONY-12 | Food, drug, apparel and reading policies, outfits and filters | screenshot-only | unverified | pending | Open policy editors and nested filters; no comprehensive structured policy route. |
| COLONY-13 | Research tree, project selection and progress | screenshot-only | unverified | pending | Current public research screen; no hidden prerequisites, future unlocks or project changes. |
| COLONY-14 | Production bills, recipes, ingredients, counts and worker limits | screenshot-only | unverified | pending | Open bill editor, reorder controls and visible recipe details; no structured bill/ingredient search. |
| COLONY-15 | Power, fuel, temperature, vents, switches and network overlays | screenshot-only | unverified | pending | Visible building/overlay state only, no private network traversal or toggle. |
| COLONY-16 | Food preparation, spoilage, nutrient systems and meals | screenshot-only | unverified | pending | Visible normal inspection and settings; resource count basis does not imply nutrition or hidden stock totals. |
| COLONY-17 | Animals, taming/training, handling, pen/zone assignment and slaughter | screenshot-only | unverified | pending | Existing animals/pen controls; baseline own-pawn fields do not expose wild/prisoner detail or animal-management schema. |
| COLONY-18 | Visitors, prisoners, slaves, recruitment, conversion and interaction policies | screenshot-only | unverified | pending | Existing public interaction panels only; hosted/selectable pawn is not automatically own-pawn DTO eligible. |
| COLONY-19 | Rooms, beauty, comfort, wealth and colony history graphs | screenshot-only | unverified | pending | Existing normal readouts only; no unaudited lazy aggregate getters or hidden-condition deductions. |
| COLONY-20 | Alerts, inspect tabs and normal overlays | baseline: ui | unverified | pending | Only audited selection/window summaries; alerts, custom tabs and overlay internals remain pixels. |
| COLONY-21 | Copy/paste settings, links, forbidding and building toggles | screenshot-only | unverified | pending | Current visible state/context choices only; read never copies, pastes or toggles. |

## Pawns and combat

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| PAWN-01 | Own colony pawn roster, name, location and visible status | baseline: pawns.list | unverified | Partial, see PAWN-15. The roster carries no location field, and positive status flags, mechs, mutants and slaves were not seen. [phase-4 and final-package evidence summary](observation-adversarial.md). | Eligible loaded-map own pawns, including supported animals/mechs/mutants; no world/holder search. |
| PAWN-02 | Individual own-pawn inspection and public current job | baseline: pawn.get | unverified | Partial. Pawn detail was read for all four fixture pawns on the final package with the job family present, but its status and job comparison with normal inspection was not completed on the final package (the formal gear and job comparison tripped its own pointer check and was not rerun); only an earlier build matched. [phase-4 and final-package evidence summary](observation-adversarial.md). | UI-matching status and independently gated job/lord reports; no queue, AI intent or internal targets. |
| PAWN-03 | Needs and visible health/body-part rows | baseline: pawn.get | unverified | Partial, see PAWN-16 for the health rows. Needs were present on every detail read but no level-by-level comparison was made; injuries, missing parts, blood loss and any hidden-condition case were not shown. [phase-4 and final-package evidence summary](observation-adversarial.md). | Shown-needs caller and visible/missing-ancestor health rules match UI; hidden conditions and aggregate health absent. |
| PAWN-04 | Worn apparel/equipped gear and inventory | baseline: pawn.get | unverified | Partial. Gear labels were present for the three colonists and the animal's gear family was omitted on the final package, but the formal gear comparison was not completed there (earlier build only); inventory is not covered. [phase-4 and final-package evidence summary](observation-adversarial.md). | Audited worn/equipped labels only; inventory/holder contents remain screenshot-only without separate audit. |
| PAWN-05 | Bio, skills, traits, relationships, mood thoughts and records | screenshot-only | unverified | pending | Existing normal tabs only; no specialist schema or hidden identity/thought traversal. |
| PAWN-06 | Medical care, tending, bed assignment, operations and surgery bills | screenshot-only | unverified | pending | Open health/medical controls; visible health rows alone do not prove medical action coverage. |
| PAWN-07 | Equip/drop/force wear, ingest/use items and consume drugs | screenshot-only | unverified | pending | Existing normal gear/context menus, no side effects from observation. |
| PAWN-08 | Draft/undraft, multi-select and movement/formation orders | screenshot-only | unverified | pending | Existing selection/gizmos/paths as pixels; no draft, move, selection or path request. |
| PAWN-09 | Ranged/melee attack, hold fire, forced target and friendly fire choices | screenshot-only | unverified | pending | Existing target mode and visible aim/range UI; no hidden target lookup or combat prediction. |
| PAWN-10 | Abilities, cooldowns, area targeting and cancellation | screenshot-only | unverified | pending | Visible normal ability/target mode; no unaudited ability getter or action endpoint. |
| PAWN-11 | Rescue, capture, carry, arrest, finish off and contextual jobs | screenshot-only | unverified | pending | Current float-menu choices only; no generic GetInspectString or target/object enumeration. |
| PAWN-12 | Downed/dead pawns, corpses, burial, hauling and gear stripping | screenshot-only | unverified | pending | Public corpse/health context only; ownership and inspectability still independently gated. |
| PAWN-13 | Hostiles, wild animals, visitors and neutral pawn inspection | screenshot-only | unverified | pending | Normal visible screen only; non-own structured details require separate audit, invisibility is not fog alone. |
| PAWN-14 | Combat log, damage details, battle overlays and threat alerts | screenshot-only | unverified | pending | Only already-rendered public combat information; no private AI plans, future raids or internal event feed. |
| PAWN-15 | Own colony pawn roster page: names and paging | baseline: pawns.list | implemented | Final package (mod DLL 2a98517a, CLI 8120201c), one copied fixture: two limit-2 pages listed the three colonists and the animal, the next cursor was reusable on the same snapshot, a wrong limit or method and a 5-second expiry gave the same expiry error. [phase-4 and final-package evidence summary](observation-adversarial.md), [route code](../../mod/Observation/GameOwnPawns.cs), [pawn tests](../JsonIngress/PawnProjectionTests.cs). | Location, positive status flags, mechs, mutants, slaves, secondary maps and DLC pawns unproved (PAWN-01). |
| PAWN-16 | Own-pawn visible health rows | baseline: pawn.get | implemented | Final package (mod DLL 2a98517a, CLI 8120201c), one copied fixture: four pawns, one timed condition row and three with no conditions, compared with the normal Health tab. [phase-4 and final-package evidence summary](observation-adversarial.md), [route code](../../mod/Observation/GameHealth.cs), [pawn tests](../JsonIngress/PawnProjectionTests.cs). | Injuries, missing parts, blood loss, non-English text and hidden-condition cases unproved (PAWN-03). |

## World and progression

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| WORLD-01 | Switch among loaded colony/encounter maps and world view | baseline: summary | unverified | pending | Query eligible loaded map without switching human view; removed/re-added maps invalidate references. |
| WORLD-02 | World tile, terrain, roads, landmarks and settlement inspection | screenshot-only | unverified | pending | Public rendered world information only; no hidden site contents or world-cell schema. |
| WORLD-03 | Form/reform caravans, choose pawns/items and split/merge | screenshot-only | unverified | pending | Existing caravan setup/loadout screens; map pawn DTOs do not imply world/contained pawn access. |
| WORLD-04 | Caravan routing, movement, rest, forage, ambush and encounter choices | screenshot-only | unverified | pending | Public path/encounter UI, no private path plans or map generation by reading. |
| WORLD-05 | Transport pods, launch destinations, cargo and arrival choices | screenshot-only | unverified | pending | Rendered transport setup/target mode only; no holder/cargo traversal. |
| WORLD-06 | Traders, settlements, orbital trade, gifts and negotiation | screenshot-only | unverified | pending | Existing trade dialogs and visible price/item rows only; no trade or private stock lookup. |
| WORLD-07 | Factions, relations, comms, goodwill and diplomacy | screenshot-only | unverified | pending | Already-open public panels; no hidden faction intent or future incidents. |
| WORLD-08 | Quests, accepted offers, rewards, expiry and site choices | screenshot-only | unverified | pending | Only public rendered quest data; no undiscovered site generation or future reward internals. |
| WORLD-09 | Read letters, public messages and notification history | baseline: events.read | unverified | Partial. Only the fixed lifecycle items, reusable event cursors and cursor expiry were shown; no natural message or letter ever appeared, so nothing was compared with normal history. [phase-4 and final-package evidence summary](observation-adversarial.md). | Accepted public deliveries, retained availability and gaps; no dismissal, raw look-targets or future letters. |
| WORLD-10 | Multiple colonies, new settlement, abandon and map removal | screenshot-only | unverified | pending | Existing normal dialogs; scope invalidation proven separately, no settle/abandon command. |
| WORLD-11 | Base-game ship construction, launch and endgame choices | screenshot-only | unverified | pending | Existing public endgame screens, not an inferred outcome or completion claim. |
| WORLD-12 | Time controls, pause, speed, autosave and ongoing progression | baseline: summary | unverified | pending | Honest tick/frame sampling at menu/pause/live; paused reads leave tick/UI unchanged, live simulation may advance. |

## Generic UI

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| UI-01 | Tabs, inspect pane, selection summaries and window stack | baseline: ui | unverified | Partial. On the final package ui reported no windows at the menu and the inspect window and selection that the harness had set up on the paused fixture. Options, Research and mod-settings windows were seen only on earlier builds; tabs, scaled, partly offscreen and custom windows were not shown. [phase-4 and final-package evidence summary](observation-adversarial.md). | Only audited summaries; no arbitrary window internals, unopened tabs or hidden labels. |
| UI-02 | Modal dialogs, popups, float menus, confirmation and cancel | screenshot-only | unverified | pending | Existing visible/occluding windows; no close/accept or metadata from covered controls. |
| UI-03 | Tooltips, hover information and delayed hover cards | screenshot-only | unverified | pending | Only tooltip already rendered by ordinary player; bridge never moves pointer or evaluates unopened tooltip. |
| UI-04 | Scrollbars, scroll wheels, virtualized tables and nested lists | screenshot-only | unverified | pending | Only clipped visible rows; offscreen/virtualized entries not scraped or scrolled into view. |
| UI-05 | Unicode text entry, search, rename, selection and clipboard | screenshot-only | unverified | pending | Existing Unicode rendered text preserved; no typing, focus, clipboard read/write or server filesystem input. |
| UI-06 | Drag/drop, sliders, reorder, resize and multi-cell selection | screenshot-only | unverified | pending | Existing rendered transient state only; bridge cannot begin/continue/finish gesture. |
| UI-07 | Camera pan, zoom, rotation where supported and map overlays | screenshot-only | unverified | pending | Current rendered viewport only; off-camera structured facts do not authorize camera control. |
| UI-08 | Hotkeys, keyboard navigation, focus and input rebinding | screenshot-only | unverified | pending | Existing visible focus/bindings only; no key dispatch or raw-input fallback. |
| UI-09 | UI scale, resolution, clipping, overlap and localization | screenshot-only | unverified | pending | Complete nonblack bounded PNG with dimensions/scale, correct session and safe render identity. |
| UI-10 | Custom/mod controls without shared widget metadata | screenshot-only | unverified | pending | Actual full-profile custom screens usable in pixels; missing metadata explicitly unsupported. |
| UI-11 | Screenshot of existing menu, paused or live screen | screenshot-only | unverified | Partial, see UI-13. Decoded PNGs were shown for the menu, a paused map and a live map, but capture time, first-use cost and memory budgets are unmet and 1920x1080 refuses by design. [phase-4 and final-package evidence summary](observation-adversarial.md). | Newer completed-safe frame plus release recheck, decoded PNG and cleanup/budgets; never desktop capture. |
| UI-12 | Rendered controls/accessibility-style metadata | screenshot-only | unverified | pending | Metadata stays absent until exact allowlist, clipping and occlusion proof; screenshot is not accessibility coverage. |
| UI-13 | Product PNG of an existing menu, paused map and live map screen | screenshot-only | implemented | Final package (mod DLL 2a98517a, CLI 8120201c), one copied fixture: complete decoded 1280x720 PNGs, equal to the normal frame except the pointer for the menu and paused map, and with a visibly advanced clock for the live map. [phase-4 and final-package evidence summary](observation-adversarial.md), [capture code](../../mod/Observation/GameScreenshot.cs), [capture tests](../JsonIngress/ScreenshotCaptureTests.cs). | Time and memory budgets, scaled UI, 1920x1080, world and specialist screens unproved (UI-11). |

## Royalty

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| ROYALTY-01 | Title/honor overview, royal favor, permits and title choices | screenshot-only | unverified | pending | Already-open title/permit screens and public requirements, no structured royalty schema. |
| ROYALTY-02 | Bestowing ceremony setup, acceptance and throne/bedroom requirements | screenshot-only | unverified | pending | Existing ceremony/setup/dialog screens; no hidden quest state or ceremony action. |
| ROYALTY-03 | Psycast learning, psylink, psyfocus, entropy and meditation | screenshot-only | unverified | pending | Public rendered psychic/meditation panels only; pawn needs contract does not authorize every psychic getter. |
| ROYALTY-04 | Psycast ability selection, target modes and area effects | screenshot-only | unverified | pending | Visible targeting/range/cancel state without hidden target data or casting. |
| ROYALTY-05 | Permit requests, royal trade, imperial quests and shuttle boarding | screenshot-only | unverified | pending | Open request/quest/boarding dialogs only; no container enumeration or permit call. |
| ROYALTY-06 | Royal progression and imperial endgame choices | screenshot-only | unverified | pending | Existing public ending/quest choices, not proof from DLC activation alone. |

## Ideology

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| IDEOLOGY-01 | Ideoligion setup, memes, precepts, styles and fluid reform | screenshot-only | unverified | pending | Existing creation/reform editors and nested choices; no setup/reform API. |
| IDEOLOGY-02 | Roles, leader/moral guide/specialist assignment and role abilities | screenshot-only | unverified | pending | Public role screens and target modes, no role assignment or ability execution. |
| IDEOLOGY-03 | Ritual setup, participants, targets, quality and outcomes | screenshot-only | unverified | pending | Existing ritual planning/result UI, no future outcome or private quality computation. |
| IDEOLOGY-04 | Conversion, certainty, ideology needs and social requirements | screenshot-only | unverified | pending | Public rendered inspection only; hidden belief/thought/state getters remain unaudited. |
| IDEOLOGY-05 | Relics, sacred sites, pilgrimages and ideology quests | screenshot-only | unverified | pending | Normal public quest/site screens, no undiscovered relic/site data. |
| IDEOLOGY-06 | Ideology buildings, apparel, food, slavery and tree-linked choices | screenshot-only | unverified | pending | Existing normal designations/policy/link dialogs; no assumed coverage from base colony screens. |
| IDEOLOGY-07 | Archonexus progression, sale/reset and ending choices | screenshot-only | unverified | pending | Existing public progression choices; game/map reset invalidates all old references. |

## Biotech

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| BIOTECH-01 | Xenotype/setup editor, gene inspection and cosmetic choices | screenshot-only | unverified | pending | Existing normal gene/setup screens, no hidden genes or generic gene getter dump. |
| BIOTECH-02 | Gene extraction, banks, assemblers, xenogerms and implantation | screenshot-only | unverified | pending | Existing recipe/editor/storage panels; no contained-gene enumeration or operation execution. |
| BIOTECH-03 | Pregnancy, birth, babies, childcare and feeding policies | screenshot-only | unverified | pending | Normal visible family/medical UI; no hidden pregnancy/health condition inference. |
| BIOTECH-04 | Children, learning, growth moments, age and education choices | screenshot-only | unverified | pending | Existing public growth/childcare dialogs, no future traits or hidden growth rolls. |
| BIOTECH-05 | Mechanitor control groups, bandwidth, work/escort/recharge modes | screenshot-only | unverified | pending | Existing mech controls; generic own-mech pawn eligibility does not imply management coverage. |
| BIOTECH-06 | Mech gestation, repair, resurrection, bosses and signal-chip progression | screenshot-only | unverified | pending | Existing bill/summon/progression screens; no hidden boss intent or summon call. |
| BIOTECH-07 | Pollution, wastepacks, detoxification and overlays | screenshot-only | unverified | pending | Existing public overlays/settings, no hidden map-grid or contained-waste dump. |
| BIOTECH-08 | Hemogen, deathrest, sanguophage abilities and related targets | screenshot-only | unverified | pending | Visible normal panels/target modes only; hidden conditions and internal resource logic remain absent. |

## Anomaly

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| ANOMALY-01 | Anomaly setup/scenario choices and monolith activation/progression | screenshot-only | unverified | pending | Existing public setup/monolith choices; no future incidents, secret stages or activation action. |
| ANOMALY-02 | Entity discovery, normal inspection and study/research | screenshot-only | unverified | pending | Representative discovered versus concealed entities; unfogged alone never authorizes a structured entity. |
| ANOMALY-03 | Capture, containment platforms, holding cells, suppression and escape alerts | screenshot-only | unverified | pending | Existing containment/order UI; no holder contents, hidden risk rolls or entity AI. |
| ANOMALY-04 | Rituals, invocations, sacrifices, participants and target selection | screenshot-only | unverified | pending | Visible ritual/setup/target modes only; no hidden result prediction or invocation. |
| ANOMALY-05 | Unnatural conditions, concealed threats and identity discoveries | screenshot-only | unverified | pending | Only currently player-known rendered disclosures; hidden diagnoses/identities absent from all DTO/count/event/error paths. |
| ANOMALY-06 | Ghouls/mutants, enhancements, resurrection and colony management | screenshot-only | unverified | pending | Existing mutant UI; own-pawn eligibility still exact-type/field audited, not blanket entity permission. |
| ANOMALY-07 | Anomaly quests, special maps and endgame choices | screenshot-only | unverified | pending | Public current quest/map/ending screens; no inaccessible map generation or future story-state lookup. |

## Odyssey

| ID | Player operation | M1 observation plan | Route state | Evidence | Required future proof |
| --- | --- | --- | --- | --- | --- |
| ODYSSEY-01 | Odyssey setup, gravship scenarios and starting choices | screenshot-only | unverified | pending | Existing setup screens/choices in ordinary flow; installed expansion is not scenario proof. |
| ODYSSEY-02 | Gravship construction, engine/substructure, layout and inspection | screenshot-only | unverified | pending | Existing public ship/build UI; no private ship connectivity or hidden cargo enumeration. |
| ODYSSEY-03 | Gravship launch, fuel/range, destination and landing target modes | screenshot-only | unverified | pending | Visible launch/destination/landing setup only; no launch/landing command or hidden destination contents. |
| ODYSSEY-04 | Gravship travel, arrival and carried colony/map transitions | screenshot-only | unverified | pending | Ordinary transitions invalidate affected game/map/entity/cursor identities; no stale retargeting. |
| ODYSSEY-05 | World exploration, landmarks, new biomes and temporary sites | screenshot-only | unverified | pending | Public current world/site screens; no undiscovered terrain/entities or unloaded-map lookup. |
| ODYSSEY-06 | Fishing, wildlife interactions and exploration resources | screenshot-only | unverified | pending | Existing normal designation/settings/inspection; no hidden fish/resource population counts. |
| ODYSSEY-07 | Orbital/space destinations, hazards, quests and progression choices | screenshot-only | unverified | pending | Public present travel/quest screens only; no future hazard/quest data or generated map access. |

## Gaps and promotion gates

1. **M1 baseline remains required, not complete.** Summary/manifest, own-pawn fields, eligible things/resources, selection/windows, public events and screenshots have adapters, authentication and main-thread/safety/budget enforcement in source. On the final package, one copied fixture showed menu, paused and live reads of summary, own pawns, pawn detail, UI and events and decoded screenshots (see the [phase-4 and final-package evidence summary](observation-adversarial.md)); only five narrow rows were promoted from that. `things.list` and `resources` refuse `unavailable` on every loaded map tried (raw-source cap) while capabilities reports them available, natural events were never observed, and normal-UI, hidden-state, off-camera, second-map, DLC, timing and capture-budget behavior remain unmet or unverified. `ui` does not promise rendered controls. Specialist structures in every section remain unsupported under current scope. Acceptance criteria 2, 3 and 5 are unmet or unverified.
2. **Pixels do not provide interaction or offscreen coverage.** Rendered target modes, tab contents, tooltips and custom widgets can only be observed after ordinary player/test-fixture setup. M1 cannot navigate to them. Later UI control needs permission, identity freshness and post-action state checks; a picture is not execution evidence.
3. **Representative scenarios may be unavailable.** Authorized copied fixture may lack particular children, entities, royal/ideology/endgame progression or a gravship. Keep those rows unverified; use approved normal disposable setup if available, never edit saves or substitute privileged fixture endpoints. Report limitations instead of inventing passes.
4. **Cross-cutting checks apply to every row.** Auth/session scope; loading/dev refusal; hidden-data absence from objects/counts/cursors/errors/events; no changed paused ticks, camera, selection, dialogs, hover or orders; finite traversal/queues/deadlines/capture cleanup. Compare live progression with a control, not a frozen-world assumption. Menu/pause/live are separate actual-game checks.
5. **All five DLCs and full solo mods remain in scope.** Custom getters/patches and custom controls need explicit audits. Co-play is deferred to M3 with separate compatible-profile evidence, not implied by solo observations. Qualification/stress evidence belongs to M5.

Asset-free [inventory guards](../test_operation_inventory.py) check coverage structure, stable IDs, status/evidence consistency and public relative links. They never launch the game or certify any row. Later evidence updates must preserve these distinctions and add precise failures/limitations rather than silently drop operations.
