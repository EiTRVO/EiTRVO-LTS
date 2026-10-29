namespace EiTRVO.Core.Launcher.Content;

/// <summary>从一个存档里读出来的事实（全部可为空：读不出来就是"不知道"）。</summary>
/// <param name="LevelName">存档显示名（<c>LevelName</c>）。</param>
/// <param name="GameType">游戏模式（0 生存 / 1 创造 / 2 冒险 / 3 旁观）；未知为 <see langword="null"/>。</param>
/// <param name="LastPlayed">最后游玩时间（<c>LastPlayed</c>，毫秒时间戳）。</param>
/// <param name="VersionName">存档的游戏版本（<c>Version.Name</c>，1.9+ 才有）。</param>
public sealed record WorldSaveFacts(
    string? LevelName,
    int? GameType,
    DateTimeOffset? LastPlayed,
    string? VersionName);

/// <summary>
/// 存档目录的读取（<c>saves/&lt;存档名&gt;/level.dat</c>）。
/// </summary>
/// <remarks>
/// 只读 <c>level.dat</c>，**不碰** <c>session.lock</c> 与任何其它文件：
/// 看存档不该有副作用，而 <c>session.lock</c> 是游戏进程判断"这个存档有没有被占用"的依据。
/// </remarks>
public static class WorldSaveReader
{
    /// <summary>读取一个存档目录。</summary>
    /// <param name="worldDirectory">存档目录。</param>
    /// <returns>事实；<c>level.dat</c> 缺失或损坏时为 <see langword="null"/>。</returns>
    public static WorldSaveFacts? Read(string worldDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);

        var root = NbtReader.ReadGzipFile(Path.Combine(worldDirectory, "level.dat"));
        if (root is null)
        {
            return null;
        }

        return new WorldSaveFacts(
            LevelName: Trimmed(root.Text("LevelName")),
            GameType: ToGameType(root.Number("GameType")),
            LastPlayed: ToTimestamp(root.Number("LastPlayed")),
            VersionName: Trimmed(root.Compound("Version")?.Text("Name")));
    }

    /// <summary>游戏模式编号；不在 0…3 之内就当成"不知道"。</summary>
    /// <param name="gameType">NBT 里读到的数字。</param>
    /// <returns>模式编号。</returns>
    public static int? ToGameType(long? gameType) =>
        gameType is >= 0 and <= 3 ? (int)gameType.Value : null;

    /// <summary>毫秒时间戳 → 时间；超出范围时当成"不知道"而不是抛异常。</summary>
    /// <param name="milliseconds">毫秒时间戳。</param>
    /// <returns>时间。</returns>
    public static DateTimeOffset? ToTimestamp(long? milliseconds)
    {
        if (milliseconds is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
