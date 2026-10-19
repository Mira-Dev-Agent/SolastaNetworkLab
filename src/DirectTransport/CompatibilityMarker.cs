namespace Solasta.LegacyPackage
{
    // Older updaters require this filename in the package. No networking code is compiled into it.
    internal static class CompatibilityMarker { internal const bool DirectNetworkingEnabled = false; }
}
