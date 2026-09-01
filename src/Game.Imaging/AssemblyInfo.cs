using System.Runtime.CompilerServices;

// The websocket frame handler and URI builder are protocol details, not public API, but
// they are exactly the parts most worth testing: they were written against ComfyUI's
// documented protocol without a live server to try them against.
[assembly: InternalsVisibleTo("Game.Imaging.Tests")]
