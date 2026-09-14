# Phase 2 plan

The goal is a playable story skeleton rendered through Z-Image. The player, in first person, meets a
main LI they described. During play they meet three alternatives built from that LI, and they end
with one of the four or alone. Characters, places and story facts persist for the whole save.

**Measure** marks a pass/fail check that gates the work after it.

HANDOFF invariants that shape everything below:

* The LLM never owns state (1.2). C# owns every fact and number; the LLM proposes JSON that C#
  validates.
* The LLM never writes prompts (1.3).

---

## 1. Images: Z-Image for everything

* **Server in the repo. Done.** `compose/zimage/`, with DiffSynth-Studio, torch, transformers
  and BiRefNet pinned.
* **Transparency. Done.** `/generate` takes `background: remove` (BiRefNet, in the same process),
  requested for `ZImageOptions.MatteWorkflows`. The matting model is in the cache key for those
  workflows only. Measured in `decisions.md`.
* **Prompts.** Add `NaturalPromptCompiler` and `stylepacks/zimage-anime.json`, written as phrases
  instead of tags. Age bands get re-measured in the new wording.
* **Style: anime, prompted directly.** A photo filter, img2img restyling and glossy 3D were
  tried and rejected (`decisions.md`). Sprite checks must also look for a second figure, which
  matting keeps.
* **img2img. Done.** `/generate` takes `input_image_id` and `denoising_strength`. It isn't used
  for styling; it's kept for the consistency fallback below.
* **The API's `/characters` store stays unused.** It isn't save-scoped and isn't in the cache key.
  The game's own character record is the source of truth.

**Measure — consistency.** *Passed on four characters, two women and two men aged 22-45:
every sprite at 86.7-98.7% IoU vs neutral, with one figure each (`decisions.md`). A new
problem it exposed: declared skin tone, build, visible age and facial hair often don't render.
Pack wording now fixes brown and tanned skin and reads older at 45 (`decisions.md`). Still
open: dark skin renders no darker than brown, build doesn't respond to wording, and beards and
freckles are faint on sprites.*
* **Test:** six expressions for four characters, each at their anchor seed.
* **Pass:** silhouette IoU ≥ 85% on 5 of 6 expressions, with no outfit changes.
* **Fallbacks, in order:**
  1. A framing sentence before the expression.
  2. The expression as a short final clause.
  3. Img2img from the portrait.
  4. A per-character LoRA.
  5. A cut between expressions instead of a fade.

**Measure — negatives at cfg 1.0.** *Done: inert, byte-identical on 3 seeds, and cfg 2.0 is no
fix. `zimage-anime` declares `negativePrompts: Ignored`, compiles no negative, and the provider
refuses one (`decisions.md`).* Render with `alwaysNegative` on and off at three seeds. If it
makes no difference, the loader refuses Z-Image packs that declare negatives. Safety then rests on
the age clamp, restricted positives and migration 002, which it already does structurally.

## 2. Settings

Each setting is a content file, `content/settings/{id}.json`, holding:
* place types and the player's routine place;
* three openings and the calendar length;
* dated setting events;
* occupation and want vocabulary, and the tone of the writing.

| Setting | Days | Routine place | Place types |
|---|---|---|---|
| `big-city` | 28 | Office | cafe, park, bar, bookshop, stairwell, rooftop, night market |
| `small-town` | 28 | Family shop | diner, main street, library, lake pier, fair, hilltop |
| `summer-camp` | 21 | Staff cabin | mess hall, lake dock, campfire, trail, boathouse, lounge |

Summer camp is about adult staff. The minimum age remains a game setting, and no setting touches
the clamp.

## 3. New game

1. Pick a setting.
2. Enter the player's name and gender. They appear in the writing only, never in art.
3. Describe the main LI:
   * gender, age, name and appearance, using the existing studio form;
   * temper, picked from the §5 list with a default preselected.
4. Pick a candidate portrait and approve it (existing flow).
5. The cast and story bible are generated. They're stored as records with no art and hidden from
   the player.
6. Pick one of the setting's three openings.

## 4. Meeting the main LI

Each opening sets the meeting place, the LI's home place and the first flags. For `big-city`:

| Opening | Meeting place | Time | Home place |
|---|---|---|---|
| Shared table | Cafe | Morning | Cafe, weekday mornings |
| Lost and found | Park | Evening | Park footpath, evenings |
| New neighbour | Stairwell | Afternoon | Their building |

Every opening then runs the same four beats:
1. **Meet** on day 1.
2. **Recognise** at the home place on day 2-4. If missed, it re-arms once at a second place.
3. **Contact:** a choice that is required for dates.
4. **First date** at a place the player picks from the ones they know.

## 5. The cast

There are three alternatives, each built from the main LI by a contrast profile
(`content/contrasts.json`). Each is a different person, not a recolour.

| Profile | Question | Look | Inner |
|---|---|---|---|
| **Bolder** | Same heart, louder life? | Striking hair and style, an added feature | A different want from the same family; energy and temper flipped |
| **Opposite** | Nothing alike? | Different aesthetic, hair, eyes, build or skin tone | Opposite temper; conflicting want |
| **Other life** | Different chapter? | Different age band, style and hair | Similar temper; different job and wants |

**What a variant may change**, always from pack or content lists:
* hair colour and style, eye colour;
* a distinguishing feature, and a style aesthetic that drives the wardrobe;
* skin tone, build and height;
* age;
* temper, occupation and wants.

**Budget.** Each variant must differ from the main LI in:
* at least 3 look dimensions, one of them visible in silhouette;
* at least 2 temper axes;
* its primary want.

No two variants may make the same silhouette change.

**Never varies:**
* gender;
* age going below 18. If the main LI is under 18, variants take their exact age;
* build and height choices marked `playerOnly`, which a player may pick but the generator never
  does.

Each variant gets its own anchor seed. A trigger in the schema enforces the gender and age rules.

**Temper** comes from `content/temper.json`, a predefined list that can grow. Each end of an axis
carries writing guidance, a resting expression and an outfit leaning.

| Axis | Ends |
|---|---|
| Temper | calm / fiery |
| Energy | reserved / outgoing |
| Warmth | guarded / open |
| Humour | dry / playful |
| Drive | easygoing / ambitious |

**How they're met.** Temper picks the route.

| Route | Suits | Trigger |
|---|---|---|
| Routine | open, easygoing | At the player's routine place from day 1; talkable after 2 visits |
| Introduced | outgoing, playful | Once the main LI reaches `dating`, during an evening with them |
| Chance | fiery, guarded | The player visits one place alone 3+ times, Evening or Night |

**Measure — distinctness.** Show the four portraits without names to someone who hasn't seen the
design. It passes if they see four different people and can guess each profile's question.

## 6. Wants and relationships

Every character, main LI included, has four layers of wants, all picked from content lists:

| Layer | Example | Role |
|---|---|---|
| Primary want | open a bakery, leave town | Drives their arc (§7) |
| Partner desires (3, weighted) | honesty, adventure, stability, humour | Scores the player's choices |
| Dealbreakers (1-2) | dishonesty, being second choice | Uncapped loss; a reason to leave |
| Need (hidden) | what would actually make them happy | Required to reach `committed` |

They also have likes and dislikes (places, activities, gifts).

**Choices carry value tags** from the partner-desire list, plus optional `helps:want` or
`hinders:want`. C# computes the change; the LLM never does. Values are clamped per scene and per
day, so no single scene decides a route; the crisis beat and dealbreakers are exempt.

```
affection  += Σ desire weight × tag × temper scale
trust      += honesty, kept promises; −= broken promises, dealbreakers
attraction += likes hit on dates; −= dislikes
suspicion  += learning about the player's other relationships, × temper
```

The characters' wants differ, so one choice raises one character and costs another. That turns
"who looks right" into "who fits".

**Stages:**
1. `stranger`
2. `acquaintance`
3. `friend`, once their want is revealed;
4. `dating`, after an accepted date;
5. `committed`, once trust is high, the crisis is resolved and the need is addressed.

Temper scales every threshold.

## 7. Plot

| Layer | Made when | By |
|---|---|---|
| Premise | Authored | The setting file: season arc and setting events |
| Story bible | New game | C# picks from seeded lists; the LLM adds flavour |
| Scene | Each time slot | The engine picks the encounter; the LLM writes it |

**Story bible.**
* **C# picks,** for each character: job, wants, secret, schedule template and ties to other cast
  members (`content/ties.json`).
* **The LLM adds** name, backstory, voice and specific details, as schema-checked JSON.
* **Storage:** accepted details become `core` facts. The rest falls back to placeholder text.

**Arcs.** Each character has four beats tied to their primary want. Each beat is an encounter with
preconditions:
1. **Reveal**
2. **Obstacle**
3. **Crisis:** the player helps, stays out, or makes it worse. This is the biggest swing.
4. **Resolution**

When two characters' wants conflict, their crises land on the same setting event, so the player
can't help both.

**Daily world tick.**
* Advance schedules.
* Roll the weather.
* Pick outfits.
* Characters may take initiative (invite, cancel, show up, confront), weighted by temper and wants.
* Evaluate leaving rules.

**Encounters** are content (`content/encounters.json`, overridable per setting). C# decides when
one fires; the LLM only writes it.

## 8. Facts and continuity

**Facts** are triples: subject, predicate, object. Each also stores its level, source and day.
* **Predicates** come from a controlled list (`content/predicates.json`). Each is marked
  single- or multi-valued, and mutable or not.
* **Levels:**
  * `core`: from the story bible;
  * `established`: shown on screen;
  * `claimed`: said by a character, and allowed to be false. Secrets work this way.
* **Contradictions:**
  * A single-valued, immutable fact that conflicts with an existing one is rejected.
  * A mutable fact must supersede the old one, and needs an event that explains the change.
* **Appearance** is stored as immutable `core` facts.

**Knowledge.** Every fact records who knows it: the player, or a specific character. The writer
only gets facts that the characters in the scene know, plus what the player knows. When a character
learns something, that is a state change, such as a rise in suspicion.

**Memory.**
* Each scene yields a short summary with an embedding (from the compose `embed` service) and
  salience tags.
* Summaries compact from scenes into days into weeks.
* Memories tagged `first` or `conflict` are never compacted.

**Schedules and promises.**
* **Schedules:** each character has a weekly template that the world tick resolves to a place per
  time slot. A character can't appear where their schedule doesn't put them, unless an encounter
  or a promise overrides it.
* **Promises** are rows: meet, call, bring, keep a secret, help. A kept or broken promise changes
  trust, and a broken promise feeds the leaving rules.

**Context packet.** C# assembles one per scene in a fixed order, within about 3k tokens for an 8B
model:
1. setting, day, time slot, weather and place;
2. the characters present, each with their canon, relationship stage described in words, open
   promises, pending beat and today's outfit;
3. what the player knows;
4. what the present characters know about the player's other relationships;
5. memories: the last shared scene, top retrieved scenes, and the week summary;
6. the encounter's required outcomes;
7. the response schema.

**Validation.** The LLM returns the text plus JSON for choices, facts, promises, knowledge, places
and expressions. C# rejects a scene and retries with the reason if it:
* breaks the schema or the vocabularies;
* contradicts a fact;
* includes someone whose schedule doesn't put them there;
* has a character use knowledge they don't have;
* goes beyond the relationship stage.

Intimacy beyond the content ceiling fades out instead of failing. An optional short LLM **judge**
checks the text against immutable facts. After two retries the beat's authored fallback is used. Every
turn is logged along with its packet, so it can be replayed.

**Visual continuity** comes from state, never from the text:
* outfit per character per day;
* expression from validated JSON;
* place and time from the encounter;
* weather from the world tick.

## 9. Endings

* **Outcomes:** the player ends with any one of the four characters, or alone.
* **The player's choice:** the player can decline everyone at the ending check, or whenever a
  character asks for commitment.
* **The characters' choices:** a character leaves after stood-up dates, suspicion past what their
  temper tolerates, a dealbreaker, or being neglected at `acquaintance`. The rules and thresholds
  are content that C# evaluates. A closed route stays closed, and if every route closes the ending
  is alone.
* **The check:** runs on the last day, or earlier if only one route is open and it has reached
  `committed`.
  1. Close the routes of anyone leaving.
  2. Offer the routes at `dating` or above, plus alone.
  3. Play the ending.
  4. Show what the playthrough revealed: who they ended with, who they passed over, and which
     looks, tempers and values they chose (`player_profile`).
* **Content:** `content/endings.json`, overridable per setting.
* **Tests:** every combination of open routes and player picks yields exactly one ending.

## 10. Persistence

**Places** are save-scoped. Each has:
* a type, a name, 0-3 details from the type's vocabulary, and its own seed;
* an origin (authored, or introduced by the story), and whether the player knows it.

The story introduces a place by proposing a type, a name and detail ids. A place is stored when
first mentioned, and its background is drawn on the first visit for each time of day. The name
never enters a prompt. `background_cache.location_id` becomes a place id, and the current
locations become authored places.

**Character records** gain name, role, `variant_of`, the variation and temper, route, home place,
and the day, place and encounter where they were met. A wardrobe of named outfits is picked from
the pack and leans the way the temper does; `sprite_cache` is already keyed by outfit. After
creation only outfit and expression change.

| Migration | Tables |
|---|---|
| 003 | `place`, `character_outfit`, `game_clock`, `flag`, `visit`; new `save` and `character` columns and the variant trigger |
| 004 | `fact`, `fact_knowledge`, `rel_state`, `schedule`, `promise`, `turn_log`; `character.story_json` for desires, dealbreakers, need and likes |
| later | `arc_beat` (step 7), `player_profile` (step 8), `memory` (step 9) |

## 11. Build order

Each step ends in something that runs.

1. **Z-Image for everything** (§1), including both measurements. The studio flow runs with ComfyUI
   stopped.
2. **Cast generator** (§5). A debug page shows the four portraits and profiles, and the
   distinctness measurement runs here. *Built: `/cast/{id}`, rules unit-tested, and two casts
   rendered as four distinct people each (`decisions.md`). The distinctness judgement by someone
   who hasn't seen the design is still to do.*
3. **Settings, places and migration 003** (§2, §10). *Done: 22 place types, three settings, places
   stored per save with their own seed, migration 003 with the cast rules in the schema, and the
   cast stored once built (`decisions.md`).*
4. **Clock, encounters and a map screen**, with no LLM and placeholder text. *Done: five-slot
   clock, encounters as validated content with setting events added, atomic turns, and
   `/play/{saveId}` (`decisions.md`).*
5. **New game and the openings** (§3-4), in `big-city` first. *Done for all three settings:
   the new-game form with setting, player, main LI name and temper; the cast built on approval;
   `/opening/{saveId}`; the four beats generated from each opening's places, with a stored contact
   choice and an invite to the first date. Played through in `big-city`, and every opening is
   unit-tested.*
6. **Story state with no LLM** (§6, §8), unit-tested with authored scenes:
   * facts and knowledge;
   * wants and scoring;
   * schedules and promises;
   * validation.

   *Done: profiles, the relationship engine, the fact ledger, schedules, promises and the scene
   validator, with content in `values.json`, `predicates.json` and `relationship.json` and
   migration 004. In play, the contact choice and the first date move the main LI's relationship,
   and the map shows it (`decisions.md`).*
7. **Variant routes and arcs** (§5, §7). *Done: routes assigned by temper, named, with generated
   meeting, contact and first-date beats; four-beat arcs per love interest, with every crisis on
   the last event for whoever the player brings; scoring for everyone in a scene
   (`decisions.md`).*
8. **Endings** (§9).
9. **LLM** (§7-8):
   * **Features:** story bible flavour, scene writing, fact extraction, the judge and place
     proposals.
   * **Measures:** fallback rate under 5% over a scripted 28-day run; judge accuracy on planted
     contradictions; whether retrieval helps; turn latency on the 12 GB profile.
