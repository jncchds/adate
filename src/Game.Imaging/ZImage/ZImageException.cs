namespace Game.Imaging.ZImage;

/// <summary>Anything the Z-Image API refused or failed to do.</summary>
public sealed class ZImageException(string message, Exception? inner = null) : Exception(message, inner);
