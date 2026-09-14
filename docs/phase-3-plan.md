# Phase 3: a playable game

Phase 2 built the systems: cast, story state, arcs, endings and an LLM that writes validated scenes.
Play is still a skeleton, though. Most turns are one line of filler, nobody is drawn in a scene, and
choices have no aftermath. This plan makes it a game. Decisions come first, then the build order.

## Decisions (from the user, 2026-09-14)

1. **Characters in scenes.**
   * A full-body standing sprite with the background removed, one person in front at a time.
   * The expression comes from the written scene, the outfit from the character's aesthetic, per day.
2. **Every turn is written.**
   * Something happens or is described on every turn: people, the place, the weather.
   * The narrator never decides or describes what the player does, says, thinks or feels. It
     describes only the world and the other people. That rule is checked, not just asked for.
3. **Choices.**
   * Written scenes end with two or three options proposed by the model.
   * The player may also type a free-form reply. The model interprets it into the same value tags
     and effects, and C# validates and clamps them like any other change.
   * A short reaction is written after either kind.
4. **Relationship numbers are hidden.** A popup appears only when a reaction is considerable.
5. **Initiative follows temper and play.**
   * Love interests reach out, invite, cancel or turn up depending on their temper and on how the
     player behaves.
   * A player who holds back gives bolder characters room to take the lead.
6. **Walking away depends on temper.** How long someone waits, and how much suspicion they
   tolerate, come from their temper.
7. **Endings.**
   * The model writes the epilogue from what happened.
   * Afterwards the player sees the choices they made and how each moved each relationship, as
     choice-based games do.
8. **Weather** changes daily, in the writing and in the backgrounds.

## Also to fix

* The player's gender reaches the writer: pronouns in other people's lines.
* A scene may not propose a place its own encounter revealed. Place details are offered as ids per
  type.
* Schedules are assigned, so people can be found at places without an encounter.
* Promises are created in play (a date agreed for later), so trust and leaving can use them.
* Relationship tuning is measured with a varied policy, not only the first option.
* Small town and summer camp are played live, not only in tests.
* The cast debug page's stale note.
* A proposed place differing only by case or a leading "The" is added again (live: "The rooftop
  garden" and "rooftop garden" both on the map).
* Scenes still hand the player things ("the book in your hands"); the player-action check only sees
  verbs.
* The measure's first-option policy rarely finds company (most turns quiet and alone), so choices
  and reactions get few samples; the varied policy should seek people out.

## Build order

Each step ends in something that runs and is committed.

1. **Weather:** content, a daily roll, background variants cached per weather, and weather in the
   packet. *Done: five kinds, a deterministic roll that holds half the time, place-type phrases per
   kind, migration 007, and weather on the map and in the packet. Clear adds nothing, so existing
   backgrounds stay valid.*
2. **Characters in scenes:** day outfits, cast sprites on demand, the stage showing whoever the
   scene is about with the scene's expression. *Done: full-body matted sprites per cast member and
   expression, rendered on first use (about 6.5 s, then cached). The person shows at their resting
   expression straight away and switches to the written one. Outfits follow each person's aesthetic.
   Day-to-day variety is still open.*
3. **Every turn written:** schedules, presence from schedule or encounter, quiet scenes, and the
   no-player-actions rule with a validator check. *Built: schedules derived per person and anchored
   where the story put them, quiet scenes with company or of the place, player-action narration
   rejected, and authored texts rewritten.*
4. **Choices:** model-proposed options, free-form replies, interpretation into tags, reactions and
   the popup. *Built: a written scene with someone present (and no authored choices) must end with
   two or three short replies, each tagged from the scoring vocabulary (desires, dealbreaker tags,
   helps/hinders:{want}); at most one may touch the want, because Gemma otherwise tags every friendly
   line helps:{want} (seen in the first measure: every reply scored only the want). The scene waits
   in `pending_scene` and turns are refused until it is answered. A reply is a proposed choice (its
   own tags) or free text (the reaction writer reads tags, unknown ones dropped). The reaction may
   restate the player's reply but add no other player action; text cut off mid-sentence or with an
   open quote is sent back, in scenes and reactions alike. Tags are scored for everyone present and
   committed with closing the scene in one transaction. The popup shows when affection or trust moves
   by 4 or more, or a dealbreaker trips; the map no longer shows relationship numbers.*
   *Measured (28 days, Gemma 12B QAT, every turn written, before the want-tag and unfinished-text
   rules): 157 scenes, 155 written, 2 fallbacks (1.3%), first try 138, mean 6.9 s, median 4.2 s,
   max 92.4 s; rejections: other 22, player action 11, judge 4, json 4, place 3, unreachable 2;
   25 choices answered, reactions written first try. The step-3 commit did not build (a missing
   using), so its earlier measure had run the old binaries.*
   *Live: a free-form reply waited 113 s and then failed with LM Studio's "Context size has been
   exceeded" (10K context shared by 4 parallel slots, while the measure ran); the retry answered.
   Requests now cap their output (scenes 1500 tokens, reactions 700), so a runaway answer ends fast
   and is retried. A reply admitting a lie was tagged only honesty, so the reaction prompt now keeps
   a dealbreaker tag even when the behaviour is confessed. Enter in the reply box raced the button's
   enabled state; the button is now only disabled while a reply is being written.*
5. **Initiative and temper:** initiative events and temper-scaled leaving. *Built: temper modifiers
   gain patience and initiative. Neglect days and forgiven broken promises scale with patience
   (fiery 0.7, ambitious 0.8, reserved 1.15, calm and easygoing 1.3), next to the existing suspicion
   scale. On a turn with no encounter and nobody scheduled at the place, a met, open person may come
   looking for the player (never at night): a 12% base plus 4% per unseen day, times their initiative
   scale (outgoing 1.6, fiery 1.3, open 1.2, calm 0.85, guarded 0.75, reserved 0.6), halved for an
   acquaintance, 1.5x when the player has never invited them and 0.6x once the player has invited
   them three times or more; capped at 60%, a deterministic roll, and a three-day cooldown per
   person. The visit is written as them taking the lead and ends in the usual replies.*
6. **Endings:** the commitment ask, the written epilogue and the choice recap. *Built: every reply
   and authored choice is logged with how it moved each person there. The ending shows up to twelve
   choices that moved someone, the most influential kept, in story order, each with plain influence
   ("Kai warmed to you", "Kai trusted you a little less", "Kai won't forget that"); numbers stay
   hidden to the end as well. Gemma writes the epilogue from the decided ending, the partner's
   temper, who was passed over or left, the last memory summaries and the recap; it must be second
   person, finished and name the partner when together, else the authored text stands. The
   commitment ask is the ending offer framed as the most attached person on offer asking; a
   separate authored ask scene was judged not worth another turn type.*
7. **Fixes and tuning:** gender, places, promises, tuning measure, live play in the other settings.
   *Built so far: the player's pronouns reach the packet; place names equal up to case, punctuation
   or a leading "the" are refused as known; scenes may not give the player possessions; the cast
   page's note is current; `measure run --policy varied` rotates through replies and choices.
   Promises are made in play: a reaction may report a meeting the two just agreed (a known place,
   1 to 3 days ahead, not at night, within the story); C# turns it into a meet promise, one open
   meeting per person, and tells the player. Turning up at that place and time puts the person
   there ("waiting, as agreed"); every turn resolves open promises, kept by being there together and
   broken once the time has passed, with the trust change committed in the turn. The promise row is
   marked right after the turn commits, not in the same transaction; a crash in between could apply
   the trust change twice, judged acceptable for now.*
