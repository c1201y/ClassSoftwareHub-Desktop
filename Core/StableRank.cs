namespace ClassSoftwareHub.Desktop.Core;

/// <summary>
/// 稳定的乱序键（FNV-1a 哈希）。
///
/// <para>
/// 用途：从清单里"随便挑几个"时，别用 <c>Random</c> —— 每次进页面顺序都变，
/// 看着像页面在跳、像坏了。用 id 的稳定哈希排序，同一台机器每次结果一致；
/// 想要"换一批"时，往键里掺一个种子即可。
/// </para>
/// </summary>
public static class StableRank
{
    public static int Of(string key)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in key) hash = (hash ^ c) * 16777619u;
            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
