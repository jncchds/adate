namespace Game.Core.Content;

/// <summary>
/// Something the player can do at a place instead of just passing time there (user feedback: wandering from
/// place to place waiting for the next meeting). Doing it builds a trait and gives the writer a pastime.
/// </summary>
/// <param name="Id">Unique within its place type; no dots.</param>
/// <param name="Label">The button: "Read with a coffee".</param>
/// <param name="Trait">A desire id: the quality doing this shows, and that people who value it warm to.</param>
/// <param name="Scene">What the player came to do, as the writer is told it: "read a book over a slow coffee".</param>
/// <param name="Habit">The same as a habit, after the player's name and "often": "reads over coffee at the cafe".</param>
public sealed record PlaceActivity(string Id, string Label, string Trait, string Scene, string Habit);
