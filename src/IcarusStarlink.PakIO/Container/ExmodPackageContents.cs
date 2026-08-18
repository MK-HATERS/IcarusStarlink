using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Container;

public sealed record ExmodPackageContents(ExmodPackage Package, IReadOnlyList<ExmodAssetEntry> Assets);
