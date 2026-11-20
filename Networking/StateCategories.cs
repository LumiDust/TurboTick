namespace TurboTick.Networking;

/// <summary>
/// The fixed order of the state-hash categories, and their user-facing names.
///
/// The names live in the shared assembly, next to nothing game-specific, for one reason: the mod builds the
/// category list and the coordinator labels it. When the two lists drift, the coordinator does not fail - it
/// silently shifts every label, which is worse. It happened on the first try: adding a seventh category
/// (生态, the wild-plant weight inputs) made "随机" point at the ecology hash and the real RNG hash print as
/// `category6`, so a run was read as "random stream diverged" when it had not.
///
/// Both sides now index <see cref="Names"/> - the mod and the coordinator both reference this assembly - so a
/// mismatch cannot happen without a compile of the same list.
/// </summary>
public static class StateCategories
{
    /// <summary>Position, health and job are split so a report names the field that differed rather than only
    /// saying "colonists". 生态 carries the wild-plant weight inputs; 随机 is the shared random stream, which
    /// leads every other category when it moves.</summary>
    public static readonly string[] Names = ["位置", "血量", "工作", "物体", "标记", "生态", "顺序", "随机"];

    public const int Position = 0;
    public const int Health = 1;
    public const int Job = 2;
    public const int Thing = 3;
    public const int Designation = 4;
    public const int Ecology = 5;
    public const int Order = 6;
    public const int Random = 7;

    public const int Count = 8;
}


