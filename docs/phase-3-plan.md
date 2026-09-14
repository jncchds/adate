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
   no-player-actions rule with a validator check.
4. **Choices:** model-proposed options, free-form replies, interpretation into tags, reactions and
   the popup.
5. **Initiative and temper:** initiative events and temper-scaled leaving.
6. **Endings:** the commitment ask, the written epilogue and the choice recap.
7. **Fixes and tuning:** gender, places, promises, tuning measure, live play in the other settings.
