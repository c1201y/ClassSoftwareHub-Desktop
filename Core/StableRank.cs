namespace ClassSoftwareHub.Desktop.Core;










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
